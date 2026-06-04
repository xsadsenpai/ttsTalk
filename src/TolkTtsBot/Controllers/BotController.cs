using Microsoft.AspNetCore.Mvc;
using TolkTtsBot.Models;
using TolkTtsBot.Services;

namespace TolkTtsBot.Controllers;

[ApiController]
[Route("api/bot")]
public sealed class BotController : ControllerBase
{
    private readonly BotOrchestrator _bot;

    public BotController(BotOrchestrator bot) => _bot = bot;

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
