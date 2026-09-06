using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pal.Client.Database;

namespace Pal.Client.Floors.Tasks
{
    internal sealed class MarkRemoteSeen : DbTask<MarkRemoteSeen>
    {
        private readonly MemoryTerritory _territory;
        private readonly IReadOnlyList<PersistentLocation> _locations;
        private readonly string _accountId;

        public MarkRemoteSeen(IServiceScopeFactory serviceScopeFactory,
            MemoryTerritory territory,
            IReadOnlyList<PersistentLocation> locations,
            string accountId)
            : base(serviceScopeFactory)
        {
            _territory = territory;
            _locations = locations;
            _accountId = accountId;
        }

        protected override void Run(PalClientContext dbContext, ILogger<MarkRemoteSeen> logger)
        {
            // 鎖內不寫 log（ILogger 走 Serilog sink：自己有鎖、還會做檔案 I/O）。
            // 在原本會寫它的那一點把要印的值記下來，出鎖之後才寫；等級、文字、觸發條件不變。
            int? pendingCount = null;

            try
            {
                lock (_territory.LockObj)
                {
                    pendingCount = _locations.Count;

                    List<int> locationIds = _locations.Select(x => x.LocalId).Where(x => x != null).Cast<int>().ToList();
                    List<ClientLocation> locationsToUpdate =
                        dbContext.Locations
                            .Include(x => x.RemoteEncounters)
                            .Where(x => locationIds.Contains(x.LocalId))
                            .ToList()
                            .Where(x => x.RemoteEncounters.All(encounter => encounter.AccountId != _accountId))
                            .ToList();
                    foreach (var clientLocation in locationsToUpdate)
                    {
                        clientLocation.RemoteEncounters.Add(new RemoteEncounter(clientLocation, _accountId));
                    }

                    dbContext.SaveChanges();
                }
            }
            finally
            {
                if (pendingCount is { } count)
                    logger.LogInformation(
                        "Marking {Count} locations as seen remotely on {Account} in territory {Territory}",
                        count, _accountId, _territory.TerritoryType);
            }
        }
    }
}
