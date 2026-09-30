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

    public TranscriptionException(TranscriptionErrorKind kind, string message, int? statusCode = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public TranscriptionErrorKind Kind { get; }

    public int? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    public bool IsRateLimit => Kind is TranscriptionErrorKind.RateLimited or TranscriptionErrorKind.DailyQuota;
}
