using System;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Pal.Client.Properties;
using ECommons.DalamudServices.Legacy;

namespace Pal.Client.DependencyInjection
{
    internal sealed class Chat
    {
        private readonly IChatGui _chatGui;
        private readonly IFramework _framework;

        public Chat(IChatGui chatGui, IFramework framework)
        {
            _chatGui = chatGui;
            _framework = framework;
        }

        /// <summary>
        /// 把一次聊天輸出送到 framework 執行緒上執行。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>IChatGui</c> 的佇列不是執行緒安全的：本 pin 的 <c>ChatGui.chatQueue</c> 是裸的
        /// <c>Queue&lt;XivChatEntry&gt;</c>（<c>Dalamud/Game/Gui/ChatGui.cs:43</c>），
        /// <c>Print</c> 只做一次 <c>Enqueue</c>、零同步（<c>:119-121</c>），
        /// 而 <c>UpdateQueue</c> 在 framework 執行緒 <c>TryDequeue</c>（<c>:205-214</c>）。
        /// PalacePal 有一半以上的聊天輸出來自 <c>Task.Run</c> 或 gRPC <c>await</c> 之後的接續，
        /// 那些跟 framework 迴圈是並行的 —— 失敗形式不是「訊息晚一點出現」，是佇列本身壞掉。
        ///
        /// 🔑 marshal 做在包裝層而不是各個呼叫點：這個型別是全外掛唯一碰 <see cref="IChatGui"/>
        /// 的地方，一處收口就涵蓋所有呼叫端，日後新增的呼叫點也自動安全。
        ///
        /// ⚠️ 用 <see cref="IFramework.RunOnFrameworkThread(Action)"/> 而不是 <c>Framework.Run</c>：
        /// 前者在「已經在 framework 執行緒上」時是<b>就地同步執行</b>
        /// （<c>Dalamud/Game/Framework.cs:171-188</c>），所以 UI 與指令回呼那些本來就在主執行緒上的
        /// 呼叫行為完全不變、不會多等一幀；後者一律 <c>StartNew</c> 排隊。
        /// 這裡刻意不去等回傳的 Task —— 送出去就好，等它會把主執行緒卡在自己排的工作上。
        /// </remarks>
        private void OnFramework(Action print)
            => _framework.RunOnFrameworkThread(print);

        public void Error(string e)
        {
            // 訊息本身仍然在呼叫端的執行緒上組好（SeStringBuilder 只碰自己的緩衝區），
            // 送去 framework 執行緒的只有那一次 Print。
            var entry = new XivChatEntry
            {
                Message = new SeStringBuilder()
                    .AddUiForeground($"[{Localization.Palace_Pal}] ", 16)
                    .AddText(e).Build(),
                Type = XivChatType.Urgent
            };

            OnFramework(() => _chatGui.PrintChat(entry));
        }

        public void Message(string message)
        {
            var built = new SeStringBuilder()
                .AddUiForeground($"[{Localization.Palace_Pal}] ", 57)
                .AddText(message).Build();

            OnFramework(() => _chatGui.Print(built));
        }

        public void UnformattedMessage(string message)
            => OnFramework(() => _chatGui.Print(message));
    }
}
