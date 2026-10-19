using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pal.Client.Net.Palantir
{
    // 本檔假設與契約：
    // - 「測試連線」測的就是下載用的那個 PalantirClient（同一個網址）。
    // - 唯讀：只 GET /metadata；刻意不打 /auth（會建立或送出匿名帳號）、不 Subscribe、不送任何資料。
    // - 不依賴 Dalamud 與在地化資源（訊息格式由呼叫端傳入），可離線測。
    internal static class PalantirConnectionTest
    {
        internal sealed record Result(bool Success, string Server, string? ServerName, string? FailureReason, bool TimedOut);

        /// <summary>呼叫端的 <paramref name="cancellationToken"/> 被取消時照樣擲出（給「又按了一次」的舊請求用）。</summary>
        public static async Task<Result> RunAsync(PalantirClient client, CancellationToken cancellationToken)
        {
            try
            {
                PalantirServerMetadata metadata = await client.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
                return new Result(true, client.Server, metadata.Name, null, false);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient 自己的逾時是 TaskCanceledException（內含 TimeoutException），不是呼叫端取消。
                bool timedOut = e is TaskCanceledException or TimeoutException;
                return new Result(false, client.Server, null, e.Message, timedOut);
            }
        }

        /// <param name="timeoutFormat">{0}＝秒數。</param>
        public static string Reason(Result result, string timeoutFormat, int timeoutSeconds) =>
            result.TimedOut ? string.Format(timeoutFormat, timeoutSeconds) : result.FailureReason ?? "?";

        /// <param name="successFormat">{0}＝伺服器網址、{1}＝伺服器自報名稱。</param>
        /// <param name="failureFormat">{0}＝伺服器網址、{1}＝原因。</param>
        public static string Format(Result result, string successFormat, string failureFormat, string timeoutFormat,
            int timeoutSeconds) =>
            result.Success
                ? string.Format(successFormat, result.Server, string.IsNullOrWhiteSpace(result.ServerName) ? "?" : result.ServerName)
                : string.Format(failureFormat, result.Server, Reason(result, timeoutFormat, timeoutSeconds));
    }
}
