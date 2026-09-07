using System;
using System.Collections.Concurrent;
using System.Threading;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Pal.Client.Properties;
using ECommons.DalamudServices.Legacy;

namespace Pal.Client.DependencyInjection
{
    internal sealed class Chat : IDisposable
    {
        private readonly IChatGui _chatGui;
        private readonly IFramework _framework;

        public Chat(IChatGui chatGui, IFramework framework)
        {
            _chatGui = chatGui;
            _framework = framework;
        }

        /// <summary>還沒送出的聊天輸出。<b>順序就是呼叫順序。</b></summary>
        /// <remarks>
        /// 靜態的：<c>Plugin</c> 在載入失敗那條路上會另外 <c>new Chat(...)</c>
        /// （<c>Plugin.cs</c> 的 <c>Error_LoadFailed</c>），共用一個佇列才不會分成兩串各自排序。
        /// <b>卸載時由 <see cref="Dispose"/> 排乾一次</b>：外掛卸載後整個 ALC 一起消失，
        /// 沒排掉的那幾則就永遠不會出現，而它們正好是「卸載前最後說的話」。
        /// </remarks>
        private static readonly ConcurrentQueue<Action> Pending = new();

        /// <summary>
        /// 把一次聊天輸出排進佇列，並要求在 framework 執行緒上排乾。
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
        /// 🔑 <b>為什麼要自己排一個佇列，而不是每一則各包一次
        /// <see cref="IFramework.RunOnFrameworkThread(Action)"/></b>：那樣每一則都是一個獨立的工作，
        /// 而排它們的 <c>ThreadBoundTaskScheduler</c> 用 <c>ConcurrentDictionary</c> 存待跑的工作、
        /// <c>Run()</c> 走訪它的 <c>Keys</c> ⇒ <b>不保證先進先出</b>。
        /// 連著送兩三則（例如匯入完成的統計、或錯誤訊息後面跟著說明）時，
        /// 出現在聊天視窗的順序可能與產生的順序不同。改成自己排隊、到了 framework 執行緒
        /// 一次排乾，順序就與呼叫順序逐字相同 —— 多排幾次也只是後面幾次發現佇列已經空了。
        ///
        /// ⚠️ 用 <c>RunOnFrameworkThread</c> 而不是 <c>Framework.Run</c>：
        /// 前者在「已經在 framework 執行緒上」時是<b>就地同步執行</b>
        /// （<c>Dalamud/Game/Framework.cs:171-188</c>），所以 UI 與指令回呼那些本來就在主執行緒上的
        /// 呼叫行為不會多等一幀；後者一律 <c>StartNew</c> 排隊。
        /// 這裡刻意不去等回傳的 Task —— 送出去就好，等它會把主執行緒卡在自己排的工作上。
        /// </remarks>
        private void OnFramework(Action print)
        {
            Pending.Enqueue(print);
            _ = _framework.RunOnFrameworkThread(Drain);
        }

        /// <summary>在 framework 執行緒上把 <see cref="Pending"/> 排乾。</summary>
        private static void Drain()
        {
            while (Pending.TryDequeue(out var pending))
            {
                // 一則印不出來不該擋住後面的。改動前每一則各自是一個 fire-and-forget 的
                // 工作，例外本來就跟著那個沒人等的 Task 被丟掉，這裡維持同樣的行為。
                try
                {
                    pending();
                }
                catch (Exception)
                {
                    // Swallowed, same as before.
                }
            }
        }

        /// <summary>卸載時把還沒送出的聊天輸出排乾。</summary>
        /// <remarks>
        /// 🔴 <b>沒有這一次排乾，卸載瞬間佇列裡的東西會靜默消失。</b>
        /// <see cref="OnFramework"/> 只是「排進佇列 ＋ 請 framework 執行緒有空時來排乾」，
        /// 兩件事之間隔著一次排程；背景執行緒（<c>Task.Run</c>、gRPC 接續）在卸載前最後一刻
        /// 排進來的那幾則，很可能還沒輪到那個工作跑，而外掛的 ALC 已經被拆掉了。
        ///
        /// 🔑 <b>這裡不需要退訂任何東西</b>：這個型別不掛 <c>Framework.Update</c>、不訂閱任何事件，
        /// 每一次 <see cref="OnFramework"/> 自帶一次 <c>RunOnFrameworkThread</c>。
        /// 唯一要收的尾就是這個共用佇列。
        ///
        /// 📌 <b>時序</b>：<c>PalacePal.json</c> 的 <c>CanUnloadAsync</c> 是 <c>false</c>，
        /// 所以 Dalamud 是用 <c>framework.RunOnFrameworkThread(inst.Dispose)</c> 呼叫外掛的
        /// <c>Dispose</c>（<c>Dalamud/Plugin/Internal/Types/LocalPlugin.cs:708-712</c>）
        /// ⇒ 走到這裡時<b>已經在 framework 執行緒上</b>，
        /// <c>RunOnFrameworkThread</c> 於是就地同步執行（<c>Framework.cs</c>），
        /// 排乾在 <c>Dispose</c> 回去之前就做完了，不是排一個沒人等的工作。
        /// ⚠️ 反過來說，萬一有人在別的執行緒上釋放它，這裡就退化成 fire-and-forget
        /// —— 那與改動前的行為相同（訊息可能還是掉），<b>不會卡住也不會死結</b>，
        /// 所以刻意不去等那個 Task。
        ///
        /// ⚠️ 佇列是靜態共用的，<b>排乾兩次是安全的</b>：第二次會發現它已經空了。
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _ = _framework.RunOnFrameworkThread(Drain);
        }

        /// <summary>只讓 <see cref="Dispose"/> 生效一次。<c>Interlocked</c> 存取。</summary>
        private int _disposed;

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
