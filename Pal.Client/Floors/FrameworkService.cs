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

        // 上一幀算出來的「本層是否該隱藏陷阱/受詛咒藏寶箱」。
        // null ＝ 剛換區還沒有基準,此時只採納現值、不寫 log 也不強制重建。
        private bool? _lastHideTraps;
        private bool? _lastHideHoardCoffers;
        private int _pomanderRedrawCount;
        private bool _pomanderRedrawCapLogged;

        /// <summary>
        /// 同一個區域內因魔陶器翻轉而重建圖層的次數上限。正常一趟(10 層)頂多數十次;
        /// 超過就代表旗標判讀有問題,退回原本的就地改色 —— 失敗方向是「維持現狀」,
        /// 不會變成每幀重建。
        /// </summary>
        private const int MaxPomanderRedrawsPerTerritory = 100;

        internal Queue<IQueueOnFrameworkThread> EarlyEventQueue { get; } = new();
        internal Queue<IQueueOnFrameworkThread> LateEventQueue { get; } = new();
        internal ConcurrentQueue<nint> NextUpdateObjects { get; } = new();

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
            PomanderSensor pomanderSensor)
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

            _framework.Update += OnUpdate;
            _configurationManager.Saved += OnSaved;
        }

        public void Dispose()
        {
            _framework.Update -= OnUpdate;
            _configurationManager.Saved -= OnSaved;
        }

        private void OnSaved(object? sender, IPalacePalConfiguration? config)
            => EarlyEventQueue.Enqueue(new QueuedConfigUpdate());

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

                    _floorService.ChangeTerritory(_territoryState.LastTerritory);

                    // 換區一律把兩個魔陶器狀態歸零。
                    // 原本這裡是兩行 PluginLog.Debug,印的內容是**常數**(剛指派完 Inactive,
                    // 再把它印出來永遠是「is now set to inactive Inactive」),
                    // 實機 log 累積了 9478 行完全沒有資訊量的輸出。改成一行、帶上真正會變的區域,
                    // 而且「進入深宮時的完整狀態」下面 CheckPomanderVisibilityTransitions 已經
                    // 以 Information 印過一次,這行維持 Debug 就夠。
                    _territoryState.PomanderOfSight = PomanderState.Inactive;
                    _territoryState.PomanderOfIntuition = PomanderState.Inactive;
                    PluginLog.Debug(
                        $"PalacePal: territory -> {(ETerritoryType)_territoryState.LastTerritory}, pomander state reset");
                    recreateLayout = true;
                    _lastHideTraps = null;
                    _lastHideHoardCoffers = null;
                    _pomanderRedrawCount = 0;
                    _pomanderRedrawCapLogged = false;
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

                if (!_floorService.IsReady(_territoryState.LastTerritory))
                    return;

                if (_renderAdapter.RequireRedraw)
                {
                    recreateLayout = true;
                    _renderAdapter.RequireRedraw = false;
                }

                // 魔陶器旗標翻轉時要重算顏色。咒印解除把陷阱清掉不會產生任何「位置變動」,
                // 所以原本沒有任何東西會在用藥當下要求重建 —— 這裡補上那個觸發點。
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

                (IReadOnlyList<PersistentLocation> visiblePersistentMarkers,
                        IReadOnlyList<EphemeralLocation> visibleEphemeralMarkers) =
                    GetRelevantGameObjects();

                HandlePersistentLocations(territoryType, visiblePersistentMarkers, recreateLayout);

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

        private void HandlePersistentLocations(ETerritoryType territoryType,
            IReadOnlyList<PersistentLocation> visiblePersistentMarkers,
            bool recreateLayout)
        {
            bool recreatePersistentLocations = _floorService.MergePersistentLocations(
                territoryType,
                visiblePersistentMarkers,
                recreateLayout,
                out List<PersistentLocation> locationsToSync);
            recreatePersistentLocations |= CheckLocationsForPomanders(visiblePersistentMarkers);
            if (locationsToSync.Count > 0)
            {
                Task.Run(async () =>
                    await SyncSeenMarkersForTerritory(_territoryState.LastTerritory, locationsToSync));
            }

            UploadLocations();

            if (recreatePersistentLocations)
                RecreatePersistentLayout(visiblePersistentMarkers);
        }

        private bool CheckLocationsForPomanders(IReadOnlyList<PersistentLocation> visibleLocations)
        {
            MemoryTerritory? memoryTerritory = _floorService.GetTerritoryIfReady(_territoryState.LastTerritory);
            if (memoryTerritory is { Locations.Count: > 0 } &&
                (_configuration.DeepDungeons.Traps.OnlyVisibleAfterPomander ||
                 _configuration.DeepDungeons.HoardCoffers.OnlyVisibleAfterPomander))
            {
                try
                {
                    foreach (var location in memoryTerritory.Locations)
                    {
                        uint desiredColor = DetermineColor(location, visibleLocations);
                        if (location.RenderElement == null || !location.RenderElement.IsValid)
                            return true;

                        if (location.RenderElement.Color != desiredColor)
                        {
                            location.RenderElement.Color = desiredColor;
                            if (location.RenderElement2 != null)
                            {
                                location.RenderElement2.Color = desiredColor == RenderData.ColorInvisible? RenderData.ColorInvisible : (desiredColor.ToVector4() with { W = 50f / 255f }).ToUint();
                            }
                        }
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

        private void UploadLocations()
        {
            MemoryTerritory? memoryTerritory = _floorService.GetTerritoryIfReady(_territoryState.LastTerritory);
            if (memoryTerritory == null || memoryTerritory.SyncState != ESyncState.Complete)
                return;

            List<PersistentLocation> locationsToUpload = memoryTerritory.Locations
                .Where(loc => loc.NetworkId == null && loc.UploadRequested == false)
                .ToList();
            if (locationsToUpload.Count > 0)
            {
                foreach (var location in locationsToUpload)
                    location.UploadRequested = true;

                Task.Run(async () =>
                    await UploadLocationsForTerritory(_territoryState.LastTerritory, locationsToUpload));
            }
        }

        private void RecreatePersistentLayout(IReadOnlyList<PersistentLocation> visibleMarkers)
        {
            _renderAdapter.ResetLayer(ELayer.TrapHoard);

            MemoryTerritory? memoryTerritory = _floorService.GetTerritoryIfReady(_territoryState.LastTerritory);
            if (memoryTerritory == null)
                return;

            List<SplatoonElement> elements = new();
            foreach (var location in memoryTerritory.Locations)
            {
                if (location.Type == MemoryLocation.EType.Trap)
                {
                    CreateRenderElement(location, elements, DetermineColor(location, visibleMarkers), _configuration.DeepDungeons.Traps);
                }
                else if (location.Type == MemoryLocation.EType.Hoard)
                {
                    CreateRenderElement(location, elements, DetermineColor(location, visibleMarkers),
                        _configuration.DeepDungeons.HoardCoffers);
                }
            }

            if (elements.Count == 0)
                return;

            _renderAdapter.SetLayer(ELayer.TrapHoard, elements);
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
        /// 🔴 2026-08-10 使用者裁決,**不要改回去**。原話:
        /// 「使用全景和咒印解除時 沒有把畫面上的陷阱刷掉」——
        /// 用藥之後遊戲自己會把真陷阱顯示出來,PalacePal 再畫一圈是冗餘的。
        /// 所以隱藏旗標成立時,**連已經顯形的實體也一起隱藏**。
        ///
        /// 原本這兩個 case 各多一個覆寫 <c>|| visibleLocations.Any(x =&gt; x == location)</c>,
        /// 當初的理由是「已經在物件表裡的陷阱是實際存在的實體(全景把它們顯形了),一律照畫;
        /// 被隱藏的只有資料庫裡的歷史/潛在點位」。那個理由在「沒用藥」的情境仍然成立 ——
        /// 但沒用藥時 ShouldHideTraps() 本來就回 false、第一個運算元直接短路命中,
        /// 覆寫根本不會被求值。也就是說「把覆寫收到旗標閘門底下」之後它沒有任何可達情境,
        /// 因此直接移除,而不是留一個永遠不成立的條件在這裡騙下一個讀碼的人。
        /// ⇒ 旗標為 false 時的行為與改動前逐位元相同。
        ///
        /// <paramref name="visibleLocations"/> 刻意保留在簽章裡:它是「這一幀實際看得到哪些實體」
        /// 的唯一入口,將來要做「只隱藏資料庫點位、保留已顯形實體」的第三種模式時會需要它。
        ///
        /// ⚠️ 這個方法只決定「畫不畫」。點位的記錄與上傳走 MergePersistentLocations,
        /// 與顏色完全無關 —— 隱藏期間照樣會學到並上傳新的陷阱點位。
        /// ⚠️ 就地改色(CheckLocationsForPomanders)與整層重建(RecreatePersistentLayout)
        /// 呼叫的是同一個方法,所以兩條路徑的隱藏語意天生一致,不會互相打架。
        /// </remarks>
        private uint DetermineColor(PersistentLocation location, IReadOnlyList<PersistentLocation> visibleLocations)
        {
            switch (location.Type)
            {
                case MemoryLocation.EType.Trap
                    when !_territoryState.ShouldHideTraps(_configuration):
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

        private void CreateRenderElement(MemoryLocation location, List<SplatoonElement> elements, uint color, MarkerConfiguration config)
        {
            var element = _renderAdapter.CreateElement(location.Type, location.Position, color, config.Fill);
            if(location.Type == MemoryLocation.EType.GoldCoffer)
            {
                /*{"Name":"Gold Treasure Coffer","type":1,"Enabled":false,"color":3355495679,"overlayBGColor":0,"overlayTextColor":4278242559,"overlayVOffset":0.6,"overlayFScale":1.3,"overlayText":" Gold Treasure Coffer","refActorPlaceholder":["<t>"],"refActorComparisonType":5,"includeOwnHitbox":true}

                {"Name":"Gold Treasure Coffer Fill","type":1,"Enabled":false,"color":838913279,"overlayVOffset":0.68,"overlayFScale":1.24,"refActorPlaceholder":["<t>"],"FillStep":0.429,"refActorComparisonType":5,"includeOwnHitbox":true,"Filled":true}
                */
                element.Delegate.color = color;
                if (P.Config.GoldText)
                {
                    element.Delegate.overlayBGColor = 0;
                    element.Delegate.overlayVOffset = 0.6f;
                    element.Delegate.overlayFScale = P.Config.OverlayFScale;
                    element.Delegate.overlayText = " Gold Treasure Coffer";
                    element.Delegate.overlayTextColor = color;
                }
                element.Delegate.radius = 1f;
                element.Delegate.Filled = false;

                var element2 = _renderAdapter.CreateElement(location.Type, location.Position, color);
                element2.Delegate.color = (color.ToVector4() with { W = 50f / 255f }).ToUint();
                element2.Delegate.radius = 1f;
                element2.Delegate.Filled = true;
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
                element.Delegate.color = color;
                if (P.Config.SilverText)
                {
                    element.Delegate.overlayBGColor = 0;
                    element.Delegate.overlayVOffset = 0.6f;
                    element.Delegate.overlayFScale = P.Config.OverlayFScale;
                    element.Delegate.overlayText = " Silver Treasure Coffer";
                    element.Delegate.overlayTextColor = color;
                }
                element.Delegate.radius = 1f;
                element.Delegate.Filled = false;

                var element2 = _renderAdapter.CreateElement(location.Type, location.Position, color);
                element2.Delegate.color = (color.ToVector4() with { W = 50f / 255f }).ToUint();
                element2.Delegate.radius = 1f;
                element2.Delegate.Filled = true;
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

                // 這裡原本無條件寫回 P.Config.TrapColor,把呼叫端算出來的「隱藏」色丟掉了 ——
                // 每次重建圖層時被隱藏的陷阱都會重新變成全彩。改成尊重傳進來的顏色:
                // 要顯示時 color 本來就等於 TrapColor,行為不變;要隱藏時才真的隱藏。
                element.Delegate.color = color == RenderData.ColorInvisible
                    ? RenderData.ColorInvisible
                    : P.Config.TrapColor.ToUint();
                element.Delegate.Filled = false;

                var element2 = _renderAdapter.CreateElement(location.Type, location.Position, color);
                element2.Delegate.color = color == RenderData.ColorInvisible
                    ? RenderData.ColorInvisible
                    : (P.Config.TrapColor with { W = 50f/255f }).ToUint();
                element2.Delegate.Filled = true;
                location.RenderElement2 = element2;
                if (config.Show && config.Fill)
                {
                    elements.Add(element2);
                }
            }
            location.RenderElement = element;

            if (config.Show)
                elements.Add(element);
        }

        #endregion

        #region Up-/Download

        private async Task DownloadLocationsForTerritory(uint territoryId)
        {
            try
            {
                _logger.LogInformation("Downloading territory {Territory} from server", (ETerritoryType)territoryId);
                var (success, downloadedMarkers) = await _remoteApi.DownloadRemoteMarkers(territoryId);
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

        private async Task UploadLocationsForTerritory(uint territoryId, List<PersistentLocation> locationsToUpload)
        {
            try
            {
                _logger.LogInformation("Uploading {Count} locations for territory {Territory} to server",
                    locationsToUpload.Count, (ETerritoryType)territoryId);
                var (success, uploadedLocations) = await _remoteApi.UploadLocations(territoryId, locationsToUpload);
                LateEventQueue.Enqueue(new QueuedSyncResponse
                {
                    Type = SyncType.Upload,
                    TerritoryType = territoryId,
                    Success = success,
                    Locations = uploadedLocations
                });
            }
            catch (Exception e)
            {
                _debugState.SetFromException(e);
            }
        }

        private async Task SyncSeenMarkersForTerritory(uint territoryId, IReadOnlyList<PersistentLocation> locationsToUpdate)
        {
            try
            {
                _logger.LogInformation("Syncing {Count} seen locations for territory {Territory} to server",
                    locationsToUpdate.Count, (ETerritoryType)territoryId);
                var success = await _remoteApi.MarkAsSeen(territoryId, locationsToUpdate);
                LateEventQueue.Enqueue(new QueuedSyncResponse
                {
                    Type = SyncType.MarkSeen,
                    TerritoryType = territoryId,
                    Success = success,
                    Locations = locationsToUpdate,
                });
            }
            catch (Exception e)
            {
                _debugState.SetFromException(e);
            }
        }

        #endregion

        private (IReadOnlyList<PersistentLocation>, IReadOnlyList<EphemeralLocation>) GetRelevantGameObjects()
        {
            List<PersistentLocation> persistentLocations = new();
            List<EphemeralLocation> ephemeralLocations = new();
            for (int i = 246; i < _objectTable.Length; i++)
            {
                IGameObject? obj = _objectTable[i];
                if (obj == null)
                    continue;

                switch (obj.DataId)
                {
                    // 已現形陷阱的事件物件 DataId：2007182~2007186 死者宮殿、2009504 天之御柱、
                    // 2013284 正統優雷卡。EO 這一個原本漏掉，所以在正統優雷卡裡永遠記不到陷阱
                    // （使用者的資料庫裡 1099~1108 這十個 territory 恆為零筆）。
                    //
                    // 2013284 的離線查表證據（台服 exd-tc 7.20）：
                    //  ① EObj 第 2013284 列與其他六個陷阱欄位完全同形 —— Data=0、PopType=2、
                    //     Invisibility=0、EventHighAddition=0、EyeCollision=False、Target=True、
                    //     SgbPath 非 0；EObjName 名稱為空（＝不可互動、沒有標題的機關物件）。
                    //     對照組：同區段的 2013285「正統神典石」/2013286「再生裝置」/2013287
                    //     「傳送裝置」都有名字，所以「名稱空白」確實能把陷阱與可互動物件分開。
                    //  ② 位移對齊：天之御柱→正統優雷卡的固定位移 +3780 同時把陷阱(2009504)、
                    //     小祠(2009505)、再生(2009506)、傳送(2009507)四個語意已確認的物件
                    //     一一對上 2013284/85/86/87，四連中不會是巧合。
                    //  ③ 兩份獨立來源同值：NecroLens DataIds.TrapIDs、BossmodReborn
                    //     AutoClear.RevealedTrapOIDs（0x1EB864 ＝ 2013284）。
                    //
                    // ⚠️ 朝聖者之路（1281~1290）的陷阱 NecroLens 記為 2014939，但台服 7.20 的
                    //    EObj 第 2014939 列是整列歸零的佔位列（PopType=0、Target=False、
                    //    SgbPath=0，整個 2014933~2014945 區段都一樣），也就是台服還沒有這份資料，
                    //    離線無從驗證 —— 刻意先不加，等台服真的上了 PT 再驗。
                    case 2007182:
                    case 2007183:
                    case 2007184:
                    case 2007185:
                    case 2007186:
                    case 2009504:
                    case 2013284:
                        persistentLocations.Add(new PersistentLocation
                        {
                            Type = MemoryLocation.EType.Trap,
                            Position = obj.Position,
                            Seen = true,
                            Source = ClientLocation.ESource.SeenLocally,
                        });
                        break;

                    case 2007542:
                    case 2007543:
                        persistentLocations.Add(new PersistentLocation
                        {
                            Type = MemoryLocation.EType.Hoard,
                            Position = obj.Position,
                            Seen = true,
                            Source = ClientLocation.ESource.SeenLocally,
                        });
                        break;

                    case 2007357:
                        ephemeralLocations.Add(new EphemeralLocation
                        {
                            Type = MemoryLocation.EType.SilverCoffer,
                            Position = obj.Position,
                            Seen = true,
                        });
                        break;

                    case 2007358:
                        ephemeralLocations.Add(new EphemeralLocation
                        {
                            Type = MemoryLocation.EType.GoldCoffer,
                            Position = obj.Position,
                            Seen = true
                        });
                        break;
                }
            }

            while (NextUpdateObjects.TryDequeue(out nint address))
            {
                var obj = _objectTable.FirstOrDefault(x => x.Address == address);
                if (obj != null && obj.Position.Length() > 0.1)
                {
                    persistentLocations.Add(new PersistentLocation
                    {
                        Type = MemoryLocation.EType.Trap,
                        Position = obj.Position,
                        Seen = true,
                        Source = ClientLocation.ESource.ExplodedLocally,

                    });
                }
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
