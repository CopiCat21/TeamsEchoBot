using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using TeamsEchoBot.Models;

namespace TeamsEchoBot.Services;

public class SpeechService : IDisposable
{
    private readonly SpeechConfiguration _config;
    private readonly ILogger _logger;
    private readonly SpeechConfig _speechConfig;
    private bool _disposed;

    public SpeechService(SpeechConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;

        _speechConfig = SpeechConfig.FromSubscription(
            config.Key, config.Region);

        _speechConfig.SpeechRecognitionLanguage = config.Language;
        _speechConfig.SpeechSynthesisVoiceName = config.VoiceName;

        _speechConfig.SetSpeechSynthesisOutputFormat(
            SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);

        _logger.LogInformation("SpeechService initialized. Region: {Region}, Language: {Language}, Voice: {Voice}",
            config.Region, config.Language, config.VoiceName);
    }

    public async Task<string> TranscribeAsync(byte[] pcmBytes)
    {
        var audioFormat = AudioStreamFormat.GetWaveFormatPCM(
            samplesPerSecond: 16000,
            bitsPerSample: 16,
            channels: 1);

        using var pushStream = AudioInputStream.CreatePushStream(audioFormat);
        using var audioConfig = AudioConfig.FromStreamInput(pushStream);
        using var recognizer = new SpeechRecognizer(_speechConfig, audioConfig);

        pushStream.Write(pcmBytes);
        pushStream.Close();

        _logger.LogDebug("STT: Recognizing {Bytes} bytes of PCM audio...", pcmBytes.Length);

        var result = await recognizer.RecognizeOnceAsync().ConfigureAwait(false);

        switch (result.Reason)
        {
            case ResultReason.RecognizedSpeech:
                _logger.LogInformation("STT success: \"{Text}\"", result.Text);
                return result.Text;

            case ResultReason.NoMatch:
                _logger.LogInformation("STT: No speech recognized (NoMatch). " +
                    "Possible causes: audio too quiet, wrong language setting, or non-speech audio.");
                return string.Empty;

            case ResultReason.Canceled:
                var cancellation = CancellationDetails.FromResult(result);
                _logger.LogWarning("STT canceled. Reason: {Reason}. Error: {Error}",
                    cancellation.Reason, cancellation.ErrorDetails);

                if (cancellation.Reason == CancellationReason.Error)
                    _logger.LogError("STT error code: {Code}. Check Speech API key and region in appsettings.json.",
                        cancellation.ErrorCode);

                return string.Empty;

            default:
                _logger.LogWarning("STT: Unexpected result reason: {Reason}", result.Reason);
                return string.Empty;
        }
    }

    public async Task<byte[]> SynthesizeAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        using var synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig: null);

        _logger.LogDebug("TTS: Synthesizing \"{Text}\" with voice {Voice}", text, _config.VoiceName);

        var result = await synthesizer.SpeakTextAsync(text).ConfigureAwait(false);

        switch (result.Reason)
        {
            case ResultReason.SynthesizingAudioCompleted:
                _logger.LogInformation("TTS success: {Bytes} bytes synthesized for \"{Text}\"",
                    result.AudioData.Length, text);
                return result.AudioData;

            case ResultReason.Canceled:
                var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                _logger.LogError("TTS canceled. Reason: {Reason}. Error: {Error}. Code: {Code}",
                    cancellation.Reason, cancellation.ErrorDetails, cancellation.ErrorCode);
                return Array.Empty<byte>();

            default:
                _logger.LogWarning("TTS: Unexpected result reason: {Reason}", result.Reason);
                return Array.Empty<byte>();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
