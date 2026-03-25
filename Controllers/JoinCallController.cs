using Microsoft.AspNetCore.Mvc;
using TeamsEchoBot.Models;
using TeamsEchoBot.Services;

namespace TeamsEchoBot.Controllers;

[ApiController]
[Route("api/joinCall")]
public class JoinCallController : ControllerBase
{
    private readonly BotService _botService;
    private readonly ILogger<JoinCallController> _logger;

    public JoinCallController(BotService botService, ILogger<JoinCallController> logger)
    {
        _botService = botService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> JoinCallAsync([FromBody] JoinCallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.JoinUrl))
        {
            _logger.LogWarning("JoinCall called with empty joinUrl");
            return BadRequest(new { error = "joinUrl is required in the request body" });
        }

        if (!request.JoinUrl.StartsWith("https://teams.microsoft.com/l/meetup-join/",
            StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("JoinCall called with invalid joinUrl: {Url}", request.JoinUrl);
            return BadRequest(new
            {
                error = "joinUrl must be a valid Teams meeting join link starting with " +
                        "https://teams.microsoft.com/l/meetup-join/"
            });
        }

        try
        {
            _logger.LogInformation("JoinCall request received. URL: {JoinUrl}", request.JoinUrl);
            var callId = await _botService.JoinCallAsync(request.JoinUrl).ConfigureAwait(false);

            return Ok(new
            {
                callId,
                message = "Bot is joining the meeting. Check the Teams meeting in a few seconds — " +
                          "the bot should appear as a participant. Watch server logs for audio activity.",
                nextStep = "Speak into the meeting. After you stop speaking (silence detected), " +
                           "the bot will echo back what you said via TTS."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join call. URL: {JoinUrl}", request.JoinUrl);
            return StatusCode(500, new
            {
                error = "Failed to join the meeting",
                detail = ex.Message,
                hint = "Check server logs for full stack trace. Common causes: " +
                       "invalid App ID/Secret, missing Graph permissions, or meeting already ended."
            });
        }
    }
}
