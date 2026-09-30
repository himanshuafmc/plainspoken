using Plainspoken.Core.Logging;

namespace Plainspoken.Core.Transcription;

/// <summary>
/// Retries a step on server errors (HTTP 5xx) with exponential backoff and jitter:
/// by default 2 retries, waiting ~1 s then ~2 s (each ±50 %). Other errors are not retried here.
/// </summary>
public sealed class RetryPolicy
{
    private readonly Random _random;

    public RetryPolicy(int maxServerRetries = 2, TimeSpan? baseDelay = null, double jitter = 0.5,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null)
    {
        MaxServerRetries = maxServerRetries;
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        Jitter = Math.Clamp(jitter, 0, 1);
        Delay = delay ?? Task.Delay;
        _random = random ?? Random.Shared;
    }

    public static RetryPolicy Default { get; } = new();

    public int MaxServerRetries { get; }

    public TimeSpan BaseDelay { get; }

    public double Jitter { get; }

    /// <summary>Injectable so tests don't sleep.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; }

    /// <summary>Delay before retry number <paramref name="retry"/> (1-based).</summary>
    public TimeSpan BackoffFor(int retry)
    {
        var baseMs = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, retry - 1));
        double r;
        lock (_random)
        {
            r = _random.NextDouble();
        }

        var factor = 1 + (Jitter * ((2 * r) - 1));
        return TimeSpan.FromMilliseconds(baseMs * factor);
    }

    public async Task<T> ExecuteAsync<T>(string operation, Func<CancellationToken, Task<T>> action, ILog log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(log);
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (TranscriptionException ex) when (ex.Kind == TranscriptionErrorKind.Server && retry < MaxServerRetries)
            {
                var wait = BackoffFor(retry + 1);
                log.Warn($"{operation}: server error ({ex.StatusCode}); retry {retry + 1}/{MaxServerRetries} in {wait.TotalMilliseconds:0} ms");
                await Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }
}
