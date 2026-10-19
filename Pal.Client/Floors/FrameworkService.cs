using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pal.Client.Configuration;
using Pal.Client.Database;
using Pal.Client.DependencyInjection;
using Pal.Client.Net;
using Pal.Client.Rendering;
using Pal.Client.Scheduled;
using Pal.Common;
using static Pal.Client.Rendering.SplatoonRenderer;

namespace Pal.Client.Floors
{
    internal sealed class FrameworkService : IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<FrameworkService> _logger;
        private readonly IFramework _framework;
        private readonly ConfigurationManager _configurationManager;
        private readonly IPalacePalConfiguration _configuration;
        private readonly IClientState _clientState;
        private readonly TerritoryState _territoryState;
        private readonly FloorService _floorService;
        private readonly DebugState _debugState;
        private readonly RenderAdapter _renderAdapter;
        private readonly IObjectTable _objectTable;
        private readonly RemoteApi _remoteApi;
        private readonly PomanderSensor _pomanderSensor;
        private readonly ConfirmedLocationStore _confirmedStore;

        // 上一幀算出來的「本層是否該隱藏陷阱/受詛咒藏寶箱」。
        // null ＝ 剛換區還沒有基準,此時只採納現值、不寫 log 也不強制重建。
        private bool? _lastHideTraps;
        private bool? _lastHideHoardCoffers;
        private int _pomanderRedrawCount;
        private bool _pomanderRedrawCapLogged;

        private readonly TrapLabelState _trapLabels = new();

        private readonly SteppedTrapState _steppedTraps = new();

        // -1＝「踩過的陷阱本層不再顯示」關著，否則＝本層觸發點數；與上一幀不同就重建一次圖層。
        private int _lastSteppedKey = -1;
        private bool _steppedLogPending;

        // 每幀清空重填、只在 framework 執行緒用；內容只在當幀有效，不可交給背景工作或留到下一幀。
        private readonly List<VisibleLocation> _visiblePersistentLocations = new();
        private readonly List<VisibleLocation> _visibleEphemeralLocations = new();
        private readonly List<(EConfirmedKind Kind, System.Numerics.Vector3 Position, EObservationSource Source)> _confirmCandidates = new();

        // 實色層的點來自我方確認資料庫，不是 memoryTerritory；只為了掛 RenderElement 讓就地改色找得到。
        private readonly List<PersistentLocation> _confirmedRenderLocations = new();

        // 只在 RecreatePersistentLayout 裡用：上一次的實色層點位依（種類、座標）找回來沿用元素，每個舊點最多沿用一次。
        private readonly Dictionary<(MemoryLocation.EType Type, System.Numerics.Vector3 Position), PersistentLocation> _previousConfirmed = new();

        /// <summary>
        /// 同一個區域內因魔陶器翻轉而重建圖層的次數上限。正常一趟(10 層)頂多數十次;
        /// 超過就代表旗標判讀有問題,退回原本的就地改色 —— 失敗方向是「維持現狀」,
        /// 不會變成每幀重建。
        /// </summary>
        private const int MaxPomanderRedrawsPerTerritory = 100;

        internal Queue<IQueueOnFrameworkThread> EarlyEventQueue { get; } = new();
        internal Queue<IQueueOnFrameworkThread> LateEventQueue { get; } = new();
        internal ConcurrentQueue<nint> NextUpdateObjects { get; } = new();

        /// <summary>踩到陷阱的位置（動作效果 hook 寫入、任何執行緒）；只在 framework 執行緒取出記錄。</summary>
        internal ConcurrentQueue<System.Numerics.Vector3> TriggeredTrapPositions { get; } = new();

        public FrameworkService(
            IServiceProvider serviceProvider,
            ILogger<FrameworkService> logger,
            IFramework framework,
            ConfigurationManager configurationManager,
            IPalacePalConfiguration configuration,
            IClientState clientState,
            TerritoryState territoryState,
            FloorService floorService,
            DebugState debugState,
            RenderAdapter renderAdapter,
            IObjectTable objectTable,
            RemoteApi remoteApi,
            PomanderSensor pomanderSensor,
            ConfirmedLocationStore confirmedStore)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _framework = framework;
            _configurationManager = configurationManager;
            _configuration = configuration;
            _clientState = clientState;
            _territoryState = territoryState;
            _floorService = floorService;
            _debugState = debugState;
            _renderAdapter = renderAdapter;
            _objectTable = objectTable;
            _remoteApi = remoteApi;
            _pomanderSensor = pomanderSensor;
            _confirmedStore = confirmedStore;

            _framework.Update += OnUpdate;
            _configurationManager.Saved += OnSaved;
        }

        public void Dispose()
        {
            _framework.Update -= OnUpdate;
            _configurationManager.Saved -= OnSaved;
        }

        private void OnSaved(object? sender, IPalacePalConfiguration? config)
        {
            EarlyEventQueue.Enqueue(new QueuedConfigUpdate());
            _trapLabels.Dirty = true;
        }

        private void OnUpdate(object framework)
        {
            if (_configuration.FirstUse)
                return;

            try
            {
                bool recreateLayout = false;

                while (EarlyEventQueue.TryDequeue(out IQueueOnFrameworkThread? queued))
                    HandleQueued(queued, ref recreateLayout);

                if (_territoryState.LastTerritory != _clientState.TerritoryType)
                {
                    MemoryTerritory? oldTerritory = _floorService.GetTerritoryIfReady(_territoryState.LastTerritory);
                    if (oldTerritory != null)
                        oldTerritory.SyncState = ESyncState.NotAttempted;

                    _territoryState.LastTerritory = _clientState.TerritoryType;
                    NextUpdateObjects.Clear();
                    TriggeredTrapPositions.Clear();
                    _confirmedStore.FlushIfDirty();

                    _floorService.ChangeTerritory(_territoryState.LastTerritory);

                    // 換區一律把兩個魔陶器狀態歸零。下面那一行每次換區都印、記 Verbose:
                    // 「進入深宮時的完整狀態」CheckPomanderVisibilityTransitions 會以 Information 印一次。
                    _territoryState.PomanderOfSight = PomanderState.Inactive;
                    _territoryState.PomanderOfIntuition = PomanderState.Inactive;
                    PluginLog.Verbose(
                        $"PalacePal: territory -> {(ETerritoryType)_territoryState.LastTerritory}, pomander state reset");
                    recreateLayout = true;
                    _lastHideTraps = null;
                    _lastHideHoardCoffers = null;
                    _pomanderRedrawCount = 0;
                    _pomanderRedrawCapLogged = false;
                    if (_trapLabels.Leave())
                        _renderAdapter.UpdateTrapLabels(false);
                    ResetSteppedTraps(floorChange: false);
                    _debugState.Reset();
                    Plugin.P._rootScope!.ServiceProvider.GetRequiredService<RenderAdapter>()._implementation.UpdateExitElement();
                    ExternalUtils.UpdateBronzeTreasureCoffers(_clientState.TerritoryType);
                }

                if (!_territoryState.IsInDeepDungeon())
                {
                    _pomanderSensor.Reset();
                    return;
                }

                // 魔陶器狀態每幀從遊戲結構重讀 —— 用藥當下就生效,不必等任何重繪事件。
                // 放在 IsReady 檢查之前,樓層變化才不會在載入畫面期間被漏掉。
                _pomanderSensor.Update();
                UpdateTrapLabels();
                ResetSteppedTraps(floorChange: true);

                if (!_floorService.IsReady(_territoryState.LastTerritory))
                    return;

                if (_renderAdapter.RequireRedraw)
                {
                    recreateLayout = true;
                    _renderAdapter.RequireRedraw = false;
                }

                // 魔陶器旗標翻轉時要重算顏色。咒印解除把陷阱清掉不會產生任何「位置變動」,
                // 所以由這裡要求重建。
                // 放在 IsReady 之後:樓層還沒載好時不比對,基準留著,載好的第一幀照樣看得到差異。
                if (CheckPomanderVisibilityTransitions())
                    recreateLayout = true;

                ETerritoryType territoryType = (ETerritoryType)_territoryState.LastTerritory;
                MemoryTerritory memoryTerritory = _floorService.GetTerritoryIfReady(territoryType)!;
                if (_configuration.Mode == EMode.Online && memoryTerritory.SyncState == ESyncState.NotAttempted)
                {
                    memoryTerritory.SyncState = ESyncState.Started;
                    Task.Run(async () => await DownloadLocationsForTerritory(_territoryState.LastTerritory));
                }

                while (LateEventQueue.TryDequeue(out IQueueOnFrameworkThread? queued))
                    HandleQueued(queued, ref recreateLayout);

                (IReadOnlyList<VisibleLocation> visiblePersistentMarkers,
                        IReadOnlyList<VisibleLocation> visibleEphemeralMarkers) =
                    GetRelevantGameObjects();

                // 把「這一幀真的看得到什麼」拍成快照給 IPC 讀。
                // 🔑 位置刻意就放在這裡:用的是外掛自己拿去畫圖的同一份清單,不另外算一份
                //    會分岔的;而且無條件每幀更新(清單是空的也照拍),時間戳才有意義。
                // ⚠️ 排在 HandlePersistentLocations 之前:快照只抄 Vector3,先拍後拍內容都一樣,
                //    但先拍語意比較乾淨(拍的是「觀測結果」不是「合併結果」)。
                _floorService.UpdateVisibleLocations(
                    _territoryState.LastTerritory,
                    _pomanderSensor.CurrentFloor,
                    visiblePersistentMarkers,
                    visibleEphemeralMarkers);

                bool confirmedAdded = RecordConfirmations();
                bool steppedChanged = SyncSteppedTraps();
                HandlePersistentLocations(territoryType, visiblePersistentMarkers, recreateLayout || confirmedAdded || steppedChanged);

                if (_floorService.MergeEphemeralLocations(visibleEphemeralMarkers, recreateLayout))
                    RecreateEphemeralLayout();
            }
            catch (Exception e)
            {
                _debugState.SetFromException(e);
                e.Log();
            }
        }

        #region Render Markers

        /// <summary>
        /// 比對「本層是否該隱藏陷阱/受詛咒藏寶箱」與上一幀的差異,翻轉時要求整層重建。
        /// 轉換極稀少(只有用藥、換層、改設定會發生),重建成本可接受。
        /// </summary>
        /// <returns>這一幀是否需要重建圖層。</returns>
        private bool CheckPomanderVisibilityTransitions()
        {
            bool hideTraps = _territoryState.ShouldHideTraps(_configuration);
            bool hideHoardCoffers = _territoryState.ShouldHideHoardCoffers(_configuration);

            // 這一區的第一次評估:把幾個總開關的實際值印出來。
            // 沒有這行的話,「使用者把總開關關掉了」與「偵測從來沒觸發」在 log 裡長得一模一樣
            // —— 前者永遠不會產生任何翻轉,也就永遠不會寫出下面那幾行轉換 log。
            if (_lastHideTraps == null || _lastHideHoardCoffers == null)
            {
                _logger.LogInformation(
                    "PalacePal:進入 {Territory},魔陶器隱藏設定:陷阱總開關={TrapGate}"
                    + "(咒印解除={OnSafety}、全景={OnSight}),受詛咒藏寶箱總開關={HoardGate}"
                    + "(感知寶藏={OnIntuition});目前判定 隱藏陷阱={HideTraps}、隱藏藏寶箱={HideHoard}。",
                    (ETerritoryType)_territoryState.LastTerritory,
                    _configuration.DeepDungeons.Traps.OnlyVisibleAfterPomander,
                    P.Config.HideTrapsOnSafety,
                    P.Config.HideTrapsOnSight,
                    _configuration.DeepDungeons.HoardCoffers.OnlyVisibleAfterPomander,
                    P.Config.HideHoardOnIntuition,
                    hideTraps, hideHoardCoffers);
            }

            bool changed = false;

            if (_lastHideTraps != hideTraps)
            {
                if (_lastHideTraps != null)
                {
                    changed = true;
                    _logger.LogInformation(
                        "PalacePal:陷阱標記隱藏狀態 {Old} -> {New}(咒印解除 結構={SafetyStruct}/聊天={SafetyChat},"
                        + "全景 結構={SightStruct}/聊天={SightChat},樓層 {Floor}),重建圖層。",
                        _lastHideTraps, hideTraps,
                        _territoryState.SafetyActiveFromMemory,
                        _territoryState.PomanderOfSight == PomanderState.PomanderOfSafetyUsed,
                        _territoryState.SightActiveFromMemory,
                        _territoryState.PomanderOfSight == PomanderState.Active,
                        _pomanderSensor.CurrentFloor);
                }

                _lastHideTraps = hideTraps;
            }

            if (_lastHideHoardCoffers != hideHoardCoffers)
            {
                if (_lastHideHoardCoffers != null)
                {
                    changed = true;
                    _logger.LogInformation(
                        "PalacePal:受詛咒藏寶箱標記隱藏狀態 {Old} -> {New}(感知寶藏 結構={IntuitionStruct}/"
                        + "聊天={IntuitionChat},樓層 {Floor}),重建圖層。",
                        _lastHideHoardCoffers, hideHoardCoffers,
                        _territoryState.IntuitionActiveFromMemory,
                        _territoryState.PomanderOfIntuition,
                        _pomanderSensor.CurrentFloor);
                }

                _lastHideHoardCoffers = hideHoardCoffers;
            }

            if (!changed)
                return false;

            if (_pomanderRedrawCount >= MaxPomanderRedrawsPerTerritory)
            {
                if (!_pomanderRedrawCapLogged)
                {
                    _pomanderRedrawCapLogged = true;
                    _logger.LogInformation(
                        "PalacePal:本區的魔陶器隱藏狀態已翻轉超過 {Count} 次,超出合理範圍,"
                        + "不再因翻轉而重建圖層(退回既有的就地改色),換區後重新計數。",
                        MaxPomanderRedrawsPerTerritory);
                }

                return false;
            }

            _pomanderRedrawCount++;
            return true;
        }

        private void UpdateTrapLabels()
        {
            bool show = P.Config.TrapKindLabels &&
                        (_territoryState.PomanderOfSight == PomanderState.Active || _territoryState.SightActiveFromMemory);
            if (_trapLabels.Next(show, _renderAdapter.TrapLabelsNeedRefresh))
                _renderAdapter.UpdateTrapLabels(show);
        }

        private void ResetSteppedTraps(bool floorChange)
        {
            byte floor = _steppedTraps.Floor;
            int cleared = floorChange ? _steppedTraps.OnFloor(_pomanderSensor.CurrentFloor) : _steppedTraps.Clear();
            if (cleared == 0)
                return;

            _steppedLogPending = false;
            if (P.Config.HideSteppedTraps)
            {
                _logger.LogInformation(
                    "PalacePal:{Reason}(離開第 {Floor} 層),恢復顯示因踩到陷阱而在該層隱藏的陷阱標記({Count} 處觸發點)。",
                    floorChange ? "換層" : "換區", floor, cleared);
            }
        }

        private void NoteSteppedTrap(System.Numerics.Vector3 position)
        {
            if (_steppedTraps.Add(position, _pomanderSensor.CurrentFloor) && P.Config.HideSteppedTraps)
                _steppedLogPending = true;
        }

        /// <returns>true＝本層觸發點數或開關變了，要重建一次圖層（每次觸發、換層、切換開關各一次，不是每幀）。</returns>
        private bool SyncSteppedTraps()
        {
            int key = P.Config.HideSteppedTraps ? _steppedTraps.Count : -1;
            if (key == _lastSteppedKey)
                return false;

            _lastSteppedKey = key;
            return true;
        }

        private bool IsSteppedTrap(PersistentLocation location)
            => location.Type == MemoryLocation.EType.Trap
               && _steppedTraps.Count != 0
               && P.Config.HideSteppedTraps
               && _steppedTraps.Covers(location.Position);

        private void HandlePersistentLocations(ETerritoryType territoryType,
            IReadOnlyList<VisibleLocation> visiblePersistentMarkers,
            bool recreateLayout)
        {
            bool recreatePersistentLocations = _floorService.MergePersistentLocations(
                territoryType,
                visiblePersistentMarkers,
                recreateLayout,
                out _);
            recreatePersistentLocations |= CheckLocationsForPomanders(visiblePersistentMarkers);

            // 刻意不上傳、也不回報「看過」：Palantir 只拿來下載。
            if (recreatePersistentLocations)
                RecreatePersistentLayout(visiblePersistentMarkers);
        }

        private IGameObject? FindObjectByAddress(nint address)
            => _objectTable.FirstOrDefault(x => x.Address == address);

        private bool CheckLocationsForPomanders(IReadOnlyList<VisibleLocation> visibleLocations)
        {
            MemoryTerritory? memoryTerritory = _floorService.GetTerritoryIfReady(_territoryState.LastTerritory);
            if (memoryTerritory != null &&
                (_configuration.DeepDungeons.Traps.OnlyVisibleAfterPomander ||
                 _configuration.DeepDungeons.HoardCoffers.OnlyVisibleAfterPomander))
            {
                try
                {
                    float faint = UnconfirmedAlpha;
                    foreach (var location in memoryTerritory.Locations.Snapshot())
                    {
                        if (location.CoveredByConfirmed || location.HiddenByThreshold)
                            continue;
                        if (!RecolorInPlace(location, visibleLocations, faint))
                            return true;
                    }

                    foreach (var location in _confirmedRenderLocations)
                    {
                        if (!RecolorInPlace(location, visibleLocations, 1f))
                            return true;
                    }
                }
                catch (Exception e)
                {
                    _debugState.SetFromException(e);
                    return true;
                }
            }

            return false;
        }

        /// <returns>false ＝元素不在了，要整層重建。</returns>
        private bool RecolorInPlace(PersistentLocation location, IReadOnlyList<VisibleLocation> visibleLocations, float alpha)
        {
            if (location.RenderElement == null || !location.RenderElement.IsValid)
                return false;

            uint baseColor = DetermineColor(location, visibleLocations);
            uint desiredColor = ConfirmationRules.ScaleAlpha(baseColor, alpha);
            if (location.RenderElement.Color != desiredColor)
            {
                location.RenderElement.Color = desiredColor;
                if (location.RenderElement2 != null)
                    location.RenderElement2.Color = FillColor(baseColor, alpha);
            }

            return true;
        }

        private static uint FillColor(uint baseColor, float alpha)
            => baseColor == RenderData.ColorInvisible
                ? RenderData.ColorInvisible
                : ConfirmationRules.ScaleAlpha((baseColor.ToVector4() with { W = 50f / 255f }).ToUint(), alpha);

        /// <summary>
        /// 淡層＝PalacePal 資料庫裡我方沒確認過的點；實色層＝我方確認資料庫的點。
        /// 兩層同一個排程一起加、實色層先加：Splatoon 由後往前處理動態元素，後加的淡層先畫、壓在下面。
        /// </summary>
        private void RecreatePersistentLayout(IReadOnlyList<VisibleLocation> visibleMarkers)
        {
            _renderAdapter.ResetLayer(ELayer.TrapHoard);
            _renderAdapter.ResetLayer(ELayer.TrapHoardConfirmed);

            MemoryTerritory? memoryTerritory = _floorService.GetTerritoryIfReady(_territoryState.LastTerritory);
            if (memoryTerritory == null)
            {
                _confirmedRenderLocations.Clear();
                return;
            }

            _previousConfirmed.Clear();
            foreach (var old in _confirmedRenderLocations)
                _previousConfirmed.TryAdd((old.Type, old.Position), old);
            _confirmedRenderLocations.Clear();

            ushort territory = (ushort)_territoryState.LastTerritory;
            float faint = UnconfirmedAlpha;
            List<SplatoonElement> faintElements = new();
            int hiddenFaint = 0, hiddenConfirmed = 0;
            foreach (var location in memoryTerritory.Locations)
            {
                if (location.Type is not (MemoryLocation.EType.Trap or MemoryLocation.EType.Hoard))
                    continue;

                location.CoveredByConfirmed = _confirmedStore.IsConfirmed(territory, ToConfirmedKind(location.Type), location.Position);
                location.HiddenByThreshold = location.IsHiddenByCommunityThreshold();
                if (location.CoveredByConfirmed || location.HiddenByThreshold)
                {
                    location.RenderElement = null;
                    location.RenderElement2 = null;
                    continue;
                }

                if (_steppedLogPending && IsSteppedTrap(location))
                    hiddenFaint++;
                AddPersistentRenderElement(location, faintElements, DetermineColor(location, visibleMarkers), ConfigFor(location.Type), faint);
            }

            List<SplatoonElement> confirmedElements = new();
            foreach (var confirmed in _confirmedStore.GetLocations(territory))
            {
                var type = confirmed.Type == EConfirmedKind.Trap ? MemoryLocation.EType.Trap : MemoryLocation.EType.Hoard;
                var position = confirmed.Position;
                if (!_previousConfirmed.Remove((type, position), out var location))
                {
                    location = new PersistentLocation
                    {
                        Type = type,
                        Position = position,
                        Seen = true,
                    };
                }

                _confirmedRenderLocations.Add(location);
                if (_steppedLogPending && IsSteppedTrap(location))
                    hiddenConfirmed++;
                AddPersistentRenderElement(location, confirmedElements, DetermineColor(location, visibleMarkers), ConfigFor(location.Type), 1f);
            }

            _previousConfirmed.Clear();
            if (_steppedLogPending)
            {
                _steppedLogPending = false;
                _logger.LogInformation(
                    "PalacePal:踩到陷阱,第 {Floor} 層不再畫觸發點 {Radius} 米內的陷阱標記:淡色 {Faint} 個、實色 {Confirmed} 個(本層已觸發 {Count} 處,換層後恢復)。",
                    _steppedTraps.Floor, SteppedTrapState.Radius, hiddenFaint, hiddenConfirmed, _steppedTraps.Count);
            }

            _renderAdapter.SetLayers((ELayer.TrapHoardConfirmed, confirmedElements), (ELayer.TrapHoard, faintElements));
        }

        private MarkerConfiguration ConfigFor(MemoryLocation.EType type)
            => type == MemoryLocation.EType.Trap ? _configuration.DeepDungeons.Traps : _configuration.DeepDungeons.HoardCoffers;

        private static EConfirmedKind ToConfirmedKind(MemoryLocation.EType type)
            => type == MemoryLocation.EType.Trap ? EConfirmedKind.Trap : EConfirmedKind.Hoard;

        private static float UnconfirmedAlpha => Math.Clamp(P.Config.UnconfirmedMarkerAlpha, 0.05f, 1f);

        /// <summary>把這一幀的觀測交給確認規則；有新點加入本區時回 true（要重建圖層）。</summary>
        private bool RecordConfirmations()
        {
            if (_confirmCandidates.Count == 0)
                return false;

            bool sight = _territoryState.PomanderOfSight == PomanderState.Active || _territoryState.SightActiveFromMemory;
            bool intuition = _territoryState.PomanderOfIntuition == PomanderState.Active || _territoryState.IntuitionActiveFromMemory;
            bool hoardFound = _territoryState.PomanderOfIntuition == PomanderState.FoundOnCurrentFloor;
            ushort territory = (ushort)_territoryState.LastTerritory;

            bool added = false;
            foreach (var (kind, position, source) in _confirmCandidates)
            {
                var method = ConfirmationRules.Classify(kind, source, sight, intuition, hoardFound);
                if (_confirmedStore.Observe(territory, _pomanderSensor.CurrentFloor, kind, position, method) == EObserveResult.Added)
                    added = true;
            }

            return added;
        }

        private void RecreateEphemeralLayout()
        {
            _renderAdapter.ResetLayer(ELayer.RegularCoffers);

            List<SplatoonElement> elements = new();
            foreach (var location in _floorService.EphemeralLocations)
            {
                if (location.Type == MemoryLocation.EType.SilverCoffer &&
                    _configuration.DeepDungeons.SilverCoffers.Show)
                {
                    CreateRenderElement(location, elements, DetermineColor(location),
                        _configuration.DeepDungeons.SilverCoffers);
                }
                else if (location.Type == MemoryLocation.EType.GoldCoffer &&
                      _configuration.DeepDungeons.GoldCoffers.Show)
                {
                    CreateRenderElement(location, elements, DetermineColor(location),
                        _configuration.DeepDungeons.GoldCoffers);
                }
            }

            if (elements.Count == 0)
                return;

            _renderAdapter.SetLayer(ELayer.RegularCoffers, elements);
        }

        /// <summary>
        /// 算出一個持久點位該用什麼顏色畫。<see cref="RenderData.ColorInvisible"/> ＝ 不顯示。
        /// </summary>
        /// <remarks>
        /// 🔴 使用者裁決,**不要改回去**:隱藏旗標成立時,**連已經顯形的實體也一起隱藏** ——
        /// 用藥之後遊戲自己會把真陷阱顯示出來,PalacePal 再畫一圈是冗餘的。
        /// <paramref name="visibleLocations"/> 刻意保留在簽章裡:它是「這一幀實際看得到哪些實體」的唯一入口,
        /// 將來要做「只隱藏資料庫點位、保留已顯形實體」的模式時會需要它。
        /// ⚠️ 這個方法只決定「畫不畫」。點位的記錄走 MergePersistentLocations,與顏色無關 —— 隱藏期間照樣會學到新的陷阱點位。
        /// ⚠️ 就地改色(CheckLocationsForPomanders)與整層重建(RecreatePersistentLayout)呼叫的是同一個方法,兩條路徑的隱藏語意一致。
        /// </remarks>
        private uint DetermineColor(PersistentLocation location, IReadOnlyList<VisibleLocation> visibleLocations)
        {
            switch (location.Type)
            {
                case MemoryLocation.EType.Trap
                    when !_territoryState.ShouldHideTraps(_configuration) && !IsSteppedTrap(location):
                    return P.Config.TrapColor.ToUint();
                case MemoryLocation.EType.Hoard
                    when !_territoryState.ShouldHideHoardCoffers(_configuration):
                    return _configuration.DeepDungeons.HoardCoffers.Color;
                default:
                    return RenderData.ColorInvisible;
            }
        }

        private uint DetermineColor(EphemeralLocation location)
        {
            return location.Type switch
            {
                MemoryLocation.EType.SilverCoffer => _configuration.DeepDungeons.SilverCoffers.Color,
                MemoryLocation.EType.GoldCoffer => _configuration.DeepDungeons.GoldCoffers.Color,
                _ => RenderData.ColorInvisible
            };
        }

        private void CreateRenderElement(MemoryLocation location, List<SplatoonElement> elements, uint color, MarkerConfiguration config, float alpha = 1f)
        {
            var element = _renderAdapter.CreateElement(location.Type, location.Position, color, config.Fill);
            if(location.Type == MemoryLocation.EType.GoldCoffer)
            {
                /*{"Name":"Gold Treasure Coffer","type":1,"Enabled":false,"color":3355495679,"overlayBGColor":0,"overlayTextColor":4278242559,"overlayVOffset":0.6,"overlayFScale":1.3,"overlayText":" Gold Treasure Coffer","refActorPlaceholder":["<t>"],"refActorComparisonType":5,"includeOwnHitbox":true}

                {"Name":"Gold Treasure Coffer Fill","type":1,"Enabled":false,"color":838913279,"overlayVOffset":0.68,"overlayFScale":1.24,"refActorPlaceholder":["<t>"],"FillStep":0.429,"refActorComparisonType":5,"includeOwnHitbox":true,"Filled":true}
                */
                element.Color = color;
                if (P.Config.GoldText)
                {
                    element.Delegate.overlayBGColor = 0;
                    element.Delegate.overlayVOffset = 0.6f;
                    element.Delegate.overlayFScale = P.Config.OverlayFScale;
                    element.Delegate.overlayText = " Gold Treasure Coffer";
                    element.Delegate.overlayTextColor = color;
                }
                element.Delegate.radius = 1f;
                element.Filled = false;

                var element2 = _renderAdapter.CreateElement(location.Type, location.Position, color);
                element2.Color = (color.ToVector4() with { W = 50f / 255f }).ToUint();
                element2.Delegate.radius = 1f;
                element2.Filled = true;
                location.RenderElement2 = element2;
                if (config.Show && config.Fill)
                {
                    elements.Add(element2);
                }
            }
            else if(location.Type == MemoryLocation.EType.SilverCoffer)
            {
                /*
                 * {"Name":"Silver Treasure Coffer","type":1,"Enabled":false,"color":3372220415,"overlayBGColor":0,"overlayTextColor":4294967295,"overlayVOffset":0.6,"overlayFScale":1.3,"overlayText":" Silver Treasure Coffer","refActorType":1,"includeOwnHitbox":true}

                {"Name":"Silver Treasure Coffer Fill","type":1,"Enabled":false,"color":855638015,"overlayVOffset":0.68,"overlayFScale":1.24,"FillStep":0.429,"refActorType":1,"includeOwnHitbox":true,"Filled":true}

                 * */
                element.Color = color;
                if (P.Config.SilverText)
                {
                    element.Delegate.overlayBGColor = 0;
                    element.Delegate.overlayVOffset = 0.6f;
                    element.Delegate.overlayFScale = P.Config.OverlayFScale;
                    element.Delegate.overlayText = " Silver Treasure Coffer";
                    element.Delegate.overlayTextColor = color;
                }
                element.Delegate.radius = 1f;
                element.Filled = false;

                var element2 = _renderAdapter.CreateElement(location.Type, location.Position, color);
                element2.Color = (color.ToVector4() with { W = 50f / 255f }).ToUint();
                element2.Delegate.radius = 1f;
                element2.Filled = true;
                location.RenderElement2 = element2;
                if (config.Show && config.Fill)
                {
                    elements.Add(element2);
                }
            }
            else if(location.Type == MemoryLocation.EType.Trap)
            {
                //{"Name":"Mimic Trap Coffer","type":1,"Enabled":false,"color":4278190335,"overlayBGColor":0,"overlayTextColor":4278190335,"overlayVOffset":0.6,"overlayFScale":1.3,"overlayText":" Mimic Trap Coffer","refActorPlaceholder":["<t>"],"FillStep":0.029,"refActorComparisonType":5,"includeOwnHitbox":true,"AdditionalRotation":0.43633232}

                //{ "Name":"Mimic Trap Coffer Fill","type":1,"Enabled":false,"color":838861055,"overlayBGColor":0,"overlayTextColor":4278190335,"overlayVOffset":0.6,"overlayFScale":1.3,"refActorPlaceholder":["<t>"],"FillStep":0.029,"refActorComparisonType":5,"includeOwnHitbox":true,"AdditionalRotation":0.43633232,"Filled":true}

                // 尊重呼叫端算出來的顏色,不可以無條件寫回 P.Config.TrapColor —— 否則每次重建圖層時
                // 被隱藏的陷阱都會重新變成全彩。要顯示時 color 本來就等於 TrapColor。
                element.Color = TrapStrokeColor(color, alpha);
                element.Filled = false;

                var element2 = _renderAdapter.CreateElement(location.Type, location.Position, color);
                element2.Color = TrapFillColor(color, alpha);
                element2.Filled = true;
                location.RenderElement2 = element2;
                if (config.Show && config.Fill)
                {
                    elements.Add(element2);
                }
            }
            if (location.Type == MemoryLocation.EType.Hoard)
            {
                element.Color = ConfirmationRules.ScaleAlpha(color, alpha);
            }

            location.RenderElement = element;

            if (config.Show)
                elements.Add(element);
        }

        private static uint TrapStrokeColor(uint color, float alpha)
            => color == RenderData.ColorInvisible
                ? RenderData.ColorInvisible
                : ConfirmationRules.ScaleAlpha(P.Config.TrapColor.ToUint(), alpha);

        private static uint TrapFillColor(uint color, float alpha)
            => color == RenderData.ColorInvisible
                ? RenderData.ColorInvisible
                : FillColor(P.Config.TrapColor.ToUint(), alpha);

        /// <summary>
        /// 陷阱／寶藏點位：上一次重建留下的元素還有效就沿用，只把顏色與 Filled 設成 <see cref="CreateRenderElement"/> 會給的值；
        /// 其餘欄位只由種類與座標決定，沿用的與新建的相同。加入清單的順序也與它相同（填色在前、描邊在後）。
        /// </summary>
        private void AddPersistentRenderElement(PersistentLocation location, List<SplatoonElement> elements, uint color, MarkerConfiguration config, float alpha)
        {
            var element = location.RenderElement;
            var element2 = location.RenderElement2;
            if (element == null || !element.IsValid)
            {
                CreateRenderElement(location, elements, color, config, alpha);
                return;
            }

            if (location.Type == MemoryLocation.EType.Trap)
            {
                if (element2 == null || !element2.IsValid)
                {
                    CreateRenderElement(location, elements, color, config, alpha);
                    return;
                }

                SetIfChanged(element, TrapStrokeColor(color, alpha), false);
                SetIfChanged(element2, TrapFillColor(color, alpha), true);
                if (config.Show && config.Fill)
                    elements.Add(element2);
            }
            else if (location.Type == MemoryLocation.EType.Hoard)
            {
                SetIfChanged(element, ConfirmationRules.ScaleAlpha(color, alpha), config.Fill);
            }
            else
            {
                CreateRenderElement(location, elements, color, config, alpha);
                return;
            }

            if (config.Show)
                elements.Add(element);
        }

        private static void SetIfChanged(SplatoonElement element, uint color, bool filled)
        {
            if (element.Color != color)
                element.Color = color;
            if (element.Filled != filled)
                element.Filled = filled;
        }

        #endregion

        #region Up-/Download

        private async Task DownloadLocationsForTerritory(uint territoryId)
        {
            try
            {
                _logger.LogInformation("Downloading territory {Territory} from Palantir (download only)", (ETerritoryType)territoryId);
                var (success, downloadedMarkers) = await _remoteApi.DownloadPalantirMarkers(territoryId);
                LateEventQueue.Enqueue(new QueuedSyncResponse
                {
                    Type = SyncType.Download,
                    TerritoryType = territoryId,
                    Success = success,
                    Locations = downloadedMarkers
                });
            }
            catch (Exception e)
            {
                _debugState.SetFromException(e);
            }
        }

        #endregion

        private (IReadOnlyList<VisibleLocation>, IReadOnlyList<VisibleLocation>) GetRelevantGameObjects()
        {
            List<VisibleLocation> persistentLocations = _visiblePersistentLocations;
            List<VisibleLocation> ephemeralLocations = _visibleEphemeralLocations;
            persistentLocations.Clear();
            ephemeralLocations.Clear();
            _confirmCandidates.Clear();
            for (int i = 246; i < _objectTable.Length; i++)
            {
                IGameObject? obj = _objectTable[i];
                if (obj == null)
                    continue;

                switch (obj.BaseId)
                {
                    // 已現形陷阱的事件物件 DataId：2007182~2007186 死者宮殿、2009504 天之御柱、2013284 正統優雷卡
                    // （與 NecroLens DataIds.TrapIDs、BossmodReborn AutoClear.RevealedTrapOIDs 同值）。
                    // ⚠️ 朝聖者之路（1281~1290）的陷阱 NecroLens 記為 2014939，但台服的 EObj 那一列是整列歸零的佔位列，
                    // 離線無從驗證 —— 刻意先不加，等台服真的開放再驗。
                    case 2007182:
                    case 2007183:
                    case 2007184:
                    case 2007185:
                    case 2007186:
                    case 2009504:
                    case 2013284:
                        persistentLocations.Add(new VisibleLocation(MemoryLocation.EType.Trap, obj.Position,
                            ClientLocation.ESource.SeenLocally));
                        _confirmCandidates.Add((EConfirmedKind.Trap, obj.Position, EObservationSource.ObjectTable));
                        break;

                    case 2007542:
                    case 2007543:
                        persistentLocations.Add(new VisibleLocation(MemoryLocation.EType.Hoard, obj.Position,
                            ClientLocation.ESource.SeenLocally));
                        _confirmCandidates.Add((EConfirmedKind.Hoard, obj.Position, EObservationSource.ObjectTable));
                        break;

                    case 2007357:
                        ephemeralLocations.Add(new VisibleLocation(MemoryLocation.EType.SilverCoffer, obj.Position));
                        break;

                    case 2007358:
                        ephemeralLocations.Add(new VisibleLocation(MemoryLocation.EType.GoldCoffer, obj.Position));
                        break;
                }
            }

            while (NextUpdateObjects.TryDequeue(out nint address))
            {
                var obj = FindObjectByAddress(address);
                if (obj != null && obj.Position.Length() > 0.1)
                {
                    persistentLocations.Add(new VisibleLocation(MemoryLocation.EType.Trap, obj.Position,
                        ClientLocation.ESource.ExplodedLocally));
                    _confirmCandidates.Add((EConfirmedKind.Trap, obj.Position, EObservationSource.TrapVfx));
                    NoteSteppedTrap(obj.Position);
                }
            }

            while (TriggeredTrapPositions.TryDequeue(out var position))
            {
                _logger.LogInformation("PalacePal:偵測到踩到陷阱(動作效果),位置 {X:F1},{Y:F1},{Z:F1}。",
                    position.X, position.Y, position.Z);
                persistentLocations.Add(new VisibleLocation(MemoryLocation.EType.Trap, position,
                    ClientLocation.ESource.ExplodedLocally));
                _confirmCandidates.Add((EConfirmedKind.Trap, position, EObservationSource.ActionEffect));
                NoteSteppedTrap(position);
            }

            return (persistentLocations, ephemeralLocations);
        }

        private void HandleQueued(IQueueOnFrameworkThread queued, ref bool recreateLayout)
        {
            Type handlerType = typeof(IQueueOnFrameworkThread.Handler<>).MakeGenericType(queued.GetType());
            var handler = (IQueueOnFrameworkThread.IHandler)_serviceProvider.GetRequiredService(handlerType);

            handler.RunIfCompatible(queued, ref recreateLayout);
        }
    }
}
