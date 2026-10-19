using Grpc.Core;
using Palace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Pal.Client.Database;
using Pal.Client.Floors;

namespace Pal.Client.Net
{
    internal partial class RemoteApi
    {
        public async Task<(bool, List<PersistentLocation>)> DownloadRemoteMarkers(uint territoryId, CancellationToken cancellationToken = default)
        {
            if (!await Connect(cancellationToken))
                return (false, new());

            var palaceClient = new PalaceService.PalaceServiceClient(_channel);
            DownloadFloorsReply downloadReply;
            try
            {
                downloadReply = await palaceClient.DownloadFloorsAsync(new DownloadFloorsRequest { TerritoryType = territoryId }, headers: AuthorizedHeaders(), deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: cancellationToken);
            }
            catch (RpcException e) when (!cancellationToken.IsCancellationRequested)
            {
                ReportServerFailure(e.Message, automatic: true);
                throw;
            }
            return (downloadReply.Success, downloadReply.Objects.Select(CreateLocationFromNetworkObject).ToList());
        }

        private PersistentLocation CreateLocationFromNetworkObject(PalaceObject obj)
        {
            return new PersistentLocation
            {
                Type = obj.Type.ToMemoryType(),
                Position = new Vector3(obj.X, obj.Y, obj.Z),
                NetworkId = Guid.Parse(obj.NetworkId),
                Source = ClientLocation.ESource.Download,
            };
        }

        public async Task<(bool, List<FloorStatistics>)> FetchStatistics(CancellationToken cancellationToken = default)
        {
            if (!await Connect(cancellationToken))
                return new(false, new List<FloorStatistics>());

            var palaceClient = new PalaceService.PalaceServiceClient(_channel);
            var statisticsReply = await palaceClient.FetchStatisticsAsync(new StatisticsRequest(), headers: AuthorizedHeaders(), deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: cancellationToken);
            return (statisticsReply.Success, statisticsReply.FloorStatistics.ToList());
        }
    }
}
