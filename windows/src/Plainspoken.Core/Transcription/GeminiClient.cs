using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Plainspoken.Core.Logging;

namespace Plainspoken.Core.Transcription;

public sealed record GeminiFile(string Name, string Uri, string State, string MimeType);

/// <summary>
/// Thin HTTP layer shared by both engines: auth header, timeouts, error mapping, 5xx retries,
/// and the Files API (resumable upload, wait-until-active, delete).
/// </summary>
public sealed class GeminiClient
{
    public const string ApiKeyHeader = "x-goog-api-key";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly bool _requireApiKey;
    private readonly ILog _log;
    private readonly RetryPolicy _retry;
    private readonly Uri _base;

    /// <param name="requireApiKey">False only for the smoke test behind a proxy that injects the key.</param>
    public GeminiClient(HttpClient http, string baseUrl, Func<string?> apiKey, ILog log, RetryPolicy? retry = null, bool requireApiKey = true)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _retry = retry ?? RetryPolicy.Default;
        _requireApiKey = requireApiKey;
        _base = new Uri(Settings.PlainspokenSettings.NormalizeBaseUrl(baseUrl) + "/");
    }

    public RetryPolicy Retry => _retry;

    public ILog Log => _log;

    public Uri Url(string relative) => new(_base, relative);

    /// <summary>POST/GET JSON with 5xx retries. Throws TranscriptionException on non-2xx.</summary>
    public Task<JsonDocument> SendJsonAsync(HttpMethod method, string relativeUrl, JsonNode? body, TimeSpan timeout,
        string operation, bool modelCall, CancellationToken ct) =>
        _retry.ExecuteAsync(operation, c => SendJsonOnceAsync(method, relativeUrl, body, timeout, operation, modelCall, c), _log, ct);

    /// <summary>Single attempt, for callers that wrap their own retry around several steps.</summary>
    public async Task<JsonDocument> SendJsonOnceAsync(HttpMethod method, string relativeUrl, JsonNode? body, TimeSpan timeout,
        string operation, bool modelCall, CancellationToken ct)
    {
        var r = await SendOnceAsync(() => Build(method, Url(relativeUrl), body), timeout, operation, modelCall, ct).ConfigureAwait(false);
        return ParseJson(r.Body, operation);
    }

    /// <summary>Resumable upload (start + upload/finalize). Retries the whole upload on 5xx.</summary>
    public Task<GeminiFile> UploadAsync(byte[] data, string mimeType, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _retry.ExecuteAsync("upload", async c =>
        {
            var start = await SendOnceAsync(() =>
            {
                var req = Build(HttpMethod.Post, Url("upload/v1beta/files"), new JsonObject
                {
                    ["file"] = new JsonObject { ["display_name"] = "plainspoken-dictation" },
                });
                req.Headers.Add("X-Goog-Upload-Protocol", "resumable");
                req.Headers.Add("X-Goog-Upload-Command", "start");
                req.Headers.Add("X-Goog-Upload-Header-Content-Length", data.Length.ToString(CultureInfo.InvariantCulture));
                req.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
                return req;
            }, timeout, "upload-start", false, c).ConfigureAwait(false);

            if (!start.Headers.TryGetValue("x-goog-upload-url", out var uploadUrl) ||
                !Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uploadUri))
            {
                throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, "upload-start: no X-Goog-Upload-URL header");
            }

            var perByte = TimeSpan.FromSeconds(data.Length / (64.0 * 1024));
            var done = await SendOnceAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, uploadUri) { Content = new ByteArrayContent(data) };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                req.Headers.Add("X-Goog-Upload-Offset", "0");
                req.Headers.TryAddWithoutValidation("X-Goog-Upload-Command", "upload, finalize");
                return req;
            }, timeout + perByte, "upload-bytes", false, c).ConfigureAwait(false);

            using var doc = ParseJson(done.Body, "upload-bytes");
            var file = doc.RootElement.TryGetProperty("file", out var f) ? f : doc.RootElement;
            return ReadFile(file, "upload-bytes");
        }, _log, ct);
    }

    /// <summary>Polls a PROCESSING file until ACTIVE (every 500 ms, up to <paramref name="maxWait"/>).</summary>
    public async Task<GeminiFile> WaitUntilActiveAsync(GeminiFile file, TimeSpan maxWait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (file.State.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) || file.State.Length == 0)
            {
                return file;
            }

            if (file.State.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                throw new TranscriptionException(TranscriptionErrorKind.Server, "file processing FAILED");
            }

            if (sw.Elapsed > maxWait)
            {
                throw new TranscriptionException(TranscriptionErrorKind.Timeout, $"file still {file.State} after {maxWait.TotalSeconds:0}s");
            }

            await _retry.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            using var doc = await SendJsonAsync(HttpMethod.Get, "v1beta/" + file.Name, null, TimeSpan.FromSeconds(15), "file-get", false, ct).ConfigureAwait(false);
            file = ReadFile(doc.RootElement, "file-get");
        }
    }

    /// <summary>Best effort; never throws. 403/404 mean the file is already gone.</summary>
    public async Task DeleteFileAsync(string name)
    {
        try
        {
            var r = await SendOnceAsync(() => Build(HttpMethod.Delete, Url("v1beta/" + name), null),
                TimeSpan.FromSeconds(20), "file-delete", false, CancellationToken.None, throwOnError: false).ConfigureAwait(false);
            if (r.Status is not (200 or 204 or 403 or 404))
            {
                _log.Warn($"file-delete: HTTP {r.Status}; Google deletes it automatically after ~48 h");
            }
        }
        catch (Exception ex) when (ex is TranscriptionException or HttpRequestException or OperationCanceledException)
        {
            _log.Warn($"file-delete failed: {ex.GetType().Name}");
        }
    }

    internal sealed record RawResponse(int Status, string Body, Dictionary<string, string> Headers);

    internal async Task<RawResponse> SendOnceAsync(Func<HttpRequestMessage> build, TimeSpan timeout, string operation,
        bool modelCall, CancellationToken ct, bool throwOnError = true)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key) && _requireApiKey)
        {
            throw new TranscriptionException(TranscriptionErrorKind.NoKey, "No API key configured.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = build();
            // Only ever send the key to the configured API host.
            if (!string.IsNullOrWhiteSpace(key) && request.RequestUri is { } u &&
                string.Equals(u.Host, _base.Host, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(ApiKeyHeader, key.Trim());
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }

            _log.Info(string.Create(CultureInfo.InvariantCulture,
                $"{operation}: HTTP {status} in {sw.ElapsedMilliseconds} ms ({request.Content?.Headers.ContentLength ?? 0} bytes up, {body.Length} chars down)"));

            if (throwOnError && (status < 200 || status > 299))
            {
                var ex = GeminiErrors.FromResponse(status, body, RetryAfter(response.Headers.RetryAfter), modelCall);
                _log.Warn($"{operation}: {ex.Kind} — {ex.Message}");
                throw ex;
            }

            return new RawResponse(status, body, headers);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn($"{operation}: timed out after {sw.ElapsedMilliseconds} ms");
            throw new TranscriptionException(TranscriptionErrorKind.Timeout, $"{operation}: timeout after {timeout.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"{operation}: network error {ex.HttpRequestError} ({ex.GetType().Name})");
            throw new TranscriptionException(TranscriptionErrorKind.Network, $"{operation}: {ex.HttpRequestError}", inner: ex);
        }
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? h)
    {
        if (h?.Delta is { } d)
        {
            return d;
        }

        if (h?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static HttpRequestMessage Build(HttpMethod method, Uri url, JsonNode? body)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return req;
    }

    internal static JsonDocument ParseJson(string body, string operation)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException ex)
        {
            throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse,
                $"{operation}: {JsonShape.Describe(body)}", inner: ex);
        }
    }

    private static GeminiFile ReadFile(JsonElement e, string operation)
    {
        string? Get(string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var name = Get("name");
        var uri = Get("uri");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uri) || !name.StartsWith("files/", StringComparison.Ordinal))
        {
            throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, $"{operation}: {JsonShape.Describe(e)}");
        }

        return new GeminiFile(name, uri, Get("state") ?? string.Empty, Get("mimeType") ?? string.Empty);
    }
}
