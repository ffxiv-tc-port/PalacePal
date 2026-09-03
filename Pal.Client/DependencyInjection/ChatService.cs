using System;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Pal.Client.Configuration;
using Pal.Client.Floors;

namespace Pal.Client.DependencyInjection
{
    internal sealed class ChatService : IDisposable
    {
        /// <summary>
        /// 遊戲的 chat type 只有低 7 位是「訊息類型」,bit 7~10 是來源、bit 11~14 是目標。
        /// Dalamud 的 IChatGui.ChatMessage 轉發的是**未遮罩的原始值**,所以比對類型前必須自己遮。
        /// </summary>
        private const int ChatTypeMask = 0x7F;

        private readonly ILogger<ChatService> _logger;
        private readonly IChatGui _chatGui;
        private readonly TerritoryState _territoryState;
        private readonly IPalacePalConfiguration _configuration;
        private readonly IDataManager _dataManager;
        private readonly LocalizedChatMessages _localizedChatMessages;

        public ChatService(ILogger<ChatService> logger, IChatGui chatGui, TerritoryState territoryState,
            IPalacePalConfiguration configuration, IDataManager dataManager)
        {
            _logger = logger;
            _chatGui = chatGui;
            _territoryState = territoryState;
            _configuration = configuration;
            _dataManager = dataManager;

            _localizedChatMessages = LoadLanguageStrings();

            _chatGui.ChatMessage += OnChatMessage;
        }

        public void Dispose()
            => _chatGui.ChatMessage -= OnChatMessage;

        // NOTE: real API13's IChatGui.ChatMessage predates XivChatRelationKind/SourceKind
        // (added later to distinguish "message about the local player" from other sources).
        // 🔴 當初把那個 filter 拿掉時,連帶漏掉了它真正承擔的工作:**遮罩 chat type**。
        // 新版 Dalamud 把來源/目標拆成獨立參數後,傳進來的 type 已經是遮罩過的裸類型;
        // API13 沒有那組參數,轉發的是遊戲原始值(ChatGui.HandlePrintMessageDetour 直接 forward)。
        // 深宮的系統訊息全部帶著 target=PC 的 0x800,實機是 2105 (0x839 = 0x800 | 57),
        // 與 XivChatType.SystemMessage(57) 永遠不相等 —— 五種偵測會**全部靜默失效**。
        //
        // 2026-08-15 實機 log 量測(dalamud.log／dalamud_001.log／dalamud.20260812-031317.old.log):
        //   ・「這一層的地圖全部被點亮了！」等 5 種目標訊息共 195 則,chat type **100% 是 2105**,
        //     一則裸 57 都沒有。
        //   ・同一批 log 裡另有 17368 則其他訊息確實以裸 57(SystemMessage)送達
        //     —— 所以不是「列舉比對整個壞掉」,而是專門漏掉帶目標欄位的這一批,失敗形式完全靜默。
        //   ・佐證:90 次「受詛咒藏寶箱標記隱藏狀態」翻轉的 log 全部是「結構=True/聊天=Inactive」,
        //     亦即聊天路徑從來沒有觸發過;而「陷阱標記隱藏狀態」翻轉次數是 0。
        private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message,
            ref bool isHandled)
        {
            if (_configuration.FirstUse)
                return;

            if (((int)type & ChatTypeMask) != (int)XivChatType.SystemMessage)
                return;

            var text = message.TextValue;
            if (_localizedChatMessages.FloorChanged.IsMatch(text))
            {
                // 樓層變化不寫 log:PomanderSensor 已經以遊戲結構為準印過「深宮樓層 X -> Y」,
                // 這裡再印一次只會製造重複。
                SetSight(PomanderState.Inactive);

                if (_territoryState.PomanderOfIntuition == PomanderState.FoundOnCurrentFloor)
                    SetIntuition(PomanderState.Inactive);
            }
            else if (text.EndsWith(_localizedChatMessages.MapRevealed))
            {
                SetSight(PomanderState.Active);
            }
            else if (text.EndsWith(_localizedChatMessages.AllTrapsRemoved))
            {
                SetSight(PomanderState.PomanderOfSafetyUsed);
            }
            else if (text.EndsWith(_localizedChatMessages.HoardNotOnCurrentFloor) ||
                     text.EndsWith(_localizedChatMessages.HoardOnCurrentFloor))
            {
                // There is no functional difference between these - if you don't open the marked coffer,
                // going to higher floors will keep the pomander active.
                SetIntuition(PomanderState.Active);
            }
            else if (text.EndsWith(_localizedChatMessages.HoardCofferOpened))
            {
                SetIntuition(PomanderState.FoundOnCurrentFloor);
            }
        }

        /// <summary>
        /// 只在狀態真的改變時寫一行 Information。使用者跑 LogLevel 1,盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒;
        /// 而「同狀態重複印」會把真正的翻轉淹掉,所以兩邊都要顧。
        /// </summary>
        private void SetSight(PomanderState state)
        {
            if (_territoryState.PomanderOfSight == state)
                return;

            _logger.LogInformation("PalacePal:系統訊息回報「全景/咒印解除」狀態 {Old} -> {New}。",
                _territoryState.PomanderOfSight, state);
            _territoryState.PomanderOfSight = state;
        }

        private void SetIntuition(PomanderState state)
        {
            if (_territoryState.PomanderOfIntuition == state)
                return;

            _logger.LogInformation("PalacePal:系統訊息回報「感知寶藏」狀態 {Old} -> {New}。",
                _territoryState.PomanderOfIntuition, state);
            _territoryState.PomanderOfIntuition = state;
        }

        private LocalizedChatMessages LoadLanguageStrings()
        {
            return new LocalizedChatMessages
            {
                MapRevealed = GetLocalizedString(7256),
                AllTrapsRemoved = GetLocalizedString(7255),
                HoardOnCurrentFloor = GetLocalizedString(7272),
                HoardNotOnCurrentFloor = GetLocalizedString(7273),
                HoardCofferOpened = GetLocalizedString(7274),
                FloorChanged =
                    new Regex(GetFloorChangedRegex()),
            };
        }

        private string GetFloorChangedRegex()
        {
            //7270	57	33	0	False	Floor <Value>IntegerParameter(1)</Value>
            //7270	57	33	0	False	地下<Value>IntegerParameter(1)</Value>階
            //7270	57	33	0	False	Ebene <Value>IntegerParameter(1)</Value> betreten!
            //7270	57	33	0	False	Sous-sol <Value>IntegerParameter(1)</Value>
            //7270	57	33	0	False	地下<Value>IntegerParameter(1)</Value>层
            if (Svc.ClientState.ClientLanguage == ClientLanguage.English) return @"^Floor (\d+)";
            if (Svc.ClientState.ClientLanguage == ClientLanguage.Japanese) return @"^地下(\d+)階";
            if (Svc.ClientState.ClientLanguage == ClientLanguage.German) return @"^Ebene (\d+) betreten!";
            if (Svc.ClientState.ClientLanguage == ClientLanguage.French) return @"^Sous-sol (\d+)";
            // 中文客戶端:TC(台服)在新版 Dalamud 是 ClientLanguage.TraditionalChinese(7),
            // 舊版列舉沒有這個值、會回報 ChineseSimplified(4)。這裡用數值比較,才能同時相容
            // CI 釘住的舊 Dalamud 與執行期的新版。字元類同時吃簡體「层」與繁體「層」。
            //
            // ⚠️ 樓層訊息有兩個 LogMessage,不是只有一個:
            //   7270「地下UNKNOWN層」= 死者宮殿(往下走)
            //   9218「第UNKNOWN層」  = 天之御柱山／正統優雷卡(往上走)
            // 原本只吃 7270 的寫法在天之御柱山永遠不匹配。2026-08-15 實機 log 量測:
            // 190 則樓層訊息**全部**是「第N層」,0 則是「地下N層」(使用者當時在天之御柱山)。
            // 這裡兩種都吃 —— 多匹配的失敗方向是「把標記重新顯示出來」,屬於安全側。
            var languageValue = (int)Svc.ClientState.ClientLanguage;
            if (languageValue is 4 or 5 or 7) return @"^(?:地下|第)(\d+)[层層]";

            // 不要因為不認識的語言就丟例外——ChatService 是在 DI 建構期跑的,
            // 丟出去會讓整個外掛的 async 載入失敗(而不只是樓層偵測失效)。
            Svc.Log.Warning($"PalacePal: 未知的客戶端語言 {Svc.ClientState.ClientLanguage},停用樓層偵測。");
            return "(?!)"; // 永不匹配

        }

        private string GetLocalizedString(uint id)
        {
            return _dataManager.GetExcelSheet<LogMessage>().GetRow(id).Text.ToString() ?? "Unknown";
        }

        private string GetLocalizedExtractedString(uint id)
        {
            return _dataManager.GetExcelSheet<LogMessage>().GetRow(id).Text.ToDalamudString().GetText() ?? "Unknown";
        }

        private sealed class LocalizedChatMessages
        {
            public string MapRevealed { get; init; } = "???"; //"The map for this floor has been revealed!";
            public string AllTrapsRemoved { get; init; } = "???"; // "All the traps on this floor have disappeared!";
            public string HoardOnCurrentFloor { get; init; } = "???"; // "You sense the Accursed Hoard calling you...";

            public string HoardNotOnCurrentFloor { get; init; } =
                "???"; // "You do not sense the call of the Accursed Hoard on this floor...";

            public string HoardCofferOpened { get; init; } = "???"; // "You discover a piece of the Accursed Hoard!";

            public Regex FloorChanged { get; init; } =
                new(@"This isn't a game message, but will be replaced"); // new Regex(@"^Floor (\d+)$");
        }
    }
}
