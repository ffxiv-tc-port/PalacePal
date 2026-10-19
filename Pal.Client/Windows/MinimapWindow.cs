using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Microsoft.Extensions.Logging;
using Pal.Client.Floors;
using Pal.Client.Properties;
using RoomFlags = FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon.RoomFlags;

namespace Pal.Client.Windows
{
    /// <summary>
    /// 深層迷宮本層小地圖浮動視窗（移植自 Palantir 的 MinimapWindow，BSD-3，授權文字見 LICENSE-Palantir.txt）。
    /// 契約：只在框架執行緒的繪製流程裡跑；<see cref="DrawConditions"/> 每幀重新取得 InstanceContentDeepDungeon，
    /// 把要畫的東西抄成受管理的快照，<see cref="Draw"/> 只讀快照。不保存任何原生指標、不寫遊戲記憶體。
    /// </summary>
    internal sealed unsafe class MinimapWindow : Window, IDisposable
    {
        private const string WindowId = "PalacePal Minimap###PalacePalMinimap";

        private const ImGuiWindowFlags Borderless =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

        private const string RoomsTexture = "ui/uld/DeepDungeonNaviMap_Rooms_hr1.tex";
        private const string NaviTexture = "ui/uld/DeepDungeonNaviMap_hr1.tex";

        private const int Columns = 5;
        private const int RoomCount = 25;
        private const float Unit = 55;
        private const byte Unlocked = 11;
        private const uint InvalidEntity = 0xE0000000;

        // RoomFlags 的八個位元加上朝聖者之路的 0x100；其餘位元有值＝偏移或語意對不上。
        private const int KnownFlagBits = 0x1FF;

        // 進層瞬間 MapData 全 0 是正常的；持續這麼多幀還是全 0 才記一次。
        private const int AllZeroLogFrames = 600;

        private const uint White = 0xFFFFFFFF;
        private const uint Dim = 0x99999999;
        private const uint Faded = 0x66FFFFFF;
        private const uint Member = 0xFFFFA64D;
        private const uint Outline = 0xFF000000;

        private const uint IconReturnClosed = 60905;
        private const uint IconReturnOpen = 60906;
        private const uint IconPassageClosed = 60907;
        private const uint IconPassageOpen = 60908;
        private static readonly uint[] ChestIcons = [60911, 60912, 60913];

        private readonly ILogger<MinimapWindow> _logger;
        private readonly WindowSystem _windowSystem;
        private readonly ITextureProvider _texture;
        private readonly IObjectTable _objectTable;
        private readonly IClientState _clientState;
        private readonly TerritoryState _territoryState;

        private readonly Dictionary<uint, ISharedImmediateTexture> _icons = new();
        private ISharedImmediateTexture? _roomsTexture;
        private ISharedImmediateTexture? _naviTexture;

        private readonly ushort[] _rooms = new ushort[RoomCount];
        private readonly byte[] _chestsNow = new byte[RoomCount];
        private readonly byte[] _chestsSeen = new byte[RoomCount];
        private readonly int[] _party = new int[3];
        private int _partyCount;
        private int _self = -1;
        private float _rotation;
        private bool _passageOpen;
        private bool _returnOpen;
        private bool _unavailable;

        // 0＝不在深宮裡；換層或重新進場時清掉 _chestsSeen。
        private ulong _floorKey;
        private int _allZeroFrames;

        private bool _loggedBadDungeonId;
        private bool _loggedBadFlags;
        private bool _loggedBadIndex;
        private bool _loggedAllZero;

        private IDisposable? _style;

        public MinimapWindow(
            ILogger<MinimapWindow> logger,
            WindowSystem windowSystem,
            ITextureProvider texture,
            IObjectTable objectTable,
            IClientState clientState,
            TerritoryState territoryState)
            : base(WindowId, Borderless)
        {
            _logger = logger;
            _windowSystem = windowSystem;
            _texture = texture;
            _objectTable = objectTable;
            _clientState = clientState;
            _territoryState = territoryState;

            IsOpen = true;
            RespectCloseHotkey = false;
            DisableWindowSounds = true;
            AllowPinning = false;
            AllowClickthrough = false;
            ShowCloseButton = false;

            _windowSystem.AddWindow(this);
        }

        public void Dispose()
            => _windowSystem.RemoveWindow(this);

        public override bool DrawConditions()
        {
            if (!P.Config.MinimapShow || !_territoryState.IsInDeepDungeon())
            {
                _floorKey = 0;
                return false;
            }

            return Capture();
        }

        public override void PreDraw()
        {
            base.PreDraw();

            bool clickThrough = P.Config.MinimapClickThrough;
            Flags = Borderless
                    | (P.Config.MinimapLocked || clickThrough ? ImGuiWindowFlags.NoMove : 0)
                    | (clickThrough ? ImGuiWindowFlags.NoInputs : 0);
            BgAlpha = Math.Clamp(P.Config.MinimapOpacity, 0f, 1f);

            _style = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 0f)
                .Push(ImGuiStyleVar.WindowPadding, new Vector2(4 * ImGuiHelpers.GlobalScale));
        }

        public override void PostDraw()
        {
            _style?.Dispose();
            _style = null;

            base.PostDraw();
        }

        public override void Draw()
        {
            if (_unavailable)
            {
                ImGui.TextDisabled(Localization.pnMinimap_Unavailable);
                return;
            }

            DrawMap(Unit * ImGuiHelpers.GlobalScale * Math.Clamp(P.Config.MinimapScale, 0.5f, 2f));
        }

        /// <summary>回 false＝這一幀不畫（不在深宮、魔王層）；回 true 且 <see cref="_unavailable"/>＝讀到不合理的值，只畫提示。</summary>
        private bool Capture()
        {
            var eventFramework = EventFramework.Instance();
            var dd = eventFramework == null ? null : eventFramework->GetInstanceContentDeepDungeon();
            if (dd == null)
            {
                _floorKey = 0;
                return false;
            }

            byte dungeonId = dd->DeepDungeonId;
            byte floor = dd->Floor;
            if (IsBossFloor(dungeonId, floor))
                return false;

            _unavailable = false;
            if (dungeonId is < 1 or > 4)
            {
                if (!_loggedBadDungeonId)
                {
                    _loggedBadDungeonId = true;
                    _logger.LogInformation("[Minimap] DeepDungeonId={DungeonId} 不在 1～4，小地圖停畫（樓層 {Floor}）",
                        dungeonId, floor);
                }

                _unavailable = true;
                return true;
            }

            ulong key = ((ulong)_clientState.TerritoryType << 16) | ((ulong)dungeonId << 8) | floor;
            if (key != _floorKey)
            {
                if (_floorKey == 0)
                    _logger.LogInformation("[Minimap] 開始繪製：DeepDungeonId={DungeonId} 樓層 {Floor} 區域 {Territory}",
                        dungeonId, floor, _clientState.TerritoryType);
                _floorKey = key;
                _allZeroFrames = 0;
                Array.Clear(_chestsSeen);
            }

            var map = dd->MapData;
            int count = Math.Min(map.Length, RoomCount);
            bool allZero = true;
            for (int i = 0; i < RoomCount; i++)
            {
                ushort value = i < count ? (ushort)map[i] : (ushort)0;
                if ((value & ~KnownFlagBits) != 0)
                {
                    if (!_loggedBadFlags)
                    {
                        _loggedBadFlags = true;
                        _logger.LogInformation("[Minimap] MapData[{Index}]=0x{Value:X4} 含未知位元，小地圖停畫（DeepDungeonId={DungeonId} 樓層 {Floor}）",
                            i, value, dungeonId, floor);
                    }

                    _unavailable = true;
                    return true;
                }

                _rooms[i] = value;
                allZero &= value == 0;
            }

            if (!allZero)
                _allZeroFrames = 0;
            else if (++_allZeroFrames == AllZeroLogFrames && !_loggedAllZero)
            {
                _loggedAllZero = true;
                _logger.LogInformation("[Minimap] MapData 連續 {Frames} 幀全為 0（DeepDungeonId={DungeonId} 樓層 {Floor}），房間格子會一直是暗的",
                    AllZeroLogFrames, dungeonId, floor);
            }

            Array.Clear(_chestsNow);
            foreach (var chest in dd->Chests)
            {
                if (chest.ChestType == 0 || chest.RoomIndex == -1)
                    continue;

                if (chest.ChestType > 3 || chest.RoomIndex < 0 || chest.RoomIndex >= RoomCount)
                {
                    LogBadIndex("Chests", chest.ChestType, chest.RoomIndex, dungeonId, floor);
                    continue;
                }

                byte bit = (byte)(1 << (chest.ChestType - 1));
                _chestsNow[chest.RoomIndex] |= bit;
                _chestsSeen[chest.RoomIndex] |= bit;
            }

            // IObjectTable 的包裝每次存取都可能改指別人：當下讀完 EntityId／Rotation 就不再碰它。
            var player = _objectTable.LocalPlayer;
            uint selfId = player?.EntityId ?? 0;
            _rotation = player?.Rotation ?? 0;

            _self = -1;
            _partyCount = 0;
            foreach (var member in dd->Party)
            {
                if (member.EntityId is 0 or InvalidEntity || member.RoomIndex == -1)
                    continue;

                if (member.RoomIndex < 0 || member.RoomIndex >= RoomCount)
                {
                    LogBadIndex("Party", member.EntityId, member.RoomIndex, dungeonId, floor);
                    continue;
                }

                if (selfId != 0 && member.EntityId == selfId)
                    _self = member.RoomIndex;
                else if (_partyCount < _party.Length)
                    _party[_partyCount++] = member.RoomIndex;
            }

            _passageOpen = dd->PassageProgress >= Unlocked;
            _returnOpen = dd->ReturnProgress >= Unlocked;
            return true;
        }

        private void LogBadIndex(string field, uint value, sbyte roomIndex, byte dungeonId, byte floor)
        {
            if (_loggedBadIndex)
                return;

            _loggedBadIndex = true;
            _logger.LogInformation("[Minimap] {Field} 讀到不合理的值 {Value}／房號 {RoomIndex}，略過該筆（DeepDungeonId={DungeonId} 樓層 {Floor}）",
                field, value, roomIndex, dungeonId, floor);
        }

        private static bool IsBossFloor(byte dungeonId, byte floor) =>
            floor == 0 || floor % 10 == 0 || (dungeonId is 3 or 4 && floor == 99);

        private bool PartyIn(int room)
        {
            for (int p = 0; p < _partyCount; p++)
                if (_party[p] == room)
                    return true;
            return false;
        }

        private bool IsActive(int room) =>
            (_rooms[room] & 0xF) != 0 || room == _self || _chestsSeen[room] != 0 || PartyIn(room);

        private void DrawMap(float room)
        {
            int minCol = Columns, maxCol = -1, minRow = Columns, maxRow = -1;
            for (int i = 0; i < RoomCount; i++)
            {
                if (!IsActive(i))
                    continue;

                minCol = Math.Min(minCol, i % Columns);
                maxCol = Math.Max(maxCol, i % Columns);
                minRow = Math.Min(minRow, i / Columns);
                maxRow = Math.Max(maxRow, i / Columns);
            }

            bool crop = P.Config.MinimapActiveOnly && maxCol >= 0;
            if (!crop)
                (minCol, maxCol, minRow, maxRow) = (0, Columns - 1, 0, (RoomCount - 1) / Columns);

            var size = new Vector2(maxCol - minCol + 1, maxRow - minRow + 1) * room;
            var draw = ImGui.GetWindowDrawList();
            var origin = ImGui.GetCursorScreenPos();
            float k = room / Unit;

            _roomsTexture ??= _texture.GetFromGame(RoomsTexture);
            _naviTexture ??= _texture.GetFromGame(NaviTexture);
            var rooms = _roomsTexture.GetWrapOrEmpty().Handle;
            var navi = _naviTexture.GetWrapOrEmpty().Handle;
            var passage = Icon(_passageOpen ? IconPassageOpen : IconPassageClosed);
            var @return = Icon(_returnOpen ? IconReturnOpen : IconReturnClosed);
            bool showParty = P.Config.MinimapParty;

            for (int i = 0; i < RoomCount; i++)
            {
                if (crop && !IsActive(i))
                    continue;

                var flags = (RoomFlags)_rooms[i];
                var cell = origin + new Vector2(i % Columns - minCol, i / Columns - minRow) * room;
                var middle = cell + new Vector2(room / 2);

                int tile = _rooms[i] & 0xF;
                var uv = new Vector2(0.0104f + tile % 4 * 0.25f, 0.0104f + tile / 4 * 0.25f);
                draw.AddImage(rooms, cell, cell + new Vector2(room), uv, uv + new Vector2(0.2292f), tile > 0 ? White : Dim);

                if (i == _self)
                    Centred(draw, navi, middle, 40 * k, new(0.2424f, 0.4571f), new(0.4848f, 0.6857f));

                if (flags.HasFlag(RoomFlags.Home))
                    Centred(draw, navi, middle, 40 * k, new(0.4848f, 0.4571f), new(0.7272f, 0.6657f));

                // 朝聖者之路的燭臺（0x100）刻意不畫：台服沒有那份圖示，也還沒開放朝聖者之路。
                var poi = cell + new Vector2(17.5f, 27.5f) * k;
                if (flags.HasFlag(RoomFlags.Passage))
                    Image(draw, passage, poi, 20 * k, White);
                if (flags.HasFlag(RoomFlags.Return))
                    Image(draw, @return, poi, 20 * k, White);

                for (int c = 0; c < ChestIcons.Length; c++)
                {
                    int bit = 1 << c;
                    uint tint = (_chestsNow[i] & bit) != 0 ? White : (_chestsSeen[i] & bit) != 0 ? Faded : 0;
                    if (tint != 0)
                        Image(draw, Icon(ChestIcons[c]), cell + new Vector2(1.25f + 11.25f * c, 1.25f) * k, 30 * k, tint);
                }

                if (showParty)
                    DrawParty(draw, i, cell, room);

                if (i == _self)
                    DrawPlayer(draw, navi, middle, _rotation, k);
            }

            ImGui.Dummy(size);
        }

        private ImTextureID Icon(uint id)
        {
            if (!_icons.TryGetValue(id, out var texture))
                _icons[id] = texture = _texture.GetFromGameIcon(new GameIconLookup(id));
            return texture.GetWrapOrEmpty().Handle;
        }

        private static void Image(ImDrawListPtr draw, ImTextureID texture, Vector2 at, float size, uint tint) =>
            draw.AddImage(texture, at, at + new Vector2(size), Vector2.Zero, Vector2.One, tint);

        private static void Centred(ImDrawListPtr draw, ImTextureID texture, Vector2 centre, float size, Vector2 uv0,
            Vector2 uv1) =>
            draw.AddImage(texture, centre - new Vector2(size / 2), centre + new Vector2(size / 2), uv0, uv1, White);

        private void DrawParty(ImDrawListPtr draw, int room, Vector2 cell, float size)
        {
            float radius = size * 0.055f;
            int stacked = 0;
            for (int p = 0; p < _partyCount; p++)
            {
                if (_party[p] != room)
                    continue;

                var dot = cell + new Vector2(0.88f, 0.9f - 0.13f * stacked++) * size;
                draw.AddCircleFilled(dot, radius, Member);
                draw.AddCircle(dot, radius, Outline);
            }
        }

        private static void DrawPlayer(ImDrawListPtr draw, ImTextureID texture, Vector2 centre, float rotation, float k)
        {
            float cos = -MathF.Cos(rotation);
            float sin = MathF.Sin(rotation);
            float width = 20 * k, front = 23.4375f * k, back = 16.5625f * k;

            Vector2 At(float x, float y) => centre + new Vector2(x * cos - y * sin, x * sin + y * cos);

            draw.AddImageQuad(texture,
                At(-width, -front), At(width, -front), At(width, back), At(-width, back),
                new(0f, 0.4571f), new(0.2424f, 0.4571f), new(0.2424f, 0.6857f), new(0f, 0.6857f),
                White);
        }
    }
}
