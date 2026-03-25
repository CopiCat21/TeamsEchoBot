using Microsoft.Skype.Bots.Media;
using System.Collections.Concurrent;
using TeamsEchoBot.Models;
using TeamsEchoBot.Services;

namespace TeamsEchoBot.Media;

/// <summary>
/// Buffers incoming PCM audio frames, detects speech vs silence,
/// and drives the STT → TTS echo pipeline.
///
/// FLOW:
///   1. EnqueueAudioBuffer() called per 20ms PCM frame (from media thread)
///   2. Background worker drains the queue continuously
///   3. RMS energy threshold distinguishes speech from silence
///   4. While speech is detected → accumulate bytes in _speechBuffer
///   5. When silence exceeds _silenceThresholdMs → flush buffer to STT
///   6. STT returns transcript → TTS synthesizes audio → callback sends to call
///
/// SILENCE DETECTION:
///   Uses Root Mean Square (RMS) energy of each frame. When RMS < threshold,
///   the frame is considered silence. After 1000ms of consecutive silence
///   following speech, the utterance is considered complete.
/// </summary>
public class AudioProcessor : IDisposable
{
    private readonly SpeechService _speechService;
    private readonly ILogger _logger;
    private readonly Func<byte[], Task> _sendAudioCallback;

    // Thread-safe queue for audio frames coming from the media thread
    private readonly BlockingCollection<byte[]> _audioQueue = new(boundedCapacity: 500);

    // Accumulated PCM bytes for the current speech utterance
    private readonly List<byte[]> _speechBuffer = new();

    // Background processing task
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cts = new();

    // ─── Silence detection settings ───────────────────────────────────────────
    // RMS threshold: frames below this are considered silence.
    // Tune this if the bot is too sensitive (lower value) or misses speech (higher value).
    private const double RmsThreshold = 500.0;

    // How many consecutive silence frames constitute end-of-utterance
    // At 20ms per frame: 1000ms / 20ms = 50 frames
    private const int SilenceFrameThreshold = 50; // ~1 second of silence

    // Minimum speech frames before we consider it real speech (filters clicks/noise)
    private const int MinSpeechFrames = 10; // ~200ms minimum utterance

    private int _consecutiveSilenceFrames = 0;
    private int _speechFrameCount = 0;
    private bool _isSpeaking = false;
    private bool _disposed = false;

    public AudioProcessor(
        SpeechConfiguration speechConfig,
        ILogger logger,
        Func<byte[], Task> sendAudioCallback)
    {
        _logger = logger;
        _sendAudioCallback = sendAudioCallback;
        _speechService = new SpeechService(speechConfig, logger);

        // Start background processing loop
        _processingTask = Task.Run(ProcessAudioLoopAsync, _cts.Token);
        _logger.LogInformation("AudioProcessor started. RMS threshold: {Threshold}, Silence window: {Ms}ms",
            RmsThreshold, SilenceFrameThreshold * 20);
    }

    /// <summary>
    /// Enqueues a raw PCM audio buffer for processing.
    /// Called from the media thread — must be non-blocking.
    /// </summary>
    public void EnqueueAudioBuffer(AudioMediaBuffer buffer)
    {
        if (_disposed) return;

        // Extract the raw PCM bytes from the unmanaged buffer
        var length = (int)buffer.Length;
        if (length <= 0) return;

        var bytes = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Data, bytes, 0, length);

        // TryAdd returns false if queue is full — drop the frame rather than block
        if (!_audioQueue.TryAdd(bytes))
            _logger.LogWarning("AudioProcessor queue full — dropping frame. Consider increasing queue size.");
    }

    // ─── Background processing loop ───────────────────────────────────────────

    private async Task ProcessAudioLoopAsync()
    {
        _logger.LogInformation("AudioProcessor background loop started.");

        try
        {
            foreach (var frame in _audioQueue.GetConsumingEnumerable(_cts.Token))
            {
                ProcessFrame(frame);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AudioProcessor loop cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioProcessor loop crashed unexpectedly.");
        }
    }

    private void ProcessFrame(byte[] pcmFrame)
    {
        var rms = CalculateRms(pcmFrame);
        bool isSpeechFrame = rms > RmsThreshold;

        if (isSpeechFrame)
        {
            // Speech detected
            _consecutiveSilenceFrames = 0;
            _speechFrameCount++;
            _speechBuffer.Add(pcmFrame);

            if (!_isSpeaking && _speechFrameCount >= MinSpeechFrames)
            {
                _isSpeaking = true;
                _logger.LogInformation("Speech detected (RMS: {Rms:F0}). Buffering utterance...", rms);
            }
        }
        else
        {
            // Silence frame
            if (_isSpeaking)
            {
                _consecutiveSilenceFrames++;

                // Continue buffering a small amount of silence (natural speech gaps)
                if (_consecutiveSilenceFrames <= 10)
                    _speechBuffer.Add(pcmFrame);

                // Silence threshold reached — end of utterance
                if (_consecutiveSilenceFrames >= SilenceFrameThreshold)
                {
                    _logger.LogInformation("Silence detected after {SpeechFrames} speech frames. " +
                        "Flushing utterance to STT...", _speechFrameCount);

                    // Merge all buffered frames into a single byte array for STT
                    var utteranceBytes = MergeFrames(_speechBuffer);

                    // Fire-and-forget the STT→TTS pipeline so we don't block the audio loop
                    _ = Task.Run(() => RunSttTtsPipelineAsync(utteranceBytes));

                    // Reset for next utterance
                    _speechBuffer.Clear();
                    _isSpeaking = false;
                    _speechFrameCount = 0;
                    _consecutiveSilenceFrames = 0;
                }
            }
            else
            {
                // Background silence — reset frame counters
                if (_speechFrameCount > 0 && _speechFrameCount < MinSpeechFrames)
                {
                    // Was a noise burst, not real speech — discard
                    _speechBuffer.Clear();
                    _speechFrameCount = 0;
                }
            }
        }
    }

    private async Task RunSttTtsPipelineAsync(byte[] pcmBytes)
    {
        try
        {
            _logger.LogInformation("Sending {Bytes} bytes to Azure STT...", pcmBytes.Length);

            // Step 1: Send PCM audio to Azure Speech STT
            var transcript = await _speechService.TranscribeAsync(pcmBytes).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                _logger.LogInformation("STT returned empty transcript — skipping TTS.");
                return;
            }

            _logger.LogInformation("STT transcript: \"{Transcript}\"", transcript);

            // Step 2: Synthesize the transcript back to PCM audio via Azure TTS
            _logger.LogInformation("Sending transcript to Azure TTS for synthesis...");
            var ttsPcmBytes = await _speechService.SynthesizeAsync(transcript).ConfigureAwait(false);

            if (ttsPcmBytes == null || ttsPcmBytes.Length == 0)
            {
                _logger.LogWarning("TTS returned empty audio — skipping playback.");
                return;
            }

            _logger.LogInformation("TTS synthesized {Bytes} bytes. Sending to call...", ttsPcmBytes.Length);

            // Step 3: Send TTS audio back into the Teams call
            await _sendAudioCallback(ttsPcmBytes).ConfigureAwait(false);

            _logger.LogInformation("Echo complete: \"{Transcript}\"", transcript);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "STT→TTS pipeline failed.");
        }
    }

    // ─── Utility ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Root Mean Square energy of a PCM frame.
    /// PCM 16-bit little-endian: each sample is 2 bytes.
    /// Higher value = louder audio.
    /// </summary>
    private static double CalculateRms(byte[] pcmFrame)
    {
        if (pcmFrame.Length < 2) return 0;

        double sumSquares = 0;
        int sampleCount = pcmFrame.Length / 2;

        for (int i = 0; i < pcmFrame.Length - 1; i += 2)
        {
            // Convert two bytes to a signed 16-bit sample
            short sample = (short)(pcmFrame[i] | (pcmFrame[i + 1] << 8));
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / sampleCount);
    }

    private static byte[] MergeFrames(IReadOnlyList<byte[]> frames)
    {
        int totalLength = frames.Sum(f => f.Length);
        var merged = new byte[totalLength];
        int offset = 0;
        foreach (var frame in frames)
        {
            Buffer.BlockCopy(frame, 0, merged, offset, frame.Length);
            offset += frame.Length;
        }
        return merged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _audioQueue.CompleteAdding();
        _speechService.Dispose();
        _logger.LogInformation("AudioProcessor disposed.");
    }
}
