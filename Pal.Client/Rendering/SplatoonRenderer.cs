using Dalamud.Plugin;
using ECommons;
using ECommons.Reflection;
using ECommons.Schedulers;
using ECommons.SplatoonAPI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState;
using Microsoft.Extensions.Logging;
using Pal.Client.Configuration;
using Pal.Client.DependencyInjection;
using Pal.Client.Floors;
using Pal.Common;
using ECommons.DalamudServices;
using Dalamud.Logging;
using Dalamud.Plugin.Services;

namespace Pal.Client.Rendering
{
    internal sealed class SplatoonRenderer : IDisposable
    {
        private const long OnTerritoryChange = -2;

        private readonly ILogger<SplatoonRenderer> _logger;
        private readonly DebugState _debugState;
        private readonly IObjectTable _objectTable;
        private readonly Chat _chat;

        public SplatoonRenderer(
            ILogger<SplatoonRenderer> logger,
            IDalamudPluginInterface pluginInterface,
            IDalamudPlugin dalamudPlugin,
            DebugState debugState,
            IObjectTable objectTable,
            Chat chat)
        {
            _logger = logger;
            _debugState = debugState;
            _objectTable = objectTable;
            _chat = chat;

            _logger.LogInformation("Initializing splatoon");
            //ECommonsMain.Init(pluginInterface, dalamudPlugin, ECommons.Module.SplatoonAPI);
            Splatoon.SetOnConnect(SplatoonOnConnect);
        }

        void SplatoonOnConnect()
        {
            UpdateExitElement();
            TrapLabelsNeedRefresh = true;
            //Splatoon.AddDynamicElement("test", Splatoon.DecodeElement("{\"Name\":\"\",\"type\":2,\"refX\":92.5,\"refY\":80.0,\"refZ\":9.5366886E-07,\"offX\":92.5,\"offY\":120.0,\"radius\":2.5}"), 0);
        }

        private bool IsDisposed { get; set; }

        public void SetLayer(ELayer layer, IReadOnlyList<SplatoonElement> elements)
        {
            // we need to delay this, as the current framework update could be before splatoon's, in which case it would immediately delete the layout
            _ = new TickScheduler(delegate
            {
                try
                {
                    Splatoon.AddDynamicElements(ToLayerName(layer),
                        elements.Select(x => x.Delegate).ToArray(),
                        new[] { Environment.TickCount64 + 60 * 60 * 1000, OnTerritoryChange });
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Could not create splatoon layer {Layer} with {Count} elements", layer,
                        elements.Count);
                    _debugState.SetFromException(e);
                }
            });
        }

        /// <summary>
        /// 依給定順序在同一個排程裡加入多層（分開排程不保證先後）。空的層略過。
        /// Splatoon 由後往前處理動態元素：越後加的層越先畫、越在下面。
        /// </summary>
        public void SetLayers((ELayer Layer, IReadOnlyList<SplatoonElement> Elements)[] layers)
        {
            _ = new TickScheduler(delegate
            {
                foreach (var (layer, elements) in layers)
                {
                    if (elements.Count == 0)
                        continue;

                    try
                    {
                        Splatoon.AddDynamicElements(ToLayerName(layer),
                            elements.Select(x => x.Delegate).ToArray(),
                            new[] { Environment.TickCount64 + 60 * 60 * 1000, OnTerritoryChange });
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Could not create splatoon layer {Layer} with {Count} elements", layer,
                            elements.Count);
                        _debugState.SetFromException(e);
                    }
                }
            });
        }

        public void ResetLayer(ELayer layer)
        {
            try
            {
                RemoveLayerElements(ToLayerName(layer));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not reset splatoon layer {Layer}", layer);
            }
        }

        // 卸載期 Splatoon 常比我們先走完，元素已隨它一起消失、沒有東西要清，
        // 而 ECommons 的包裝會對每一次失敗的呼叫寫一行 Warning。
        // 只在「框架正在卸載且確實已斷線」時降級，其餘時機照舊警告。
        private void RemoveLayerElements(string layerName)
        {
            if (Svc.Framework.IsFrameworkUnloading && !Splatoon.IsConnected())
            {
                _logger.LogTrace("Splatoon already unloaded, skipping cleanup of {Layer}", layerName);
                return;
            }

            Splatoon.RemoveDynamicElements(layerName);
        }

        private string ToLayerName(ELayer layer)
            => $"PalacePal.{layer}";

        public SplatoonElement CreateElement(MemoryLocation.EType type, Vector3 pos, uint color, bool fill = false)
        {
            MarkerConfig config = MarkerConfig.ForType(type);
            Element element = new Element(ElementType.CircleAtFixedCoordinates)
            {
                refX = pos.X,
                refY = pos.Z, // z and y are swapped
                refZ = pos.Y,
                offX = 0,
                offY = 0,
                offZ = config.OffsetY,
                Filled = fill,
                radius = config.Radius,
                FillStep = 1,
                color = color,
                thicc = 2,
            };
            return new SplatoonElement(this, element);
        }

        public void DrawDebugItems(uint trapColor, uint hoardColor)
        {
            try
            {
                // IClientState.LocalPlayer 在 API13 已標為過時，它只是 IObjectTable.LocalPlayer 的純轉發，所以直接讀後者。
                Vector3? pos = _objectTable.LocalPlayer?.Position;
                if (pos != null)
                {
                    ResetLayer(ELayer.Test);

                    var elements = new List<SplatoonElement>
                    {
                        CreateElement(MemoryLocation.EType.Trap, pos.Value, trapColor),
                        CreateElement(MemoryLocation.EType.Hoard, pos.Value, hoardColor),
                    };

                    if (!Splatoon.AddDynamicElements(ToLayerName(ELayer.Test),
                            elements.Cast<SplatoonElement>().Select(x => x.Delegate).ToArray(),
                            new[] { Environment.TickCount64 + RenderData.TestLayerTimeout }))
                    {
                        _chat.Message("Could not draw markers :(");
                    }
                }
            }
            catch (Exception)
            {
                try
                {
                    var pluginManager = DalamudReflector.GetPluginManager();
                    IList installedPlugins =
                        pluginManager.GetType().GetProperty("InstalledPlugins")?.GetValue(pluginManager) as IList ??
                        new List<object>();

                    foreach (var t in installedPlugins)
                    {
                        AssemblyName? assemblyName =
                            (AssemblyName?)t.GetType().GetProperty("AssemblyName")?.GetValue(t);
                        string? pluginName = (string?)t.GetType().GetProperty("Name")?.GetValue(t);
                        if (assemblyName?.Name == "Splatoon" && pluginName != "Splatoon")
                        {
                            _chat.Error(
                                $"Splatoon is installed under the plugin name '{pluginName}', which is incompatible with the Splatoon API.");
                            _chat.Message(
                                "You need to install Splatoon from the Traditional Chinese plugin repository at https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json");
                            return;
                        }
                    }
                }
                catch (Exception)
                {
                    // not relevant
                }

                _chat.Error("Could not draw markers, is Splatoon installed and enabled?");
            }
        }

        public void Dispose()
        {
            _logger.LogInformation("Disposing splatoon rendered");

            IsDisposed = true;

            ResetLayer(ELayer.TrapHoard);
            ResetLayer(ELayer.TrapHoardConfirmed);
            ResetLayer(ELayer.RegularCoffers);
            ResetLayer(ELayer.Test);

            // Clean up the extra elements as well
            RemoveLayerElements("PalacePal.ExitElement");
            RemoveLayerElements("PalacePal.BronzeTreasure");
            RemoveLayerElements(TrapLabelsName);

            //ECommonsMain.Dispose();
        }

        private const string TrapLabelsName = "PalacePal.TrapLabels";

        // 已現形陷阱事件物件 DataId → 同種陷阱的動作 id（名稱取自 Action 表）；對照同 Palantir DungeonObjects.TrapVfx。
        // 刻意不含朝聖者之路（2014939／44038）：台服 EObj 與 Action 那兩列是歸零的佔位列。
        private static readonly (uint DataId, uint ActionId)[] TrapKinds =
        {
            (2007182, 6275), (2007183, 6276), (2007184, 6277), (2007185, 6278), (2007186, 6279),
            (2009504, 11287), (2013284, 32375),
        };

        private (uint DataId, string Name)[]? _trapKindNames;

        /// <summary>Splatoon 重新連上後元素都沒了，要重送一次（framework 執行緒讀寫）。</summary>
        public bool TrapLabelsNeedRefresh { get; set; }

        /// <summary>只在框架執行緒呼叫。show＝true 時在每個已現形陷阱上方加一行小字種類名稱，不畫圈。</summary>
        public void UpdateTrapLabels(bool show)
        {
            TrapLabelsNeedRefresh = false;
            try
            {
                Splatoon.RemoveDynamicElements(TrapLabelsName);
                if (!show)
                    return;

                List<Element> elements = new();
                foreach (var (dataId, name) in _trapKindNames ??= LoadTrapKindNames())
                {
                    elements.Add(new Element(ElementType.CircleRelativeToActorPosition)
                    {
                        radius = 0f,
                        thicc = 0f,
                        color = 0, // 全透明的點：只要字
                        overlayText = name,
                        overlayTextColor = 0xB3FFFFFF,
                        overlayBGColor = 0x40000000,
                        overlayVOffset = 0.3f,
                        overlayFScale = P.Config.OverlayFScale * 0.75f,
                        refActorComparisonType = RefActorComparisonType.DataID,
                        refActorDataID = dataId,
                        includeHitbox = false,
                    });
                }

                bool added = Splatoon.AddDynamicElements(TrapLabelsName, elements.ToArray(), new[] { OnTerritoryChange });
                _logger.LogInformation("PalacePal:魔陶器：全景生效，在已現形的陷阱上標出種類（{Count} 種，Splatoon 接受={Added}）。",
                    elements.Count, added);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not update splatoon trap labels");
            }
        }

        private static (uint DataId, string Name)[] LoadTrapKindNames()
        {
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            List<(uint, string)> names = new();
            foreach (var (dataId, actionId) in TrapKinds)
            {
                string name = sheet.GetRowOrDefault(actionId)?.Name.GetText() ?? string.Empty;
                if (name.Length > 0)
                    names.Add((dataId, name));
            }

            return names.ToArray();
        }

        // 再生石塚（死者宮殿）、再生燈籠（天之御柱）、再生裝置（正統優雷卡）；對照同 Palantir DungeonObjects.Return。
        // 刻意不含朝聖者之路（2014755）：台服 EObj 那一列是歸零的佔位列。
        private static readonly uint[] ReturnDevices = { 2007187, 2009506, 2013286 };

        private (uint DataId, string Name)[]? _returnDeviceNames;

        private static (uint DataId, string Name)[] LoadReturnDeviceNames()
        {
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.EObjName>();
            List<(uint, string)> names = new();
            foreach (uint dataId in ReturnDevices)
            {
                string name = sheet.GetRowOrDefault(dataId)?.Singular.GetText() ?? string.Empty;
                names.Add((dataId, name.Length > 0 ? name : "Return"));
            }

            return names.ToArray();
        }

        public void UpdateExitElement()
        {
            const string Name = "PalacePal.ExitElement";
            Splatoon.RemoveDynamicElements(Name);
            if (Plugin.P.Config.DisplayReturn &&
                Enum.GetValues<ETerritoryType>().Contains((ETerritoryType)Svc.ClientState.TerritoryType))
            {
                foreach (var (dataId, name) in _returnDeviceNames ??= LoadReturnDeviceNames())
                {
                    Splatoon.AddDynamicElement(Name, new Element(ElementType.CircleRelativeToActorPosition)
                    {
                        radius = 2.0f,
                        color = Plugin.P.Config.ReturnColor.ToUint(),
                        overlayVOffset = 0.76f,
                        overlayText = Plugin.P.Config.ReturnText ? name : "",
                        overlayFScale = P.Config.OverlayFScale,
                        refActorComparisonType = RefActorComparisonType.DataID,
                        refActorDataID = dataId,
                        includeHitbox = false,
                    }, 0);
                }
            }

            if (Plugin.P.Config.DisplayExit)
            {
                if (Enum.GetValues<ETerritoryType>().Contains((ETerritoryType)Svc.ClientState.TerritoryType))
                {
                    uint[] IDs = new uint[] { 2007188, 2009507, 2013287 };
                    foreach (var x in IDs)
                    {
                        Splatoon.AddDynamicElement(Name, new Element(ElementType.CircleRelativeToActorPosition)
                        {
                            radius = 2.0f,
                            color = Plugin.P.Config.ExitColor.ToUint(),
                            overlayVOffset = 0.76f,
                            overlayText = Plugin.P.Config.ExitText ? "Passage" : "",
                            overlayFScale = P.Config.OverlayFScale,
                            refActorComparisonType = RefActorComparisonType.DataID,
                            refActorDataID = x,
                            includeHitbox = false,
                        }, 0);

                        if (Plugin.P.Config.DisplayExitOnlyActive)
                        {
                            Splatoon.AddDynamicElement(Name, new Element(ElementType.CircleRelativeToActorPosition)
                            {
                                radius = 2.0f,
                                color = 1684471552,
                                refActorDataID = x,
                                includeHitbox = false,
                                Filled = true,
                                refActorComparisonAnd = true,
                                refActorObjectEffectData1 = 4,
                                refActorObjectEffectData2 = 8,
                                refActorObjectEffectMax = int.MaxValue,
                            }, 0);
                        }
                    }
                }
            }
        }

        internal sealed class SplatoonElement
        {
            private readonly SplatoonRenderer _renderer;

            // Delegate.color 的本地副本：那個屬性每讀一次就反射一次並把值裝箱。
            // 🔴 元素的顏色一律經由 Color 寫；直接寫 Delegate.color 會讓這份副本與元素不同步。
            // Splatoon 自己不會改動我們送去的元素顏色。
            private uint? _color;

            // 同上，Delegate.Filled 的本地副本；Splatoon 只會改非圓形元素的 Filled，我們送的都是圓。
            private bool? _filled;

            public SplatoonElement(SplatoonRenderer renderer, Element element)
            {
                _renderer = renderer;
                Delegate = element;
            }

            public Element Delegate { get; }

            public bool IsValid => !_renderer.IsDisposed && Delegate.IsValid();

            public uint Color
            {
                get => _color ??= Delegate.color;
                set
                {
                    Delegate.color = value;
                    _color = value;
                }
            }

            /// <summary>🔴 同 <see cref="Color"/>：Filled 一律經由這裡寫。</summary>
            public bool Filled
            {
                get => _filled ??= Delegate.Filled;
                set
                {
                    Delegate.Filled = value;
                    _filled = value;
                }
            }
        }
    }
}
