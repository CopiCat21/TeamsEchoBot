using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;
using Microsoft.Skype.Bots.Media;
using TeamsEchoBot.Models;

namespace TeamsEchoBot.Media;

/// <summary>
/// Manages a single active Teams call's audio lifecycle.
///
/// CONSTRUCTION ORDER (critical — matches official Microsoft samples):
///   1. new CallHandler(mediaSession, ...) — subscribe AudioSocket IMMEDIATELY
///   2. client.Calls().AddAsync(joinParams)
///   3. handler.AttachCall(call) — attach for state tracking
///
/// This order ensures AudioMediaReceived events are wired before media flows.
/// The media platform can start sending frames as soon as the session is created,
/// even before the call object is returned from AddAsync.
/// </summary>
public class CallHandler : IDisposable
{
    private readonly ILocalMediaSession _mediaSession;
    private readonly IAudioSocket _audioSocket;
    private readonly AudioProcessor _audioProcessor;
    private readonly ILogger _logger;

    private ICall? _call;
    private bool _disposed;
    private MediaSendStatus _audioSendStatus = MediaSendStatus.Inactive;

    public CallHandler(
        ILocalMediaSession mediaSession,
        SpeechConfiguration speechConfig,
        ILogger logger)
    {
        _mediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _logger = logger;

        // AudioSocket is available on the mediaSession immediately after CreateMediaSession().
        // It is NOT null at this point — the socket is created with the session.
        _audioSocket = mediaSession.AudioSocket
            ?? throw new InvalidOperationException(
                "AudioSocket is null on the ILocalMediaSession. " +
                "Ensure AudioSocketSettings were provided to CreateMediaSession().");

        // Wire up AudioProcessor with a callback to send TTS audio back into the call
        _audioProcessor = new AudioProcessor(speechConfig, logger, SendTtsAudioAsync);

        // Subscribe to audio events BEFORE AddAsync is called
        _audioSocket.AudioMediaReceived += OnAudioMediaReceived;
        _audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;

        _logger.LogInformation("[Session {Id}] CallHandler created. AudioSocket subscribed.",
            mediaSession.MediaSessionId);
    }

    /// <summary>
    /// Called by BotService after Calls().AddAsync() returns the stateful ICall.
    /// Attaches the call for state-change monitoring and logging.
    /// </summary>
    public void AttachCall(ICall call)
    {
        _call = call ?? throw new ArgumentNullException(nameof(call));
        _call.OnUpdated += OnCallUpdated;
        _call.Participants.OnUpdated += OnParticipantsUpdated;
        _logger.LogInformation("[{CallId}] ICall attached to handler.", call.Id);
    }

    // ─── Call state events ────────────────────────────────────────────────────

    private void OnCallUpdated(ICall sender, ResourceEventArgs<Call> args)
    {
        var state = _call?.Resource?.State;
        _logger.LogInformation("[{CallId}] Call state → {State}", _call?.Id, state);

        if (state == CallState.Terminated)
        {
            _logger.LogInformation("[{CallId}] Call terminated. Disposing handler.", _call?.Id);
            Dispose();
        }
    }

    private void OnParticipantsUpdated(IParticipantCollection sender,
        CollectionEventArgs<IParticipant> args)
    {
        foreach (var p in args.AddedResources)
            _logger.LogInformation("[{CallId}] Participant joined: {Name}",
                _call?.Id, p.Resource?.Info?.Identity?.User?.DisplayName ?? "Unknown");

        foreach (var p in args.RemovedResources)
            _logger.LogInformation("[{CallId}] Participant left: {Name}",
                _call?.Id, p.Resource?.Info?.Identity?.User?.DisplayName ?? "Unknown");
    }

    private void OnAudioSendStatusChanged(object? sender, AudioSendStatusChangedEventArgs e)
    {
        _audioSendStatus = e.MediaSendStatus;
        _logger.LogInformation("[{CallId}] AudioSendStatus → {Status}", _call?.Id, e.MediaSendStatus);
    }

    // ─── Incoming audio ───────────────────────────────────────────────────────

    /// <summary>
    /// Fires every ~20ms with a PCM 16kHz 16-bit mono audio buffer.
    /// MUST be non-blocking — runs on the media platform thread.
    /// Buffers are pooled — MUST call e.Buffer.Dispose() in finally.
    /// </summary>
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
            // CRITICAL: Always dispose — failure causes media engine memory leaks
            e.Buffer.Dispose();
        }
    }

    // ─── Outgoing audio (TTS echo) ────────────────────────────────────────────

    /// <summary>
    /// Sends synthesized TTS PCM audio back into the call via the AudioSocket.
    /// Called by AudioProcessor after STT + TTS pipeline completes.
    ///
    /// Teams expects 20ms frames at 16kHz 16-bit mono:
    ///   16000 samples/sec × 2 bytes/sample × 0.020 sec = 640 bytes per frame
    /// </summary>
    private Task SendTtsAudioAsync(byte[] pcmBytes)
    {
        if (_disposed || pcmBytes.Length == 0)
            return Task.CompletedTask;

        if (_audioSendStatus != MediaSendStatus.Active)
        {
            _logger.LogWarning("[{CallId}] Skipping TTS send — AudioSendStatus is {Status}, not Active.",
                _call?.Id, _audioSendStatus);
            return Task.CompletedTask;
        }

        try
        {
            const int bytesPerFrame = 640;
            const int frameMs       = 20;

            // Timestamp must be in SAMPLES (not milliseconds).
            // At 16kHz, each 20ms frame = 320 samples.
            // The media engine uses sample-based timestamps to sequence audio correctly.
            const int samplesPerFrame = 320;
            var timestamp = (long)(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds
                * 16000; // convert to sample count

            // Stopwatch pacing: send one frame every 20ms by measuring real elapsed time.
            // This avoids flooding the socket (which disconnects the bot) while keeping
            // the thread alive without yielding via Task.Delay (which lets the call drop).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int frameIndex = 0;

            for (int offset = 0; offset < pcmBytes.Length && !_disposed; offset += bytesPerFrame)
            {
                if (_audioSendStatus != MediaSendStatus.Active)
                {
                    _logger.LogWarning("[{CallId}] AudioSendStatus went {Status} mid-TTS — stopping.",
                        _call?.Id, _audioSendStatus);
                    break;
                }

                var frameBytes = new byte[bytesPerFrame];
                var available  = Math.Min(bytesPerFrame, pcmBytes.Length - offset);
                Array.Copy(pcmBytes, offset, frameBytes, 0, available);

                var sendBuffer = new AudioSendMediaBuffer(frameBytes, AudioFormat.Pcm16K, timestamp);
                _audioSocket.Send(sendBuffer);
                // Do NOT dispose here — the SDK calls Dispose() when it's done with the buffer

                // Increment timestamp by exact sample count per frame
                timestamp += samplesPerFrame;
                frameIndex++;

                // Pace to real time: wait until this frame's scheduled send time
                // Expected elapsed time for frameIndex frames = frameIndex * 20ms
                var expectedMs = frameIndex * frameMs;
                var elapsedMs  = sw.Elapsed.TotalMilliseconds;
                if (elapsedMs < expectedMs)
                {
                    var sleepMs = (int)(expectedMs - elapsedMs);
                    if (sleepMs > 0)
                        Thread.Sleep(sleepMs);
                }
            }

            _logger.LogInformation("[{CallId}] TTS sent: {Bytes} bytes ({Frames} frames)",
                _call?.Id, pcmBytes.Length,
                (int)Math.Ceiling((double)pcmBytes.Length / bytesPerFrame));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CallId}] Error sending TTS audio", _call?.Id);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _audioSocket.AudioMediaReceived    -= OnAudioMediaReceived;
        _audioSocket.AudioSendStatusChanged -= OnAudioSendStatusChanged;

        if (_call != null)
        {
            _call.OnUpdated              -= OnCallUpdated;
            _call.Participants.OnUpdated -= OnParticipantsUpdated;
        }

        _audioProcessor.Dispose();

        _logger.LogInformation("[{CallId}] CallHandler disposed.", _call?.Id);
    }
}
