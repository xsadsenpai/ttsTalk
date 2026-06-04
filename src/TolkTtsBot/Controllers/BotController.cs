using Microsoft.AspNetCore.Mvc;
using TolkTtsBot.Models;
using TolkTtsBot.Services;

namespace TolkTtsBot.Controllers;

[ApiController]
[Route("api/bot")]
public sealed class BotController : ControllerBase
{
    private readonly BotOrchestrator _bot;
    private readonly IHttpClientFactory _httpFactory;

    public BotController(BotOrchestrator bot, IHttpClientFactory httpFactory)
    {
        _bot = bot;
        _httpFactory = httpFactory;
    }

    /// <summary>Диагностика сети — проверяет доступность kontur.ktalk.ru из .NET</summary>
    [HttpGet("netcheck")]
    public async Task<ActionResult> NetCheck([FromQuery] string url = "https://kontur.ktalk.ru")
    {
        var results = new List<object>();
        var client  = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            var resp = await client.GetAsync(url);
            results.Add(new { url, status = (int)resp.StatusCode, ok = true });
        }
        catch (Exception ex)
        {
            results.Add(new { url, error = ex.Message, ok = false });
        }

        return Ok(new { results, time = DateTimeOffset.UtcNow });
    }

    [HttpGet("status")]
    public ActionResult<BotStatusResponse> GetStatus()
        => Ok(_bot.BuildStatusResponse());

    [HttpPost("start")]
    public async Task<ActionResult<BotStatusResponse>> Start([FromBody] StartBotRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.RoomUrl))
            return BadRequest(new { error = "roomUrl обязателен" });

        if (!Uri.TryCreate(req.RoomUrl, UriKind.Absolute, out _))
            return BadRequest(new { error = "Некорректный URL" });

        var ok = await _bot.StartAsync(req.RoomUrl);
        return ok
            ? Ok(_bot.BuildStatusResponse())
            : Conflict(new { error = "Бот уже запущен или ошибка конфигурации" });
    }

    [HttpPost("stop")]
    public async Task<ActionResult<BotStatusResponse>> Stop()
    {
        await _bot.StopAsync();
        return Ok(_bot.BuildStatusResponse());
    }
}
