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

        private LocationBag<EphemeralLocation> _ephemeralLocations = new();

        // 只在 framework 執行緒讀寫。
        private (Guid AccountId, string PartialId)? _partialAccountId;

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
            IReadOnlyList<VisibleLocation> visiblePersistentLocations,
            IReadOnlyList<VisibleLocation> visibleEphemeralLocations)
        {
            _visibleLocations = VisibleLocationSnapshot.Capture(
                territoryType, floor, visiblePersistentLocations, visibleEphemeralLocations, _visibleLocations);
        }

        public void ChangeTerritory(uint territoryType)
        {
            _ephemeralLocations = new LocationBag<EphemeralLocation>();

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
            // _territories 的鍵就是列舉的全部成員，查不到＝不是深層迷宮。
            if (_territories.TryGetValue((ETerritoryType)territoryType, out MemoryTerritory? territory) &&
                territory.ReadyState == MemoryTerritory.EReadyState.Ready)
                return territory;

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
            IReadOnlyList<VisibleLocation> visibleLocations,
            bool recreateLayout,
            out List<PersistentLocation>? locationsToSync)
        {
            MemoryTerritory? territory = GetTerritoryIfReady(territoryType);
            locationsToSync = null;
            if (territory == null)
                return false;

            // 這一幀開始時的內容；迴圈裡新加進去的點位不在裡面（同一幀不會拿來互相比對）。
            ReadOnlySpan<PersistentLocation> persistentLocations = territory.Locations.Snapshot();

            // 帳號只有在「對上的已知點位有網路 id、還沒要求過同步」時才用得到，那時才去讀（一幀最多一次）。
            string? partialAccountId = null;
            bool partialAccountIdResolved = false;

            List<PersistentLocation>? markAsSeen = null;
            List<PersistentLocation>? newLocations = null;
            for (int i = 0; i < visibleLocations.Count; i++)
            {
                VisibleLocation visibleLocation = visibleLocations[i];
                PersistentLocation? existingLocation = FindSingle(persistentLocations, visibleLocation);
                if (existingLocation != null)
                {
                    if (existingLocation is { Seen: false, LocalId: { } })
                    {
                        existingLocation.Seen = true;
                        (markAsSeen ??= new()).Add(existingLocation);
                    }

                    // This requires you to have seen a trap/hoard marker once per floor to synchronize this for older local states,
                    // markers discovered afterwards are automatically marked seen.
                    if (existingLocation is { LocalId: { }, NetworkId: { }, RemoteSeenRequested: false })
                    {
                        if (!partialAccountIdResolved)
                        {
                            partialAccountId = ResolvePartialAccountId();
                            partialAccountIdResolved = true;
                        }

                        if (partialAccountId != null && !existingLocation.RemoteSeenOn.Contains(partialAccountId))
                        {
                            existingLocation.RemoteSeenRequested = true;
                            (locationsToSync ??= new()).Add(existingLocation);
                        }
                    }

                    continue;
                }

                PersistentLocation newLocation = new()
                {
                    Type = visibleLocation.Type,
                    Position = visibleLocation.Position,
                    Seen = true,
                    Source = visibleLocation.Source,
                };
                territory.Locations.Add(newLocation);
                (newLocations ??= new()).Add(newLocation);
                recreateLayout = true;
            }

            if (markAsSeen != null)
                new MarkLocalSeen(_serviceScopeFactory, territory, markAsSeen).Start();

            if (newLocations != null)
                new SaveNewLocations(_serviceScopeFactory, territory, newLocations).Start();

            return recreateLayout;
        }

        /// <summary>
        /// 與 <c>SingleOrDefault(x => 同種類同一格)</c> 同義：沒有回 null，超過一個擲例外。
        /// </summary>
        private static PersistentLocation? FindSingle(ReadOnlySpan<PersistentLocation> locations,
            VisibleLocation target)
        {
            PersistentLocation? found = null;
            foreach (PersistentLocation location in locations)
            {
                if (!target.Matches(location))
                    continue;

                if (found != null)
                    return ThrowMoreThanOneMatch(locations.ToArray(), target);

                found = location;
            }

            return found;
        }

        // 交給 LINQ 擲，例外的型別與訊息才會和原本一樣。
        private static PersistentLocation? ThrowMoreThanOneMatch(PersistentLocation[] locations,
            VisibleLocation target)
            => locations.SingleOrDefault(x => target.Matches(x));

        private string? ResolvePartialAccountId()
        {
            Guid? accountId = _configuration.FindAccount(RemoteApi.RemoteUrl)?.AccountId;
            if (accountId == null)
                return null;

            if (_partialAccountId is not { } cached || cached.AccountId != accountId.Value)
                _partialAccountId = cached = (accountId.Value, accountId.Value.ToPartialId());

            return cached.PartialId;
        }

        /// <returns>Whether the locations have changed</returns>
        public bool MergeEphemeralLocations(IReadOnlyList<VisibleLocation> visibleLocations, bool recreate)
        {
            if (!recreate && !Differs(_ephemeralLocations.Snapshot(), visibleLocations))
                return false;

            _ephemeralLocations.Clear();
            for (int i = 0; i < visibleLocations.Count; i++)
            {
                _ephemeralLocations.Add(new EphemeralLocation
                {
                    Type = visibleLocations[i].Type,
                    Position = visibleLocations[i].Position,
                    Seen = true,
                });
            }

            return true;
        }

        /// <summary>
        /// 兩邊是不是「互相都找得到對應」：有任何一邊的點位在另一邊沒有相符的就算不同。
        /// 不看順序，也不看重複幾次。
        /// </summary>
        private static bool Differs(ReadOnlySpan<EphemeralLocation> current,
            IReadOnlyList<VisibleLocation> visibleLocations)
        {
            foreach (EphemeralLocation loc in current)
            {
                bool found = false;
                for (int i = 0; i < visibleLocations.Count && !found; i++)
                    found = visibleLocations[i].Matches(loc);

                if (!found)
                    return true;
            }

            for (int i = 0; i < visibleLocations.Count; i++)
            {
                VisibleLocation visible = visibleLocations[i];
                bool found = false;
                foreach (EphemeralLocation loc in current)
                {
                    if (visible.Matches(loc))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return true;
            }

            return false;
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
