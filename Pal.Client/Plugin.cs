using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Pal.Client.Rendering;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Pal.Client.Properties;
using ECommons;
using ECommons.DalamudServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pal.Client.Commands;
using Pal.Client.Configuration;
using Pal.Client.DependencyInjection;
using PunishLib;
using ECommons.Configuration;
using ECommons.Schedulers;
using Dalamud.Plugin.Services;

namespace Pal.Client
{
    /// <summary>
    /// With all DI logic elsewhere, this plugin shell really only takes care of a few things around events that
    /// need to be sent to different receivers depending on priority or configuration .
    /// </summary>
    /// <see cref="DependencyInjectionContext"/>
    internal sealed class Plugin : IDalamudPlugin
    {
        private readonly CancellationTokenSource _initCts = new();

        private  IDalamudPluginInterface _pluginInterface;
        private ICommandManager _commandManager;
        private IClientState _clientState;
        private IChatGui _chatGui;
        private IFramework _framework;

        private readonly TaskCompletionSource<IServiceScope> _rootScopeCompletionSource = new();
        private ELoadState _loadState = ELoadState.Initializing;

        private DependencyInjectionContext? _dependencyInjectionContext;
        private ILogger _logger = DependencyInjectionContext.LoggerProvider.CreateLogger<Plugin>();
        private WindowSystem? _windowSystem;
        internal IServiceScope? _rootScope;
        private Action? _loginAction;

        /// <summary>
        /// 載入失敗那條路上自己 <c>new</c> 出來的 <see cref="Chat"/> —— 那時候 DI 容器要不是沒建好、
        /// 就是根本拿不到 singleton。留住參考是為了卸載時釋放它：<see cref="Chat"/> 是 IDisposable，
        /// 而它的 <c>Dispose</c> 負責把共用的聊天佇列排乾，這條路上沒有別的擁有者會做這件事。
        /// </summary>
        private Chat? _loadFailedChat;

        internal static Plugin P = null!;
        internal AdditionalConfiguration Config;

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IClientState clientState,
            IChatGui chatGui,
            IFramework framework)
        {
            P = this;
            ECommonsMain.Init(pluginInterface, this, Module.SplatoonAPI, Module.DalamudReflector);
            PunishLibMain.Init(pluginInterface, Name);
            new TickScheduler(delegate
            {
                Config = EzConfig.Init<AdditionalConfiguration>(); // TODO temp solution, move it to main config later (maybe)
                _pluginInterface = pluginInterface;
                _commandManager = commandManager;
                _clientState = clientState;
                _chatGui = chatGui;
                _framework = framework;

                // set up the current UI language before creating anything
                Localization.Culture = new CultureInfo(MapDalamudLanguage(_pluginInterface.UiLanguage));

                _commandManager.AddHandler("/pal", new CommandInfo(OnCommand)
                {
                    HelpMessage = Localization.Command_pal_HelpText
                });

                // Using TickScheduler requires ECommons to at least be partially initialized
                // ECommonsMain.Dispose leaves this untouched.
                Svc.Init(pluginInterface);

                Task.Run(async () => await CreateDependencyContext());
            });
        }

        public string Name => Localization.Palace_Pal;

        private async Task CreateDependencyContext()
        {
            try
            {
                _dependencyInjectionContext = _pluginInterface.Create<DependencyInjectionContext>(this)
                                              ?? throw new Exception("Could not create DI root context class");
                var serviceProvider = _dependencyInjectionContext.BuildServiceContainer();
                _initCts.Token.ThrowIfCancellationRequested();

                _logger = serviceProvider.GetRequiredService<ILogger<Plugin>>();
                _windowSystem = serviceProvider.GetRequiredService<WindowSystem>();
                _rootScope = serviceProvider.CreateScope();

                var loader = _rootScope.ServiceProvider.GetRequiredService<DependencyContextInitializer>();
                await loader.InitializeAsync(_initCts.Token);

                await _framework.RunOnFrameworkThread(() =>
                {
                    _pluginInterface.UiBuilder.Draw += Draw;
                    _pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
                    _pluginInterface.LanguageChanged += LanguageChanged;
                    _clientState.Login += Login;
                });
                _rootScopeCompletionSource.SetResult(_rootScope);
                _loadState = ELoadState.Loaded;
            }
            catch (ObjectDisposedException e)
            {
                _rootScopeCompletionSource.SetException(e);
                _loadState = ELoadState.Error;
            }
            catch (OperationCanceledException e)
            {
                _rootScopeCompletionSource.SetException(e);
                _loadState = ELoadState.Error;
            }
            catch (Exception e)
            {
                _rootScopeCompletionSource.SetException(e);
                _logger.LogError(e, "Async load failed");
                // 這個實例要留住：它是這條路上唯一的 Chat，卸載時要靠它的 Dispose
                // 把共用佇列排乾（DI 的 singleton 在載入失敗時根本沒被建出來）。
                var loadFailedChat = new Chat(_chatGui, _framework);
                _loadFailedChat = loadFailedChat;
                ShowErrorOnLogin(() =>
                    loadFailedChat.Error(string.Format(Localization.Error_LoadFailed,
                        $"{e.GetType()} - {e.Message}")));

                _loadState = ELoadState.Error;
            }
        }

        private void ShowErrorOnLogin(Action? loginAction)
        {
            if (_clientState.IsLoggedIn)
            {
                loginAction?.Invoke();
                _loginAction = null;
            }
            else
                _loginAction = loginAction;
        }

        private void Login()
        {
            _loginAction?.Invoke();
            _loginAction = null;
        }

        private void OnCommand(string command, string arguments)
        {
            arguments = arguments.Trim();

            Task.Run(async () =>
            {
                IServiceScope rootScope;
                Chat chat;

                try
                {
                    rootScope = await _rootScopeCompletionSource.Task;
                    chat = rootScope.ServiceProvider.GetRequiredService<Chat>();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Could not wait for command root scope");
                    return;
                }

                try
                {
                    IPalacePalConfiguration configuration =
                        rootScope.ServiceProvider.GetRequiredService<IPalacePalConfiguration>();
                    if (configuration.FirstUse && arguments != "" && arguments != "config")
                    {
                        chat.Error(Localization.Error_FirstTimeSetupRequired);
                        return;
                    }

                    Action<string> commandHandler = rootScope.ServiceProvider
                        .GetRequiredService<IEnumerable<ISubCommand>>()
                        .SelectMany(cmd => cmd.GetHandlers())
                        .Where(cmd => cmd.Key == arguments.ToLowerInvariant())
                        .Select(cmd => cmd.Value)
                        .SingleOrDefault(missingCommand =>
                        {
                            chat.Error(string.Format(Localization.Command_pal_UnknownSubcommand, missingCommand,
                                command));
                        });
                    commandHandler.Invoke(arguments);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Could not execute command '{Command}' with arguments '{Arguments}'", command,
                        arguments);
                    chat.Error(string.Format(Localization.Error_CommandFailed,
                        $"{e.GetType()} - {e.Message}"));
                }
            });
        }

        private void OpenConfigUi()
            => _rootScope!.ServiceProvider.GetRequiredService<PalConfigCommand>().Execute();

        private void LanguageChanged(string languageCode)
        {
            _logger.LogInformation("Language set to '{Language}'", languageCode);

            Localization.Culture = new CultureInfo(MapDalamudLanguage(languageCode));
            _windowSystem!.Windows.OfType<ILanguageChanged>()
                .Each(w => w.LanguageChanged());
        }

        // TC Dalamud reports UiLanguage "tw", which CultureInfo resolves to
        // the Twi (Ghana) language, silently falling back to English; map
        // Chinese-flavoured codes onto the shipped neutral zh satellite.
        private static string MapDalamudLanguage(string languageCode) => languageCode switch
        {
            "tw" or "zh" or "zh-TW" or "zh-CN" or "zh-Hant" or "zh-Hans" => "zh",
            _ => languageCode,
        };

        private void Draw()
        {
            _rootScope!.ServiceProvider.GetRequiredService<RenderAdapter>().DrawLayers();
            _windowSystem!.Draw();
        }

        public void Dispose()
        {
            _commandManager.RemoveHandler("/pal");

            if (_loadState == ELoadState.Loaded)
            {
                _pluginInterface.UiBuilder.Draw -= Draw;
                _pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
                _pluginInterface.LanguageChanged -= LanguageChanged;
                _clientState.Login -= Login;
            }

            _initCts.Cancel();
            _rootScope?.Dispose();

            // DI 容器收掉 singleton 的 Chat 時，它自己的 Dispose 會把共用佇列排乾一次。
            _dependencyInjectionContext?.Dispose();
            PunishLibMain.Dispose();

            // 放最後：上面每一步都還可能印東西，而載入失敗那條路上根本沒有 singleton 可以排乾。
            // 佇列是共用的，排乾兩次安全（第二次會發現它空了）。
            _loadFailedChat?.Dispose();
        }

        private enum ELoadState
        {
            Initializing,
            Loaded,
            Error
        }
    }
}
