using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using TeamsEchoBot.Services;

namespace TeamsEchoBot.Bot;

/// <summary>
/// Handles Teams 1:1 chat messages.
///
/// Commands:
///   join {url}  — Join a meeting and start transcribing
///   leave       — Leave the current meeting
///   active      — Resume transcription (bot stays in meeting)
///   inactive    — Pause transcription (bot stays in meeting)
///   help        — Show available commands
/// </summary>
public class TranscriptBot : ActivityHandler
{
    private readonly BotService _botService;
    private readonly ILogger<TranscriptBot> _logger;

    public TranscriptBot(BotService botService, ILogger<TranscriptBot> logger)
    {
        _botService = botService;
        _logger = logger;
    }

    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken cancellationToken)
    {
        var text = turnContext.Activity.Text?.Trim() ?? string.Empty;
        _logger.LogInformation("Message received: \"{Text}\"", text);

        // ─── JOIN ─────────────────────────────────────────────────────────
        if (text.StartsWith("join ", StringComparison.OrdinalIgnoreCase))
        {
            var joinUrl = text["join ".Length..].Trim();

            if (!joinUrl.StartsWith("https://teams.microsoft.com/l/meetup-join/",
                    StringComparison.OrdinalIgnoreCase))
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text(
                        "That doesn't look like a valid Teams meeting link.\n\n" +
                        "Usage: `join https://teams.microsoft.com/l/meetup-join/...`"),
                    cancellationToken);
                return;
            }

            try
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text(
                        "Joining the meeting... I'll start transcribing once connected."),
                    cancellationToken);

                var callId = await _botService.JoinCallAsync(joinUrl);

                await turnContext.SendActivityAsync(
                    MessageFactory.Text(
                        $"Joined successfully. Call ID: `{callId}`\n\n" +
                        "Transcription is **active** and streaming to server logs.\n\n" +
                        "Commands:\n" +
                        "- `inactive` — pause transcription\n" +
                        "- `active` — resume transcription\n" +
                        "- `leave` — disconnect from meeting"),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to join meeting: {Url}", joinUrl);
                await turnContext.SendActivityAsync(
                    MessageFactory.Text($"Failed to join: {ex.Message}"),
                    cancellationToken);
            }
        }
        // ─── LEAVE ────────────────────────────────────────────────────────
        else if (text.Equals("leave", StringComparison.OrdinalIgnoreCase))
        {
            if (!_botService.HasActiveCalls())
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("I'm not currently in any meeting."),
                    cancellationToken);
                return;
            }

            var count = await _botService.LeaveAllCallsAsync();
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    count > 0
                        ? $"Left {count} meeting(s). Transcription stopped."
                        : "No active meetings to leave."),
                cancellationToken);
        }
        // ─── ACTIVE ───────────────────────────────────────────────────────
        else if (text.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            if (!_botService.HasActiveCalls())
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("I'm not currently in any meeting."),
                    cancellationToken);
                return;
            }

            var count = await _botService.SetTranscriptionActiveAsync(true);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    count > 0
                        ? "Transcription **resumed**. I'm listening again."
                        : "No active meetings found."),
                cancellationToken);
        }
        // ─── INACTIVE ─────────────────────────────────────────────────────
        else if (text.Equals("inactive", StringComparison.OrdinalIgnoreCase))
        {
            if (!_botService.HasActiveCalls())
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("I'm not currently in any meeting."),
                    cancellationToken);
                return;
            }

            var count = await _botService.SetTranscriptionActiveAsync(false);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    count > 0
                        ? "Transcription **paused**. I'm still in the meeting but not listening. " +
                          "Send `active` to resume."
                        : "No active meetings found."),
                cancellationToken);
        }
        // ─── HELP / UNKNOWN ───────────────────────────────────────────────
        else
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    "**TranscriptBot Commands:**\n\n" +
                    "- `join <meeting-url>` — Join a meeting and start transcribing\n" +
                    "- `leave` — Disconnect from the current meeting\n" +
                    "- `active` — Resume transcription (stays in meeting)\n" +
                    "- `inactive` — Pause transcription (stays in meeting)\n" +
                    "- `help` — Show this message"),
                cancellationToken);
        }
    }
}