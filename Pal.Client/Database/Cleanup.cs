using System;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pal.Client.Configuration;
using Pal.Common;

namespace Pal.Client.Database
{
    internal sealed class Cleanup
    {
        private readonly ILogger<Cleanup> _logger;
        private readonly IPalacePalConfiguration _configuration;

        public Cleanup(ILogger<Cleanup> logger, IPalacePalConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>持鎖時產生、出鎖之後才寫出去的一則 log。</summary>
        /// <remarks>
        /// <see cref="ILogger"/> 走 Serilog sink：它自己有鎖，還會做檔案 I/O。
        /// <see cref="Purge(PalClientContext, ETerritoryType, out PendingLog)"/> 的呼叫端是在
        /// <c>MemoryTerritory.LockObj</c> 裡呼叫它的，在那裡面寫 log 等於把死鎖面積擴大到別人的
        /// 元件上，也讓其他要讀同一層樓的執行緒排在 log 檔後面。
        ///
        /// 刻意做成純資料：寫出的動作只存在於 <see cref="EmitPending"/>，這樣「鎖內可達的東西」
        /// 裡不存在任何會寫 log 的成員，之後改碼也不容易不小心繞回去。
        /// </remarks>
        public readonly record struct PendingLog(int Count, ETerritoryType? TerritoryType);

        /// <summary>寫出延後的那一行；呼叫時必須已經離開鎖。等級與文字都與延後前相同。</summary>
        public void EmitPending(PendingLog? pending)
        {
            if (pending is not { } log)
                return;

            if (log.TerritoryType is { } territoryType)
                _logger.LogInformation("Cleaning up {Count} outdated locations for territory {Territory}", log.Count,
                    territoryType);
            else
                _logger.LogInformation("Cleaning up {Count} outdated locations", log.Count);
        }

        public void Purge(PalClientContext dbContext, out PendingLog pending)
        {
            var toDelete = dbContext.Locations
                .Include(o => o.ImportedBy)
                .Include(o => o.RemoteEncounters)
                .AsSplitQuery()
                .Where(DefaultPredicate())
                .Where(AnyRemoteEncounter())
                .ToList();
            pending = new PendingLog(toDelete.Count, null);
            dbContext.Locations.RemoveRange(toDelete);
        }

        public void Purge(PalClientContext dbContext, ETerritoryType territoryType, out PendingLog pending)
        {
            var toDelete = dbContext.Locations
                .Include(o => o.ImportedBy)
                .Include(o => o.RemoteEncounters)
                .AsSplitQuery()
                .Where(o => o.TerritoryType == (ushort)territoryType)
                .Where(DefaultPredicate())
                .Where(AnyRemoteEncounter())
                .ToList();
            pending = new PendingLog(toDelete.Count, territoryType);
            dbContext.Locations.RemoveRange(toDelete);
        }

        private Expression<Func<ClientLocation, bool>> DefaultPredicate()
        {
            return o => !o.Seen &&
                        o.ImportedBy.Count == 0 &&
                        o.Source != ClientLocation.ESource.SeenLocally &&
                        o.Source != ClientLocation.ESource.ExplodedLocally;
        }

        private Expression<Func<ClientLocation, bool>> AnyRemoteEncounter()
        {
            if (_configuration.Mode == EMode.Offline)
                return o => true;
            else
                // keep downloaded markers
                return o => o.Source != ClientLocation.ESource.Download;
        }
    }
}
