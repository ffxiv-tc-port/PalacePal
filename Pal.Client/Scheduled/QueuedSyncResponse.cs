using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pal.Client.Configuration;
using Pal.Client.DependencyInjection;
using Pal.Client.Extensions;
using Pal.Client.Floors;
using Pal.Client.Floors.Tasks;
using Pal.Client.Net;
using Pal.Common;

namespace Pal.Client.Scheduled
{
    internal sealed class QueuedSyncResponse : IQueueOnFrameworkThread
    {
        public required SyncType Type { get; init; }
        public required uint TerritoryType { get; init; }
        public required bool Success { get; init; }
        public required IReadOnlyList<PersistentLocation> Locations { get; init; }

        internal sealed class Handler : IQueueOnFrameworkThread.Handler<QueuedSyncResponse>
        {
            private readonly IServiceScopeFactory _serviceScopeFactory;
            private readonly IPalacePalConfiguration _configuration;
            private readonly FloorService _floorService;
            private readonly TerritoryState _territoryState;
            private readonly DebugState _debugState;

            public Handler(
                ILogger<Handler> logger,
                IServiceScopeFactory serviceScopeFactory,
                IPalacePalConfiguration configuration,
                FloorService floorService,
                TerritoryState territoryState,
                DebugState debugState)
                : base(logger)
            {
                _serviceScopeFactory = serviceScopeFactory;
                _configuration = configuration;
                _floorService = floorService;
                _territoryState = territoryState;
                _debugState = debugState;
            }

            protected override void Run(QueuedSyncResponse queued, ref bool recreateLayout)
            {
                recreateLayout = true;

                _logger.LogDebug(
                    "Sync response for territory {Territory} of type {Type}, success = {Success}, response objects = {Count}",
                    (ETerritoryType)queued.TerritoryType, queued.Type, queued.Success, queued.Locations.Count);
                var memoryTerritory = _floorService.GetTerritoryIfReady(queued.TerritoryType);
                if (memoryTerritory == null)
                {
                    _logger.LogWarning("Discarding sync response for territory {Territory} as it isn't ready",
                        (ETerritoryType)queued.TerritoryType);
                    return;
                }

                try
                {
                    var remoteMarkers = queued.Locations;
                    if (_configuration.Mode == EMode.Online && queued.Success && remoteMarkers.Count > 0)
                    {
                        switch (queued.Type)
                        {
                            case SyncType.Download:
                            case SyncType.Upload:
                                List<PersistentLocation> newLocations = new();
                                ReadOnlySpan<PersistentLocation> existingLocations = memoryTerritory.Locations.Snapshot();
                                foreach (var remoteMarker in remoteMarkers)
                                {
                                    // Both uploads and downloads return the network id to be set, but only the downloaded marker is new as in to-be-saved.
                                    PersistentLocation? localLocation =
                                        FindSingle(existingLocations, newLocations, remoteMarker);
                                    if (localLocation != null)
                                    {
                                        localLocation.NetworkId = remoteMarker.NetworkId;
                                        // 同一格可能對到多筆下載點；Palantir 的次數只增不減，取大的。
                                        localLocation.CommunityConfirmations = Math.Max(
                                            localLocation.CommunityConfirmations, remoteMarker.CommunityConfirmations);
                                        continue;
                                    }

                                    if (queued.Type == SyncType.Download)
                                    {
                                        memoryTerritory.Locations.Add(remoteMarker);
                                        newLocations.Add(remoteMarker);
                                    }
                                }

                                if (newLocations.Count > 0)
                                    new SaveNewLocations(_serviceScopeFactory, memoryTerritory, newLocations).Start();

                                break;

                            case SyncType.MarkSeen:
                                var partialAccountId =
                                    _configuration.FindAccount(RemoteApi.RemoteUrl)?.AccountId.ToPartialId();
                                if (partialAccountId == null)
                                    break;

                                List<PersistentLocation> locationsToUpdate = new();
                                foreach (var remoteMarker in remoteMarkers)
                                {
                                    PersistentLocation? localLocation =
                                        memoryTerritory.Locations.SingleOrDefault(x => x == remoteMarker);
                                    if (localLocation != null)
                                    {
                                        localLocation.RemoteSeenOn.Add(partialAccountId);
                                        locationsToUpdate.Add(localLocation);
                                    }
                                }

                                if (locationsToUpdate.Count > 0)
                                {
                                    new MarkRemoteSeen(_serviceScopeFactory, memoryTerritory, locationsToUpdate,
                                        partialAccountId).Start();
                                }

                                break;
                        }
                    }

                    // don't modify state for outdated floors
                    if (_territoryState.LastTerritory != queued.TerritoryType)
                        return;

                    if (queued.Type == SyncType.Download)
                    {
                        if (queued.Success)
                            memoryTerritory.SyncState = ESyncState.Complete;
                        else
                            memoryTerritory.SyncState = ESyncState.Failed;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Sync failed for territory {Territory}", (ETerritoryType)queued.TerritoryType);
                    _debugState.SetFromException(e);
                    if (queued.Type == SyncType.Download)
                        memoryTerritory.SyncState = ESyncState.Failed;
                }
            }

            /// <summary>
            /// 與 <c>Locations.SingleOrDefault(x =&gt; x == remote)</c> 同義：這一輪開始時的點位加上這一輪剛加進去的
            /// （<paramref name="added"/> 就是這一輪加進容器的全部）；超過一個相符時照樣由 LINQ 擲同一種例外。
            /// 不用每筆重新列舉容器：ConcurrentBag 每列舉一次就整包複製一份。
            /// </summary>
            private static PersistentLocation? FindSingle(ReadOnlySpan<PersistentLocation> existing,
                List<PersistentLocation> added, PersistentLocation remote)
            {
                PersistentLocation? found = null;
                int matches = 0;
                foreach (PersistentLocation location in existing)
                {
                    if (location == remote && ++matches == 1)
                        found = location;
                }

                foreach (PersistentLocation location in added)
                {
                    if (location == remote && ++matches == 1)
                        found = location;
                }

                if (matches > 1)
                    return ThrowMoreThanOneMatch(existing.ToArray(), added, remote);

                return found;
            }

            // 另開方法：閉包留在這裡，常走的路徑才不會每次呼叫都配置。
            private static PersistentLocation? ThrowMoreThanOneMatch(PersistentLocation[] existing,
                List<PersistentLocation> added, PersistentLocation remote)
                => existing.Concat(added).SingleOrDefault(x => x == remote);
        }
    }

    public enum ESyncState
    {
        NotAttempted,
        NotNeeded,
        Started,
        Complete,
        Failed,
    }

    public enum SyncType
    {
        Upload,
        Download,
        MarkSeen,
    }
}
