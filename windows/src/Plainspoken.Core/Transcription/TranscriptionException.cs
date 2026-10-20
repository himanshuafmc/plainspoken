using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

public enum TranscriptionErrorKind
{
    NoKey,
    InvalidKey,
    RateLimited,
    DailyQuota,
    Network,
    Timeout,
    Server,
    ModelNotFound,
    BadRequest,
    UnexpectedResponse,
    Blocked,

    /// <summary>The account is out of credits or over a paid limit (402, or a quota message from a paid service).</summary>
    NoCredits,
}

/// <summary>
/// Any failure talking to the speech engine. <see cref="Exception.Message"/> is a sanitised
/// diagnostic for the log (status codes, error codes, JSON shape); it never contains transcript
/// text or the key and is never shown to the user (see Dictation.UserMessages for that).
/// </summary>
public sealed class TranscriptionException : Exception
{
    public TranscriptionException()
        : this(TranscriptionErrorKind.UnexpectedResponse, "Unknown transcription error.")
    {
    }

    public TranscriptionException(string message)
        : this(TranscriptionErrorKind.UnexpectedResponse, message)
    {
    }

    public TranscriptionException(string message, Exception innerException)
        : this(TranscriptionErrorKind.UnexpectedResponse, message, inner: innerException)
    {
    }

    /// <param name="service">The service that failed, when known; used to name it in the message the user sees.</param>
    public TranscriptionException(TranscriptionErrorKind kind, string message, int? statusCode = null, TimeSpan? retryAfter = null,
        Exception? inner = null, SpeechService? service = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        Service = service;
    }

    public TranscriptionErrorKind Kind { get; }

    public int? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    public SpeechService? Service { get; }

    public bool IsRateLimit => Kind is TranscriptionErrorKind.RateLimited or TranscriptionErrorKind.DailyQuota;

    /// <summary>This error, tagged with the service it came from (unchanged if it already names one).</summary>
    public TranscriptionException WithService(SpeechService service) =>
        Service is not null ? this : new TranscriptionException(Kind, Message, StatusCode, RetryAfter, InnerException, service);
}
