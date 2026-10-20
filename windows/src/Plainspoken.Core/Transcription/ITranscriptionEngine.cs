using Plainspoken.Core.Audio;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

/// <summary>
/// What to transcribe and how. <see cref="Wav"/> is always 16 kHz mono PCM16; <see cref="Compressed"/> is the same
/// audio in a smaller format, for services that accept it.
/// </summary>
public sealed record TranscriptionRequest(
    byte[] Wav,
    double DurationSeconds,
    TranscriptionMode Mode,
    LanguagePreset Languages,
    IReadOnlyList<string> Vocabulary,
    HindiScript HindiScript = HindiScript.Roman,
    EncodedAudio? Compressed = null)
{
    public IReadOnlyList<string> LanguageCodes => LanguagePresets.Codes(Languages);

    /// <summary>The audio to upload to <paramref name="service"/>: compressed when it accepts that and it's available, else the WAV.</summary>
    public EncodedAudio AudioFor(SpeechService service) =>
        Compressed is not null && service.Info().AcceptsCompressedAudio ? Compressed : EncodedAudio.FromWav(Wav);

    public static TranscriptionRequest From(byte[] wav, double durationSeconds, PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var t = settings.Transcription;
        return new TranscriptionRequest(wav, durationSeconds, t.Mode, t.Languages, t.CustomVocabulary.ToArray(), t.HindiScript);
    }
}

/// <summary>
/// A speech-to-text backend. Returns the transcript ("" when no speech was found) or throws
/// <see cref="TranscriptionException"/>. Cancellation via the token throws OperationCanceledException.
/// </summary>
public interface ITranscriptionEngine
{
    SpeechService Service { get; }

    Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}
