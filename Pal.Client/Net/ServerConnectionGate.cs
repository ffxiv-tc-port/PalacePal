using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pal.Client.Net
{
    internal enum EServerState
    {
        NotAttempted,
        Connected,
        Unreachable,
    }

    /// <summary>不可變快照：背景連線工作整份替換，繪製執行緒只讀參考。</summary>
    internal sealed record ServerStatus(
        EServerState State,
        DateTime? LastAttemptUtc,
        string? Reason,
        DateTime? RetryAfterUtc)
    {
        public static readonly ServerStatus Initial = new(EServerState.NotAttempted, null, null, null);
    }

    /// <summary>
    /// 連線失敗後的退避與「同一段連續失敗只報一次」的簿記。不依賴 Dalamud，可離線測。
    /// 執行緒：Record*／ShouldSkip 可從任何背景工作呼叫；鎖內只改欄位，不記 log、不碰 UI。
    /// </summary>
    internal sealed class ServerConnectionGate
    {
        public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(10);

        private readonly object _lock = new();
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _backoff;
        private bool _inFailureStreak;
        private volatile ServerStatus _status = ServerStatus.Initial;

        public ServerConnectionGate(TimeSpan? backoff = null, Func<DateTime>? utcNow = null)
        {
            _backoff = backoff ?? DefaultBackoff;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public ServerStatus Status => _status;

        /// <summary>自動（非手動測試）的連線嘗試是否該跳過：仍在退避期內就回 true。</summary>
        public bool ShouldSkipAutomaticAttempt()
        {
            ServerStatus s = _status;
            return s.State == EServerState.Unreachable && s.RetryAfterUtc is { } until && _utcNow() < until;
        }

        /// <returns>true＝這次失敗開啟新的一段連續失敗（呼叫端只在這時寫 Warning）。</returns>
        public bool RecordFailure(string reason)
        {
            lock (_lock)
            {
                DateTime now = _utcNow();
                bool newStreak = !_inFailureStreak;
                _inFailureStreak = true;
                _status = new ServerStatus(EServerState.Unreachable, now, reason, now + _backoff);
                return newStreak;
            }
        }

        /// <returns>true＝剛從一段連續失敗恢復。</returns>
        public bool RecordSuccess()
        {
            lock (_lock)
            {
                bool recovered = _inFailureStreak;
                _inFailureStreak = false;
                _status = new ServerStatus(EServerState.Connected, _utcNow(), null, null);
                return recovered;
            }
        }

        /// <summary>
        /// 跑 <paramref name="operation"/>，超過 <paramref name="timeout"/> 就取消並回 false。
        /// 外部 token 被取消時照常擲 <see cref="OperationCanceledException"/>（那不是逾時）。
        /// </summary>
        public static async Task<bool> RunWithTimeout(Func<CancellationToken, Task> operation, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                await operation(cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && cts.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
