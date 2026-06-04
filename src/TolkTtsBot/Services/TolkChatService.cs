using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

/// <summary>
/// Реализует протокол Контур.Толка (восстановлен из HAR-анализа).
///
/// Факты из HAR:
/// - Авторизация: POST /api/authorize/session → { token, anonymousId }
/// - WS connect/auth/chat_join работает правильно
/// - Сервер НЕ пушит новые chat_message через system/ws
/// - Новые сообщения получаются через REST polling:
///   GET /api/chat/{roomId}/messages?pageSize=30&channel=general
/// - Формат отправителя: createdBy.userInfo.firstname + surname
/// - Заголовок: X-Platform: web (обязателен)
/// </summary>
public sealed class TolkChatService : IAsyncDisposable
{
    private readonly HttpClient               _http;
    private readonly BotOptions               _opts;
    private readonly ILogger<TolkChatService> _log;

    private string? _signInToken;
    private string? _botAnonymousId;

    public TolkChatService(
        HttpClient http,
        IOptions<BotOptions> opts,
        ILogger<TolkChatService> log)
    {
        _http = http;
        _opts = opts.Value;
        _log  = log;
    }

    // ── Публичный метод ───────────────────────────────────────────────────────

    public async Task ListenAsync(
        string baseUrl,
        string roomId,
        Func<string, string, Task> onMessage,
        CancellationToken ct)
    {
        _log.LogInformation("[Chat] baseUrl={B} roomId={R}", baseUrl, roomId);

        // Авторизация
        _signInToken = await AuthorizeAsync(baseUrl, roomId, ct);
        if (_signInToken is null)
        {
            _log.LogError("[Chat] Авторизация не удалась");
            return;
        }

        // WS handshake (connect → auth → chat_join) — для присутствия в комнате
        _ = Task.Run(() => MaintainWebSocketAsync(baseUrl, roomId, ct), ct);

        // REST polling — единственный способ получать новые сообщения
        await PollChatAsync(baseUrl, roomId, onMessage, ct);
    }

    // ── Авторизация ───────────────────────────────────────────────────────────

    private async Task<string?> AuthorizeAsync(
        string baseUrl, string roomId, CancellationToken ct)
    {
        try
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var secret = new string(Enumerable.Range(0, 15)
                .Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());

            var url  = $"{baseUrl}/api/authorize/session";
            var body = new
            {
                name            = _opts.Name,
                anonymousSecret = secret,
                consentOnCreate = true
            };

            _log.LogInformation("[Auth] POST {U} name={N}", url, _opts.Name);

            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.TryAddWithoutValidation("Referer", $"{baseUrl}/{roomId}");
            req.Headers.TryAddWithoutValidation("Origin",  baseUrl);

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogError("[Auth] HTTP {S}: {E}", (int)resp.StatusCode,
                    err[..Math.Min(err.Length, 200)]);
                return null;
            }

            var result = await resp.Content.ReadFromJsonAsync<AuthResponse>(
                cancellationToken: ct);
            _botAnonymousId = result?.AnonymousId;
            _log.LogInformation("[Auth] ✓ token получен, anonymousId={A}",
                result?.AnonymousId?[..Math.Min(16, result.AnonymousId?.Length ?? 0)]);
            return result?.Token;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Auth] Исключение");
            return null;
        }
    }

    // ── REST polling — основной способ получать сообщения ────────────────────

    private async Task PollChatAsync(
        string baseUrl,
        string roomId,
        Func<string, string, Task> onMessage,
        CancellationToken ct)
    {
        _log.LogInformation("[Poll] Запуск polling чата room={R}", roomId);

        var seen      = new HashSet<string>(256);
        var url       = $"{baseUrl}/api/chat/{roomId}/messages?pageSize=30&channel=general";
        var interval  = 1500; // мс между запросами

        // Первый запрос — загружаем историю только чтобы заполнить seen
        // (не озвучиваем старые сообщения)
        await SeedSeenAsync(url, seen, ct);
        _log.LogInformation("[Poll] История загружена ({N} сообщений в seen)", seen.Count);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-Platform", "web");

                var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("[Poll] HTTP {S}", (int)resp.StatusCode);
                    await Task.Delay(interval * 2, ct);
                    continue;
                }

                var data = await resp.Content.ReadFromJsonAsync<ChatMessagesResponse>(
                    cancellationToken: ct);

                foreach (var msg in data?.Messages ?? [])
                {
                    if (string.IsNullOrEmpty(msg.Id) || !seen.Add(msg.Id)) continue;

                    // Чистим seen чтобы не рос бесконечно
                    if (seen.Count > 1000)
                    {
                        var stale = seen.Take(500).ToArray();
                        foreach (var k in stale) seen.Remove(k);
                    }

                    var text = msg.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(text)) continue;

                    // Имя отправителя из createdBy.userInfo (реальный формат из HAR)
                    var sender = BuildSenderName(msg);

                    // Не обрабатываем собственные сообщения
                    if (!string.IsNullOrEmpty(_botAnonymousId) &&
                        msg.CreatedBy?.AnonymousId == _botAnonymousId)
                    {
                        _log.LogDebug("[Poll] Пропуск: собственное сообщение");
                        continue;
                    }

                    _log.LogDebug("[Poll] [{S}] {T}", sender, text[..Math.Min(60, text.Length)]);
                    await onMessage(sender, text);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Poll] Ошибка");
                await Task.Delay(interval * 3, ct);
                continue;
            }

            await Task.Delay(interval, ct);
        }

        _log.LogInformation("[Poll] Остановлен");
    }

    private async Task SeedSeenAsync(string url, HashSet<string> seen, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Platform", "web");
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;
            var data = await resp.Content.ReadFromJsonAsync<ChatMessagesResponse>(
                cancellationToken: ct);
            foreach (var msg in data?.Messages ?? [])
                if (!string.IsNullOrEmpty(msg.Id)) seen.Add(msg.Id);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[Poll] Ошибка при загрузке истории"); }
    }

    private static string BuildSenderName(ChatMessage msg)
    {
        // Реальный формат из HAR: createdBy.userInfo.firstname + surname
        var ui = msg.CreatedBy?.UserInfo;
        if (ui is not null)
        {
            var full = $"{ui.Firstname} {ui.Surname}".Trim();
            if (!string.IsNullOrEmpty(full)) return full;
            if (!string.IsNullOrEmpty(ui.Login)) return ui.Login.Split('\\').Last();
        }
        // Fallback для анонимных пользователей
        if (!string.IsNullOrEmpty(msg.CreatedBy?.Name)) return msg.CreatedBy.Name;
        return "Участник";
    }

    // ── WebSocket — только для присутствия в комнате (пинги) ─────────────────

    private async Task MaintainWebSocketAsync(
        string baseUrl, string roomId, CancellationToken ct)
    {
        var wsBase = baseUrl
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://",  "ws://",  StringComparison.OrdinalIgnoreCase);
        var wsUrl = $"{wsBase}/system/ws";

        _log.LogInformation("[WS] Подключение: {U}", wsUrl);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin", baseUrl);
                ws.Options.SetRequestHeader("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");

                await ws.ConnectAsync(new Uri(wsUrl), ct);
                _log.LogInformation("[WS] ✓ Подключён");

                var sessionId = await WsHandshakeAsync(ws, roomId, ct);
                _log.LogInformation("[WS] ✓ Handshake завершён, sessionId={S}", sessionId);

                // Пинги каждые 30с
                await WsPingLoopAsync(ws, sessionId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning("[WS] Ошибка: {E}. Переподключение через 10с...", ex.Message);
                try { await Task.Delay(10000, ct); } catch { break; }
            }
        }
    }

    private async Task<string?> WsHandshakeAsync(
        ClientWebSocket ws, string roomId, CancellationToken ct)
    {
        string? sessionId = null;

        // connect
        await WsSendAsync(ws, new
        {
            a = "connect", reqId = NewReqId(),
            data = new { clientType = "Web", webAppVersion = "master-bot-1.0" }
        }, ct);
        var r1 = await WsReceiveAsync(ws, ct);
        if (r1?.RootElement.TryGetProperty("data", out var d1) == true &&
            d1.TryGetProperty("sessionId", out var s1))
            sessionId = s1.GetString();

        // message_subscribe
        await WsSendAsync(ws, new
        {
            a = "message_subscribe", reqId = NewReqId(),
            data = new { topic = "personal" }, session = sessionId
        }, ct);
        await WsReceiveAsync(ws, ct);

        // auth
        await WsSendAsync(ws, new
        {
            a = "auth", reqId = NewReqId(),
            data = new { signInToken = _signInToken }, session = sessionId
        }, ct);
        var r3 = await WsReceiveAsync(ws, ct);
        if (r3?.RootElement.TryGetProperty("data", out var d3) == true &&
            d3.TryGetProperty("sessionId", out var s3))
            sessionId = s3.GetString();

        // chat_join
        await WsSendAsync(ws, new
        {
            a = "chat_join", reqId = NewReqId(),
            data = new { name = roomId, popup = false, platform = "web" },
            session = sessionId
        }, ct);
        await WsReceiveAsync(ws, ct);

        // resource_active_get
        await WsSendAsync(ws, new
        {
            a = "resource_active_get", reqId = NewReqId(),
            data = new { roomName = roomId }, session = sessionId
        }, ct);
        await WsReceiveAsync(ws, ct);

        return sessionId;
    }

    private async Task WsPingLoopAsync(
        ClientWebSocket ws, string? sessionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(30_000, ct);
            if (ws.State != WebSocketState.Open) break;
            await WsSendAsync(ws, new { a = "ping", reqId = NewReqId(), session = sessionId }, ct);
            _log.LogDebug("[WS] ping");
            // Читаем ответ на ping
            try
            {
                using var pongCts = new CancellationTokenSource(5000);
                await WsReceiveAsync(ws, pongCts.Token);
            }
            catch { /* игнорируем */ }
        }
    }

    // ── WS helpers ────────────────────────────────────────────────────────────

    private static string NewReqId()
    {
        const string c = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, 10)
            .Select(_ => c[Random.Shared.Next(c.Length)]).ToArray());
    }

    private async Task WsSendAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        _log.LogDebug("[WS] send: {J}", json[..Math.Min(150, json.Length)]);
        await ws.SendAsync(Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonDocument?> WsReceiveAsync(
        ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new ArraySegment<byte>(new byte[65536]);
        using var ms = new System.IO.MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf.Array!, buf.Offset, result.Count);
        } while (!result.EndOfMessage);
        ms.Seek(0, System.IO.SeekOrigin.Begin);
        try { return await JsonDocument.ParseAsync(ms, cancellationToken: ct); }
        catch { return null; }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── DTO (реальная структура из HAR) ──────────────────────────────────────

    private sealed class AuthResponse
    {
        [JsonPropertyName("token")]       public string? Token       { get; init; }
        [JsonPropertyName("anonymousId")] public string? AnonymousId { get; init; }
    }

    private sealed class ChatMessagesResponse
    {
        [JsonPropertyName("messages")] public List<ChatMessage>? Messages { get; init; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("id")]        public string?    Id        { get; init; }
        [JsonPropertyName("text")]      public string?    Text      { get; init; }
        [JsonPropertyName("createdBy")] public CreatedBy? CreatedBy { get; init; }
    }

    private sealed class CreatedBy
    {
        // Авторизованный пользователь
        [JsonPropertyName("userInfo")]   public UserInfo? UserInfo   { get; init; }
        // Анонимный пользователь
        [JsonPropertyName("name")]       public string?   Name       { get; init; }
        [JsonPropertyName("anonymousId")] public string?  AnonymousId { get; init; }
    }

    private sealed class UserInfo
    {
        [JsonPropertyName("firstname")] public string? Firstname { get; init; }
        [JsonPropertyName("surname")]   public string? Surname   { get; init; }
        [JsonPropertyName("login")]     public string? Login     { get; init; }
    }
}
