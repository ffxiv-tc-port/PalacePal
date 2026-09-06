using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pal.Client.Database;

namespace Pal.Client.Floors.Tasks
{
    internal sealed class MarkLocalSeen : DbTask<MarkLocalSeen>
    {
        private readonly MemoryTerritory _territory;
        private readonly IReadOnlyList<PersistentLocation> _locations;

        public MarkLocalSeen(IServiceScopeFactory serviceScopeFactory, MemoryTerritory territory,
            IReadOnlyList<PersistentLocation> locations)
            : base(serviceScopeFactory)
        {
            _territory = territory;
            _locations = locations;
        }

        protected override void Run(PalClientContext dbContext, ILogger<MarkLocalSeen> logger)
        {
            // 鎖內不寫 log（ILogger 走 Serilog sink：自己有鎖、還會做檔案 I/O）。
            // 在原本會寫它的那一點把要印的值記下來，出鎖之後才寫；等級、文字、觸發條件不變。
            int? pendingCount = null;

            try
            {
                lock (_territory.LockObj)
                {
                    pendingCount = _locations.Count;
                    List<int> localIds = _locations.Select(l => l.LocalId).Where(x => x != null).Cast<int>().ToList();
                    dbContext.Locations
                        .Where(loc => localIds.Contains(loc.LocalId))
                        .ExecuteUpdate(loc => loc.SetProperty(l => l.Seen, true));
                    dbContext.SaveChanges();
                }
            }
            finally
            {
                if (pendingCount is { } count)
                    logger.LogInformation("Marking {Count} locations as seen locally in territory {Territory}", count,
                        _territory.TerritoryType);
            }
        }
    }
}
