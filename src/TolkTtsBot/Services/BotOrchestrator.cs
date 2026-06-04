using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TolkTtsBot.Hubs;
using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

public sealed class BotOrchestrator : IAsyncDisposable
{
    private readonly ITtsService                        _tts;
    private readonly IBrowserService                    _browser;
    private readonly TolkChatService                    _chat;
    private readonly BotOptions                         _opts;
    private readonly IHubContext<BotHub, IBotHubClient> _hub;
    private readonly ILogger<BotOrchestrator>           _log;

    public BotState State { get; } = new();

    private Channel<TtsQueueItem>?   _queue;
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, DateTimeOffset> _spamFilter = new();

    public BotOrchestrator(
        ITtsService tts,
        IBrowserService browser,
        TolkChatService chat,
        IOptions<BotOptions> opts,
        IHubContext<BotHub, IBotHubClient> hub,
        ILogger<BotOrchestrator> log)
    {
        _tts     = tts;
        _browser = browser;
        _chat    = chat;
        _opts    = opts.Value;
        _hub     = hub;
        _log     = log;
    }

    // ── Управление ────────────────────────────────────────────────────────────

    public async Task<bool> StartAsync(string roomUrl)
    {
        if (State.Status is BotStatus.Running or BotStatus.Connecting)
        {
            _log.LogWarning("Бот уже запущен");
            return false;
        }

        if (!TryParseRoomUrl(roomUrl, out var baseUrl, out var roomId))
        {
            await SetErrorAsync($"Некорректная ссылка: {roomUrl}");
            return false;
        }

        State.RoomUrl         = roomUrl;
        State.RoomId          = roomId;
        State.StartedAt       = DateTimeOffset.UtcNow;
        State.MessagesSpoken  = 0;
        State.ReconnectCount  = 0;
        State.TtsCommandCount = 0;
        State.ErrorMessage    = null;

        _cts = new CancellationTokenSource();
        _queue = Channel.CreateBounded<TtsQueueItem>(new BoundedChannelOptions(_opts.QueueCapacity)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        await SetStatusAsync(BotStatus.Connecting);
        PushLog("Info", $"Комната: {roomUrl}");
        PushLog("Info", $"baseUrl={baseUrl} | roomId={roomId} | команда={_opts.TtsCommand}");

        _ = RunLoopAsync(roomUrl, baseUrl, roomId, _cts.Token);
        return true;
    }

    public async Task StopAsync()
    {
        if (State.Status == BotStatus.Stopped) return;
        PushLog("Info", "Остановка...");
        _cts?.Cancel();
        await _browser.LeaveRoomAsync();
        await SetStatusAsync(BotStatus.Stopped);
        PushLog("Info", "Бот остановлен");
    }

    // ── Основной цикл ─────────────────────────────────────────────────────────

    private async Task RunLoopAsync(
        string roomUrl, string baseUrl, string roomId, CancellationToken ct)
    {
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            if (attempt > 1)
            {
                await SetStatusAsync(BotStatus.Reconnecting);
                State.ReconnectCount++;
                PushLog("Warning", $"Переподключение #{State.ReconnectCount}...");
                await PushStatusAsync();
                try { await Task.Delay(_opts.ReconnectDelaySeconds * 1000, ct); }
                catch (OperationCanceledException) { break; }
            }

            if (_opts.MaxReconnectAttempts > 0 && attempt > _opts.MaxReconnectAttempts)
            {
                await SetErrorAsync($"Превышено число попыток: {_opts.MaxReconnectAttempts}");
                break;
            }

            // Ждём TTS sidecar
            if (!await WaitForSidecarAsync(ct)) break;

            // Браузер входит в комнату для WebRTC аудио
            PushLog("Info", "Открываю комнату в браузере...");
            var joined = await _browser.JoinRoomAsync(
                roomUrl, _opts.Name, ct,
                onLog: msg => PushLog("Info", msg));
            if (!joined)
                PushLog("Warning", "Браузер не вошёл в комнату — аудио недоступно, чат работает");

            await SetStatusAsync(BotStatus.Running);
            PushLog("Info", $"✓ Слушаю команду \"{_opts.TtsCommand}\" через WebSocket");

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await Task.WhenAny(
                    _chat.ListenAsync(baseUrl, roomId, OnChatMessageAsync, sessionCts.Token),
                    RunTtsWorkerAsync(sessionCts.Token)
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                PushLog("Warning", $"Сессия прервана: {ex.Message}");
                _log.LogWarning(ex, "Сессия прервана");
            }
            finally { sessionCts.Cancel(); }
        }

        if (!ct.IsCancellationRequested)
            await SetStatusAsync(BotStatus.Stopped);
    }

    // ── Фильтр /tts ───────────────────────────────────────────────────────────

    private async Task OnChatMessageAsync(string sender, string rawText)
    {
        // Проверяем команду /tts (без учёта регистра)
        if (!TryExtractTtsText(rawText, out var ttsText))
        {
            _log.LogDebug("Нет {C}: {T}", _opts.TtsCommand,
                rawText[..Math.Min(40, rawText.Length)]);
            return;
        }

        if (string.IsNullOrWhiteSpace(ttsText))
        {
            PushLog("Info", $"{_opts.TtsCommand} без текста от {sender}");
            return;
        }

        // Не озвучиваем себя
        if (string.Equals(sender.Trim(), _opts.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        // Спам-фильтр
        var now = DateTimeOffset.UtcNow;
        var key = sender.ToLowerInvariant();
        if (_spamFilter.TryGetValue(key, out var last) &&
            (now - last).TotalSeconds < _opts.SpamCooldownSeconds)
        {
            _log.LogDebug("Спам: {S}", sender);
            return;
        }
        _spamFilter[key] = now;

        // Ограничение длины
        if (ttsText.Length > _opts.MaxMessageLength)
            ttsText = ttsText[.._opts.MaxMessageLength] + "... обрезано";

        ttsText = SanitizeText(ttsText);
        if (string.IsNullOrWhiteSpace(ttsText)) return;

        var phrase = $"{sender} сказал: {ttsText}";
        _log.LogInformation("[TTS] {S} → {T}", sender, ttsText);
        State.TtsCommandCount++;

        var item = new TtsQueueItem(phrase, sender, ttsText, DateTimeOffset.UtcNow);
        if (_queue is not null && _queue.Writer.TryWrite(item))
        {
            State.QueueLength = _queue.Reader.Count;
            await _hub.Clients.All.TtsCommandReceived(sender, ttsText);
            await PushStatusAsync();
        }
        else PushLog("Warning", "Очередь TTS переполнена");
    }

    private bool TryExtractTtsText(string raw, out string ttsText)
    {
        ttsText = "";
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith(_opts.TtsCommand, StringComparison.OrdinalIgnoreCase))
            return false;
        ttsText = trimmed[_opts.TtsCommand.Length..].Trim();
        return true;
    }

    // ── TTS воркер ────────────────────────────────────────────────────────────

    private async Task RunTtsWorkerAsync(CancellationToken ct)
    {
        if (_queue is null) return;
        _log.LogInformation("TTS воркер запущен");

        await foreach (var item in _queue.Reader.ReadAllAsync(ct))
        {
            State.QueueLength = _queue.Reader.Count;
            await PushStatusAsync();

            try
            {
                _log.LogInformation("Озвучиваю: {P}", item.Phrase[..Math.Min(100, item.Phrase.Length)]);
                var wav = await _tts.SynthesizeAsync(item.Phrase, ct);
                await _browser.InjectAudioAsync(wav, ct);
                State.MessagesSpoken++;
                PushLog("Info", $"✓ [{item.Sender}]: {item.OriginalText[..Math.Min(50, item.OriginalText.Length)]}");
                await PushStatusAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка TTS");
                PushLog("Error", $"TTS ошибка: {ex.Message}");
            }
        }
        _log.LogInformation("TTS воркер остановлен");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> WaitForSidecarAsync(CancellationToken ct)
    {
        PushLog("Info", "Ожидание TTS sidecar...");
        for (int i = 0; i < 30; i++)
        {
            if (ct.IsCancellationRequested) return false;
            if (await _tts.IsHealthyAsync(ct))
            {
                PushLog("Info", "TTS sidecar готов ✓");
                return true;
            }
            await Task.Delay(2000, ct);
        }
        await SetErrorAsync("TTS sidecar недоступен (60с)");
        return false;
    }

    /// <summary>
    /// Из любого URL извлекает baseUrl и roomId.
    /// https://kontur.ktalk.ru/abc123  →  baseUrl=https://kontur.ktalk.ru, roomId=abc123
    /// </summary>
    private static bool TryParseRoomUrl(string url, out string baseUrl, out string roomId)
    {
        baseUrl = "";
        roomId  = "";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;

        baseUrl = $"{uri.Scheme}://{uri.Host}";
        if (!uri.IsDefaultPort) baseUrl += $":{uri.Port}";

        roomId = uri.Segments
            .Select(s => s.Trim('/'))
            .LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";

        return !string.IsNullOrEmpty(roomId);
    }

    private static string SanitizeText(string text)
    {
        text = Regex.Replace(text, @"https?://\S+", "ссылка");
        text = Regex.Replace(text, @"[*_`~#>]", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    private async Task SetStatusAsync(BotStatus s)
    {
        State.Status = s;
        await PushStatusAsync();
    }

    private async Task SetErrorAsync(string msg)
    {
        State.Status       = BotStatus.Error;
        State.ErrorMessage = msg;
        _log.LogError("{M}", msg);
        PushLog("Error", msg);
        await PushStatusAsync();
    }

    private async Task PushStatusAsync()
        => await _hub.Clients.All.StatusUpdated(BuildStatusResponse());

    private void PushLog(string level, string message)
    {
        _log.Log(level switch
        {
            "Error"   => LogLevel.Error,
            "Warning" => LogLevel.Warning,
            "Debug"   => LogLevel.Debug,
            _         => LogLevel.Information
        }, "{M}", message);
        _ = _hub.Clients.All.LogReceived(new LogEntry { Level = level, Message = message });
    }

    public BotStatusResponse BuildStatusResponse() => new()
    {
        Status          = State.Status.ToString(),
        RoomUrl         = State.RoomUrl,
        RoomId          = State.RoomId,
        ErrorMessage    = State.ErrorMessage,
        StartedAt       = State.StartedAt,
        MessagesSpoken  = State.MessagesSpoken,
        QueueLength     = State.QueueLength,
        ReconnectCount  = State.ReconnectCount,
        TtsCommandCount = State.TtsCommandCount,
        UptimeSeconds   = State.StartedAt.HasValue
            ? (DateTimeOffset.UtcNow - State.StartedAt.Value).TotalSeconds.ToString("F0") : "0",
        TtsCommand      = _opts.TtsCommand
    };

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        await _chat.DisposeAsync();
        await _browser.DisposeAsync();
    }
}
