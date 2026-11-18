using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pal.Client.Configuration.Legacy;
using Pal.Client.Database;
using NJson = Newtonsoft.Json;

namespace Pal.Client.Configuration
{
    internal sealed class ConfigurationManager
    {
        private readonly ILogger<ConfigurationManager> _logger;
        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly IServiceProvider _serviceProvider;

        public event EventHandler<IPalacePalConfiguration>? Saved;

        public ConfigurationManager(ILogger<ConfigurationManager> logger, IDalamudPluginInterface pluginInterface,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _pluginInterface = pluginInterface;
            _serviceProvider = serviceProvider;
        }

        private string ConfigPath =>
            Path.Join(_pluginInterface.GetPluginConfigDirectory(), ConfigurationData.ConfigFileName);

        public IPalacePalConfiguration Load()
        {
            if (!File.Exists(ConfigPath))
            {
                _logger.LogInformation("No config file exists, creating one");
                WriteConfigFile(new ConfigurationV7());
            }

            return JsonSerializer.Deserialize<ConfigurationV7>(File.ReadAllText(ConfigPath, Encoding.UTF8)) ??
                   new ConfigurationV7();
        }

        /// <returns>寫入失敗回 false（只記錄、不擲出），原檔維持上一次的完整內容；<see cref="Saved"/> 只在寫成功時觸發。</returns>
        public bool Save(IConfigurationInConfigDirectory config, bool queue = true)
        {
            try
            {
                WriteConfigFile(config);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "PalacePal:寫入 {File} 失敗（{Error}），原檔維持上一次的內容，下次存檔再試。",
                    ConfigurationData.ConfigFileName, $"{e.GetType().Name}: {e.Message}");
                return false;
            }

            if (queue && config is ConfigurationV7 v7)
                Saved?.Invoke(this, v7);
            return true;
        }

        // Load／Migrate 走這條、照舊擲例外：遷移寫不進新檔時，不能接著把舊設定檔改名成 .old。
        private void WriteConfigFile(IConfigurationInConfigDirectory config)
        {
            FilesystemUtil.WriteAllTextSafe(ConfigPath,
                JsonSerializer.Serialize(config, config.GetType(),
                    new JsonSerializerOptions
                    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }

#pragma warning disable CS0612
#pragma warning disable CS0618
        public void Migrate()
        {
            if (_pluginInterface.ConfigFile.Exists)
            {
                _logger.LogInformation("Migrating config file from v1-v6 format");

                ConfigurationV1 configurationV1 =
                    NJson.JsonConvert.DeserializeObject<ConfigurationV1>(
                        File.ReadAllText(_pluginInterface.ConfigFile.FullName)) ?? new ConfigurationV1();
                configurationV1.Migrate(_pluginInterface,
                    _serviceProvider.GetRequiredService<ILogger<ConfigurationV1>>());
                configurationV1.Save(_pluginInterface);

                var v7 = MigrateToV7(configurationV1);
                WriteConfigFile(v7);

                using (var scope = _serviceProvider.CreateScope())
                {
                    using var dbContext = scope.ServiceProvider.GetRequiredService<PalClientContext>();
                    dbContext.Imports.RemoveRange(dbContext.Imports);

                    foreach (var importHistory in configurationV1.ImportHistory)
                    {
                        _logger.LogInformation("Migrating import {Id}", importHistory.Id);
                        dbContext.Imports.Add(new ImportHistory
                        {
                            Id = importHistory.Id,
                            RemoteUrl = importHistory.RemoteUrl?.Replace(".μ.tv", ".liza.sh"),
                            ExportedAt = importHistory.ExportedAt,
                            ImportedAt = importHistory.ImportedAt
                        });
                    }

                    dbContext.SaveChanges();
                }

                File.Move(_pluginInterface.ConfigFile.FullName, _pluginInterface.ConfigFile.FullName + ".old", true);
            }
        }

        private ConfigurationV7 MigrateToV7(ConfigurationV1 v1)
        {
            ConfigurationV7 v7 = new()
            {
                Version = 7,
                FirstUse = v1.FirstUse,
                Mode = v1.Mode,
                BetaKey = v1.BetaKey,

                DeepDungeons = new DeepDungeonConfiguration
                {
                    Traps = new MarkerConfiguration
                    {
                        Show = v1.ShowTraps,
                        Color = ImGui.ColorConvertFloat4ToU32(v1.TrapColor),
                        OnlyVisibleAfterPomander = v1.OnlyVisibleTrapsAfterPomander,
                        Fill = false
                    },
                    HoardCoffers = new MarkerConfiguration
                    {
                        Show = v1.ShowHoard,
                        Color = ImGui.ColorConvertFloat4ToU32(v1.HoardColor),
                        OnlyVisibleAfterPomander = v1.OnlyVisibleHoardAfterPomander,
                        Fill = false
                    },
                    SilverCoffers = new MarkerConfiguration
                    {
                        Show = v1.ShowSilverCoffers,
                        Color = ImGui.ColorConvertFloat4ToU32(v1.SilverCofferColor),
                        OnlyVisibleAfterPomander = false,
                        Fill = v1.FillSilverCoffers
                    }
                }
            };

            foreach (var (server, oldAccount) in v1.Accounts)
            {
                string? accountId = oldAccount.Id;
                if (string.IsNullOrEmpty(accountId))
                    continue;

                string serverName = server.Replace(".μ.tv", ".liza.sh");
                IAccountConfiguration newAccount = v7.CreateAccount(serverName, accountId);
                newAccount.CachedRoles = oldAccount.CachedRoles.ToList();
            }

            // TODO Migrate ImportHistory

            return v7;
        }
#pragma warning restore CS0618
#pragma warning restore CS0612
    }
}
