using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;
using ECommons.DalamudServices.Legacy;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Microsoft.Extensions.Logging;
using Pal.Client.Floors;
using Pal.Common;

namespace Pal.Client.DependencyInjection
{
    internal sealed unsafe class GameHooks : IDisposable
    {
        /// <summary>死者宮殿的「陷阱」BattleChara NameId。</summary>
        private const uint PotdTrapNameId = 5042;

        /// <summary>天之御柱的「陷阱」BattleChara NameId。</summary>
        private const uint HohTrapNameId = 7395;

        // 已知的陷阱狀態效果 VFX。前兩個會觸發記錄;第三個不列入記錄判斷,
        // 只拿來標記診斷行的可信度。
        private const string TrapVfxStun = "vfx/common/eff/dk05th_stdn0t.avfx";
        private const string TrapVfxImpedingA = "vfx/common/eff/dk05ht_ipws0t.avfx";
        private const string TrapVfxImpedingB = "vfx/common/eff/dk05ht_slet0t.avfx";

        /// <summary>狀態效果 VFX 的共同前綴。陷阱造成的暈眩/沉默/靜寂/衰弱/變蛙都在這底下。</summary>
        private const string CommonEffectVfxPrefix = "vfx/common/eff/";

        /// <summary>怪物出生特效(mon_pop1t 等)也在 <see cref="CommonEffectVfxPrefix"/> 底下,診斷時排除。</summary>
        private const string MonsterSpawnVfxPrefix = "vfx/common/eff/mon_pop";

        /// <summary>
        /// 同一層最多記錄幾個相異 NameId。純防呆:正常一層的相異 NameId 是個位數到數十,
        /// 超過就停手,失敗方向是「少印幾行」而不是洗版。
        /// </summary>
        private const int MaxUnknownVfxActorsPerFloor = 64;

        // 踩到陷阱時的動作 ID（移植自 Palantir）：地雷、誘餌、弱化、妨礙、蛙變、獺獺（天之御柱）、貓頭小鷹（正統優雷卡）、朝聖者之路的陷阱。
        private static readonly HashSet<uint> TrapActionIds = new() { 6275, 6276, 6277, 6278, 6279, 11287, 32375, 44038 };

        private readonly ILogger<GameHooks> _logger;
        private readonly IObjectTable _objectTable;
        private readonly TerritoryState _territoryState;
        private readonly FrameworkService _frameworkService;
        private readonly PomanderSensor _pomanderSensor;

        // 本層已經回報過的 NameId。換層或換區就清空 —— 「同一個 NameId 每層只印一行」。
        // 🔴 這裡只存 uint/byte 這類值,不存任何原生指標。
        private readonly HashSet<uint> _reportedUnknownVfxActors = new();
        private uint _diagnosticTerritory;
        private byte _diagnosticFloor;

#pragma warning disable CS0649
        private delegate nint ActorVfxCreateDelegate(char* a1, nint a2, nint a3, float a4, char a5, ushort a6, char a7);

        [Signature("40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24", DetourName = nameof(ActorVfxCreate), Fallibility = Fallibility.Fallible)]
        private Hook<ActorVfxCreateDelegate>? ActorVfxCreateHook { get; init; }
#pragma warning restore CS0649

        private delegate void ActionEffectReceiveDelegate(uint casterEntityId, Character* caster, Vector3* targetPos,
            ActionEffectHandler.Header* header, nint effects, nint targetEntityIds);

        private Hook<ActionEffectReceiveDelegate>? _actionEffectHook;

        public GameHooks(ILogger<GameHooks> logger, IObjectTable objectTable, TerritoryState territoryState,
            FrameworkService frameworkService, PomanderSensor pomanderSensor)
        {
            _logger = logger;
            _objectTable = objectTable;
            _territoryState = territoryState;
            _frameworkService = frameworkService;
            _pomanderSensor = pomanderSensor;

            _logger.LogDebug("Initializing game hooks");
            SignatureHelper.Initialise(this);
            InstallActionEffectHook();
            if (ActorVfxCreateHook == null)
            {
                // 刻意不擲：這裡擲例外會讓整個非同步初始化中斷，連顯示與一般記錄都起不來。
                _logger.LogWarning(
                    "PalacePal:ActorVfxCreate 的特徵碼對不上(遊戲改版?),「踩到陷阱時補記陷阱位置」已停用;"
                    + "顯示已記錄的陷阱與寶箱、記錄看得到的陷阱與寶箱不受影響。");
                return;
            }

            ActorVfxCreateHook.Enable();

            _logger.LogDebug("Game hooks initialized");
        }

        private void InstallActionEffectHook()
        {
            nint address = ActionEffectHandler.Addresses.Receive.Value;
            if (address == 0)
            {
                _logger.LogInformation(
                    "PalacePal:找不到動作效果的處理函式(遊戲改版?),「踩到陷阱時依動作效果記錄陷阱」已停用;VFX 偵測與其他功能不受影響。");
                return;
            }

            try
            {
                _actionEffectHook = Svc.Hook.HookFromAddress<ActionEffectReceiveDelegate>(address, ActionEffectReceive);
                _actionEffectHook.Enable();
                _logger.LogInformation("PalacePal:踩到陷阱偵測(動作效果)已啟用,函式位址 0x{Address:X}。", (long)address);
            }
            catch (Exception e)
            {
                _actionEffectHook?.Dispose();
                _actionEffectHook = null;
                _logger.LogInformation(e,
                    "PalacePal:無法掛上動作效果的處理函式,「踩到陷阱時依動作效果記錄陷阱」已停用;VFX 偵測與其他功能不受影響。");
            }
        }

        /// <summary>
        /// 只讀遊戲已解析好的動作效果，原樣呼叫原函式；位置只抄值、轉交 framework 執行緒記錄，不在這裡做 I/O。
        /// 陷阱實體的座標在觸發時才移到真正位置（見 <see cref="ActorVfxCreate"/>），所以先取施放者、取不到再用目標座標。
        /// </summary>
        private void ActionEffectReceive(uint casterEntityId, Character* caster, Vector3* targetPos,
            ActionEffectHandler.Header* header, nint effects, nint targetEntityIds)
        {
            try
            {
                if (header != null && TrapActionIds.Contains(header->ActionId) && _territoryState.IsInDeepDungeon())
                {
                    Vector3 position = caster != null ? caster->GameObject.Position : Vector3.Zero;
                    if (position.LengthSquared() < 0.01f && targetPos != null)
                        position = *targetPos;
                    if (position.LengthSquared() >= 0.01f)
                        _frameworkService.TriggeredTrapPositions.Enqueue(position);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Action effect hook failed");
            }

            _actionEffectHook!.OriginalDisposeSafe(casterEntityId, caster, targetPos, header, effects, targetEntityIds);
        }

        /// <summary>
        /// Even with a pomander of sight, the BattleChara's position for the trap remains at {0, 0, 0} until it is activated.
        /// Upon exploding, the trap's position is moved to the exact location that the pomander of sight would have revealed.
        ///
        /// That exact position appears to be used for VFX playing when you walk into it - even if you barely walk into the
        /// outer ring of an otter/luring/impeding/landmine trap, the VFX plays at the exact center and not at your character's
        /// location.
        ///
        /// Especially at higher floors, you're more likely to walk into an undiscovered trap compared to e.g. 51-60,
        /// and you probably don't want to/can't use sight on every floor - yet the trap location is still useful information.
        ///
        /// Some (but not all) chests also count as BattleChara named 'Trap', however the effect upon opening isn't played via 
        /// ActorVfxCreate even if they explode (but probably as a Vfx with static location, doesn't matter for here).
        /// 
        /// Landmines and luring traps also don't play a VFX attached to their BattleChara.
        /// 
        /// otter:      vfx/common/eff/dk05th_stdn0t.avfx <br/>
        /// toading:    vfx/common/eff/dk05th_stdn0t.avfx <br/>
        /// enfeebling: vfx/common/eff/dk05th_stdn0t.avfx <br/>
        /// landmine:   none <br/>
        /// luring:     none <br/>
        /// impeding:   vfx/common/eff/dk05ht_ipws0t.avfx (one of silence/pacification) <br/>
        /// impeding:   vfx/common/eff/dk05ht_slet0t.avfx (the other of silence/pacification) <br/>
        /// 
        /// It is of course annoying that, when testing, almost all traps are landmines.
        /// There's also vfx/common/eff/dk01gd_inv0h.avfx for e.g. impeding when you're invulnerable, but not sure if that
        /// has other trigger conditions.
        /// </summary>
        public nint ActorVfxCreate(char* a1, nint a2, nint a3, float a4, char a5, ushort a6, char a7)
        {
            try
            {
                if (_territoryState.IsInDeepDungeon())
                {
                    var vfxPath = MemoryHelper.ReadString(new nint(a1), Encoding.ASCII, 256);
                    var obj = _objectTable.CreateObjectReference(a2);

                    /*
                    if (Service.Configuration.BetaKey == "VFX")
                        _chat.PalPrint($"{vfxPath} on {obj}");
                    */

                    if (obj is IBattleChara bc && (bc.NameId == /* potd */ PotdTrapNameId || bc.NameId == /* hoh */ HohTrapNameId))
                    {
                        if (vfxPath == TrapVfxStun || vfxPath == TrapVfxImpedingA)
                        {
                            _logger.LogDebug("VFX '{Path}' playing at {Location}", vfxPath, obj.Position);
                            _frameworkService.NextUpdateObjects.Enqueue(obj.Address);
                        }
                    }

                    // 只多印一行 log,不碰上面的記錄邏輯。
                    TryLogUnknownVfxActor(vfxPath, obj);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "VFX Create Hook failed");
            }
            // 這個方法只會經由已建立的 hook 被呼叫，所以這裡 hook 必不為 null。
            return ActorVfxCreateHook!.OriginalDisposeSafe(a1, a2, a3, a4, a5, a6, a7);
        }

        /// <summary>
        /// 診斷用:記錄「在深宮裡對某個 BattleChara 播放狀態效果 VFX,但它的 NameId 不在已知陷阱名單裡」。
        /// 上面的記錄只認 5042(死者宮殿)與 7395(天之御柱)兩個 NameId,其他深宮踩爆陷阱一律記不到,
        /// 而它們的陷阱 NameId 離線查不出來,只能靠實機把它印出來。
        /// 判定「疑似」的條件刻意放寬到整個 vfx/common/eff/ 家族:別的深宮有可能用不同的特效檔,寫死路徑會漏。
        /// 這個前綴底下也有一般怪的特效(出生、衝鋒等)與玩家自己身上的狀態特效:
        /// 怪物出生特效與 NameId=0(玩家角色)排除,其餘照印。每個 NameId 每層只印一行,量是有界的。
        /// ⚠️ 這個方法只寫 log,不改任何狀態、不 enqueue 任何東西。
        /// </summary>
        private void TryLogUnknownVfxActor(string vfxPath, IGameObject? obj)
        {
            if (obj is not IBattleChara bc)
                return;

            uint nameId = bc.NameId;
            if (nameId == 0 || nameId == PotdTrapNameId || nameId == HohTrapNameId)
                return;

            if (!vfxPath.StartsWith(CommonEffectVfxPrefix, StringComparison.Ordinal)
                || vfxPath.StartsWith(MonsterSpawnVfxPrefix, StringComparison.Ordinal))
                return;

            byte floor = _pomanderSensor.CurrentFloor;
            uint territory = _territoryState.LastTerritory;
            if (territory != _diagnosticTerritory || floor != _diagnosticFloor)
            {
                _diagnosticTerritory = territory;
                _diagnosticFloor = floor;
                _reportedUnknownVfxActors.Clear();
            }

            if (_reportedUnknownVfxActors.Count >= MaxUnknownVfxActorsPerFloor)
                return;

            if (!_reportedUnknownVfxActors.Add(nameId))
                return;

            bool knownTrapVfx = vfxPath == TrapVfxStun || vfxPath == TrapVfxImpedingA || vfxPath == TrapVfxImpedingB;

            _logger.LogInformation(
                "PalacePal:深宮 VFX 診斷 —— NameId={NameId}(名稱「{Name}」)不在已知陷阱名單裡,"
                + "於 {Territory} 第 {Floor} 層播放 {VfxPath},位置 {Position};"
                + "與已知陷阱特效相符={KnownTrapVfx}。"
                + "若這行是在踩到陷阱的當下出現的,請連同這行回報,以便把該 NameId 補進陷阱名單。",
                nameId, bc.Name.TextValue, (ETerritoryType)territory, floor, vfxPath, bc.Position, knownTrapVfx);
        }

        public void Dispose()
        {
            _logger.LogDebug("Disposing game hooks");
            ActorVfxCreateHook?.Dispose();
            _actionEffectHook?.Dispose();
        }
    }
}
