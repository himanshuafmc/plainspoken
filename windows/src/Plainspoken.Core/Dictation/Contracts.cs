namespace Plainspoken.Core.Dictation;

public enum DictationState
{
    Idle,
    Recording,
    Transcribing,
}

/// <summary>16 kHz mono PCM16 audio.</summary>
public sealed record RecordedAudio(short[] Samples, int SampleRate)
{
    public double DurationSeconds => SampleRate <= 0 ? 0 : (double)Samples.Length / SampleRate;
}

public enum RecorderErrorKind
{
    AccessDenied,
    NoDevice,
    DeviceBusy,
    Failed,
}

public sealed class RecorderException : Exception
{
    public RecorderException()
        : this(RecorderErrorKind.Failed, "Recorder failed.")
    {
    }

    public RecorderException(string message)
        : this(RecorderErrorKind.Failed, message)
    {
    }

    public RecorderException(string message, Exception innerException)
        : this(RecorderErrorKind.Failed, message, innerException)
    {
    }

    public RecorderException(RecorderErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public RecorderErrorKind Kind { get; }
}

/// <summary>Microphone capture. Implemented per platform (Windows: NAudio WASAPI).</summary>
public interface IAudioRecorder
{
    /// <summary>Starts capturing. Throws <see cref="RecorderException"/> on failure.</summary>
    Task StartAsync(string? deviceId, CancellationToken ct);

    /// <summary>Stops, releases the microphone and returns the audio.</summary>
    Task<RecordedAudio> StopAsync();

    /// <summary>Stops, releases the microphone and discards the audio.</summary>
    Task CancelAsync();

    bool IsRecording { get; }

    TimeSpan Elapsed { get; }

    /// <summary>0..1, safe to read from the UI thread.</summary>
    float Level { get; }

    /// <summary>
    /// Optional: receives each new block of 16 kHz mono samples as it is captured (on the audio thread),
    /// e.g. to stream audio while the user is still speaking. Set before <see cref="StartAsync"/>.
    /// </summary>
    Action<short[]>? SampleSink { get; set; }
}

public enum InsertOutcome
{
    Inserted,
    CopiedToClipboard,
    Failed,
}

/// <summary>Puts text where the user's cursor is (Windows: clipboard paste or SendInput typing).</summary>
public interface ITextInserter
{
    Task<InsertOutcome> InsertAsync(string text, CancellationToken ct);

    Task<bool> CopyAsync(string text);
}

public interface ISoundPlayer
{
    void PlayStart();

    void PlayStop();
}

public enum UserAction
{
    None,
    OpenSettings,
    Retry,
    OpenMicrophonePrivacySettings,
}

/// <param name="RunActionNow">Run the action immediately (e.g. open Settings for a key problem) instead of waiting for a click.</param>
public sealed record UserMessage(string Text, UserAction Action = UserAction.None, bool RunActionNow = false);

/// <summary>
/// Everything the user sees during dictation: the pill overlay, hints and error notices.
/// All members are called on the UI thread.
/// </summary>
public interface IDictationView
{
    void OnStateChanged(DictationState state);

    void ShowRecording();

    void ShowTranscribing(string text);

    void ShowWarning(string text);

    /// <summary>Short, self-dismissing hint (e.g. "Didn't catch that").</summary>
    void ShowHint(string text);

    /// <summary>Error the user needs to read, optionally with an action button.</summary>
    void ShowError(UserMessage message);

    void HideOverlay();
}
