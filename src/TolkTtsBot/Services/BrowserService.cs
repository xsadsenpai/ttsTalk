using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

public interface IBrowserService : IAsyncDisposable
{
    Task<bool> JoinRoomAsync(string roomUrl, string botName, CancellationToken ct);
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

    public bool IsInRoom => _isInRoom;

    public PlaywrightBrowserService(
        IOptions<BrowserOptions> opts,
        ILogger<PlaywrightBrowserService> log)
    {
        _opts = opts.Value;
        _log  = log;
    }

    // ── Вход в комнату ────────────────────────────────────────────────────────

    public async Task<bool> JoinRoomAsync(string roomUrl, string botName, CancellationToken ct)
    {
        // Жёсткий таймаут на весь процесс входа
        using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        joinCts.CancelAfter(TimeSpan.FromSeconds(_opts.JoinTimeoutSeconds));
        var token = joinCts.Token;

        try
        {
            await CleanupAsync();

            _log.LogInformation("[Browser] Инициализация Playwright...");
            _playwright = await Playwright.CreateAsync();

            var chromium = FindChromium();
            _log.LogInformation("[Browser] Chromium: {C}", chromium ?? "(встроенный)");

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
                }
            });
            _log.LogInformation("[Browser] ✓ Chromium запущен");

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

            // ── Открываем страницу (Load, не NetworkIdle — SPA никогда не достигает NetworkIdle) ──
            _log.LogInformation("[Browser] Открываем: {U}", roomUrl);
            var response = await _page.GotoAsync(roomUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout   = _opts.NavigationTimeoutMs
            });
            _log.LogInformation("[Browser] HTTP {S}", response?.Status);

            // Ждём рендера Angular/React
            await Task.Delay(3000, token);

            // Диагностика
            var title   = await _page.TitleAsync();
            var body    = await _page.EvaluateAsync<string>("() => document.body?.innerText?.slice(0,300)??''");
            var inputs  = await _page.EvaluateAsync<string[]>(
                "()=>[...document.querySelectorAll('input')].map(i=>`${i.type}|${i.name}|${i.placeholder}`)");
            var buttons = await _page.EvaluateAsync<string[]>(
                "()=>[...document.querySelectorAll('button')].map(b=>b.innerText.trim()).filter(Boolean)");
            _log.LogInformation("[Browser] Title={T}", title);
            _log.LogInformation("[Browser] Body={B}", body);
            _log.LogInformation("[Browser] Inputs: {I}",  string.Join("; ", inputs  ?? []));
            _log.LogInformation("[Browser] Buttons: {B}", string.Join("; ", buttons ?? []));

            // ── Гостевой вход ─────────────────────────────────────────────
            await GuestJoinAsync(botName, token);

            // ── Web Audio injection ───────────────────────────────────────
            await SetupAudioAsync();

            _isInRoom = true;
            _log.LogInformation("[Browser] ✓ Бот в комнате как \"{N}\"", botName);
            return true;
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[Browser] Таймаут входа ({T}с)", _opts.JoinTimeoutSeconds);
            await CleanupAsync();
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Browser] Ошибка: {M}", ex.Message);
            try { if (_page is not null) _log.LogError("[Browser] URL={U}", _page.Url); } catch { }
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
        _log.LogInformation("[Join] Вход как \"{N}\"", botName);

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
                    _log.LogInformation("[Join] ✓ Имя введено [{S}]", sel);
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
                    _log.LogInformation("[Join] ✓ Кнопка \"{T}\" нажата", txt.Trim());
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
        _log.LogInformation("[Join] URL после входа: {U}", urlAfter);
        _log.LogInformation("[Join] Body после входа: {B}", bodyAfter);

        // Проверяем и включаем микрофон
        await EnsureMicOnAsync(ct);
    }

    private async Task EnsureMicOnAsync(CancellationToken ct)
    {
        if (_page is null) return;
        _log.LogInformation("[Mic] Проверка...");

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
                    _log.LogInformation("[Mic] ✓ Включён [{S}]", sel);
                    return;
                }
            }
            catch { }
        }
        _log.LogInformation("[Mic] Уже включён или не найден");
    }

    // ── Web Audio API injection ───────────────────────────────────────────────

    private async Task SetupAudioAsync()
    {
        if (_page is null) return;
        _log.LogInformation("[Audio] Настройка Web Audio injection...");

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

        _log.LogInformation("[Audio] ✓ Готов");
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
        _log.LogInformation("[Browser] Закрыт");
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
}
