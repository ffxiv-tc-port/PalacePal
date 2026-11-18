using System;
using System.Collections.Generic;
using Pal.Client.DependencyInjection;
using Pal.Client.Properties;

namespace Pal.Client.Commands
{
    /// <summary>全體統計只存在於已停用的舊伺服器；保留子指令是為了回說明，而不是讓舊使用者看到「未知子指令」。</summary>
    internal sealed class PalStatsCommand : ISubCommand
    {
        private readonly Chat _chat;

        public PalStatsCommand(Chat chat)
        {
            _chat = chat;
        }

        public IReadOnlyDictionary<string, Action<string>> GetHandlers()
            => new Dictionary<string, Action<string>>
            {
                { "stats", _ => Execute() },
            };

        private void Execute()
            => _chat.Message(Localization.Command_pal_stats_Retired);
    }
}
