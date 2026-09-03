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
    /// 刻意不加設定開關：三個端點都是純讀取記憶體裡既有的清單，沒有查資料庫、沒有連線、
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
    /// </remarks>
    internal sealed class IpcProvider : IDisposable
    {
        /// <summary>
        /// 合約版本。⚠️ 端點名稱與簽章是對外合約，改動要連同消費端一起改並提高這個數字。
        /// </summary>
        public const int ApiVersion = 1;

        private const string LabelApiVersion = "PalacePal.ApiVersion";
        private const string LabelGetTrapLocations = "PalacePal.GetTrapLocations";
        private const string LabelGetHoardLocations = "PalacePal.GetHoardLocations";

        private readonly ILogger<IpcProvider> _logger;
        private readonly FloorService _floorService;

        private readonly ICallGateProvider<int> _apiVersionProvider;
        private readonly ICallGateProvider<ushort, List<Vector3>> _trapLocationsProvider;
        private readonly ICallGateProvider<ushort, List<Vector3>> _hoardLocationsProvider;

        /// <summary>
        /// 上一次記錄過的查詢結果摘要，用來避免每幀重複寫 log。
        /// 只是防洗版，races 最多造成多印一行。
        /// </summary>
        private string _lastLoggedSummary = string.Empty;

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

            _apiVersionProvider.RegisterFunc(GetApiVersion);
            _trapLocationsProvider.RegisterFunc(GetTrapLocations);
            _hoardLocationsProvider.RegisterFunc(GetHoardLocations);

            // 使用者跑 LogLevel 1，要能回報就得是 Information。
            _logger.LogInformation(
                "已註冊唯讀 IPC 端點 (v{Version}): {ApiVersionLabel}, {TrapLabel}, {HoardLabel}",
                ApiVersion, LabelApiVersion, LabelGetTrapLocations, LabelGetHoardLocations);
        }

        private static int GetApiVersion() => ApiVersion;

        private List<Vector3> GetTrapLocations(ushort territoryType)
            => GetLocations(territoryType, MemoryLocation.EType.Trap);

        private List<Vector3> GetHoardLocations(ushort territoryType)
            => GetLocations(territoryType, MemoryLocation.EType.Hoard);

        private List<Vector3> GetLocations(ushort territoryType, MemoryLocation.EType type)
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
                    if (location.Type == type)
                        positions.Add(location.Position);
                }

                string summary = $"{territoryType}/{type}/{positions.Count}";
                if (summary != _lastLoggedSummary)
                {
                    _lastLoggedSummary = summary;
                    _logger.LogInformation("IPC 查詢 territory {Territory} 的 {Type}：回傳 {Count} 個座標",
                        territoryType, type, positions.Count);
                }

                return positions;
            }
            catch (Exception e)
            {
                // 例外絕不能穿過 CallGate 回到呼叫端的外掛裡。
                _logger.LogWarning(e, "IPC 查詢 territory {Territory} 的 {Type} 失敗，回傳空清單",
                    territoryType, type);
                return new List<Vector3>();
            }
        }

        public void Dispose()
        {
            // 卸載時 CallGate 可能已經被 Dalamud 收走，逐個包防護，不要讓其中一個失敗擋掉其他兩個。
            UnregisterSafely(_apiVersionProvider, LabelApiVersion);
            UnregisterSafely(_trapLocationsProvider, LabelGetTrapLocations);
            UnregisterSafely(_hoardLocationsProvider, LabelGetHoardLocations);
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
