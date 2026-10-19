namespace Pal.Client.Floors
{
    /// <summary>
    /// Palantir 下載點的回報次數門檻（移植自 Palantir 的 Integrity）。
    /// 只擋「下載來、自己沒看過、也不在我方確認資料庫」而且次數已知的點；次數不知道（-1）照常顯示。
    /// </summary>
    internal static class CommunityThreshold
    {
        public const int Max = 10;

        public static bool Hides(bool fromDownload, bool seen, bool coveredByConfirmed, int confirmations, int threshold)
            => fromDownload && !seen && !coveredByConfirmed && confirmations >= 0 && confirmations < threshold;
    }
}
