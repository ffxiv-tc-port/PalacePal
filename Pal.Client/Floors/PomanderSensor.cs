using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Microsoft.Extensions.Logging;

namespace Pal.Client.Floors
{
    /// <summary>
    /// 直接從 InstanceContentDeepDungeon 讀「本層目前生效中的魔陶器」。
    ///
    /// 這是**狀態**不是事件:中途載入外掛、斷線重連、或漏接系統訊息時一樣對得上,
    /// 這點是 ChatService(靠 LogMessage 系統訊息判斷)做不到的。
    /// ChatService 原本的偵測完全保留,兩邊是 OR 的關係 —— 任一邊偵測到就算數,
    /// 所以這個感知器就算在台服完全失效,也只會退回原本的行為,不會讓既有功能變差。
    ///
    /// 欄位索引 -> 魔陶器的對應取自 DeepDungeon 資料表的 PomanderSlot 欄位(跟隨本服資料),
    /// 不寫死欄位索引;查不到對應時一律當作「沒有偵測到」,寧可多畫也不要讓標記憑空消失。
    ///
    /// 🔴 **這條路徑對「咒印解除」與「全景」永遠偵測不到,那不是 bug,是結構本身沒有這個訊號。**
    /// 2026-08-15 用實機 log 的 1422 筆 16 槽原值 dump ＋ NecroLens 的 545 筆用藥記錄逐筆對照:
    ///   ・Flags bit1 只有「留有持續效果」的 6 種會置位 ——
    ///     寶箱增加/改變敵人/減少敵人(下一層生效)、運氣上升(本層持續)、
    ///     感知寶藏(挖到為止)、復生(用掉為止)。
    ///   ・咒印解除用了 34 次、全景用了 39 次,對應槽位(欄位 0 與 1)的 Flags
    ///     **一次都沒有出現 bit1**:欄位 0 觀測到 18 種相異位元組、欄位 1 觀測到 22 種,
    ///     每一種的 bit1 都是 0。這兩個是**瞬時效果**(當下清掉陷阱／點亮地圖就結束),
    ///     director 沒有東西要繼續追蹤,自然不會有「生效中」旗標。
    ///   ・同理,強化自身/強化防禦/隱形/弱化敵人/石化敵人/解咒/魔法效果解除/形態變化
    ///     也都不置位(共 313 次用藥,0 次)。
    /// ⇒ 這兩個魔陶器只能靠 ChatService 的系統訊息偵測(那條路徑 2026-08-15 已修好);
    ///   這裡照樣把三種都讀進來是刻意的:成本是 0,而且哪天遊戲改成有旗標就自動接上,
    ///   兩邊在 TerritoryState 是 OR 的關係,不會互相蓋掉。
    /// </summary>
    internal sealed unsafe class PomanderSensor
    {
        private const int SlotCount = 16;

        /// <summary>
        /// 16 個欄位裡同時回報「生效中」的數量若達到這個值,就視為我們對 Flags bit1 的
        /// 判讀是錯的(正常一層不可能幾乎每個魔陶器都在生效)。此時整條結構路徑停用,
        /// 退回只靠 ChatService,失敗方向是「不隱藏」而不是「永遠隱藏」。
        /// </summary>
        private const int ImplausibleActiveSlots = 12;

        private readonly ILogger<PomanderSensor> _logger;
        private readonly IDataManager _dataManager;
        private readonly TerritoryState _territoryState;

        private readonly Dictionary<byte, EPomander[]?> _slotCache = new();

        private byte _lastFloor;

        /// <summary>目前所在的深宮樓層(0 ＝ 尚未得知)。純診斷用。</summary>
        public byte CurrentFloor => _lastFloor;

        // 結構回報的原始值(未套用樓層閂鎖)
        private bool _rawSafety;
        private bool _rawSight;

        // 「跨過樓層之後還亮著」的旗標:在看到它落下(1 -> 0)之前一律不採信。
        private bool _safetyStale;
        private bool _sightStale;

        private bool _distrusted;
        private bool _distrustLogged;

        // 只在狀態改變時寫 log,避免每幀刷版
        private bool _loggedSafety;
        private bool _loggedSight;
        private bool _loggedIntuition;

        // 16 個欄位的原始位元組快照(道具/數量/旗標各一 byte),只在內容真的變了才寫 log。
        // 這份原值是「Flags bit1 在台服是不是 IsActive」唯一的實機量測手段。
        private readonly byte[] _lastSlotBytes = new byte[SlotCount * 3];
        private bool _slotSnapshotValid;
        private int _slotLogsThisFloor;
        private bool _slotLogCapLogged;

        /// <summary>同一層最多輸出幾行原值。純防呆,正常一層只會有個位數次變動。</summary>
        private const int MaxSlotLogsPerFloor = 30;

        public PomanderSensor(
            ILogger<PomanderSensor> logger,
            IDataManager dataManager,
            TerritoryState territoryState)
        {
            _logger = logger;
            _dataManager = dataManager;
            _territoryState = territoryState;
        }

        private enum EPomander
        {
            None,
            Safety,
            Sight,
            Intuition,
        }

        /// <summary>
        /// DeepDungeonItem 的列號。死者宮殿/天之御柱山用 1/2/14(魔陶器),
        /// 厄薩雷之底用 23/24/34(魔科學器)。以上為台服 7.20 EXD 實際內容。
        /// </summary>
        private static EPomander Classify(uint deepDungeonItemRowId) => deepDungeonItemRowId switch
        {
            1 or 23 => EPomander.Safety,    // 咒印解除:清除本層所有陷阱
            2 or 24 => EPomander.Sight,     // 全景:點亮本層地圖,可看到本層所有陷阱
            14 or 34 => EPomander.Intuition, // 感知寶藏:顯示埋藏寶藏位置
            _ => EPomander.None,
        };

        /// <summary>
        /// 這裡刻意不保存任何原生指標:每次呼叫都重新取得 director,用完即丟。
        /// 也刻意不包 try/catch —— 懸空指標造成的 AccessViolationException 在 .NET Core
        /// 屬於 corrupted-state exception,try/catch 攔不到,加了只會製造假的安全感。
        /// </summary>
        private static InstanceContentDeepDungeon* GetDeepDungeon()
        {
            var eventFramework = EventFramework.Instance();
            return eventFramework == null ? null : eventFramework->GetInstanceContentDeepDungeon();
        }

        /// <summary>每幀呼叫。不在深宮內時會把狀態清乾淨。</summary>
        public void Update()
        {
            var dd = GetDeepDungeon();
            if (dd == null)
            {
                Reset();
                return;
            }

            UpdateFloor(dd);

            // 原值診斷放在所有判斷之前:就算我們對旗標的解讀是錯的(或已進入不信任模式),
            // 也還是要能從 log 看到遊戲結構實際回報了什麼。
            LogRawSlots(dd);

            if (_distrusted)
            {
                PublishInactive();
                return;
            }

            EPomander[]? slots = GetPomanderSlots(dd->DeepDungeonId);
            if (slots == null)
            {
                PublishInactive();
                return;
            }

            bool rawSafety = false, rawSight = false, rawIntuition = false;
            int activeCount = 0;

            for (int i = 0; i < SlotCount; i++)
            {
                if (!dd->Items[i].IsActive)
                    continue;

                activeCount++;
                switch (slots[i])
                {
                    case EPomander.Safety: rawSafety = true; break;
                    case EPomander.Sight: rawSight = true; break;
                    case EPomander.Intuition: rawIntuition = true; break;
                }
            }

            if (activeCount >= ImplausibleActiveSlots)
            {
                _distrusted = true;
                if (!_distrustLogged)
                {
                    _distrustLogged = true;
                    _logger.LogInformation(
                        "PalacePal:深宮結構回報 {Count}/{Total} 個魔陶器同時生效,這不合理,"
                        + "判定 DeepDungeonItemInfo.Flags 的解讀有誤。改為只採用系統訊息偵測,"
                        + "本次遊戲期間不再採信結構旗標。", activeCount, SlotCount);
                }

                PublishInactive();
                return;
            }

            // 樓層閂鎖:咒印解除與全景都是「本層」效果(資料表說明即為「本層」),
            // 若跨層之後旗標還亮著,代表遊戲沒有清掉、或我們讀錯了欄位 —— 兩種都不該繼續隱藏。
            // 看到旗標落下(1 -> 0)就解除不信任,之後同一層再用一次可以正常偵測。
            if (!rawSafety) _safetyStale = false;
            if (!rawSight) _sightStale = false;

            _rawSafety = rawSafety;
            _rawSight = rawSight;

            bool safety = rawSafety && !_safetyStale;
            bool sight = rawSight && !_sightStale;

            // 感知寶藏的效果依資料表說明「將持續到發現埋藏的寶藏為止」,會跨樓層,
            // 因此不套用樓層閂鎖;「已找到」的收斂沿用 ChatService 既有的 FoundOnCurrentFloor。
            bool intuition = rawIntuition;

            Publish(safety, sight, intuition);
        }

        private void UpdateFloor(InstanceContentDeepDungeon* dd)
        {
            byte floor = dd->Floor;
            if (floor == 0 || floor == _lastFloor)
                return;

            byte previous = _lastFloor;
            _lastFloor = floor;

            // 換層(含剛進場)一律重新輸出一次欄位原值,並重置本層的輸出額度。
            _slotSnapshotValid = false;
            _slotLogsThisFloor = 0;
            _slotLogCapLogged = false;

            if (previous == 0)
                return; // 剛進場,不是換層

            _logger.LogInformation("PalacePal:深宮樓層 {Previous} -> {Floor},重設本層的魔陶器隱藏狀態。",
                previous, floor);

            // 跨層時仍亮著的本層效果旗標一律標記為過期(見 Update 的樓層閂鎖說明)
            _safetyStale = _rawSafety;
            _sightStale = _rawSight;

            // 與 ChatService 收到「地下N層」系統訊息時做的事完全一致。
            // 兩邊都做是刻意的:任一條路徑失效,換層重置都還在。
            _territoryState.PomanderOfSight = PomanderState.Inactive;
            if (_territoryState.PomanderOfIntuition == PomanderState.FoundOnCurrentFloor)
                _territoryState.PomanderOfIntuition = PomanderState.Inactive;
        }

        /// <summary>
        /// 把 16 個欄位的原始位元組(道具/數量/旗標)緊湊印成一行。
        /// 觸發條件只有兩個:①每層第一次進入 ②原始位元組真的變了 —— 同一層維持同一個狀態
        /// 不會重複刷版。沒有變動時這個方法只做 48 次位元組比較,不配置任何記憶體。
        /// </summary>
        private void LogRawSlots(InstanceContentDeepDungeon* dd)
        {
            bool changed = !_slotSnapshotValid;
            for (int i = 0; i < SlotCount; i++)
            {
                var item = dd->Items[i];
                int o = i * 3;
                if (_lastSlotBytes[o] != item.ItemId ||
                    _lastSlotBytes[o + 1] != item.Count ||
                    _lastSlotBytes[o + 2] != item.Flags)
                {
                    _lastSlotBytes[o] = item.ItemId;
                    _lastSlotBytes[o + 1] = item.Count;
                    _lastSlotBytes[o + 2] = item.Flags;
                    changed = true;
                }
            }

            _slotSnapshotValid = true;
            if (!changed)
                return;

            if (_slotLogsThisFloor >= MaxSlotLogsPerFloor)
            {
                if (!_slotLogCapLogged)
                {
                    _slotLogCapLogged = true;
                    _logger.LogInformation(
                        "PalacePal:本層的深宮欄位原值已輸出 {Max} 行,本層不再輸出(換層後重新計數)。",
                        MaxSlotLogsPerFloor);
                }

                return;
            }

            _slotLogsThisFloor++;

            var sb = new StringBuilder(SlotCount * 12);
            for (int i = 0; i < SlotCount; i++)
            {
                int o = i * 3;
                sb.Append(i.ToString("D2")).Append('=')
                    .Append(_lastSlotBytes[o].ToString("X2")).Append('/')
                    .Append(_lastSlotBytes[o + 1].ToString("X2")).Append('/')
                    .Append(_lastSlotBytes[o + 2].ToString("X2")).Append(' ');
            }

            _logger.LogInformation(
                "PalacePal:深宮欄位原值(深宮 {DeepDungeonId},樓層 {Floor};格式 欄位=道具/數量/旗標,值為十六進位;"
                + "旗標 bit0=可用、bit1=生效中【只有留有持續效果的魔陶器會設,咒印解除/全景這類瞬時效果不設】,"
                + "其餘位元用途不明且逐欄位不同):{Slots}",
                dd->DeepDungeonId, _lastFloor, sb.ToString());
        }

        private void LogTransition(ref bool previous, bool current, string name)
        {
            if (previous == current)
                return;

            previous = current;
            _logger.LogInformation("PalacePal:結構回報魔陶器「{Name}」{State}(樓層 {Floor})。",
                name, current ? "生效中" : "已結束", _lastFloor);
        }

        /// <summary>
        /// 所有對外狀態都走這裡,讓「翻轉才寫 log」與「寫進 TerritoryState」永遠是同一組動作。
        /// ⚠️ 之前 PublishInactive/Reset 是直接寫欄位、繞過 LogTransition,結果離開深宮或
        /// 進入不信任模式時只會靜默轉成 false ——「已結束」那一行從來不會印,
        /// log 上看起來就像效果一直開著(實測 dalamud_001.log 有 4 處連續兩筆「生效中」)。
        /// </summary>
        private void Publish(bool safety, bool sight, bool intuition)
        {
            LogTransition(ref _loggedSafety, safety, "咒印解除");
            LogTransition(ref _loggedSight, sight, "全景");
            LogTransition(ref _loggedIntuition, intuition, "感知寶藏");

            _territoryState.SafetyActiveFromMemory = safety;
            _territoryState.SightActiveFromMemory = sight;
            _territoryState.IntuitionActiveFromMemory = intuition;
        }

        private void PublishInactive() => Publish(false, false, false);

        /// <summary>離開深宮(或未在深宮內)時清空狀態。</summary>
        public void Reset()
        {
            // 先發布再清樓層:這樣「已結束」那一行帶得到我們原本在哪一層。
            // Publish 內的 LogTransition 會順手把 _loggedXxx 歸位,不必再各自指派一次。
            PublishInactive();

            _lastFloor = 0;
            _rawSafety = false;
            _rawSight = false;
            _safetyStale = false;
            _sightStale = false;
            _slotSnapshotValid = false;
            _slotLogsThisFloor = 0;
            _slotLogCapLogged = false;
        }

        /// <summary>
        /// 用 DeepDungeon 資料表的 PomanderSlot 把「欄位索引」翻成魔陶器種類。
        /// 查不到、或整列都是 0(本服尚未開放這座深宮)時回 null,呼叫端就完全不隱藏。
        /// </summary>
        private EPomander[]? GetPomanderSlots(byte deepDungeonId)
        {
            if (deepDungeonId == 0)
                return null;

            if (_slotCache.TryGetValue(deepDungeonId, out var cached))
                return cached;

            EPomander[]? slots = null;
            if (_dataManager.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeon>()
                .TryGetRow(deepDungeonId, out var row))
            {
                slots = new EPomander[SlotCount];
                int limit = Math.Min(SlotCount, row.PomanderSlot.Count);
                bool any = false;
                for (int i = 0; i < limit; i++)
                {
                    slots[i] = Classify(row.PomanderSlot[i].RowId);
                    if (slots[i] != EPomander.None)
                        any = true;
                }

                if (!any)
                {
                    _logger.LogInformation(
                        "PalacePal:DeepDungeon 資料表第 {Id} 列找不到咒印解除/全景/感知寶藏的欄位對應"
                        + "(本服可能尚未開放這座深宮),這座深宮不使用結構旗標偵測。", deepDungeonId);
                    slots = null;
                }
                else
                {
                    // 每座深宮只印一次(算完就進快取)。這行是解讀上面那行「欄位原值」的對照表:
                    // 沒有它就無法判斷某個欄位的旗標變化對應到哪個魔陶器。
                    var sb = new StringBuilder(SlotCount * 8);
                    for (int i = 0; i < limit; i++)
                        sb.Append(i.ToString("D2")).Append('=')
                            .Append(row.PomanderSlot[i].RowId).Append(' ');

                    _logger.LogInformation(
                        "PalacePal:深宮 {Id} 的欄位對應(欄位=DeepDungeonItem 列號):{Mapping};咒印解除=欄位 {Safety},全景=欄位 {Sight},感知寶藏=欄位 {Intuition}",
                        deepDungeonId, sb.ToString(),
                        Array.IndexOf(slots, EPomander.Safety),
                        Array.IndexOf(slots, EPomander.Sight),
                        Array.IndexOf(slots, EPomander.Intuition));
                }
            }
            else
            {
                _logger.LogInformation(
                    "PalacePal:DeepDungeon 資料表查無第 {Id} 列,這座深宮不使用結構旗標偵測。",
                    deepDungeonId);
            }

            _slotCache[deepDungeonId] = slots;
            return slots;
        }
    }
}
