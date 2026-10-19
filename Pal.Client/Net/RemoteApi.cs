using Dalamud.Logging;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using System;
using Dalamud.Game.Gui;
using Pal.Client.Configuration;
using Pal.Client.DependencyInjection;
using Pal.Client.Net.Palantir;
using Dalamud.Plugin.Services;

namespace Pal.Client.Net
{
    internal sealed partial class RemoteApi : IDisposable
    {
#if DEBUG
        public const string RemoteUrl = "https://deep-dungeon.api.puni.sh";
#else
        public const string RemoteUrl = "https://deep-dungeon.api.puni.sh";
#endif

        /// <summary>現行的下載來源。舊的 gRPC 伺服器（<see cref="RemoteUrl"/>）已停用，不再連線。</summary>
        public const string PalantirUrl = "https://palantir.puni.sh";
        private readonly string _userAgent =
            $"{typeof(RemoteApi).Assembly.GetName().Name?.Replace(" ", "")}/{typeof(RemoteApi).Assembly.GetName().Version?.ToString()}";

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<RemoteApi> _logger;
        private readonly Chat _chat;
        private readonly ConfigurationManager _configurationManager;
        private readonly IPalacePalConfiguration _configuration;
        private readonly IFramework _framework;
        private readonly PalantirClient _palantir = new(PalantirUrl);

        private GrpcChannel? _channel;
        private LoginInfo _loginInfo = new(null);
        private bool _warnedAboutUpgrade;
        private readonly ServerConnectionGate _gate = new();
        private int _chatWarnedUnreachable;

        /// <summary>給設定視窗讀的快照；任何執行緒都可讀。</summary>
        public ServerStatus ServerStatus => _gate.Status;

        public RemoteApi(
            ILoggerFactory loggerFactory,
            ILogger<RemoteApi> logger,
            Chat chat,
            ConfigurationManager configurationManager,
            IPalacePalConfiguration configuration,
            IFramework framework)
        {
            _loggerFactory = loggerFactory;
            _logger = logger;
            _chat = chat;
            _configurationManager = configurationManager;
            _configuration = configuration;
            _framework = framework;
        }

        public void Dispose()
        {
            _logger.LogDebug("Disposing gRPC channel");
            _channel?.Dispose();
            _channel = null;
            _palantir.Dispose();
        }
    }
}
