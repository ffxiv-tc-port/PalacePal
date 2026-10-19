// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026, Puni.sh — 驗證流程（/metadata、/auth、JWT 到期）移植自 Palantir（https://github.com/PunishXIV/Palantir）Palantir.Client/Network.cs。
// 授權條款與免責聲明全文：同目錄 LICENSE-Palantir.txt。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pal.Client.Net.Palantir
{
    // 本檔假設與契約：
    // - 只下載。能送到伺服器的只有 GET /metadata、POST /auth（匿名帳號）、hub 協商，與 hub 方法 Subscribe；
    //   沒有任何能呼叫別的 hub 方法的入口（目標名是常數，不接受外部字串）。
    // - 每次下載都是「連線 → Subscribe → 拿到回覆 → 關閉」，不保持長連線，所以不會收到即時推送。
    // - 不依賴 Dalamud，可離線測；任何執行緒都可呼叫，帳號權杖的快取由鎖保護。
    internal sealed class PalantirClient : IDisposable
    {
        public const string SubscribeTarget = "Subscribe";

        private const byte RecordSeparator = 0x1E;
        private const int MaxMessageBytes = 32 * 1024 * 1024;
        private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _http;
        private readonly string _root;
        private readonly SemaphoreSlim _authLock = new(1, 1);
        private PalantirAuthResponse? _auth;
        private DateTime _authExpiresUtc;

        public PalantirClient(string server, HttpMessageHandler? handler = null)
        {
            _root = server.TrimEnd('/');
            _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public string Server => _root;

        public TimeSpan RequestTimeout => _http.Timeout;

        public void Dispose()
        {
            _http.Dispose();
            _authLock.Dispose();
        }

        public async Task<PalantirServerMetadata> GetMetadataAsync(CancellationToken cancellationToken)
        {
            var metadata = await _http.GetFromJsonAsync<PalantirServerMetadata>($"{_root}/metadata", Json, cancellationToken)
                .ConfigureAwait(false) ?? throw new PalantirException("伺服器沒有回傳 metadata");
            if (metadata.ProtocolVersion != PalantirProtocol.Version)
                throw new PalantirException(
                    $"伺服器協定版本 v{metadata.ProtocolVersion}，本插件只支援 v{PalantirProtocol.Version}");
            return metadata;
        }

        /// <param name="accountId">已存的匿名帳號；null＝請伺服器建一個新的。</param>
        public async Task<PalantirAuthResponse> AuthenticateAsync(Guid? accountId, CancellationToken cancellationToken)
        {
            await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_auth != null && (accountId == null || accountId == _auth.AccountId) &&
                    DateTime.UtcNow < _authExpiresUtc - TokenRefreshMargin)
                    return _auth;

                using var response = await _http.PostAsJsonAsync($"{_root}/auth", new PalantirAuthRequest(accountId), Json,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new PalantirException(
                        $"驗證失敗（HTTP {(int)response.StatusCode}）：{Detail(body) ?? response.ReasonPhrase}");
                }

                var auth = await response.Content.ReadFromJsonAsync<PalantirAuthResponse>(Json, cancellationToken)
                    .ConfigureAwait(false);
                if (auth == null || string.IsNullOrEmpty(auth.Token) || auth.AccountId == Guid.Empty)
                    throw new PalantirException("驗證沒有回傳帳號或權杖");

                _auth = auth;
                _authExpiresUtc = Expiry(auth.Token);
                return auth;
            }
            finally
            {
                _authLock.Release();
            }
        }

        /// <summary>連上 hub、呼叫 Subscribe(territory)、取回該區域全部的點後關閉連線。</summary>
        public async Task<IReadOnlyList<PalantirMarker>> SubscribeAsync(ushort territory, string token,
            CancellationToken cancellationToken)
        {
            string connectionToken = await NegotiateAsync(token, cancellationToken).ConfigureAwait(false);

            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            await socket.ConnectAsync(HubSocketUri(_root, connectionToken), cancellationToken).ConfigureAwait(false);
            try
            {
                return await RunSubscribeAsync(socket, territory, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await CloseQuietly(socket).ConfigureAwait(false);
            }
        }

        private async Task<string> NegotiateAsync(string token, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_root}/hub/negotiate?negotiateVersion=1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new ByteArrayContent(Array.Empty<byte>());
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new PalantirException($"hub 協商失敗（HTTP {(int)response.StatusCode}）：{Detail(body) ?? response.ReasonPhrase}");

            return ParseNegotiateResponse(body);
        }

        internal static string ParseNegotiateResponse(string body)
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new PalantirException($"hub 協商被拒：{error.GetString()}");
            if (root.TryGetProperty("url", out _))
                throw new PalantirException("hub 協商要求轉址，不支援");

            bool webSockets = false;
            if (root.TryGetProperty("availableTransports", out var transports) && transports.ValueKind == JsonValueKind.Array)
                foreach (var t in transports.EnumerateArray())
                    if (t.TryGetProperty("transport", out var name) && name.GetString() == "WebSockets")
                        webSockets = true;
            if (!webSockets)
                throw new PalantirException("伺服器沒有提供 WebSocket 傳輸");

            string? id = root.TryGetProperty("connectionToken", out var tokenElement) ? tokenElement.GetString()
                : root.TryGetProperty("connectionId", out var idElement) ? idElement.GetString() : null;
            return string.IsNullOrEmpty(id) ? throw new PalantirException("hub 協商沒有回傳連線代碼") : id;
        }

        internal static Uri HubSocketUri(string root, string connectionToken)
        {
            var builder = new UriBuilder($"{root}/hub")
            {
                Query = "id=" + Uri.EscapeDataString(connectionToken),
            };
            builder.Scheme = builder.Scheme == Uri.UriSchemeHttp ? "ws" : "wss";
            builder.Port = builder.Uri.IsDefaultPort ? -1 : builder.Port;
            return builder.Uri;
        }

        internal static byte[] HandshakeRequest() => Record("{\"protocol\":\"json\",\"version\":1}");

        internal static byte[] SubscribeInvocation(string invocationId, ushort territory)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("type", 1);
                writer.WriteString("invocationId", invocationId);
                writer.WriteString("target", SubscribeTarget);
                writer.WriteStartArray("arguments");
                writer.WriteNumberValue(territory);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            stream.WriteByte(RecordSeparator);
            return stream.ToArray();
        }

        private static byte[] Record(string json)
        {
            byte[] bytes = new byte[Encoding.UTF8.GetByteCount(json) + 1];
            Encoding.UTF8.GetBytes(json, bytes);
            bytes[^1] = RecordSeparator;
            return bytes;
        }

        private static async Task<IReadOnlyList<PalantirMarker>> RunSubscribeAsync(ClientWebSocket socket, ushort territory,
            CancellationToken cancellationToken)
        {
            const string invocationId = "0";
            await socket.SendAsync(HandshakeRequest(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            var records = new RecordBuffer();
            byte[] chunk = new byte[16 * 1024];
            bool handshakeDone = false;
            while (true)
            {
                while (records.TryTake(out byte[] record))
                {
                    if (!handshakeDone)
                    {
                        ThrowIfHandshakeFailed(record);
                        handshakeDone = true;
                        await socket.SendAsync(SubscribeInvocation(invocationId, territory), WebSocketMessageType.Text, true,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (TryReadCompletion(record, invocationId, out IReadOnlyList<PalantirMarker>? markers))
                        return markers!;
                }

                var result = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new PalantirException($"伺服器關閉了連線（{result.CloseStatus} {result.CloseStatusDescription}）");
                records.Append(chunk.AsSpan(0, result.Count));
            }
        }

        internal static void ThrowIfHandshakeFailed(byte[] record)
        {
            using var doc = JsonDocument.Parse(record);
            if (doc.RootElement.TryGetProperty("error", out var error))
                throw new PalantirException($"hub 握手失敗：{error.GetString()}");
        }

        /// <returns>true＝這是我們那次呼叫的回覆；其餘訊息（推送、ping）一律略過。</returns>
        internal static bool TryReadCompletion(byte[] record, string invocationId, out IReadOnlyList<PalantirMarker>? markers)
        {
            markers = null;
            using var doc = JsonDocument.Parse(record);
            JsonElement root = doc.RootElement;
            int type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetInt32() : 0;
            if (type == 7)
            {
                string? reason = root.TryGetProperty("error", out var closeError) ? closeError.GetString() : null;
                throw new PalantirException($"伺服器結束了連線：{reason ?? "（沒有說明）"}");
            }

            if (type != 3 || !root.TryGetProperty("invocationId", out var id) || id.GetString() != invocationId)
                return false;

            if (root.TryGetProperty("error", out var error))
                throw new PalantirException($"伺服器回覆錯誤：{error.GetString()}");

            markers = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array
                ? result.Deserialize<PalantirMarker[]>(Json) ?? Array.Empty<PalantirMarker>()
                : Array.Empty<PalantirMarker>();
            return true;
        }

        private static async Task CloseQuietly(ClientWebSocket socket)
        {
            if (socket.State != WebSocketState.Open)
                return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 已經拿到資料，關不乾淨不影響結果；ClientWebSocket.Dispose 會收尾。
            }
        }

        private static string? Detail(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static DateTime Expiry(string jwt)
        {
            try
            {
                string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
                byte[] json = Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '='));
                using var doc = JsonDocument.Parse(json);
                return DateTimeOffset.FromUnixTimeSeconds(doc.RootElement.GetProperty("exp").GetInt64()).UtcDateTime;
            }
            catch (Exception)
            {
                // 讀不出到期時間就當作快過期：下一次下載重新驗證。
                return DateTime.UtcNow + TokenRefreshMargin;
            }
        }

        internal sealed class RecordBuffer
        {
            private byte[] _buffer = new byte[16 * 1024];
            private int _length;

            public void Append(ReadOnlySpan<byte> data)
            {
                if (_length + data.Length > MaxMessageBytes)
                    throw new PalantirException($"伺服器訊息超過 {MaxMessageBytes / 1024 / 1024} MB");
                if (_length + data.Length > _buffer.Length)
                    Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
                data.CopyTo(_buffer.AsSpan(_length));
                _length += data.Length;
            }

            public bool TryTake(out byte[] record)
            {
                int end = _buffer.AsSpan(0, _length).IndexOf(RecordSeparator);
                if (end < 0)
                {
                    record = Array.Empty<byte>();
                    return false;
                }

                record = _buffer.AsSpan(0, end).ToArray();
                int rest = _length - end - 1;
                _buffer.AsSpan(end + 1, rest).CopyTo(_buffer);
                _length = rest;
                return true;
            }
        }
    }

    internal sealed class PalantirException : Exception
    {
        public PalantirException(string message) : base(message)
        {
        }
    }
}
