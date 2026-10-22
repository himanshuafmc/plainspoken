using Plainspoken.Core.Settings;
using Plainspoken.Core.Time;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Dictation;

/// <summary>Friendly, stack-trace-free wording for everything that can go wrong (SPEC §4.3).</summary>
public static class UserMessages
{
    public const string DidntCatch = "Didn't catch that";
    public const string Busy = "Busy — still transcribing";
    public const string Copied = "Copied — press Ctrl+V";

    public const string TypedOffline = "Typed offline";
    public const string Cancelled = "Cancelled";
    public const string CancelledSaved = "Stopped — recording saved. Use \"Retry last dictation\" in the tray menu.";
    public const string NothingToRetry = "Nothing to retry";
    public const string ThirtySecondsLeft = "30 s left";

    public static UserMessage NoKey { get; } =
        new("Add a key for your speech service in Settings to start.", UserAction.OpenSettings, RunActionNow: true);

    public static UserMessage Unexpected { get; } =
        new("Something went wrong. Your recording is saved — use Retry.", UserAction.Retry);

    public static UserMessage InsertFailed { get; } =
        new("Couldn't type or copy the text. It is in Recent transcripts in the tray menu.");

    public static UserMessage For(TranscriptionException ex, DateTimeOffset now, TimeZoneInfo local)
    {
        ArgumentNullException.ThrowIfNull(ex);
        // Gemini errors name Google (whose servers they are); the others name the service.
        var google = ex.Service is not { } s || s.IsGemini();
        var who = google ? "Google" : KeyTester.Capitalise(ex.Service!.Value.ShortName());
        var keyName = google ? "Gemini API key" : RemoveThe(ex.Service!.Value.ShortName()) + " key";
        return ex.Kind switch
        {
            TranscriptionErrorKind.NoKey => NoKey,
            TranscriptionErrorKind.InvalidKey => new($"Your {keyName} was not accepted. Please check it in Settings. Your recording is saved.", UserAction.OpenSettings, RunActionNow: true),
            TranscriptionErrorKind.DailyQuota => google
                ? new($"Free daily limit reached. Resets at {QuotaReset.FormatNextReset(now, local)}. Your recording is saved.", UserAction.Retry)
                : new($"{who}'s daily limit is reached. Your recording is saved — try Retry later.", UserAction.Retry),
            TranscriptionErrorKind.NoCredits => new($"Your {who} account is out of credits or over its limit. Your recording is saved.", UserAction.Retry),
            TranscriptionErrorKind.RateLimited => new("Too many requests right now. Your recording is saved — try Retry in a minute.", UserAction.Retry),
            TranscriptionErrorKind.Network => new("No internet connection. Your recording is saved — use Retry when you're back online.", UserAction.Retry),
            TranscriptionErrorKind.Timeout => new($"{who} took too long to answer. Your recording is saved — try Retry.", UserAction.Retry),
            TranscriptionErrorKind.Server => new($"{who}'s service had a problem. Your recording is saved — try Retry.", UserAction.Retry),
            TranscriptionErrorKind.ModelNotFound => new("The speech model isn't available. Check the model name in Settings.", UserAction.OpenSettings),
            TranscriptionErrorKind.Blocked => new($"{who} declined to transcribe this recording. It is saved — you can try Retry.", UserAction.Retry),
            _ => new($"Something unexpected came back from {who}. Your recording is saved — try Retry.", UserAction.Retry),
        };
    }

    private static string RemoveThe(string name) => name.StartsWith("the ", StringComparison.Ordinal) ? name[4..] : name;

    public static UserMessage For(RecorderErrorKind kind) => kind switch
    {
        RecorderErrorKind.AccessDenied => new(
            "Microphone access is turned off for desktop apps. In Windows Settings, turn on \"Let desktop apps access your microphone\".",
            UserAction.OpenMicrophonePrivacySettings),
        RecorderErrorKind.NoDevice => new("No microphone found. Plug one in, or pick another in Settings.", UserAction.OpenSettings),
        RecorderErrorKind.DeviceBusy => new("The microphone is busy in another app. Close it and try again."),
        _ => new("Couldn't start the microphone. Try again, or pick another microphone in Settings.", UserAction.OpenSettings),
    };
}
