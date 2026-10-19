using System;
using System.Collections.Generic;
using Pal.Client.Database;

namespace Pal.Client.Floors
{
    /// <summary>
    /// A <see cref="ClientLocation"/> loaded in memory, with certain extra attributes as needed.
    /// </summary>
    internal sealed class PersistentLocation : MemoryLocation
    {
        /// <see cref="ClientLocation.LocalId"/>
        public int? LocalId { get; set; }

        /// <summary>
        /// Network id for the server you're currently connected to.
        /// </summary>
        public Guid? NetworkId { get; set; }

        /// <summary>
        /// For markers that the server you're connected to doesn't know: Whether this was requested to be uploaded, to avoid duplicate requests.
        /// </summary>
        public bool UploadRequested { get; set; }

        /// <see cref="ClientLocation.RemoteEncounters"/>
        ///
        public List<string> RemoteSeenOn { get; set; } = new();

        /// <summary>
        /// Whether this marker was requested to be seen, to avoid duplicate requests.
        /// </summary>
        public bool RemoteSeenRequested { get; set; }

        public ClientLocation.ESource Source { get; init; }

        /// <summary>上次重建圖層時，我方確認資料庫已有這一點（改由實色層畫，淡層跳過）。</summary>
        public bool CoveredByConfirmed { get; set; }

        /// <summary>
        /// Palantir 回報的確認次數；-1＝不知道（這次執行還沒下載到這一點）。只存在記憶體，每次進區下載時更新。
        /// framework 執行緒寫、IPC 執行緒讀（int 讀寫不會撕裂）。
        /// </summary>
        public int CommunityConfirmations { get; set; } = -1;

        /// <summary>上次重建圖層時被回報次數門檻擋下（沒有畫，就地改色也跳過）。</summary>
        public bool HiddenByThreshold { get; set; }

        /// <summary>依目前設定的門檻，這一點該不該藏起來（畫面與 IPC 共用同一個判斷）。</summary>
        public bool IsHiddenByCommunityThreshold()
            => CommunityThreshold.Hides(Source == ClientLocation.ESource.Download, Seen, CoveredByConfirmed,
                CommunityConfirmations,
                Type == EType.Hoard ? P.Config.CommunityHoardThreshold : P.Config.CommunityTrapThreshold);

        public override bool Equals(object? obj) => obj is PersistentLocation && base.Equals(obj);

        public override int GetHashCode() => base.GetHashCode();

        public static bool operator ==(PersistentLocation? a, object? b)
        {
            return Equals(a, b);
        }

        public static bool operator !=(PersistentLocation? a, object? b)
        {
            return !Equals(a, b);
        }

        public override string ToString()
        {
            return $"PersistentLocation(Position={Position}, Type={Type})";
        }
    }
}
