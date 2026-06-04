using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

public interface IBrowserService : IAsyncDisposable
{
    Task<bool> JoinRoomAsync(string roomUrl, string botName, CancellationToken ct,
        Action<string>? onLog = null);
    Task InjectAudioAsync(byte[] wavBytes, CancellationToken ct);
    Task LeaveRoomAsync();
    bool IsInRoom { get; }
}

public sealed class PlaywrightBrowserService : IBrowserService
{
    private readonly BrowserOptions                   _opts;
    private readonly ILogger<PlaywrightBrowserService> _log;

    private IPlaywright?     _playwright;
    private IBrowser?        _browser;
    private IBrowserContext? _context;
    private IPage?           _page;
    private bool             _isInRoom;
    private Action<string>?  _onLog;

    public bool IsInRoom => _isInRoom;

    private void Log(string msg)
    {
        _log.LogInformation("{M}", msg);
        _onLog?.Invoke(msg);
    }

    public PlaywrightBrowserService(
        IOptions<BrowserOptions> opts,
        ILogger<PlaywrightBrowserService> log)
    {
        _opts = opts.Value;
        _log  = log;
    }

    // ── Вход в комнату ────────────────────────────────────────────────────────

    public async Task<bool> JoinRoomAsync(string roomUrl, string botName, CancellationToken ct,
        Action<string>? onLog = null)
    {
        // Жёсткий таймаут на весь процесс входа
        _onLog = onLog;
        using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        joinCts.CancelAfter(TimeSpan.FromSeconds(_opts.JoinTimeoutSeconds));
        var token = joinCts.Token;

        try
        {
            await CleanupAsync();

            Log("[Browser] Инициализация Playwright...");
            _playwright = await Playwright.CreateAsync();

            var chromium = FindChromium();
            Log($"[Browser] Chromium: {chromium ?? "(встроенный)"}");

            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless       = _opts.Headless,
                SlowMo         = _opts.SlowMo,
                ExecutablePath = chromium,
                Timeout        = 30000,
                Args           = new[]
                {
                    "--use-fake-ui-for-media-stream",
                    "--use-fake-device-for-media-stream",
                    "--autoplay-policy=no-user-gesture-required",
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu",
                    "--single-process",
                    "--no-first-run",
                    "--ignore-certificate-errors",
                    "--disable-web-security",
                    "--disable-features=VizDisplayCompositor",
                    "--disable-background-timer-throttling",
                    "--disable-renderer-backgrounding",
                }
            });
            Log("[Browser] ✓ Chromium запущен");

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                Permissions       = new[] { "microphone" },
                IgnoreHTTPSErrors = true,
                UserAgent         = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
            });

            _page = await _context.NewPageAsync();
            _page.Console  += (_, e) => _log.LogDebug("[Page] {T}: {M}", e.Type, e.Text);
            _page.PageError += (_, e) => _log.LogWarning("[Page] Error: {E}", e);

            // ── Диагностика сети ─────────────────────────────────────────
            try
            {
                var netTest = await _page.GotoAsync("https://kontur.ktalk.ru",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15000 });
                Log($"[Browser] Сеть OK: kontur.ktalk.ru HTTP {netTest?.Status}");
            }
            catch (Exception netEx)
            {
                Log($"[Browser] Сеть НЕДОСТУПНА из контейнера: {netEx.Message[..Math.Min(netEx.Message.Length, 100)]}");
            }

            // ── Открываем комнату (DOMContentLoaded — быстрее чем Load) ──
            Log($"[Browser] Открываем: {roomUrl}");
            var response = await _page.GotoAsync(roomUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout   = 60000
            });
            Log($"[Browser] HTTP {response?.Status}");

            // Ждём рендера Angular/React
            await Task.Delay(3000, token);

            // Диагностика
            var title   = await _page.TitleAsync();
            var body    = await _page.EvaluateAsync<string>("() => document.body?.innerText?.slice(0,300)??''");
            var inputs  = await _page.EvaluateAsync<string[]>(
                "()=>[...document.querySelectorAll('input')].map(i=>`${i.type}|${i.name}|${i.placeholder}`)");
            var buttons = await _page.EvaluateAsync<string[]>(
                "()=>[...document.querySelectorAll('button')].map(b=>b.innerText.trim()).filter(Boolean)");
            Log($"[Browser] Title={title}");
            Log($"[Browser] Body={body[..Math.Min(body.Length, 150)]}");
            Log($"[Browser] Inputs: {string.Join("; ", inputs ?? [])}");
            Log($"[Browser] Buttons: {string.Join("; ", buttons ?? [])}");

            // ── Гостевой вход ─────────────────────────────────────────────
            await GuestJoinAsync(botName, token);

            // ── Web Audio injection ───────────────────────────────────────
            await SetupAudioAsync();

            _isInRoom = true;
            Log($"[Browser] ✓ Бот в комнате как \"{botName}\"");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log($"[Browser] ⚠ Таймаут входа ({_opts.JoinTimeoutSeconds}с)");
            await CleanupAsync();
            return false;
        }
        catch (Exception ex)
        {
            Log($"[Browser] ✗ Ошибка: {ex.Message}"); _log.LogError(ex, "[Browser] Error");
            try { if (_page is not null) Log($"[Browser] URL при ошибке: {_page.Url}"); } catch { }
            await CleanupAsync();
            return false;
        }
    }

    private static string? FindChromium()
    {
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"),
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
            "/usr/bin/google-chrome-stable",
            "/usr/bin/google-chrome",
        };
        return paths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    private async Task GuestJoinAsync(string botName, CancellationToken ct)
    {
        if (_page is null) return;
        Log($"[Join] Вход как \"{botName}\"");

        await Task.Delay(1000, ct);

        // Поле ввода имени
        string[] nameSelectors =
        [
            "input[placeholder*='имя' i]",
            "input[placeholder*='name' i]",
            "input[placeholder*='Введите' i]",
            "input[name='name']",
            "input[name='displayName']",
            "input[name='userName']",
            "[data-testid*='name'] input",
            "input[type='text']",
        ];

        foreach (var sel in nameSelectors)
        {
            try
            {
                var loc = _page.Locator(sel).First;
                if (await loc.IsVisibleAsync())
                {
                    await loc.ClearAsync();
                    await loc.FillAsync(botName);
                    await loc.PressAsync("Tab");
                    await Task.Delay(300, ct);
                    Log($"[Join] ✓ Имя введено [{sel}]");
                    break;
                }
            }
            catch (Exception ex) { _log.LogDebug("[Join] input {S}: {E}", sel, ex.Message); }
        }

        // Кнопка входа
        string[] joinSelectors =
        [
            "button:has-text('Присоединиться')",
            "button:has-text('Войти')",
            "button:has-text('Подключиться')",
            "button:has-text('Продолжить')",
            "button:has-text('Join')",
            "button:has-text('Enter')",
            "[data-testid*='join']",
            "[data-testid*='enter']",
        ];

        foreach (var sel in joinSelectors)
        {
            try
            {
                var btn = _page.Locator(sel).First;
                if (await btn.IsVisibleAsync())
                {
                    var txt = await btn.InnerTextAsync();
                    await btn.ClickAsync();
                    Log($"[Join] ✓ Кнопка \"{txt.Trim()}\" нажата");
                    break;
                }
            }
            catch (Exception ex) { _log.LogDebug("[Join] btn {S}: {E}", sel, ex.Message); }
        }

        // Ждём загрузки комнаты
        await Task.Delay(5000, ct);

        var urlAfter  = _page.Url;
        var bodyAfter = await _page.EvaluateAsync<string>(
            "()=>document.body?.innerText?.slice(0,200)??''");
        Log($"[Join] URL после входа: {urlAfter}");
        Log($"[Join] Body после входа: {bodyAfter}");

        // Проверяем и включаем микрофон
        await EnsureMicOnAsync(ct);
    }

    private async Task EnsureMicOnAsync(CancellationToken ct)
    {
        if (_page is null) return;
        Log("[Mic] Проверка...");

        string[] micOffSelectors =
        [
            "[aria-label*='Включить микрофон' i]",
            "[aria-label*='Unmute' i]",
            "[aria-label*='microphone' i][aria-pressed='false']",
            "[data-testid*='mic'][aria-pressed='false']",
            "button[class*='muted'][class*='mic']",
        ];

        foreach (var sel in micOffSelectors)
        {
            try
            {
                var btn = _page.Locator(sel).First;
                if (await btn.IsVisibleAsync())
                {
                    await btn.ClickAsync();
                    Log($"[Mic] ✓ Включён [{sel}]");
                    return;
                }
            }
            catch { }
        }
        Log("[Mic] Уже включён или не найден");
    }

    // ── Web Audio API injection ───────────────────────────────────────────────

    private async Task SetupAudioAsync()
    {
        if (_page is null) return;
        Log("[Audio] Настройка Web Audio injection...");

        await _page.EvaluateAsync("""
            (function() {
                if (window.__ttsReady) return;
                const ctx  = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
                const dest = ctx.createMediaStreamDestination();
                const orig = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
                navigator.mediaDevices.getUserMedia = async c => c?.audio ? dest.stream : orig(c);
                window.__ttsCtx     = ctx;
                window.__ttsDest    = dest;
                window.__ttsQueue   = [];
                window.__ttsPlaying = false;
                window.__ttsReady   = true;

                async function playNext() {
                    if (window.__ttsPlaying || !window.__ttsQueue.length) return;
                    window.__ttsPlaying = true;
                    const b64 = window.__ttsQueue.shift();
                    try {
                        const bin = atob(b64);
                        const buf = new Uint8Array(bin.length);
                        for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i);
                        const ab  = await ctx.decodeAudioData(buf.buffer);
                        const src = ctx.createBufferSource();
                        src.buffer = ab;
                        src.connect(dest);
                        src.connect(ctx.destination);
                        src.onended = () => { window.__ttsPlaying = false; playNext(); };
                        src.start(0);
                    } catch(e) {
                        console.error('[TTS]', e);
                        window.__ttsPlaying = false;
                        playNext();
                    }
                }
                window.__ttsEnqueue = b64 => { window.__ttsQueue.push(b64); playNext(); };
                console.log('[TTS Bot] Audio injection ready');
            })();
        """);

        Log("[Audio] ✓ Готов");
    }

    public async Task InjectAudioAsync(byte[] wavBytes, CancellationToken ct)
    {
        if (_page is null || !_isInRoom)
            throw new InvalidOperationException("Браузер не в комнате");

        await _page.EvaluateAsync(
            "b64 => window.__ttsEnqueue(b64)",
            Convert.ToBase64String(wavBytes));

        _log.LogDebug("[Audio] Инжектировано {B} байт", wavBytes.Length);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public async Task LeaveRoomAsync()
    {
        _isInRoom = false;
        await CleanupAsync();
        Log("[Browser] Закрыт");
    }

    private async Task CleanupAsync()
    {
        _isInRoom = false;
        try { if (_page    is not null) await _page.CloseAsync();    } catch { }
        try { if (_context is not null) await _context.CloseAsync(); } catch { }
        try { if (_browser is not null) await _browser.CloseAsync(); } catch { }
        try { _playwright?.Dispose(); } catch { }
        _page = null; _context = null; _browser = null; _playwright = null;
    }

    public async ValueTask DisposeAsync() => await CleanupAsync();

/// <summary>
/// Заглушка IBrowserService — используется когда Playwright/Chromium
/// не может подключиться к комнате. Чат работает, аудио недоступно.
/// </summary>
public sealed class NullBrowserService : IBrowserService
{
    private readonly ILogger<NullBrowserService> _log;
    public bool IsInRoom => false;

    public NullBrowserService(ILogger<NullBrowserService> log) => _log = log;

    public Task<bool> JoinRoomAsync(string roomUrl, string botName,
        CancellationToken ct, Action<string>? onLog = null)
    {
        _log.LogWarning("[NullBrowser] Playwright отключён — аудио недоступно");
        onLog?.Invoke("[Browser] Режим только чат — аудио через браузер отключено");
        return Task.FromResult(false);
    }

    public Task InjectAudioAsync(byte[] wavBytes, CancellationToken ct)
    {
        _log.LogDebug("[NullBrowser] InjectAudio вызван, но браузер отключён");
        return Task.CompletedTask;
    }

    public Task LeaveRoomAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

}
