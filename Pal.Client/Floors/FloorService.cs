using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Pal.Client.Configuration;
using Pal.Client.Database;
using Pal.Client.Extensions;
using Pal.Client.Floors.Tasks;
using Pal.Client.Net;
using Pal.Common;

namespace Pal.Client.Floors
{
    internal sealed class FloorService
    {
        private readonly IPalacePalConfiguration _configuration;
        private readonly Cleanup _cleanup;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IReadOnlyDictionary<ETerritoryType, MemoryTerritory> _territories;

        private ConcurrentBag<EphemeralLocation> _ephemeralLocations = new();

        /// <summary>
        /// 上一幀真的看得到的實體。framework 執行緒寫、IPC 執行緒讀。
        /// <para>
        /// 🔴 <c>volatile</c> 是必要的，不是裝飾：寫入端是「先把陣列填好，再把整個快照物件
        /// 指派上來」，volatile 寫提供 release 語意，保證讀到新參考的執行緒一定也看得到
        /// 陣列裡已經填好的內容。讀取端一律「先抄進區域變數再用」，不要重複讀這個欄位。
        /// </para>
        /// <para>
        /// <c>null</c> ＝ <b>不知道</b>（還沒進深宮、樓層還沒載入完、或剛換區清掉了），
        /// 與「知道，而且看得到 0 個」是兩件事 —— IPC 端點把這兩者分開回報。
        /// </para>
        /// </summary>
        private volatile VisibleLocationSnapshot? _visibleLocations;

        public FloorService(IPalacePalConfiguration configuration, Cleanup cleanup,
            IServiceScopeFactory serviceScopeFactory)
        {
            _configuration = configuration;
            _cleanup = cleanup;
            _serviceScopeFactory = serviceScopeFactory;
            _territories = Enum.GetValues<ETerritoryType>().ToDictionary(o => o, o => new MemoryTerritory(o));
        }

        public IReadOnlyCollection<EphemeralLocation> EphemeralLocations => _ephemeralLocations;
        public bool IsImportRunning { get; private set; }

        /// <summary>
        /// 上一幀真的看得到的實體，<c>null</c> ＝ 不知道。語意的完整定義見
        /// <see cref="VisibleLocationSnapshot"/>。
        /// </summary>
        public VisibleLocationSnapshot? VisibleLocations => _visibleLocations;

        /// <summary>
        /// 把這一幀算出來的可見清單拍成快照公開出去。🔴 只准在 framework 執行緒上呼叫。
        /// </summary>
        /// <remarks>
        /// 每幀無條件覆蓋一次（就算四種都是空的也要），<b>快照的時間戳本身就是資料</b> ——
        /// 消費端靠它分辨「現在看得到 0 個」與「這份資料已經舊了、不知道現在如何」。
        /// 只在「有東西」時才更新會讓那兩件事變得分不出來。
        /// </remarks>
        public void UpdateVisibleLocations(
            uint territoryType,
            byte floor,
            IReadOnlyList<PersistentLocation> visiblePersistentLocations,
            IReadOnlyList<EphemeralLocation> visibleEphemeralLocations)
        {
            _visibleLocations = VisibleLocationSnapshot.Capture(
                territoryType, floor, visiblePersistentLocations, visibleEphemeralLocations);
        }

        public void ChangeTerritory(uint territoryType)
        {
            _ephemeralLocations = new ConcurrentBag<EphemeralLocation>();

            // 換區（含離開深宮）一律把可見快照清成「不知道」。留著舊的會讓消費端在載入畫面
            // 期間照舊畫出上一層的實體，而且時間戳還是新的 —— 那比什麼都不畫更糟。
            _visibleLocations = null;

            if (typeof(ETerritoryType).IsEnumDefined(territoryType))
                ChangeTerritory((ETerritoryType)territoryType);
        }

        private void ChangeTerritory(ETerritoryType newTerritory)
        {
            var territory = _territories[newTerritory];
            if (territory.ReadyState == MemoryTerritory.EReadyState.NotLoaded)
            {
                territory.ReadyState = MemoryTerritory.EReadyState.Loading;
                new LoadTerritory(_serviceScopeFactory, _cleanup, territory).Start();
            }
        }

        public MemoryTerritory? GetTerritoryIfReady(uint territoryType)
        {
            if (typeof(ETerritoryType).IsEnumDefined(territoryType))
                return GetTerritoryIfReady((ETerritoryType)territoryType);

            return null;
        }

        public MemoryTerritory? GetTerritoryIfReady(ETerritoryType territoryType)
        {
            var territory = _territories[territoryType];
            if (territory.ReadyState != MemoryTerritory.EReadyState.Ready)
                return null;

            return territory;
        }

        public bool IsReady(uint territoryId) => GetTerritoryIfReady(territoryId) != null;

        public bool MergePersistentLocations(
            ETerritoryType territoryType,
            IReadOnlyList<PersistentLocation> visibleLocations,
            bool recreateLayout,
            out List<PersistentLocation> locationsToSync)
        {
            MemoryTerritory? territory = GetTerritoryIfReady(territoryType);
            locationsToSync = new();
            if (territory == null)
                return false;

            var partialAccountId = _configuration.FindAccount(RemoteApi.RemoteUrl)?.AccountId.ToPartialId();
            var persistentLocations = territory.Locations.ToList();

            List<PersistentLocation> markAsSeen = new();
            List<PersistentLocation> newLocations = new();
            foreach (var visibleLocation in visibleLocations)
            {
                PersistentLocation? existingLocation = persistentLocations.SingleOrDefault(x => x == visibleLocation);
                if (existingLocation != null)
                {
                    if (existingLocation is { Seen: false, LocalId: { } })
                    {
                        existingLocation.Seen = true;
                        markAsSeen.Add(existingLocation);
                    }

                    // This requires you to have seen a trap/hoard marker once per floor to synchronize this for older local states,
                    // markers discovered afterwards are automatically marked seen.
                    if (partialAccountId != null &&
                        existingLocation is { LocalId: { }, NetworkId: { }, RemoteSeenRequested: false } &&
                        !existingLocation.RemoteSeenOn.Contains(partialAccountId))
                    {
                        existingLocation.RemoteSeenRequested = true;
                        locationsToSync.Add(existingLocation);
                    }

                    continue;
                }

                territory.Locations.Add(visibleLocation);
                newLocations.Add(visibleLocation);
                recreateLayout = true;
            }

            if (markAsSeen.Count > 0)
                new MarkLocalSeen(_serviceScopeFactory, territory, markAsSeen).Start();

            if (newLocations.Count > 0)
                new SaveNewLocations(_serviceScopeFactory, territory, newLocations).Start();

            return recreateLayout;
        }

        /// <returns>Whether the locations have changed</returns>
        public bool MergeEphemeralLocations(IReadOnlyList<EphemeralLocation> visibleLocations, bool recreate)
        {
            recreate |= _ephemeralLocations.Any(loc => visibleLocations.All(x => x != loc));
            recreate |= visibleLocations.Any(loc => _ephemeralLocations.All(x => x != loc));

            if (!recreate)
                return false;

            _ephemeralLocations.Clear();
            foreach (var visibleLocation in visibleLocations)
                _ephemeralLocations.Add(visibleLocation);

            return true;
        }

        public void ResetAll()
        {
            IsImportRunning = false;
            foreach (var memoryTerritory in _territories.Values)
            {
                lock (memoryTerritory.LockObj)
                    memoryTerritory.Reset();
            }
        }

        public void SetToImportState()
        {
            IsImportRunning = true;
            foreach (var memoryTerritory in _territories.Values)
            {
                lock (memoryTerritory.LockObj)
                    memoryTerritory.ReadyState = MemoryTerritory.EReadyState.Importing;
            }
        }
    }
}
