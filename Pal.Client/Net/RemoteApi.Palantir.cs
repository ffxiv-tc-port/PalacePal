using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pal.Client.Configuration;
using Pal.Client.Database;
using Pal.Client.Extensions;
using Pal.Client.Floors;
using Pal.Client.Net.Palantir;
using Pal.Client.Properties;
using Pal.Common;

namespace Pal.Client.Net
{
    // 本檔假設與契約：
    // - Palantir 只拿來下載；本插件沒有任何把點位送出去的路徑（舊 gRPC 的上傳與「看過」回報已移除）。
    // - 下載來的點一律是「下載」來源（淡層），不碰我方確認資料庫。
    // - 匿名帳號存在設定的 Accounts（伺服器＝PalantirUrl），DPAPI 加密；讀寫都在 framework 執行緒。
    internal partial class RemoteApi
    {
        private static readonly TimeSpan PalantirDownloadTimeout = TimeSpan.FromSeconds(30);

        /// <returns>失敗（含退避期內跳過）回 (false, 空清單)，不擲例外。</returns>
        public async Task<(bool, List<PersistentLocation>)> DownloadPalantirMarkers(uint territoryId,
            CancellationToken cancellationToken = default)
        {
            if (_configuration.Mode != EMode.Online || territoryId > ushort.MaxValue)
                return (false, new());

            if (_gate.ShouldSkipAutomaticAttempt())
            {
                _logger.LogDebug("Palantir was unreachable recently, using local data only until {RetryAfter:HH:mm}",
                    _gate.Status.RetryAfterUtc?.ToLocalTime());
                return (false, new());
            }

            ushort territory = (ushort)territoryId;
            IReadOnlyList<PalantirMarker> markers = Array.Empty<PalantirMarker>();
            try
            {
                bool completed = await ServerConnectionGate.RunWithTimeout(
                    async ct => markers = await FetchPalantirMarkers(territory, ct).ConfigureAwait(false),
                    PalantirDownloadTimeout, cancellationToken).ConfigureAwait(false);
                if (!completed)
                {
                    ReportServerFailure(
                        string.Format(Localization.ConnectionError_Timeout, (int)PalantirDownloadTimeout.TotalSeconds),
                        automatic: true);
                    return (false, new());
                }
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                ReportServerFailure(e.Message, automatic: true);
                return (false, new());
            }

            ReportServerSuccess();
            List<PersistentLocation> locations = ToLocations(markers, territory, out int traps, out int hoards);
            int trapThreshold = P.Config.CommunityTrapThreshold, hoardThreshold = P.Config.CommunityHoardThreshold;
            int trapsShown = 0, hoardsShown = 0;
            foreach (var location in locations)
            {
                if (location.Type == MemoryLocation.EType.Trap && location.CommunityConfirmations >= trapThreshold)
                    trapsShown++;
                else if (location.Type == MemoryLocation.EType.Hoard && location.CommunityConfirmations >= hoardThreshold)
                    hoardsShown++;
            }

            _logger.LogInformation(
                "PalacePal:從 Palantir 下載 {Territory}：收到 {Received} 筆，採用陷阱 {Traps}、寶藏 {Hoards}（其餘類型或座標不合的略過）；"
                + "達回報門檻的陷阱 {TrapsShown}（門檻 {TrapThreshold}）、寶藏 {HoardsShown}（門檻 {HoardThreshold}）。",
                (ETerritoryType)territory, markers.Count, traps, hoards,
                trapsShown, trapThreshold, hoardsShown, hoardThreshold);
            return (true, locations);
        }

        private async Task<IReadOnlyList<PalantirMarker>> FetchPalantirMarkers(ushort territory, CancellationToken ct)
        {
            await _palantir.GetMetadataAsync(ct).ConfigureAwait(false);
            string token = await AuthenticatePalantir(ct).ConfigureAwait(false);
            return await _palantir.SubscribeAsync(territory, token, ct).ConfigureAwait(false);
        }

        private async Task<string> AuthenticatePalantir(CancellationToken ct)
        {
            Guid? stored = await _framework.RunOnFrameworkThread(
                () => _configuration.FindAccount(PalantirUrl)?.AccountId).ConfigureAwait(false);
            PalantirAuthResponse auth = await _palantir.AuthenticateAsync(stored, ct).ConfigureAwait(false);
            if (stored != auth.AccountId)
                await _framework.RunOnFrameworkThread(() => SavePalantirAccount(auth.AccountId, stored != null))
                    .ConfigureAwait(false);
            return auth.Token;
        }

        private void SavePalantirAccount(Guid accountId, bool replaced)
        {
            _configuration.RemoveAccount(PalantirUrl);
            _configuration.CreateAccount(PalantirUrl, accountId);
            _configurationManager.Save(_configuration);
            _logger.LogInformation(
                "PalacePal:Palantir 匿名帳號 {AccountId} 已存入設定（{Kind}；只用來下載）",
                accountId.ToPartialId(), replaced ? "伺服器換發了新帳號" : "第一次建立");
        }

        internal static List<PersistentLocation> ToLocations(IReadOnlyList<PalantirMarker> markers, ushort territory,
            out int traps, out int hoards)
        {
            traps = 0;
            hoards = 0;
            List<PersistentLocation> locations = new(markers.Count);
            foreach (var marker in markers)
            {
                if (marker.Territory != territory ||
                    !PalantirProtocol.IsValidPosition(marker.X, marker.Y, marker.Z))
                    continue;

                MemoryLocation.EType type;
                if (marker.Type == PalantirProtocol.Trap)
                {
                    type = MemoryLocation.EType.Trap;
                    traps++;
                }
                else if (marker.Type == PalantirProtocol.Hoard)
                {
                    type = MemoryLocation.EType.Hoard;
                    hoards++;
                }
                else
                    continue;

                locations.Add(new PersistentLocation
                {
                    Type = type,
                    Position = new Vector3(marker.X, marker.Y, marker.Z),
                    NetworkId = marker.Id,
                    Source = ClientLocation.ESource.Download,
                    CommunityConfirmations = Math.Max(0, marker.Confirmations),
                });
            }

            return locations;
        }

        /// <summary>設定視窗「測試連線」：測下載用的 Palantir 伺服器，唯讀（見 <see cref="PalantirConnectionTest"/>）。</summary>
        public async Task<string> VerifyConnection(CancellationToken cancellationToken = default)
        {
            var result = await PalantirConnectionTest.RunAsync(_palantir, cancellationToken).ConfigureAwait(false);
            int timeoutSeconds = (int)_palantir.RequestTimeout.TotalSeconds;
            if (result.Success)
                ReportServerSuccess();
            else
                ReportServerFailure(PalantirConnectionTest.Reason(result, Localization.ConnectionError_Timeout, timeoutSeconds),
                    automatic: false);

            _logger.LogInformation("PalacePal:手動測試連線 {Url}：{Result}（只 GET /metadata）",
                result.Server, result.Success ? "OK " + result.ServerName : "失敗 " + result.FailureReason);
            return PalantirConnectionTest.Format(result, Localization.ConnectionTest_Success,
                Localization.ConnectionTest_Failure, Localization.ConnectionError_Timeout, timeoutSeconds);
        }
    }
}
