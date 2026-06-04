using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

public interface ITtsService
{
    Task<byte[]> SynthesizeAsync(string text, CancellationToken ct = default);
    Task<bool>   IsHealthyAsync(CancellationToken ct = default);
}

public sealed class SileroTtsService : ITtsService
{
    private readonly HttpClient             _http;
    private readonly TtsOptions             _opts;
    private readonly ILogger<SileroTtsService> _log;

    public SileroTtsService(
        HttpClient http,
        IOptions<TtsOptions> opts,
        ILogger<SileroTtsService> log)
    {
        _http = http;
        _opts = opts.Value;
        _log  = log;
    }

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

        var payload  = new { text, voice = _opts.Voice, sample_rate = _opts.SampleRate };
        _log.LogDebug("[TTS] Синтез: {T}", text[..Math.Min(text.Length, 80)]);

        var response = await _http.PostAsJsonAsync("/synthesize", payload, cts.Token);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
        _log.LogDebug("[TTS] Получено {B} байт", bytes.Length);
        return bytes;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var r = await _http.GetAsync("/health", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
