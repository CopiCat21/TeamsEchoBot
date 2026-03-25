using Microsoft.AspNetCore.Mvc;
using TeamsEchoBot.Services;

namespace TeamsEchoBot.Controllers;

[ApiController]
[Route("api/calling")]
public class CallingController : ControllerBase
{
    private readonly BotService _botService;
    private readonly ILogger<CallingController> _logger;

    public CallingController(BotService botService, ILogger<CallingController> logger)
    {
        _botService = botService;
        _logger = logger;
    }

    [HttpPost]
    public async Task PostAsync()
    {
        _logger.LogInformation("Teams notification received at /api/calling from {RemoteIp}",
            HttpContext.Connection.RemoteIpAddress);

        try
        {
            await _botService.ProcessNotificationAsync(Request, Response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Teams notification");
            Response.StatusCode = StatusCodes.Status200OK;
        }
    }

    [HttpGet("health")]
    public IActionResult HealthCheck()
    {
        _logger.LogInformation("Health check called");
        return Ok(new
        {
            status = "running",
            bot = "TeamsEchoBot",
            timestamp = DateTimeOffset.UtcNow,
            webhook = "https://YOUR_DNS/api/calling"
        });
    }
}
