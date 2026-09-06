using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pal.Client.Database;

namespace Pal.Client.Floors.Tasks
{
    internal sealed class LoadTerritory : DbTask<LoadTerritory>
    {
        private readonly Cleanup _cleanup;
        private readonly MemoryTerritory _territory;

        public LoadTerritory(IServiceScopeFactory serviceScopeFactory,
            Cleanup cleanup,
            MemoryTerritory territory)
            : base(serviceScopeFactory)
        {
            _cleanup = cleanup;
            _territory = territory;
        }

        protected override void Run(PalClientContext dbContext, ILogger<LoadTerritory> logger)
        {
            // 鎖內不寫 log：ILogger 走 Serilog sink，它自己有鎖、還會做檔案 I/O，
            // 在 LockObj 裡寫等於讓其他要動同一層樓的執行緒排在 log 檔後面。
            // 每一則各自用一個變數記在「原本會寫它的那一點」，出鎖之後照原順序寫出去
            // ——等級、文字、觸發條件都不變，中途擲例外時該寫的仍然會寫、不該寫的仍然不寫。
            MemoryTerritory.EReadyState? stateLog = null;
            bool loadingLog = false;
            Cleanup.PendingLog? purgeLog = null;
            int? loadedCount = null;

            try
            {
                lock (_territory.LockObj)
                {
                    if (_territory.ReadyState != MemoryTerritory.EReadyState.Loading)
                    {
                        stateLog = _territory.ReadyState;
                    }
                    else
                    {
                        loadingLog = true;

                        // purge outdated locations
                        _cleanup.Purge(dbContext, _territory.TerritoryType, out var purged);
                        purgeLog = purged;

                        // load good locations
                        List<ClientLocation> locations = dbContext.Locations
                            .Where(o => o.TerritoryType == (ushort)_territory.TerritoryType)
                            .Include(o => o.ImportedBy)
                            .Include(o => o.RemoteEncounters)
                            .AsSplitQuery()
                            .ToList();
                        _territory.Initialize(locations.Select(ToMemoryLocation));

                        loadedCount = locations.Count;
                    }
                }
            }
            finally
            {
                // TerritoryType 是建構時就定案的唯讀屬性，鎖外讀它是安全的。
                if (stateLog is { } state)
                    logger.LogInformation("Territory {Territory} is in state {State}", _territory.TerritoryType,
                        state);

                if (loadingLog)
                    logger.LogInformation("Loading territory {Territory}", _territory.TerritoryType);

                _cleanup.EmitPending(purgeLog);

                if (loadedCount is { } count)
                    logger.LogInformation("Loaded {Count} locations for territory {Territory}", count,
                        _territory.TerritoryType);
            }
        }

        public static PersistentLocation ToMemoryLocation(ClientLocation location)
        {
            return new PersistentLocation
            {
                LocalId = location.LocalId,
                Type = ToMemoryLocationType(location.Type),
                Position = new Vector3(location.X, location.Y, location.Z),
                Seen = location.Seen,
                Source = location.Source,
                RemoteSeenOn = location.RemoteEncounters.Select(o => o.AccountId).ToList(),
            };
        }

        private static MemoryLocation.EType ToMemoryLocationType(ClientLocation.EType type)
        {
            return type switch
            {
                ClientLocation.EType.Trap => MemoryLocation.EType.Trap,
                ClientLocation.EType.Hoard => MemoryLocation.EType.Hoard,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
        }
    }
}
