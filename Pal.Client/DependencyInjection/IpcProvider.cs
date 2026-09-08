using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;
using Pal.Client.Floors;

namespace Pal.Client.DependencyInjection
{
    /// <summary>
    /// 唯讀 IPC 供應端：把 Palace Pal 已經在畫面上標示的陷阱／埋藏寶藏座標開放給其他外掛讀取。
    /// </summary>
    /// <remarks>
    /// 刻意不加設定開關：所有端點都是純讀取記憶體裡既有的清單，沒有查資料庫、沒有連線、
    /// 沒有寫入，沒被呼叫時成本為零。
    /// <para>
    /// 🔴 跨 AssemblyLoadContext 只能傳共用執行期型別 —— 這裡一律用 <see cref="int"/>、
    /// <see cref="ushort"/> 與 <see cref="List{T}"/> of <see cref="Vector3"/>，
    /// 絕對不要改成回傳自訂 class/record/tuple（呼叫端會拿到型別載入失敗）。
    /// </para>
    /// <para>
    /// 資料來源是 <see cref="FloorService"/> 的記憶體樓層，也就是
    /// <c>FrameworkService.RecreatePersistentLayout</c> 實際拿去畫的同一份清單
    /// （伺服器下載 ＋ 本機看過，已經合併過）。territory 沒載入／不是深層迷宮時回空清單，
    /// 不擲例外。
    /// </para>
    /// <para>
    /// 🔴 IPC 實作跑在**呼叫端的執行緒**上，不是 framework 執行緒。所以這個類別裡
    /// 只准碰「跨執行緒讀取安全」的東西：<c>ConcurrentBag</c> 的列舉（快照）、
    /// 參考型別欄位的整體指派。不要在這裡呼叫 ECommons 的 <c>EzThrottler</c>、
    /// 不要碰物件表、不要碰任何只能在 framework 執行緒讀的原生狀態。
    /// </para>
    /// </remarks>
    internal sealed class IpcProvider : IDisposable
    {
        /// <summary>
        /// 原始合約版本。⚠️ 端點名稱與簽章是對外合約，改動要連同消費端一起改並提高這個數字。
        /// <para>
        /// 🔴🔴 **這個數字實務上已經不能再動了。** 2026-09-08 實查兩個消費端：
        /// BossmodReborn 的 <c>PalacePalIpc.cs</c> 判的是
        /// <c>ApiVersion.InvokeFunc() == SupportedApiVersion</c>（**逐字相等**，常數為 1），
        /// 把這裡改成 2 會讓 BMR 的深層迷宮整合靜默失效；
        /// TCToolbox 判的是 <c>&gt;=</c>（安全）。
        /// ⇒ 新增能力一律用**新端點名**表達，並用下面的
        /// <see cref="LocationStateApiVersion"/> 當第二條版本線。
        /// </para>
        /// </summary>
        public const int ApiVersion = 1;

        /// <summary>
        /// 「兩態查詢」這一組端點的版本。與 <see cref="ApiVersion"/> 各自獨立遞增。
        /// 對方沒註冊這個端點 ＝ 沒有兩態能力（呼叫時會擲 <c>IpcNotReadyError</c>，
        /// 消費端接住就乾淨落回單態畫法）。
        /// </summary>
        public const int LocationStateApiVersion = 1;

        private const string LabelApiVersion = "PalacePal.ApiVersion";
        private const string LabelGetTrapLocations = "PalacePal.GetTrapLocations";
        private const string LabelGetHoardLocations = "PalacePal.GetHoardLocations";

        private const string LabelLocationStateApiVersion = "PalacePal.LocationStateApiVersion";
        private const string LabelGetConfirmedTrapLocations = "PalacePal.GetConfirmedTrapLocations";
        private const string LabelGetUnconfirmedTrapLocations = "PalacePal.GetUnconfirmedTrapLocations";
        private const string LabelGetConfirmedHoardLocations = "PalacePal.GetConfirmedHoardLocations";
        private const string LabelGetUnconfirmedHoardLocations = "PalacePal.GetUnconfirmedHoardLocations";

        private readonly ILogger<IpcProvider> _logger;
        private readonly FloorService _floorService;

        private readonly ICallGateProvider<int> _apiVersionProvider;
        private readonly ICallGateProvider<ushort, List<Vector3>> _trapLocationsProvider;
        private readonly ICallGateProvider<ushort, List<Vector3>> _hoardLocationsProvider;

        private readonly ICallGateProvider<int> _locationStateApiVersionProvider;
        private readonly ICallGateProvider<ushort, List<Vector3>> _confirmedTrapLocationsProvider;
        private readonly ICallGateProvider<ushort, List<Vector3>> _unconfirmedTrapLocationsProvider;
        private readonly ICallGateProvider<ushort, List<Vector3>> _confirmedHoardLocationsProvider;
        private readonly ICallGateProvider<ushort, List<Vector3>> _unconfirmedHoardLocationsProvider;

        /// <summary>
        /// 每一種查詢各自記住上一次的結果摘要，用來避免每幀重複寫 log。
        /// <para>
        /// ⚠️ 原本這裡是**單一個**欄位，所有查詢共用。消費端只要在同一幀裡先問陷阱再問寶藏
        /// （TCToolbox 與 BossmodReborn 都是這樣用的），摘要就會每次都不同 ——
        /// 於是「防洗版」的欄位反而讓每一次呼叫都寫一行 Information。改成一格一種查詢。
        /// </para>
        /// <para>
        /// 只是防洗版，races 最多造成多印一行；字串參考的整體指派本身是原子的，不需要鎖。
        /// </para>
        /// </summary>
        private readonly string[] _lastLoggedSummaries = new string[6];

        public IpcProvider(
            ILogger<IpcProvider> logger,
            IDalamudPluginInterface pluginInterface,
            FloorService floorService)
        {
            _logger = logger;
            _floorService = floorService;

            _apiVersionProvider = pluginInterface.GetIpcProvider<int>(LabelApiVersion);
            _trapLocationsProvider =
                pluginInterface.GetIpcProvider<ushort, List<Vector3>>(LabelGetTrapLocations);
            _hoardLocationsProvider =
                pluginInterface.GetIpcProvider<ushort, List<Vector3>>(LabelGetHoardLocations);

            _locationStateApiVersionProvider =
                pluginInterface.GetIpcProvider<int>(LabelLocationStateApiVersion);
            _confirmedTrapLocationsProvider =
                pluginInterface.GetIpcProvider<ushort, List<Vector3>>(LabelGetConfirmedTrapLocations);
            _unconfirmedTrapLocationsProvider =
                pluginInterface.GetIpcProvider<ushort, List<Vector3>>(LabelGetUnconfirmedTrapLocations);
            _confirmedHoardLocationsProvider =
                pluginInterface.GetIpcProvider<ushort, List<Vector3>>(LabelGetConfirmedHoardLocations);
            _unconfirmedHoardLocationsProvider =
                pluginInterface.GetIpcProvider<ushort, List<Vector3>>(LabelGetUnconfirmedHoardLocations);

            _apiVersionProvider.RegisterFunc(GetApiVersion);
            _trapLocationsProvider.RegisterFunc(GetTrapLocations);
            _hoardLocationsProvider.RegisterFunc(GetHoardLocations);

            _locationStateApiVersionProvider.RegisterFunc(GetLocationStateApiVersion);
            _confirmedTrapLocationsProvider.RegisterFunc(GetConfirmedTrapLocations);
            _unconfirmedTrapLocationsProvider.RegisterFunc(GetUnconfirmedTrapLocations);
            _confirmedHoardLocationsProvider.RegisterFunc(GetConfirmedHoardLocations);
            _unconfirmedHoardLocationsProvider.RegisterFunc(GetUnconfirmedHoardLocations);

            // 使用者跑 LogLevel 1，要能回報就得是 Information。
            _logger.LogInformation(
                "已註冊唯讀 IPC 端點 (v{Version}): {ApiVersionLabel}, {TrapLabel}, {HoardLabel}",
                ApiVersion, LabelApiVersion, LabelGetTrapLocations, LabelGetHoardLocations);
            _logger.LogInformation(
                "已註冊兩態 IPC 端點 (v{Version}): {VersionLabel}, {ConfirmedTrapLabel}, "
                + "{UnconfirmedTrapLabel}, {ConfirmedHoardLabel}, {UnconfirmedHoardLabel}",
                LocationStateApiVersion, LabelLocationStateApiVersion,
                LabelGetConfirmedTrapLocations, LabelGetUnconfirmedTrapLocations,
                LabelGetConfirmedHoardLocations, LabelGetUnconfirmedHoardLocations);
        }

        private static int GetApiVersion() => ApiVersion;

        private static int GetLocationStateApiVersion() => LocationStateApiVersion;

        private List<Vector3> GetTrapLocations(ushort territoryType)
            => GetLocations(territoryType, MemoryLocation.EType.Trap, null);

        private List<Vector3> GetHoardLocations(ushort territoryType)
            => GetLocations(territoryType, MemoryLocation.EType.Hoard, null);

        /// <summary>
        /// 已確認的陷阱點位：本機玩家**親眼遇到過**該座標的陷阱實體。
        /// </summary>
        private List<Vector3> GetConfirmedTrapLocations(ushort territoryType)
            => GetLocations(territoryType, MemoryLocation.EType.Trap, true);

        /// <summary>
        /// 未確認的陷阱點位：只來自伺服器下載或檔案匯入，本機從來沒有親眼確認過。
        /// </summary>
        private List<Vector3> GetUnconfirmedTrapLocations(ushort territoryType)
            => GetLocations(territoryType, MemoryLocation.EType.Trap, false);

        /// <summary>
        /// 已確認的受詛咒藏寶箱點位：本機玩家**親眼遇到過**該座標的實體。
        /// </summary>
        private List<Vector3> GetConfirmedHoardLocations(ushort territoryType)
            => GetLocations(territoryType, MemoryLocation.EType.Hoard, true);

        /// <summary>
        /// 未確認的受詛咒藏寶箱點位：只來自伺服器下載或檔案匯入，本機從來沒有親眼確認過。
        /// </summary>
        private List<Vector3> GetUnconfirmedHoardLocations(ushort territoryType)
            => GetLocations(territoryType, MemoryLocation.EType.Hoard, false);

        /// <summary>
        /// 從記憶體樓層挑出符合條件的座標。
        /// </summary>
        /// <param name="territoryType">區域 id。不是深層迷宮或還沒載入完都回空清單。</param>
        /// <param name="type">要哪一種點位。</param>
        /// <param name="seen">
        /// <c>null</c> ＝ 不篩（維持舊端點的行為，回傳合併後的全部）；
        /// <c>true</c> ＝ 只回 <see cref="MemoryLocation.Seen"/> 為真的；
        /// <c>false</c> ＝ 只回為假的。
        /// <para>
        /// 🔑 兩態的依據是外掛自己的資料模型 <c>ClientLocation.Seen</c>
        /// （原註解：「Whether we have encountered the trap/coffer at this location in-game」），
        /// 由 <c>LoadTerritory.ToMemoryLocation</c> 從資料庫載進記憶體。
        /// 本機目擊（<c>ESource.SeenLocally</c>）與踩爆（<c>ESource.ExplodedLocally</c>）
        /// 建立的點位一律 <c>Seen = true</c>；下載（<c>Download</c>）與匯入（<c>Import</c>）
        /// 建立的是 <c>false</c>，等玩家實際走到那裡看見實體，
        /// <c>FloorService.MergePersistentLocations</c> 才會把它翻成 <c>true</c>。
        /// </para>
        /// <para>
        /// ⚠️ **兩邊都不代表「這一趟這一層現在真的有」。** 深層迷宮每趟重新產生樓層，
        /// 而一個 territory 涵蓋十層，所以這兩份清單都是「這十層累積下來的候選生成點」。
        /// 差別只在資料可信度：已確認＝我方親眼驗證過這個座標真的會出東西；
        /// 未確認＝只有別人的資料這樣說。消費端的圖例要照這個意思寫。
        /// </para>
        /// </param>
        private List<Vector3> GetLocations(ushort territoryType, MemoryLocation.EType type, bool? seen)
        {
            List<Vector3> positions = new();
            try
            {
                // GetTerritoryIfReady 對「不是深層迷宮的 territory」與「還沒載入完」都回 null。
                MemoryTerritory? territory = _floorService.GetTerritoryIfReady((uint)territoryType);
                if (territory == null)
                    return positions;

                // ConcurrentBag 的列舉是快照，從其他執行緒呼叫也安全。
                foreach (PersistentLocation location in territory.Locations)
                {
                    if (location.Type != type)
                        continue;

                    if (seen != null && location.Seen != seen.Value)
                        continue;

                    positions.Add(location.Position);
                }

                LogSummaryIfChanged(territoryType, type, seen, positions.Count);
                return positions;
            }
            catch (Exception e)
            {
                // 例外絕不能穿過 CallGate 回到呼叫端的外掛裡。
                _logger.LogWarning(e, "IPC 查詢 territory {Territory} 的 {Type}（篩選 {Seen}）失敗，回傳空清單",
                    territoryType, type, DescribeFilter(seen));
                return new List<Vector3>();
            }
        }

        private void LogSummaryIfChanged(ushort territoryType, MemoryLocation.EType type, bool? seen, int count)
        {
            int slot = SummarySlot(type, seen);
            if (slot < 0)
                return;

            string summary = $"{territoryType}/{type}/{DescribeFilter(seen)}/{count}";
            if (summary == _lastLoggedSummaries[slot])
                return;

            _lastLoggedSummaries[slot] = summary;
            _logger.LogInformation("IPC 查詢 territory {Territory} 的 {Type}（{Seen}）：回傳 {Count} 個座標",
                territoryType, type, DescribeFilter(seen), count);
        }

        /// <summary>
        /// 把（點位種類 × 篩選條件）對到 <see cref="_lastLoggedSummaries"/> 的一格。
        /// 認不得的組合回 -1（＝這次不寫 log），不要讓它變成陣列越界。
        /// </summary>
        private static int SummarySlot(MemoryLocation.EType type, bool? seen)
        {
            int typeSlot = type switch
            {
                MemoryLocation.EType.Trap => 0,
                MemoryLocation.EType.Hoard => 1,
                _ => -1,
            };
            if (typeSlot < 0)
                return -1;

            int filterSlot = seen switch
            {
                null => 0,
                true => 1,
                false => 2,
            };

            return filterSlot * 2 + typeSlot;
        }

        private static string DescribeFilter(bool? seen)
            => seen switch
            {
                null => "全部",
                true => "已確認",
                false => "未確認",
            };

        public void Dispose()
        {
            // 卸載時 CallGate 可能已經被 Dalamud 收走，逐個包防護，不要讓其中一個失敗擋掉其他的。
            UnregisterSafely(_apiVersionProvider, LabelApiVersion);
            UnregisterSafely(_trapLocationsProvider, LabelGetTrapLocations);
            UnregisterSafely(_hoardLocationsProvider, LabelGetHoardLocations);

            UnregisterSafely(_locationStateApiVersionProvider, LabelLocationStateApiVersion);
            UnregisterSafely(_confirmedTrapLocationsProvider, LabelGetConfirmedTrapLocations);
            UnregisterSafely(_unconfirmedTrapLocationsProvider, LabelGetUnconfirmedTrapLocations);
            UnregisterSafely(_confirmedHoardLocationsProvider, LabelGetConfirmedHoardLocations);
            UnregisterSafely(_unconfirmedHoardLocationsProvider, LabelGetUnconfirmedHoardLocations);
        }

        private void UnregisterSafely(ICallGateProvider? provider, string label)
        {
            try
            {
                provider?.UnregisterFunc();
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "註銷 IPC 端點 {Label} 失敗", label);
            }
        }
    }
}
