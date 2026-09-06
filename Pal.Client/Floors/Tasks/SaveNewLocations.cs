using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pal.Client.Database;
using Pal.Common;

namespace Pal.Client.Floors.Tasks
{
    internal sealed class SaveNewLocations : DbTask<SaveNewLocations>
    {
        private readonly MemoryTerritory _territory;
        private readonly List<PersistentLocation> _newLocations;

        public SaveNewLocations(IServiceScopeFactory serviceScopeFactory, MemoryTerritory territory,
            List<PersistentLocation> newLocations)
            : base(serviceScopeFactory)
        {
            _territory = territory;
            _newLocations = newLocations;
        }

        protected override void Run(PalClientContext dbContext, ILogger<SaveNewLocations> logger)
        {
            Run(_territory, dbContext, logger, _newLocations);
        }

        public static void Run<T>(
            MemoryTerritory territory,
            PalClientContext dbContext,
            ILogger<T> logger,
            List<PersistentLocation> locations)
        {
            // 鎖內不寫 log（ILogger 走 Serilog sink：自己有鎖、還會做檔案 I/O）。
            // 在原本會寫它的那一點把要印的值記下來，出鎖之後才寫；等級、文字、觸發條件不變。
            int? pendingCount = null;

            try
            {
                lock (territory.LockObj)
                {
                    pendingCount = locations.Count;

                    Dictionary<PersistentLocation, ClientLocation> mapping =
                        locations.ToDictionary(x => x, x => ToDatabaseLocation(x, territory.TerritoryType));
                    dbContext.Locations.AddRange(mapping.Values);
                    dbContext.SaveChanges();

                    foreach ((PersistentLocation persistentLocation, ClientLocation clientLocation) in mapping)
                    {
                        persistentLocation.LocalId = clientLocation.LocalId;
                    }
                }
            }
            finally
            {
                if (pendingCount is { } count)
                    logger.LogInformation("Saving {Count} new locations for territory {Territory}", count,
                        territory.TerritoryType);
            }
        }

        private static ClientLocation ToDatabaseLocation(PersistentLocation location, ETerritoryType territoryType)
        {
            return new ClientLocation
            {
                TerritoryType = (ushort)territoryType,
                Type = ToDatabaseType(location.Type),
                X = location.Position.X,
                Y = location.Position.Y,
                Z = location.Position.Z,
                Seen = location.Seen,
                Source = location.Source,
                SinceVersion = typeof(Plugin).Assembly.GetName().Version!.ToString(),
            };
        }

        private static ClientLocation.EType ToDatabaseType(MemoryLocation.EType type)
        {
            return type switch
            {
                MemoryLocation.EType.Trap => ClientLocation.EType.Trap,
                MemoryLocation.EType.Hoard => ClientLocation.EType.Hoard,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
        }
    }
}
