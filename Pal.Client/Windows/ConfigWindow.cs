using Account;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Configuration;
using ECommons.ImGuiMethods;
using Google.Protobuf;
using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pal.Client.Configuration;
using Pal.Client.Database;
using Pal.Client.DependencyInjection;
using Pal.Client.Extensions;
using Pal.Client.Floors;
using Pal.Client.Net;
using Pal.Client.Properties;
using Pal.Client.Rendering;
using Pal.Client.Scheduled;
using PunishLib.ImGuiMethods;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Pal.Client.Windows
{
    internal sealed class ConfigWindow : Window, ILanguageChanged, IDisposable
    {
        private const string WindowId = "###PalPalaceConfig";

        public OpenWindow OpenWindow { get; private set; } = OpenWindow.DeepDungeons;

        private readonly ILogger<ConfigWindow> _logger;
        private readonly WindowSystem _windowSystem;
        private readonly ConfigurationManager _configurationManager;
        private readonly IPalacePalConfiguration _configuration;
        private readonly IClientState _clientState;
        private readonly RenderAdapter _renderAdapter;
        private readonly TerritoryState _territoryState;
        private readonly FrameworkService _frameworkService;
        private readonly FloorService _floorService;
        private readonly DebugState _debugState;
        private readonly Chat _chat;
        private readonly RemoteApi _remoteApi;
        private readonly ImportService _importService;

        private int _mode;
        private ConfigurableMarker _trapConfig = new();
        private ConfigurableMarker _hoardConfig = new();
        private ConfigurableMarker _silverConfig = new();
        private ConfigurableMarker _goldConfig = new();

        private string? _connectionText;
        private bool _switchToCommunityTab;
        private string _openImportPath = string.Empty;
        private string _saveExportPath = string.Empty;
        private string? _openImportDialogStartPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        private string? _saveExportDialogStartPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        private readonly FileDialogManager _importDialog;
        private readonly FileDialogManager _exportDialog;
        private ImportHistory? _lastImport;

        private CancellationTokenSource? _testConnectionCts;
        private CancellationTokenSource? _lastImportCts;

        public ConfigWindow(
            ILogger<ConfigWindow> logger,
            WindowSystem windowSystem,
            ConfigurationManager configurationManager,
            IClientState clientState,
            IPalacePalConfiguration configuration,
            RenderAdapter renderAdapter,
            TerritoryState territoryState,
            FrameworkService frameworkService,
            FloorService floorService,
            DebugState debugState,
            Chat chat,
            RemoteApi remoteApi,
            ImportService importService)
            : base(WindowId)
        {
            _logger = logger;
            _windowSystem = windowSystem;
            _configurationManager = configurationManager;
            _clientState = clientState;
            _configuration = configuration;
            _renderAdapter = renderAdapter;
            _territoryState = territoryState;
            _frameworkService = frameworkService;
            _floorService = floorService;
            _debugState = debugState;
            _chat = chat;
            _remoteApi = remoteApi;
            _importService = importService;

            LanguageChanged();

            Size = new Vector2(500, 400);
            SizeCondition = ImGuiCond.FirstUseEver;
            Position = new Vector2(300, 300);
            PositionCondition = ImGuiCond.FirstUseEver;
            Flags = ImGuiWindowFlags.NoScrollbar;

            _importDialog = new FileDialogManager
            { AddedWindowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking };
            _exportDialog = new FileDialogManager
            { AddedWindowFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking };

            _windowSystem.AddWindow(this);
        }

        public void Dispose()
        {
            _windowSystem.RemoveWindow(this);
            _lastImportCts?.Cancel();
            _testConnectionCts?.Cancel();
        }

        public void LanguageChanged()
        {
            var version = typeof(Plugin).Assembly.GetName().Version!.ToString();
            WindowName = $"{Localization.Palace_Pal} v{version}{WindowId}";
        }

        public override void OnOpen()
        {
            _mode = (int)_configuration.Mode;
            _trapConfig = new ConfigurableMarker(_configuration.DeepDungeons.Traps);
            _hoardConfig = new ConfigurableMarker(_configuration.DeepDungeons.HoardCoffers);
            _silverConfig = new ConfigurableMarker(_configuration.DeepDungeons.SilverCoffers);
            _goldConfig = new ConfigurableMarker(_configuration.DeepDungeons.GoldCoffers);
            _connectionText = null;

            UpdateLastImport();
        }

        public override void OnClose()
        {
            _importDialog.Reset();
            _exportDialog.Reset();
            _testConnectionCts?.Cancel();
            _testConnectionCts = null;
        }

        public override void Draw()
        {
            bool save = false;
            bool saveAndClose = false;

            var itemSpacing = ImGui.GetStyle().ItemSpacing;
            var topLeftSideHeight = ImGui.GetContentRegionAvail().Y;

            if (ImGui.BeginTable("$PPTableContainer", 2, ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn($"###LeftColumn", ImGuiTableColumnFlags.WidthFixed, ImGui.GetWindowWidth() / 2);
                ImGui.TableNextColumn();

                var regionSize = ImGui.GetContentRegionAvail();
                ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 15f));

                if (ImGui.BeginChild($"###PPLeft", regionSize with { Y = topLeftSideHeight }, false, ImGuiWindowFlags.NoDecoration))
                {
                    var imagePath = "https://s3.puni.sh/media/plugin/32/icon-vrl2a8dqi3h.png";

                    if (ThreadLoadImageHandler.TryGetTextureWrap(imagePath, out var logo))
                    {
                        ImGuiEx.LineCentered("###Logo", () => { ImGui.Image(logo.Handle, new(125f.Scale(), 125f.Scale())); });
                    }
                    else
                    {
                        ImGuiEx.LineCentered("###Logo", () => { ImGui.Dummy(new(125f.Scale(), 125f.Scale())); });
                    }

                    ImGui.Spacing();
                    ImGui.Separator();

                    foreach (var window in Enum.GetValues(typeof(OpenWindow)))
                    {
                        if ((OpenWindow)window == OpenWindow.None) continue;

                        if ((OpenWindow)window == OpenWindow.Export && !_configuration.HasRoleOnCurrentServer(RemoteApi.RemoteUrl, "export:run"))
                            continue;

                        if (ImGui.Selectable($"{string.Join(" ", window.ToString().SplitCamelCase())}", OpenWindow == (OpenWindow)window))
                        {
                            OpenWindow = (OpenWindow)window;
                        }
                    }

                    ImGui.Spacing();
                }
                ImGui.EndChild();

                ImGui.PopStyleVar(2);
                ImGui.TableNextColumn();
                if (ImGui.BeginChild($"###PPRight", Vector2.Zero, false))
                {
                    switch (OpenWindow)
                    {
                        case OpenWindow.DeepDungeons:
                            DrawDeepDungeonItemsTab(ref save, ref saveAndClose);
                            break;
                        case OpenWindow.Community:
                            DrawCommunityTab(ref saveAndClose);
                            break;
                        case OpenWindow.Import:
                            DrawImportTab();
                            break;
                        case OpenWindow.Export:
                            DrawExportTab();
                            break;
                        case OpenWindow.Debug:
                            DrawDebugTab();
                            break;
                        case OpenWindow.About:
                            AboutTab.Draw(P.Name);
                            break;
                    }
                }
                ImGui.EndChild();


                ImGui.EndTable();
            }

            _importDialog.Draw();

            if (save || saveAndClose)
            {
                _configuration.Mode = (EMode)_mode;
                _configuration.DeepDungeons.Traps = _trapConfig.Build();
                _configuration.DeepDungeons.HoardCoffers = _hoardConfig.Build();
                _configuration.DeepDungeons.SilverCoffers = _silverConfig.Build();
                _configuration.DeepDungeons.GoldCoffers = _goldConfig.Build();

                _configurationManager.Save(_configuration);
                EzConfig.Save();

                if (saveAndClose)
                    IsOpen = false;
            }
        }

        private void DrawDeepDungeonItemsTab(ref bool save, ref bool saveAndClose)
        {

            ImGuiEx.Text(Localization.pnTraps);
            if (ImGuiGroup.BeginGroupBox())
            {
                ImGui.Checkbox(Localization.pnDisplay_Potential_Trap_Locations, ref _trapConfig.Show);
                ImGuiComponents.HelpMarker(Localization.pnDisplay_Potential_Trap_Locations_Help);
                Spacing(true); ImGui.ColorEdit4(Localization.pnTrap_Outline_Colour, ref P.Config.TrapColor, ImGuiColorEditFlags.NoInputs);
                Spacing(true); ImGui.Checkbox(Localization.pnDraw_Traps_Filled, ref _trapConfig.Fill);
                DrawCommunityThreshold("##trapThreshold", ref P.Config.CommunityTrapThreshold);
                Spacing(true); ImGui.Checkbox(Localization.pnTrap_Kind_Labels, ref P.Config.TrapKindLabels);
                ImGuiComponents.HelpMarker(Localization.pnTrap_Kind_Labels_Help);
                Spacing(true); ImGui.Checkbox(Localization.pnHide_Stepped_Traps, ref P.Config.HideSteppedTraps);
                ImGuiComponents.HelpMarker(Localization.pnHide_Stepped_Traps_Help);
                Spacing(); ImGui.Checkbox(Localization.pnHide_Traps_on_Safety_Sight_Use, ref _trapConfig.OnlyVisibleAfterPomander);
                ImGuiComponents.HelpMarker(Localization.pnHide_Traps_on_Safety_Sight_Use_Help);

                // 上面那個是總開關,底下兩個是各自的魔陶器細項(預設都開)
                using (ImRaii.Disabled(!_trapConfig.OnlyVisibleAfterPomander))
                {
                    ImGui.Indent();
                    Spacing(); ImGui.Checkbox(Localization.pnHide_Traps_on_Safety_Use, ref P.Config.HideTrapsOnSafety);
                    ImGuiComponents.HelpMarker(Localization.pnHide_Traps_on_Safety_Use_Help);
                    Spacing(); ImGui.Checkbox(Localization.pnHide_Traps_on_Sight_Use, ref P.Config.HideTrapsOnSight);
                    ImGuiComponents.HelpMarker(Localization.pnHide_Traps_on_Sight_Use_Help);
                    ImGui.Unindent();
                }

                ImGuiGroup.EndGroupBox();
            }

            ImGuiEx.Text(Localization.pnAccursed_Hoard);
            if (ImGuiGroup.BeginGroupBox())
            {
                ImGui.Checkbox(Localization.pnDisplay_Potential_Accursed_Hoard_Coffer_Locations, ref _hoardConfig.Show);
                ImGuiComponents.HelpMarker(Localization.pnDisplay_Potential_Accursed_Hoard_Coffer_Locations_Help);
                Spacing(true); ImGui.ColorEdit4(Localization.pnAccursed_Hoard_Outline_Colour, ref _hoardConfig.Color, ImGuiColorEditFlags.NoInputs);
                DrawCommunityThreshold("##hoardThreshold", ref P.Config.CommunityHoardThreshold);
                Spacing(); ImGui.Checkbox(Localization.pnHide_Accursed_Hoard_Locations_on_Intuition_Use, ref _hoardConfig.OnlyVisibleAfterPomander);
                ImGuiComponents.HelpMarker(Localization.pnHide_Accursed_Hoard_Locations_on_Intuition_Use_Help);

                using (ImRaii.Disabled(!_hoardConfig.OnlyVisibleAfterPomander))
                {
                    ImGui.Indent();
                    Spacing(); ImGui.Checkbox(Localization.pnHide_Hoard_on_Intuition_Use, ref P.Config.HideHoardOnIntuition);
                    ImGuiComponents.HelpMarker(Localization.pnHide_Hoard_on_Intuition_Use_Help);
                    ImGui.Unindent();
                }

                ImGuiGroup.EndGroupBox();
            }

            ImGuiEx.Text(Localization.pnCoffers);
            if (ImGuiGroup.BeginGroupBox())
            {
                ImGui.PushID("coffer1");
                ImGui.Checkbox(Localization.Config_GoldCoffer_Show, ref _goldConfig.Show);
                ImGuiComponents.HelpMarker(Localization.Config_GoldCoffers_ToolTip);
                Spacing(true); ImGui.ColorEdit4(Localization.Config_GoldCoffer_Color, ref _goldConfig.Color, ImGuiColorEditFlags.NoInputs);
                Spacing(true); ImGui.Checkbox(Localization.Config_GoldCoffer_Filled, ref _goldConfig.Fill);
                Spacing(); ImGui.Checkbox(Localization.pnText_overlay, ref P.Config.GoldText);
                ImGui.PopID();

                ImGui.Separator();

                ImGui.PushID("coffer2");
                ImGui.Checkbox(Localization.Config_SilverCoffer_Show, ref _silverConfig.Show);
                ImGuiComponents.HelpMarker(Localization.Config_SilverCoffers_ToolTip);
                Spacing(true); ImGui.ColorEdit4(Localization.Config_SilverCoffer_Color, ref _silverConfig.Color, ImGuiColorEditFlags.NoInputs);
                Spacing(true); ImGui.Checkbox(Localization.Config_SilverCoffer_Filled, ref _silverConfig.Fill);
                Spacing(); ImGui.Checkbox(Localization.pnText_overlay, ref P.Config.SilverText);
                ImGui.PopID();

                ImGui.Separator();

                ImGui.PushID("coffer3");
                if (ImGui.Checkbox(Localization.pnDisplay_Bronze_Treasure_Coffer_Locations, ref P.Config.BronzeShow)) UpdateRender();
                //ImGuiComponents.HelpMarker(Localization.Config_SilverCoffers_ToolTip);
                Spacing(true); if (ImGui.ColorEdit4(Localization.pnBronze_Coffer_color, ref P.Config.BronzeColor, ImGuiColorEditFlags.NoInputs)) UpdateRender();
                Spacing(true); if (ImGui.ColorEdit4(Localization.pnMimic_Coffer_color, ref P.Config.MimicColor, ImGuiColorEditFlags.NoInputs)) UpdateRender();
                ImGuiComponents.HelpMarker(Localization.pnMimic_Help);
                Spacing(true); if (ImGui.Checkbox(Localization.Config_SilverCoffer_Filled, ref P.Config.BronzeFill)) UpdateRender();
                Spacing(); if (ImGui.Checkbox(Localization.pnText_overlay, ref P.Config.BronzeText)) UpdateRender();
                ImGui.PopID();

                ImGuiGroup.EndGroupBox();
            }


            ImGuiEx.Text(Localization.pnPassages);
            if (ImGuiGroup.BeginGroupBox())
            {
                if (ImGui.Checkbox(Localization.pnDisplay_Passages, ref P.Config.DisplayExit)) UpdateRender();
                Spacing(true); if (ImGui.ColorEdit4(Localization.pnExit_Outline_Colour, ref P.Config.ExitColor, ImGuiColorEditFlags.NoInputs)) UpdateRender();
                Spacing(true); if (ImGui.Checkbox(Localization.pnHighlight_When_Activated, ref P.Config.DisplayExitOnlyActive)) UpdateRender();
                ImGuiComponents.HelpMarker(Localization.pnHighlight_When_Activated_Help);
                Spacing(); if (ImGui.Checkbox(Localization.pnText_overlay, ref P.Config.ExitText)) UpdateRender();

                ImGui.Separator();

                ImGui.PushID("return");
                if (ImGui.Checkbox(Localization.pnDisplay_Return_Devices, ref P.Config.DisplayReturn)) UpdateRender();
                ImGuiComponents.HelpMarker(Localization.pnDisplay_Return_Devices_Help);
                Spacing(true); if (ImGui.ColorEdit4(Localization.pnReturn_Outline_Colour, ref P.Config.ReturnColor, ImGuiColorEditFlags.NoInputs)) UpdateRender();
                Spacing(); if (ImGui.Checkbox(Localization.pnText_overlay, ref P.Config.ReturnText)) UpdateRender();
                ImGui.PopID();
                ImGuiGroup.EndGroupBox();
            }

            ImGuiEx.Text(Localization.pnMinimap);
            if (ImGuiGroup.BeginGroupBox())
            {
                ImGui.PushID("minimap");
                ImGui.Checkbox(Localization.pnShow_Minimap, ref P.Config.MinimapShow);
                ImGuiComponents.HelpMarker(Localization.pnShow_Minimap_Help);
                Spacing(true); ImGui.Checkbox(Localization.pnMinimap_Party, ref P.Config.MinimapParty);
                Spacing(true); ImGui.Checkbox(Localization.pnMinimap_Active_Only, ref P.Config.MinimapActiveOnly);
                ImGuiComponents.HelpMarker(Localization.pnMinimap_Active_Only_Help);
                Spacing(true); ImGui.SetNextItemWidth(160f);
                ImGui.SliderFloat(Localization.pnMinimap_Scale, ref P.Config.MinimapScale, 0.5f, 2f);
                P.Config.MinimapScale = Math.Clamp(P.Config.MinimapScale, 0.5f, 2f);
                Spacing(true); ImGui.SetNextItemWidth(160f);
                ImGui.SliderFloat(Localization.pnMinimap_Opacity, ref P.Config.MinimapOpacity, 0f, 1f);
                P.Config.MinimapOpacity = Math.Clamp(P.Config.MinimapOpacity, 0f, 1f);
                Spacing(true); ImGui.Checkbox(Localization.pnMinimap_Lock, ref P.Config.MinimapLocked);
                Spacing(); ImGui.Checkbox(Localization.pnMinimap_Click_Through, ref P.Config.MinimapClickThrough);
                ImGuiComponents.HelpMarker(Localization.pnMinimap_Click_Through_Help);
                ImGui.PopID();
                ImGuiGroup.EndGroupBox();
            }

            ImGuiEx.Text(Localization.pnCommon_settings);
            if (ImGuiGroup.BeginGroupBox())
            {
                ImGui.SetNextItemWidth(200f);
                ImGui.SliderFloat(Localization.pnText_overlay_font_scale, ref P.Config.OverlayFScale, 0.1f, 5f);
                P.Config.OverlayFScale.ValidateRange(0.1f, 10f);
                ImGui.SetNextItemWidth(200f);
                ImGui.SliderFloat(Localization.pnUnconfirmed_Marker_Opacity, ref P.Config.UnconfirmedMarkerAlpha, 0.05f, 1f);
                P.Config.UnconfirmedMarkerAlpha.ValidateRange(0.05f, 1f);
                ImGuiComponents.HelpMarker(Localization.pnUnconfirmed_Marker_Opacity_Help);
                ImGuiGroup.EndGroupBox();
            }

            ImGui.Separator();


            save = ImGui.Button(Localization.Save);
            ImGui.SameLine();
            saveAndClose = ImGui.Button(Localization.SaveAndClose);
        }

        private static void DrawCommunityThreshold(string id, ref int threshold)
        {
            Spacing(true);
            ImGui.SetNextItemWidth(120f);
            ImGui.SliderInt(Localization.pnCommunity_Min_Confirmations + id, ref threshold, 0, CommunityThreshold.Max);
            threshold = Math.Clamp(threshold, 0, CommunityThreshold.Max);
            ImGuiComponents.HelpMarker(Localization.pnCommunity_Min_Confirmations_Help);
        }

        private void UpdateRender()
        {
            Plugin.P._rootScope!.ServiceProvider.GetRequiredService<RenderAdapter>()._implementation.UpdateExitElement();
            ExternalUtils.UpdateBronzeTreasureCoffers(_clientState.TerritoryType);
        }

        private void DrawCommunityTab(ref bool saveAndClose)
        {

            _switchToCommunityTab = false;

            ImGui.TextWrapped(Localization.Explanation_3);
            ImGui.TextWrapped(Localization.Explanation_4);

            PalImGui.RadioButtonWrapped(Localization.Config_UploadMyDiscoveries_ShowOtherTraps, ref _mode,
                (int)EMode.Online);
            PalImGui.RadioButtonWrapped(Localization.Config_NeverUploadDiscoveries_ShowMyTraps, ref _mode,
                (int)EMode.Offline);
            saveAndClose = ImGui.Button(Localization.SaveAndClose);

            ImGui.Separator();

            ImGui.BeginDisabled(_configuration.Mode != EMode.Online);
            if (ImGui.Button(Localization.Config_TestConnection))
                TestConnection();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1f);
                ImGui.SetTooltip(string.Format(Localization.Config_TestConnection_Tooltip, RemoteApi.PalantirUrl));
                ImGui.PopStyleVar();
            }

            if (_connectionText != null)
                ImGui.TextWrapped(_connectionText);

            ImGui.EndDisabled();

            if (_configuration.Mode == EMode.Online)
            {
                ImGui.TextWrapped(string.Format(Localization.Config_PalantirServer, RemoteApi.PalantirUrl));
                DrawServerStatus();
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                ImGui.TextWrapped(string.Format(Localization.Config_LegacyServerRetired, RemoteApi.RemoteUrl));
                ImGui.PopStyleColor();
            }
        }

        private void DrawImportTab()
        {

            ImGui.TextWrapped(Localization.Config_ImportExplanation1);
            ImGui.TextWrapped(Localization.Config_ImportExplanation2);
            ImGui.TextWrapped(Localization.Config_ImportExplanation3);
            /*ImGui.Separator();
            ImGui.TextWrapped(string.Format(Localization.Config_ImportDownloadLocation,
                "https://github.com/carvelli/PalacePal/releases/"));
            if (ImGui.Button(Localization.Config_Import_VisitGitHub))
                GenericHelpers.ShellStart("https://github.com/carvelli/PalacePal/releases/latest");*/
            ImGui.Separator();
            ImGui.Text(Localization.Config_SelectImportFile);
            ImGui.SameLine();
            ImGui.InputTextWithHint("", Localization.Config_SelectImportFile_Hint, ref _openImportPath, 260);
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Search))
            {
                _importDialog.OpenFileDialog(Localization.Palace_Pal, $"{Localization.Palace_Pal} (*.pal) {{.pal}}",
                    (success, paths) =>
                    {
                        if (success && paths.Count == 1)
                        {
                            _openImportPath = paths.First();
                        }
                    }, selectionCountMax: 1, startPath: _openImportDialogStartPath, isModal: false);
                _openImportDialogStartPath =
                    null; // only use this once, FileDialogManager will save path between calls
            }

            ImGui.BeginDisabled(string.IsNullOrEmpty(_openImportPath) || !File.Exists(_openImportPath) || _floorService.IsImportRunning);
            if (ImGui.Button(Localization.Config_StartImport))
                DoImport(_openImportPath);
            ImGui.EndDisabled();

            ImportHistory? importHistory = _lastImport;
            if (importHistory != null)
            {
                ImGui.Separator();
                ImGui.TextWrapped(string.Format(Localization.Config_UndoImportExplanation1,
                    importHistory.ImportedAt.ToLocalTime(),
                    importHistory.RemoteUrl,
                    importHistory.ExportedAt.ToUniversalTime()));
                ImGui.TextWrapped(Localization.Config_UndoImportExplanation2);

                ImGui.BeginDisabled(_floorService.IsImportRunning);
                if (ImGui.Button(Localization.Config_UndoImport))
                    UndoImport(importHistory.Id);
                ImGui.EndDisabled();
            }
        }

        private void DrawExportTab()
        {
            if (_configuration.HasRoleOnCurrentServer(RemoteApi.RemoteUrl, "export:run"))
            {
                string todaysFileName = $"export-{DateTime.Today:yyyy-MM-dd}.pal";
                if (string.IsNullOrEmpty(_saveExportPath) && !string.IsNullOrEmpty(_saveExportDialogStartPath))
                    _saveExportPath = Path.Join(_saveExportDialogStartPath, todaysFileName);

                ImGui.TextWrapped(string.Format(Localization.Config_ExportSource, RemoteApi.RemoteUrl));
                ImGui.Text(Localization.Config_Export_SaveAs);
                ImGui.SameLine();
                ImGui.InputTextWithHint("", Localization.Config_SelectImportFile_Hint, ref _saveExportPath, 260);
                ImGui.SameLine();
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Search))
                {
                    _importDialog.SaveFileDialog(Localization.Palace_Pal, $"{Localization.Palace_Pal} (*.pal) {{.pal}}",
                        todaysFileName, "pal", (success, path) =>
                        {
                            if (success && !string.IsNullOrEmpty(path))
                            {
                                _saveExportPath = path;
                            }
                        }, startPath: _saveExportDialogStartPath, isModal: false);
                    _saveExportDialogStartPath =
                        null; // only use this once, FileDialogManager will save path between calls
                }

                ImGui.BeginDisabled(string.IsNullOrEmpty(_saveExportPath) || File.Exists(_saveExportPath));
                if (ImGui.Button(Localization.Config_StartExport))
                    DoExport(_saveExportPath);
                ImGui.EndDisabled();
            }
        }

        private void DrawDebugTab()
        {

            if (_territoryState.IsInDeepDungeon())
            {
                MemoryTerritory? memoryTerritory = _floorService.GetTerritoryIfReady(_territoryState.LastTerritory);
                ImGui.Text(string.Format(Localization.Debug_InDeepDungeon, _territoryState.LastTerritory));
                ImGui.Text(string.Format(Localization.Debug_SyncState,
                    memoryTerritory?.SyncState.ToString() ?? Localization.Debug_SyncState_Unknown));
                ImGui.Text($"{_debugState.DebugMessage}");

                ImGui.Indent();
                if (memoryTerritory != null)
                {
                    if (_trapConfig.Show)
                    {
                        int traps = memoryTerritory.Locations.Count(x => x.Type == MemoryLocation.EType.Trap);
                        ImGui.Text(string.Format(Localization.Debug_KnownTraps, traps));
                    }

                    if (_hoardConfig.Show)
                    {
                        int hoardCoffers =
                            memoryTerritory.Locations.Count(x => x.Type == MemoryLocation.EType.Hoard);
                        ImGui.Text(string.Format(Localization.Debug_KnownHoardCoffers, hoardCoffers));
                    }

                    if (_silverConfig.Show)
                    {
                        int silverCoffers =
                            _floorService.EphemeralLocations.Count(x =>
                                x.Type == MemoryLocation.EType.SilverCoffer);
                        ImGui.Text(string.Format(Localization.Debug_SilverCoffersVisible, silverCoffers));
                    }

                    if (_goldConfig.Show)
                    {
                        int goldCoffers =
                            _floorService.EphemeralLocations.Count(x =>
                                x.Type == MemoryLocation.EType.GoldCoffer);
                        ImGui.Text(string.Format(Localization.Debug_GoldCoffersVisible, goldCoffers));
                    }

                    ImGui.Text(string.Format(Localization.Debug_PomanderOfSight, _territoryState.PomanderOfSight));
                    ImGui.Text(string.Format(Localization.Debug_PomanderOfIntuition, _territoryState.PomanderOfIntuition));
                    ImGui.Text(string.Format(Localization.Debug_PomanderFromMemory,
                        _territoryState.SafetyActiveFromMemory,
                        _territoryState.SightActiveFromMemory,
                        _territoryState.IntuitionActiveFromMemory));
                }
                else
                    ImGui.Text(Localization.Debug_CouldNotQueryCount);

                ImGui.Unindent();
                ImGui.TextWrapped(Localization.Debug_TrapsCoffersNotDiscoveredNote);
            }
            else
                ImGui.Text(Localization.Config_Debug_NotInADeepDungeon);
        }

        private void DrawServerStatus()
        {
            ServerStatus status = _remoteApi.ServerStatus;
            string at = status.LastAttemptUtc?.ToLocalTime().ToString("HH:mm") ?? "?";
            switch (status.State)
            {
                case EServerState.Connected:
                    ImGui.TextWrapped(string.Format(Localization.Config_ServerStatus_Connected, at));
                    break;
                case EServerState.Unreachable:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                    ImGui.TextWrapped(string.Format(Localization.Config_ServerStatus_Unreachable, at));
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Format(Localization.Config_ServerStatus_UnreachableTooltip,
                            status.Reason ?? "?", status.RetryAfterUtc?.ToLocalTime().ToString("HH:mm") ?? "?"));
                    break;
                default:
                    ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
                    ImGui.TextWrapped(Localization.Config_ServerStatus_NotAttempted);
                    ImGui.PopStyleColor();
                    break;
            }
        }

        internal void TestConnection()
        {
            Task.Run(async () =>
            {
                _connectionText = Localization.Config_TestConnection_Connecting;
                _switchToCommunityTab = true;

                _testConnectionCts?.Cancel();

                CancellationTokenSource cts = new();
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                _testConnectionCts = cts;

                try
                {
                    _connectionText = await _remoteApi.VerifyConnection(cts.Token);
                }
                catch (Exception e)
                {
                    if (cts == _testConnectionCts)
                    {
                        _logger.LogError(e, "Could not establish remote connection");
                        _connectionText = e.ToString();
                    }
                    else
                        _logger.LogWarning(e,
                            "Could not establish a remote connection, but user also clicked 'test connection' again so not updating UI");
                }
            });
        }

        private void DoImport(string sourcePath)
        {
            _frameworkService.EarlyEventQueue.Enqueue(new QueuedImport(sourcePath));
        }

        private void UndoImport(Guid importId)
        {
            _frameworkService.EarlyEventQueue.Enqueue(new QueuedUndoImport(importId));
        }

        internal void UpdateLastImport()
        {
            _lastImportCts?.Cancel();
            CancellationTokenSource cts = new CancellationTokenSource();
            _lastImportCts = cts;

            Task.Run(async () =>
            {
                try
                {
                    _lastImport = await _importService.FindLast(cts.Token);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Unable to fetch last import");
                }
            }, cts.Token);
        }

        private void DoExport(string destinationPath)
        {
            Task.Run(async () =>
            {
                try
                {
                    (bool success, ExportRoot export) = await _remoteApi.DoExport();
                    if (success)
                    {
                        await using var output = File.Create(destinationPath);
                        export.WriteTo(output);

                        _chat.Message($"Export saved as {destinationPath}.");
                    }
                    else
                    {
                        _chat.Error("Export failed due to server error.");
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Export failed");
                    _chat.Error($"Export failed: {e}");
                }
            });
        }

        internal static void Spacing(bool cont = false)
        {
            ImGuiEx.TextV($" {(cont ? "├" : "└")} ");
            ImGui.SameLine();
        }


        private sealed class ConfigurableMarker
        {
            public bool Show;
            public Vector4 Color;
            public bool OnlyVisibleAfterPomander;
            public bool Fill;

            public ConfigurableMarker()
            {
            }

            public ConfigurableMarker(MarkerConfiguration config)
            {
                Show = config.Show;
                Color = ImGui.ColorConvertU32ToFloat4(config.Color);
                OnlyVisibleAfterPomander = config.OnlyVisibleAfterPomander;
                Fill = config.Fill;
            }

            public MarkerConfiguration Build()
            {
                return new MarkerConfiguration
                {
                    Show = Show,
                    Color = ImGui.ColorConvertFloat4ToU32(Color),
                    OnlyVisibleAfterPomander = OnlyVisibleAfterPomander,
                    Fill = Fill
                };
            }
        }
    }

    public enum OpenWindow
    {
        None,
        DeepDungeons,
        Community,
        Import,
        Export,
        Debug,
        About
    }
}
