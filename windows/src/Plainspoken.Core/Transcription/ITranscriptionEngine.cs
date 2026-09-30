using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

/// <summary>What to transcribe and how. Audio is always 16 kHz mono PCM16 WAV.</summary>
public sealed record TranscriptionRequest(
    byte[] Wav,
    double DurationSeconds,
    TranscriptionMode Mode,
    LanguagePreset Languages,
    IReadOnlyList<string> Vocabulary)
{
    public IReadOnlyList<string> LanguageCodes => LanguagePresets.Codes(Languages);

    public static TranscriptionRequest From(byte[] wav, double durationSeconds, PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TranscriptionRequest(wav, durationSeconds, settings.Transcription.Mode,
            settings.Transcription.Languages, settings.Transcription.CustomVocabulary.ToArray());
    }
}

/// <summary>
/// A speech-to-text backend. Returns the transcript ("" when no speech was found) or throws
/// <see cref="TranscriptionException"/>. Cancellation via the token throws OperationCanceledException.
/// </summary>
public interface ITranscriptionEngine
{
    EngineKind Kind { get; }

    Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}
