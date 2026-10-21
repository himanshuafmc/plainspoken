using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

/// <summary>
/// HTTP for the non-Google speech services: adds the service's auth header (only ever to its own host),
/// applies the timeout, maps errors to <see cref="TranscriptionException"/> and retries 5xx with <see cref="Retry"/>.
/// </summary>
public sealed class ServiceClient
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;

    public ServiceClient(HttpClient http, SpeechService service, string baseUrl, Func<string?> apiKey, ILog log, RetryPolicy? retry = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        Service = service;
        Retry = retry ?? RetryPolicy.Default;
        Base = new Uri(PlainspokenSettings.NormalizeBaseUrl(baseUrl, service.Info().DefaultBaseUrl) + "/");
    }

    public SpeechService Service { get; }

    public ILog Log { get; }

    public RetryPolicy Retry { get; }

    public Uri Base { get; }

    /// <summary><paramref name="relative"/> is resolved against the base address, keeping its path (for example /v1 of an OpenAI-style API).</summary>
    public Uri Url(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return new Uri(Base, relative.TrimStart('/'));
    }

    /// <summary>Sends with 5xx retries; returns the parsed JSON body. <paramref name="build"/> is called once per attempt.</summary>
    public Task<JsonDocument> SendJsonAsync(Func<HttpRequestMessage> build, TimeSpan timeout, string operation, CancellationToken ct) =>
        Retry.ExecuteAsync(operation, async c =>
        {
            var r = await SendOnceAsync(build, timeout, operation, c).ConfigureAwait(false);
            return Parse(r.Body, operation);
        }, Log, ct);

    internal async Task<GeminiClient.RawResponse> SendOnceAsync(Func<HttpRequestMessage> build, TimeSpan timeout, string operation,
        CancellationToken ct, bool throwOnError = true)
    {
        ArgumentNullException.ThrowIfNull(build);
        var key = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new TranscriptionException(TranscriptionErrorKind.NoKey, $"No {Service.Id()} key configured.", service: Service);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = build();
            // Only ever send the key to the service's own host.
            if (request.RequestUri is { } u && string.Equals(u.Host, Base.Host, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (name, value) in AuthHeaders(Service, key))
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }

            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"{operation}: HTTP {status} in {sw.ElapsedMilliseconds} ms ({request.Content?.Headers.ContentLength ?? 0} bytes up, {body.Length} chars down)"));

            if (throwOnError && (status < 200 || status > 299))
            {
                var ex = ServiceErrors.FromResponse(Service, status, body, GeminiClient.RetryAfter(response.Headers.RetryAfter));
                Log.Warn($"{operation}: {ex.Kind} — {ex.Message}");
                throw ex;
            }

            return new GeminiClient.RawResponse(status, body, headers);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"{operation}: timed out after {sw.ElapsedMilliseconds} ms");
            throw new TranscriptionException(TranscriptionErrorKind.Timeout, $"{operation}: timeout after {timeout.TotalSeconds:0}s", inner: ex, service: Service);
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"{operation}: network error {ex.HttpRequestError} ({ex.GetType().Name})");
            throw new TranscriptionException(TranscriptionErrorKind.Network, $"{operation}: {ex.HttpRequestError}", inner: ex, service: Service);
        }
    }

    internal JsonDocument Parse(string body, string operation)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException ex)
        {
            throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, $"{operation}: {JsonShape.Describe(body)}", inner: ex, service: Service);
        }
    }

    /// <summary>The auth header a service expects (also used for its WebSocket handshake).</summary>
    public static IReadOnlyDictionary<string, string> AuthHeaders(SpeechService service, string key) => service switch
    {
        SpeechService.Sarvam => new Dictionary<string, string> { ["api-subscription-key"] = key },
        SpeechService.Deepgram => new Dictionary<string, string> { ["Authorization"] = "Token " + key },
        SpeechService.ElevenLabs => new Dictionary<string, string> { ["xi-api-key"] = key },
        _ when service.IsGemini() => new Dictionary<string, string> { [GeminiClient.ApiKeyHeader] = key },
        _ => new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
    };
}

/// <summary>
/// Maps errors of the non-Google services. Body shapes seen in their SDKs:
/// OpenAI/Sarvam {"error":{"message","code"}}, Deepgram {"err_code","err_msg"}, ElevenLabs {"detail":{"status","message"}} or {"detail":"…"}.
/// </summary>
public static partial class ServiceErrors
{
    public static TranscriptionException FromResponse(SpeechService service, int status, string? body, TimeSpan? retryAfter)
    {
        var (message, code) = ReadError(body);
        var text = message + " " + code;
        var credits = CreditWords().IsMatch(text);
        var model = ModelWords().IsMatch(text);
        var kind = status switch
        {
            401 => TranscriptionErrorKind.InvalidKey,
            402 => TranscriptionErrorKind.NoCredits,
            403 => credits ? TranscriptionErrorKind.NoCredits : TranscriptionErrorKind.InvalidKey,
            404 => model ? TranscriptionErrorKind.ModelNotFound : TranscriptionErrorKind.BadRequest,
            408 or 504 => TranscriptionErrorKind.Timeout,
            429 => credits ? TranscriptionErrorKind.NoCredits : TranscriptionErrorKind.RateLimited,
            >= 500 and <= 599 => TranscriptionErrorKind.Server,
            _ when credits => TranscriptionErrorKind.NoCredits,
            400 or 422 when model => TranscriptionErrorKind.ModelNotFound,
            _ => TranscriptionErrorKind.BadRequest,
        };

        var detail = string.Create(CultureInfo.InvariantCulture, $"HTTP {status} {(code.Length == 0 ? "-" : code)}: {Redactor.Clean(message, 200)}");
        return new TranscriptionException(kind, detail, status, retryAfter, service: service);
    }

    /// <summary>(message, code) from any of the known error shapes; empty strings when absent.</summary>
    internal static (string Message, string Code) ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, string.Empty);
            }

            if (root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    return (Str(e, "message") ?? string.Empty, CodeOf(e, "code") ?? Str(e, "type") ?? string.Empty);
                }

                if (e.ValueKind == JsonValueKind.String)
                {
                    return (e.GetString() ?? string.Empty, CodeOf(root, "code") ?? string.Empty);
                }
            }

            if (Str(root, "err_msg") is { } errMsg)
            {
                return (errMsg, Str(root, "err_code") ?? string.Empty);
            }

            if (root.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.Object)
                {
                    return (Str(detail, "message") ?? string.Empty, Str(detail, "status") ?? string.Empty);
                }

                if (detail.ValueKind == JsonValueKind.String)
                {
                    return (detail.GetString() ?? string.Empty, string.Empty);
                }
            }

            if (Str(root, "message") is { } msg)
            {
                return (msg, CodeOf(root, "code") ?? string.Empty);
            }
        }
        catch (JsonException)
        {
            // Not JSON: nothing to read.
        }

        return (string.Empty, string.Empty);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? CodeOf(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

    [GeneratedRegex("credit|balance|insufficient|quota|billing|payment|exceeded your", RegexOptions.IgnoreCase)]
    private static partial Regex CreditWords();

    [GeneratedRegex(@"\bmodel", RegexOptions.IgnoreCase)]
    private static partial Regex ModelWords();
}
