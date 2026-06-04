using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

/// <summary>
/// Реализует нативный протокол Контур.Толка (из HAR-анализа):
///   POST {baseUrl}/api/authorize/session  → { token, anonymousId }
///   WSS  {baseUrl}/system/ws             → connect → auth → chat_join → recv push events
/// </summary>
public sealed class TolkChatService : IAsyncDisposable
{
    private readonly HttpClient               _http;
    private readonly BotOptions               _opts;
    private readonly ILogger<TolkChatService> _log;

    private ClientWebSocket? _ws;
    private string?          _sessionId;
    private string?          _signInToken;
    private string?          _botAnonymousId;

    private static string NewReqId()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, 10)
            .Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }

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

    /// <summary>
    /// Авторизуется как гость и слушает push-сообщения чата.
    /// baseUrl извлекается из ссылки на комнату: https://host → wss://host/system/ws
    /// </summary>
    public async Task ListenAsync(
        string baseUrl,
        string roomId,
        Func<string, string, Task> onMessage,
        CancellationToken ct)
    {
        _log.LogInformation("[Chat] baseUrl={B} roomId={R}", baseUrl, roomId);

        _signInToken = await AuthorizeAsync(baseUrl, roomId, ct);
        if (_signInToken is null)
        {
            _log.LogError("[Chat] Авторизация не удалась");
            return;
        }

        await RunWebSocketAsync(baseUrl, roomId, onMessage, ct);
    }

    // ── Авторизация как гость ─────────────────────────────────────────────────

    private async Task<string?> AuthorizeAsync(
        string baseUrl, string roomId, CancellationToken ct)
    {
        try
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var secret = new string(Enumerable.Range(0, 15)
                .Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());

            var url  = $"{baseUrl}/api/authorize/session";
            var body = new { name = _opts.Name, anonymousSecret = secret, consentOnCreate = true };

            _log.LogInformation("[Auth] POST {U}", url);

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
            _log.LogInformation("[Auth] ✓ token={T}... anonymousId={A}...",
                result?.Token?[..Math.Min(8, result.Token?.Length ?? 0)],
                result?.AnonymousId?[..Math.Min(16, result.AnonymousId?.Length ?? 0)]);
            return result?.Token;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Auth] Исключение");
            return null;
        }
    }

    // ── WebSocket протокол ────────────────────────────────────────────────────

    private async Task RunWebSocketAsync(
        string baseUrl,
        string roomId,
        Func<string, string, Task> onMessage,
        CancellationToken ct)
    {
        var wsBase = baseUrl
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://",  "ws://",  StringComparison.OrdinalIgnoreCase);
        var wsUrl = $"{wsBase}/system/ws";

        _log.LogInformation("[WS] Подключение: {U}", wsUrl);

        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");
        _ws.Options.SetRequestHeader("Origin", baseUrl);

        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        _log.LogInformation("[WS] ✓ Подключён");

        // 1. connect
        await SendAsync(new
        {
            a     = "connect",
            reqId = NewReqId(),
            data  = new { clientType = "Web", webAppVersion = "master-bot-1.0" }
        }, ct);

        var doc1 = await ReceiveAsync(ct);
        if (doc1 is not null &&
            doc1.RootElement.TryGetProperty("data", out var d1) &&
            d1.TryGetProperty("sessionId", out var sid1))
            _sessionId = sid1.GetString();
        _log.LogInformation("[WS] sessionId={S}", _sessionId);

        // 2. message_subscribe
        await SendAsync(new
        {
            a       = "message_subscribe",
            reqId   = NewReqId(),
            data    = new { topic = "personal" },
            session = _sessionId
        }, ct);
        await ReceiveAsync(ct);

        // 3. auth
        await SendAsync(new
        {
            a       = "auth",
            reqId   = NewReqId(),
            data    = new { signInToken = _signInToken },
            session = _sessionId
        }, ct);
        var doc3 = await ReceiveAsync(ct);
        if (doc3 is not null &&
            doc3.RootElement.TryGetProperty("data", out var d3) &&
            d3.TryGetProperty("sessionId", out var sid3))
            _sessionId = sid3.GetString();
        _log.LogInformation("[WS] ✓ Авторизован sessionId={S}", _sessionId);

        // 4. chat_join
        await SendAsync(new
        {
            a       = "chat_join",
            reqId   = NewReqId(),
            data    = new { name = roomId, popup = false, platform = "web" },
            session = _sessionId
        }, ct);
        await ReceiveAsync(ct);
        _log.LogInformation("[WS] ✓ chat_join room={R}", roomId);

        // 5. resource_active_get
        await SendAsync(new
        {
            a       = "resource_active_get",
            reqId   = NewReqId(),
            data    = new { roomName = roomId },
            session = _sessionId
        }, ct);
        await ReceiveAsync(ct);

        _log.LogInformation("[WS] ✓ Слушаю сообщения...");

        // Ping + receive loop параллельно
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = PingLoopAsync(pingCts.Token);
        try
        {
            await ReceiveLoopAsync(onMessage, ct);
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask; } catch { }
        }
    }

    // ── Цикл приёма сообщений ─────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(
        Func<string, string, Task> onMessage,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var doc = await ReceiveAsync(ct);
                if (doc is null) continue;

                var root     = doc.RootElement;
                var action   = root.TryGetProperty("a",     out var ap) ? ap.GetString() : null;
                var hasReqId = root.TryGetProperty("reqId", out _);

                _log.LogDebug("[WS] recv a={A}: {J}", action,
                    root.ToString()[..Math.Min(150, root.ToString().Length)]);

                // Push: новое сообщение чата
                if (action == "chat_message" &&
                    root.TryGetProperty("data", out var chatData))
                {
                    await HandleMessageAsync(chatData, onMessage);
                    continue;
                }

                // Push без reqId — возможно входящее событие
                if (!hasReqId && root.TryGetProperty("data", out var pushData))
                {
                    if (pushData.TryGetProperty("text", out _) ||
                        pushData.TryGetProperty("messages", out _))
                        await HandleMessageAsync(pushData, onMessage);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (WebSocketException wex)
            {
                _log.LogWarning("[WS] Ошибка соединения: {E}", wex.Message);
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[WS] Ошибка приёма");
                await Task.Delay(500, ct);
            }
        }
        _log.LogInformation("[WS] Цикл завершён (state={S})", _ws?.State.ToString() ?? "null");
    }

    private async Task HandleMessageAsync(
        JsonElement data,
        Func<string, string, Task> onMessage)
    {
        try
        {
            var text = data.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) return;

            // Имя отправителя
            var sender = "";
            if (data.TryGetProperty("sender", out var sp))
            {
                var first   = sp.TryGetProperty("firstname", out var fn) ? fn.GetString() ?? "" : "";
                var surname = sp.TryGetProperty("surname",   out var sn) ? sn.GetString() ?? "" : "";
                var name    = sp.TryGetProperty("name",      out var nm) ? nm.GetString() ?? "" : "";
                sender = $"{first} {surname}".Trim();
                if (string.IsNullOrEmpty(sender)) sender = name;
            }
            if (string.IsNullOrEmpty(sender) &&
                data.TryGetProperty("senderName", out var snp))
                sender = snp.GetString() ?? "";
            if (string.IsNullOrEmpty(sender)) sender = "Участник";

            // Не озвучиваем собственные сообщения
            if (!string.IsNullOrEmpty(_botAnonymousId) &&
                data.TryGetProperty("senderId", out var sidp) &&
                sidp.GetString() == _botAnonymousId)
            {
                _log.LogDebug("[Chat] Пропуск: собственное");
                return;
            }

            _log.LogDebug("[Chat] [{S}] {T}", sender, text[..Math.Min(60, text.Length)]);
            await onMessage(sender, text);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[Chat] Ошибка обработки"); }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(30_000, ct);
                if (_ws?.State == WebSocketState.Open && _sessionId is not null)
                {
                    await SendAsync(new { a = "ping", reqId = NewReqId(), session = _sessionId }, ct);
                    _log.LogDebug("[WS] ping");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogDebug("[WS] ping err: {E}", ex.Message); }
        }
    }

    // ── WS helpers ────────────────────────────────────────────────────────────

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;
        var json  = JsonSerializer.Serialize(payload);
        _log.LogDebug("[WS] send: {J}", json[..Math.Min(150, json.Length)]);
        await _ws.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text, true, ct);
    }

    private async Task<JsonDocument?> ReceiveAsync(CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return null;

        var buffer = new ArraySegment<byte>(new byte[65536]);
        using var ms = new System.IO.MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _log.LogWarning("[WS] Сервер закрыл соединение");
                return null;
            }
            ms.Write(buffer.Array!, buffer.Offset, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, System.IO.SeekOrigin.Begin);
        try   { return await JsonDocument.ParseAsync(ms, cancellationToken: ct); }
        catch (JsonException ex) { _log.LogDebug("[WS] JSON err: {E}", ex.Message); return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
            _ws.Dispose();
        }
    }

    // ── DTO ───────────────────────────────────────────────────────────────────

    private sealed class AuthResponse
    {
        [JsonPropertyName("token")]       public string? Token       { get; init; }
        [JsonPropertyName("anonymousId")] public string? AnonymousId { get; init; }
    }
}
