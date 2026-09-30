using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plainspoken.Core.Logging;

namespace Plainspoken.Core.Transcription;

/// <summary>
/// Maps Gemini HTTP errors to <see cref="TranscriptionErrorKind"/>. Two body shapes exist:
/// Interactions API: {"error":{"message":"…","code":"too_many_requests"}}
/// Classic APIs:     {"error":{"code":429,"message":"…","status":"RESOURCE_EXHAUSTED","details":[…]}}
/// </summary>
public static partial class GeminiErrors
{
    /// <summary>A 429 whose retry delay is at most this long is treated as a per-minute limit.</summary>
    public static readonly TimeSpan PerMinuteThreshold = TimeSpan.FromSeconds(120);

    public static TranscriptionException FromResponse(int status, string? body, TimeSpan? retryAfterHeader, bool modelCall)
    {
        var info = ParseBody(body);
        var retryAfter = retryAfterHeader ?? info.RetryDelay ?? ParseRetryFromMessage(info.Message);
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"HTTP {status} {info.Code ?? "-"}/{info.Status ?? "-"}{(info.Reason is null ? "" : "/" + info.Reason)}: {Redactor.Clean(info.Message, 200)}");
        var msg = info.Message ?? string.Empty;

        TranscriptionErrorKind kind;
        switch (status)
        {
            case 400:
                kind = info.Reason == "API_KEY_INVALID" || msg.Contains("API key", StringComparison.OrdinalIgnoreCase)
                    ? TranscriptionErrorKind.InvalidKey
                    : TranscriptionErrorKind.BadRequest;
                break;
            case 401:
                kind = TranscriptionErrorKind.InvalidKey;
                break;
            case 403:
                // A missing/foreign uploaded file also returns 403 ("…permission to access the File…").
                kind = msg.Contains("File", StringComparison.Ordinal)
                    ? TranscriptionErrorKind.BadRequest
                    : TranscriptionErrorKind.InvalidKey;
                break;
            case 404:
                kind = modelCall || msg.Contains("model", StringComparison.OrdinalIgnoreCase)
                    ? TranscriptionErrorKind.ModelNotFound
                    : TranscriptionErrorKind.BadRequest;
                break;
            case 408:
                kind = TranscriptionErrorKind.Timeout;
                break;
            case 429:
                kind = Classify429(msg, info.QuotaIds, retryAfter);
                break;
            case >= 500 and <= 599:
                kind = TranscriptionErrorKind.Server;
                break;
            default:
                kind = TranscriptionErrorKind.BadRequest;
                break;
        }

        return new TranscriptionException(kind, detail, status, retryAfter);
    }

    /// <summary>
    /// Structured quota ids (classic APIs) are trusted as they are. The Interactions API only has a
    /// message, and it names the model's daily limit even when a short per-minute window tripped
    /// (seen 2026-09-30: "limit: 25 requests per day … Please retry in 17s", and a retry 30 s later
    /// succeeded), so there a short retry delay wins over the "per day" wording.
    /// </summary>
    public static TranscriptionErrorKind Classify429(string message, IEnumerable<string> quotaIds, TimeSpan? retryAfter)
    {
        ArgumentNullException.ThrowIfNull(quotaIds);
        var ids = Normalise(string.Join(' ', quotaIds));
        if (MentionsDay(ids))
        {
            return TranscriptionErrorKind.DailyQuota;
        }

        var shortWait = retryAfter is { } d && d <= PerMinuteThreshold;
        var text = Normalise(message);
        if (MentionsMinute(ids) || MentionsMinute(text) || shortWait)
        {
            return TranscriptionErrorKind.RateLimited;
        }

        return TranscriptionErrorKind.DailyQuota;
    }

    private static string Normalise(string? s) =>
        (s ?? string.Empty).ToUpperInvariant().Replace("_", " ", StringComparison.Ordinal);

    private static bool MentionsDay(string t) =>
        t.Contains("PER DAY", StringComparison.Ordinal) || t.Contains("PERDAY", StringComparison.Ordinal) ||
        t.Contains("DAILY", StringComparison.Ordinal);

    private static bool MentionsMinute(string t) =>
        t.Contains("PER MINUTE", StringComparison.Ordinal) || t.Contains("PERMINUTE", StringComparison.Ordinal);

    public static TimeSpan? ParseRetryFromMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var m = RetryIn().Match(message);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return null;
        }

        return m.Groups[2].Value.Equals("ms", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromMilliseconds(v) : TimeSpan.FromSeconds(v);
    }

    /// <summary>Parses "43s" / "1.5s" (protobuf Duration JSON).</summary>
    public static TimeSpan? ParseDuration(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.EndsWith('s'))
        {
            return null;
        }

        return double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? TimeSpan.FromSeconds(v)
            : null;
    }

    internal static ErrorInfo ParseBody(string? body)
    {
        var info = new ErrorInfo();
        if (string.IsNullOrWhiteSpace(body))
        {
            return info;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0]; // some Google endpoints wrap errors in an array
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object)
            {
                return info;
            }

            info.Message = GetString(err, "message");
            info.Status = GetString(err, "status");
            if (err.TryGetProperty("code", out var code))
            {
                info.Code = code.ValueKind == JsonValueKind.Number ? code.GetRawText() : code.ToString();
            }

            if (err.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in details.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var type = GetString(d, "@type") ?? string.Empty;
                    if (type.EndsWith("RetryInfo", StringComparison.Ordinal))
                    {
                        info.RetryDelay = ParseDuration(GetString(d, "retryDelay"));
                    }
                    else if (type.EndsWith("ErrorInfo", StringComparison.Ordinal))
                    {
                        info.Reason = GetString(d, "reason");
                    }
                    else if (type.EndsWith("QuotaFailure", StringComparison.Ordinal) &&
                             d.TryGetProperty("violations", out var v) && v.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var violation in v.EnumerateArray())
                        {
                            if (violation.ValueKind == JsonValueKind.Object && GetString(violation, "quotaId") is { } q)
                            {
                                info.QuotaIds.Add(q);
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: leave info empty.
        }

        return info;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"retry in\s+(\d+(?:\.\d+)?)\s*(ms|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RetryIn();

    internal sealed class ErrorInfo
    {
        public string? Message { get; set; }

        public string? Code { get; set; }

        public string? Status { get; set; }

        public string? Reason { get; set; }

        public TimeSpan? RetryDelay { get; set; }

        public List<string> QuotaIds { get; } = [];
    }
}
