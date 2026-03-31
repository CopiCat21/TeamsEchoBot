using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using TeamsEchoBot.Models;

namespace TeamsEchoBot.Services;

/// <summary>
/// Streaming Speech-to-Text using Azure continuous recognition.
///
/// TEARDOWN SAFETY:
///   - TeardownRecognizerAsync wraps StopContinuousRecognitionAsync in a timeout
///   - _isRunning flag prevents double-teardown
///   - SemaphoreSlim prevents concurrent Start/Stop/Pause/Resume
/// </summary>
public class StreamingSpeechService : IDisposable
{
    private readonly SpeechConfiguration _config;
    private readonly ILogger _logger;
    private readonly SpeechConfig _speechConfig;

    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private SpeechRecognizer? _recognizer;

    private bool _disposed;
    private bool _isRunning;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    // Timeout for StopContinuousRecognitionAsync — the Azure SDK can hang
    // on this call if the native session is in a bad state.
    private const int StopTimeoutMs = 5_000;

    public StreamingSpeechService(SpeechConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;

        _speechConfig = SpeechConfig.FromSubscription(config.Key, config.Region);
        _speechConfig.SpeechRecognitionLanguage = config.Language;

        _logger.LogInformation(
            "StreamingSpeechService created. Region: {Region}, Language: {Language}",
            config.Region, config.Language);
    }

    public async Task StartAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isRunning || _disposed) return;
            await CreateAndStartRecognizerAsync().ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void PushAudio(byte[] pcmFrame)
    {
        if (_disposed || !_isRunning) return;

        try
        {
            _pushStream?.Write(pcmFrame);
        }
        catch (ObjectDisposedException)
        {
            // Stream was closed between the check and the write
        }
    }

    public async Task PauseAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isRunning || _disposed) return;
            _logger.LogInformation("Pausing continuous recognition...");
            await TeardownRecognizerAsync().ConfigureAwait(false);
            _logger.LogInformation("Recognition paused.");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isRunning || _disposed) return;
            _logger.LogInformation("Resuming continuous recognition...");
            await CreateAndStartRecognizerAsync().ConfigureAwait(false);
            _logger.LogInformation("Recognition resumed.");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isRunning) return;
            _logger.LogInformation("Stopping continuous recognition (final)...");
            await TeardownRecognizerAsync().ConfigureAwait(false);
            _logger.LogInformation("Recognition stopped.");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    // ─── Internal lifecycle ───────────────────────────────────────────────

    private async Task CreateAndStartRecognizerAsync()
    {
        var audioFormat = AudioStreamFormat.GetWaveFormatPCM(
            samplesPerSecond: 16000,
            bitsPerSample: 16,
            channels: 1);

        _pushStream = AudioInputStream.CreatePushStream(audioFormat);
        _audioConfig = AudioConfig.FromStreamInput(_pushStream);
        _recognizer = new SpeechRecognizer(_speechConfig, _audioConfig);

        _recognizer.Recognizing += OnRecognizing;
        _recognizer.Recognized += OnRecognized;
        _recognizer.Canceled += OnCanceled;
        _recognizer.SessionStarted += (s, e) =>
            _logger.LogInformation("STT session started. SessionId: {Id}", e.SessionId);
        _recognizer.SessionStopped += (s, e) =>
            _logger.LogInformation("STT session stopped. SessionId: {Id}", e.SessionId);

        await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
        _isRunning = true;
        _logger.LogInformation("Continuous recognition started.");
    }

    private async Task TeardownRecognizerAsync()
    {
        // Mark as not running FIRST to stop PushAudio from writing
        _isRunning = false;

        // Close the push stream first — this signals end-of-audio to the SDK
        // and helps StopContinuousRecognitionAsync complete faster.
        if (_pushStream != null)
        {
            try { _pushStream.Close(); }
            catch (ObjectDisposedException) { }
        }

        // Stop recognition with a timeout.
        // StopContinuousRecognitionAsync is a blocking native call that can hang
        // if the SDK's internal session is in a bad state. Without a timeout,
        // this blocks the thread for up to 10+ seconds (the SDK's internal timeout).
        if (_recognizer != null)
        {
            try
            {
                var stopTask = _recognizer.StopContinuousRecognitionAsync();
                if (await Task.WhenAny(stopTask, Task.Delay(StopTimeoutMs)).ConfigureAwait(false) != stopTask)
                {
                    _logger.LogWarning(
                        "StopContinuousRecognitionAsync timed out after {Ms}ms. " +
                        "Force-disposing recognizer.", StopTimeoutMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping recognizer.");
            }

            // Unwire events before dispose to prevent callbacks during teardown
            _recognizer.Recognizing -= OnRecognizing;
            _recognizer.Recognized -= OnRecognized;
            _recognizer.Canceled -= OnCanceled;

            try { _recognizer.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing recognizer.");
            }
            _recognizer = null;
        }

        // Dispose audio config after recognizer is gone
        if (_audioConfig != null)
        {
            try { _audioConfig.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing audio config.");
            }
            _audioConfig = null;
        }

        _pushStream = null;
    }

    // ─── Event handlers ───────────────────────────────────────────────────

    private void OnRecognizing(object? sender, SpeechRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.RecognizingSpeech)
        {
            _logger.LogInformation("[PARTIAL] {Text}", e.Result.Text);
        }
    }

    private void OnRecognized(object? sender, SpeechRecognitionEventArgs e)
    {
        switch (e.Result.Reason)
        {
            case ResultReason.RecognizedSpeech:
                _logger.LogInformation("[FINAL] {Text}", e.Result.Text);
                break;

            case ResultReason.NoMatch:
                _logger.LogDebug("STT: No match (background noise or silence).");
                break;
        }
    }

    private void OnCanceled(object? sender, SpeechRecognitionCanceledEventArgs e)
    {
        _logger.LogWarning("STT canceled. Reason: {Reason}, Error: {Error}",
            e.Reason, e.ErrorDetails);

        if (e.Reason == CancellationReason.Error)
        {
            _logger.LogError(
                "STT error code: {Code}. Check Speech API key/region.",
                e.ErrorCode);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // If StopAsync was already called by the background task,
        // these are all null and this is a no-op.
        // If not (abnormal path), do a best-effort synchronous cleanup.
        _isRunning = false;

        try { _pushStream?.Close(); } catch { }
        try { _recognizer?.Dispose(); } catch { }
        try { _audioConfig?.Dispose(); } catch { }

        _pushStream = null;
        _recognizer = null;
        _audioConfig = null;

        _stateLock.Dispose();
        _logger.LogInformation("StreamingSpeechService disposed.");
    }
}