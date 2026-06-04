using TolkTtsBot.Models;

namespace TolkTtsBot.Services;

/// <summary>
/// Заглушка IBrowserService — используется когда Playwright недоступен
/// (например, Render блокирует исходящие соединения из Chromium).
/// Чат работает через WebSocket, аудио недоступно.
/// </summary>
public sealed class NullBrowserService : IBrowserService
{
    private readonly ILogger<NullBrowserService> _log;

    public bool IsInRoom => false;

    public NullBrowserService(ILogger<NullBrowserService> log) => _log = log;

    public Task<bool> JoinRoomAsync(string roomUrl, string botName,
        CancellationToken ct, Action<string>? onLog = null)
    {
        _log.LogInformation("[NullBrowser] Playwright отключён — только чат, без аудио");
        onLog?.Invoke("[Browser] Режим: только чат (аудио через браузер недоступно на этом хостинге)");
        return Task.FromResult(false);
    }

    public Task InjectAudioAsync(byte[] wavBytes, CancellationToken ct)
        => Task.CompletedTask;

    public Task LeaveRoomAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
