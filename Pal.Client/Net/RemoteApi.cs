using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using Pal.Client.Configuration;
using Pal.Client.DependencyInjection;
using Pal.Client.Net.Palantir;
using Pal.Client.Properties;
using Dalamud.Plugin.Services;

namespace Pal.Client.Net
{
    internal sealed partial class RemoteApi : IDisposable
    {
        /// <summary>原作者的舊 gRPC 伺服器，已停用、不再連線；只剩設定裡的舊帳號與匯入紀錄拿它當鍵。</summary>
        public const string RemoteUrl = "https://deep-dungeon.api.puni.sh";

        /// <summary>現行的下載來源。舊的 gRPC 伺服器（<see cref="RemoteUrl"/>）已停用，不再連線。</summary>
        public const string PalantirUrl = "https://palantir.puni.sh";

        private readonly ILogger<RemoteApi> _logger;
        private readonly Chat _chat;
        private readonly ConfigurationManager _configurationManager;
        private readonly IPalacePalConfiguration _configuration;
        private readonly IFramework _framework;
        private readonly PalantirClient _palantir = new(PalantirUrl);

        private readonly ServerConnectionGate _gate = new();
        private int _chatWarnedUnreachable;

        /// <summary>給設定視窗讀的快照；任何執行緒都可讀。</summary>
        public ServerStatus ServerStatus => _gate.Status;

        public RemoteApi(
            ILogger<RemoteApi> logger,
            Chat chat,
            ConfigurationManager configurationManager,
            IPalacePalConfiguration configuration,
            IFramework framework)
        {
            _logger = logger;
            _chat = chat;
            _configurationManager = configurationManager;
            _configuration = configuration;
            _framework = framework;
        }

        /// <param name="automatic">false＝使用者手動測試（不跳聊天提示）。</param>
        private void ReportServerFailure(string reason, bool automatic)
        {
            if (!_gate.RecordFailure(reason))
                return;

            _logger.LogWarning(
                "Could not reach community server {Url}: {Reason}. Only locally recorded traps/coffers will be shown; no automatic retry until {RetryAfter:HH:mm} (manual 'Test Connection' retries immediately)",
                PalantirUrl, reason, _gate.Status.RetryAfterUtc?.ToLocalTime());
            if (automatic && Interlocked.Exchange(ref _chatWarnedUnreachable, 1) == 0)
                _chat.Message(Localization.ConnectionError_UnreachableChat);
        }

        private void ReportServerSuccess()
        {
            if (_gate.RecordSuccess())
                _logger.LogInformation("Community server {Url} is reachable again", PalantirUrl);
        }

        /// <summary>只在 DI 範圍結束（插件卸載）時呼叫：<see cref="_palantir"/> 沒有重建路徑，中途呼叫＝下載失效到重載。</summary>
        public void Dispose()
        {
            _palantir.Dispose();
        }
    }
}
