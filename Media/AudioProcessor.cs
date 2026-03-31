using Microsoft.Skype.Bots.Media;
using System.Collections.Concurrent;
using TeamsEchoBot.Models;
using TeamsEchoBot.Services;

namespace TeamsEchoBot.Media;

/// <summary>
/// Receives PCM audio frames from the media socket thread and pushes them
/// into the Azure Speech SDK's continuous recognizer.
///
/// DISPOSAL PATTERN (critical for avoiding hangs):
///   The background task owns the StreamingSpeechService lifecycle.
///   Dispose() only signals cancellation and waits for the background task
///   to finish its cleanup. This prevents two threads from racing to tear
///   down the Azure Speech SDK's native resources simultaneously.
/// </summary>
public class AudioProcessor : IDisposable
{
    private readonly StreamingSpeechService _speechService;
    private readonly ILogger _logger;

    private readonly BlockingCollection<byte[]> _audioQueue = new(boundedCapacity: 500);
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private volatile bool _isActive = true;

    public AudioProcessor(SpeechConfiguration speechConfig, ILogger logger)
    {
        _logger = logger;
        _speechService = new StreamingSpeechService(speechConfig, logger);
        _processingTask = Task.Run(ProcessAudioLoopAsync, _cts.Token);
        _logger.LogInformation("AudioProcessor started (streaming mode, active).");
    }

    public async Task SetActiveAsync(bool active)
    {
        if (_isActive == active) return;

        _isActive = active;

        if (active)
        {
            _logger.LogInformation("AudioProcessor → ACTIVE. Resuming transcription.");
            await _speechService.ResumeAsync().ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("AudioProcessor → INACTIVE. Pausing transcription.");
            await _speechService.PauseAsync().ConfigureAwait(false);
        }
    }

    public void EnqueueAudioBuffer(AudioMediaBuffer buffer)
    {
        if (_disposed) return;

        var length = (int)buffer.Length;
        if (length <= 0) return;

        var bytes = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Data, bytes, 0, length);

        if (!_audioQueue.TryAdd(bytes))
            _logger.LogWarning("AudioProcessor queue full — dropping frame.");
    }

    private async Task ProcessAudioLoopAsync()
    {
        _logger.LogInformation("AudioProcessor background loop starting...");

        try
        {
            await _speechService.StartAsync().ConfigureAwait(false);

            foreach (var frame in _audioQueue.GetConsumingEnumerable(_cts.Token))
            {
                if (_isActive)
                {
                    _speechService.PushAudio(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AudioProcessor loop cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioProcessor loop crashed.");
        }
        finally
        {
            // ─── THIS is the single teardown point for the speech service ───
            // Only the background task tears down the speech service.
            // Dispose() waits for this task to complete — it does NOT
            // touch the speech service directly.
            try
            {
                _logger.LogInformation("AudioProcessor: cleaning up speech service...");
                await _speechService.StopAsync().ConfigureAwait(false);
                _speechService.Dispose();
                _logger.LogInformation("AudioProcessor: speech service cleaned up.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during speech service cleanup.");
                // Still try to dispose even if StopAsync failed
                try { _speechService.Dispose(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Step 1: Signal the background loop to stop
        _cts.Cancel();
        _audioQueue.CompleteAdding();

        // Step 2: Wait for the background task to finish its cleanup
        // (including StopAsync + Dispose on the speech service).
        // Use a timeout so we NEVER hang the calling thread indefinitely.
        // The speech SDK's internal timeout is ~10s, so 12s gives it room
        // while still unblocking the caller.
        if (!_processingTask.Wait(TimeSpan.FromSeconds(12)))
        {
            _logger.LogWarning(
                "AudioProcessor: background task did not finish within 12s. " +
                "Continuing disposal — speech service may leak.");
        }

        _cts.Dispose();
        _logger.LogInformation("AudioProcessor disposed.");
    }
}