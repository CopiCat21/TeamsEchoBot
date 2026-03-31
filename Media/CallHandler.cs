using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;
using Microsoft.Skype.Bots.Media;
using TeamsEchoBot.Models;

namespace TeamsEchoBot.Media;

/// <summary>
/// Manages a single active Teams call — receive-only with transcription control.
///
/// Features:
///   - Receives audio and feeds to AudioProcessor for streaming STT
///   - HangupAsync() to leave the meeting on demand
///   - Auto-leave when bot is the only participant (30s grace period)
///   - Active/Inactive toggle to pause/resume transcription without leaving
/// </summary>
public class CallHandler : IDisposable
{
    private readonly ILocalMediaSession _mediaSession;
    private readonly IAudioSocket _audioSocket;
    private readonly AudioProcessor _audioProcessor;
    private readonly ILogger _logger;
    private readonly Action<string>? _onRequestLeave;

    private ICall? _call;
    private bool _disposed;

    // ─── Auto-leave state ─────────────────────────────────────────────────
    // When the bot is the only participant, start a timer.
    // If no one else joins within the grace period, leave automatically.
    private Timer? _autoLeaveTimer;
    private const int AutoLeaveGracePeriodMs = 30_000; // 30 seconds
    private readonly object _participantLock = new();
    private int _nonBotParticipantCount = 0;

    public CallHandler(
        ILocalMediaSession mediaSession,
        SpeechConfiguration speechConfig,
        ILogger logger,
        Action<string>? onRequestLeave = null)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _logger = logger;
        _onRequestLeave = onRequestLeave;

        _audioSocket = mediaSession.AudioSocket
            ?? throw new InvalidOperationException(
                "AudioSocket is null. Ensure AudioSocketSettings were provided.");

        _audioProcessor = new AudioProcessor(speechConfig, logger);

        _audioSocket.AudioMediaReceived += OnAudioMediaReceived;

        _logger.LogInformation(
            "[Session {Id}] CallHandler created (receive-only, transcription active).",
            mediaSession.MediaSessionId);
    }

    public void AttachCall(ICall call)
    {
        _call = call ?? throw new ArgumentNullException(nameof(call));
        _call.OnUpdated += OnCallUpdated;
        _call.Participants.OnUpdated += OnParticipantsUpdated;

        // Do an initial participant count
        UpdateParticipantCount();

        _logger.LogInformation("[{CallId}] ICall attached.", call.Id);
    }

    // ─── Public control methods ───────────────────────────────────────────

    /// <summary>
    /// Hangs up the call via Graph API. The bot leaves the meeting.
    /// </summary>
    public async Task HangupAsync()
    {
        if (_call == null)
        {
            _logger.LogWarning("HangupAsync called but no ICall attached.");
            return;
        }

        try
        {
            _logger.LogInformation("[{CallId}] Hanging up call...", _call.Id);
            await _call.DeleteAsync().ConfigureAwait(false);
            _logger.LogInformation("[{CallId}] Hangup request sent.", _call.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CallId}] Error during hangup.", _call?.Id);
            // Force dispose even if DeleteAsync fails
            Dispose();
        }
    }

    /// <summary>
    /// Enables or disables transcription. When inactive, the bot stays in
    /// the meeting but stops processing audio frames.
    /// </summary>
    public async Task SetTranscriptionActiveAsync(bool active)
    {
        var callId = _call?.Id ?? "unknown";
        _logger.LogInformation("[{CallId}] Transcription → {State}",
            callId, active ? "ACTIVE" : "INACTIVE");

        await _audioProcessor.SetActiveAsync(active).ConfigureAwait(false);
    }

    // ─── Call state events ────────────────────────────────────────────────

    private void OnCallUpdated(ICall sender, ResourceEventArgs<Call> args)
    {
        var state = _call?.Resource?.State;
        _logger.LogInformation("[{CallId}] Call state → {State}", _call?.Id, state);
    
        if (state == CallState.Terminated)
        {
            _logger.LogInformation("[{CallId}] Call terminated. Scheduling disposal...", _call?.Id);
            // Do NOT dispose on this callback thread — it blocks the HTTP pipeline.
            // Fire-and-forget disposal on a background thread.
            Task.Run(() => Dispose());
        }
    }

    private void OnParticipantsUpdated(IParticipantCollection sender,
        CollectionEventArgs<IParticipant> args)
    {
        foreach (var p in args.AddedResources)
            _logger.LogInformation("[{CallId}] Participant joined: {Name}",
                _call?.Id,
                p.Resource?.Info?.Identity?.User?.DisplayName ?? "Unknown");

        foreach (var p in args.RemovedResources)
            _logger.LogInformation("[{CallId}] Participant left: {Name}",
                _call?.Id,
                p.Resource?.Info?.Identity?.User?.DisplayName ?? "Unknown");

        UpdateParticipantCount();
    }

    // ─── Participant tracking & auto-leave ─────────────────────────────────

    private void UpdateParticipantCount()
    {
        if (_call?.Participants == null) return;

        // Count participants that are NOT this bot.
        // The bot's own participant entry has Identity.Application set
        // (not Identity.User), and its App ID matches our bot ID.
        var botAppId = _call.Resource?.Source?.Identity?.Application?.Id;

        int nonBotCount = 0;
        foreach (var participant in _call.Participants)
        {
            var identity = participant.Resource?.Info?.Identity;
            if (identity == null) continue;

            // A participant is the bot if it has an Application identity
            // matching the bot's app ID
            var appId = identity.Application?.Id;
            if (appId != null && appId.Equals(botAppId, StringComparison.OrdinalIgnoreCase))
                continue;

            nonBotCount++;
        }

        lock (_participantLock)
        {
            _nonBotParticipantCount = nonBotCount;
            _logger.LogInformation("[{CallId}] Non-bot participants: {Count}",
                _call?.Id, nonBotCount);

            if (nonBotCount == 0)
            {
                // Bot is alone — start the auto-leave countdown
                if (_autoLeaveTimer == null)
                {
                    _logger.LogInformation(
                        "[{CallId}] Bot is alone. Starting {Seconds}s auto-leave timer...",
                        _call?.Id, AutoLeaveGracePeriodMs / 1000);

                    _autoLeaveTimer = new Timer(
                        OnAutoLeaveTimerElapsed,
                        null,
                        AutoLeaveGracePeriodMs,
                        Timeout.Infinite); // fire once
                }
            }
            else
            {
                // Someone is here — cancel the timer if running
                if (_autoLeaveTimer != null)
                {
                    _logger.LogInformation(
                        "[{CallId}] Participant rejoined. Cancelling auto-leave timer.",
                        _call?.Id);
                    _autoLeaveTimer.Dispose();
                    _autoLeaveTimer = null;
                }
            }
        }
    }

    private void OnAutoLeaveTimerElapsed(object? state)
    {
        lock (_participantLock)
        {
            // Double-check nobody joined during the timer
            if (_nonBotParticipantCount > 0)
            {
                _logger.LogInformation(
                    "[{CallId}] Auto-leave timer fired but participants are back. Ignoring.",
                    _call?.Id);
                return;
            }
        }

        _logger.LogInformation(
            "[{CallId}] Auto-leave triggered — bot is alone for {Seconds}s. Leaving meeting.",
            _call?.Id, AutoLeaveGracePeriodMs / 1000);

        // Notify BotService to perform the hangup (can't await in a Timer callback)
        var callId = _call?.Id;
        if (callId != null && _onRequestLeave != null)
        {
            _onRequestLeave(callId);
        }
        else
        {
            // Fallback: fire-and-forget hangup directly
            _ = Task.Run(async () =>
            {
                try { await HangupAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{CallId}] Auto-leave hangup failed.", callId);
                }
            });
        }
    }

    // ─── Incoming audio ───────────────────────────────────────────────────

    private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        try
        {
            if (!_disposed)
                _audioProcessor.EnqueueAudioBuffer(e.Buffer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CallId}] Error enqueuing audio buffer", _call?.Id);
        }
        finally
        {
            e.Buffer.Dispose();
        }
    }

    // ─── Dispose ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;

        if (_call != null)
        {
            _call.OnUpdated -= OnCallUpdated;
            _call.Participants.OnUpdated -= OnParticipantsUpdated;
        }

        lock (_participantLock)
        {
            _autoLeaveTimer?.Dispose();
            _autoLeaveTimer = null;
        }

        _audioProcessor.Dispose();

        _logger.LogInformation("[{CallId}] CallHandler disposed.", _call?.Id);
    }
}