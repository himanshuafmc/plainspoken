using System.Net;
using System.Text;
using Plainspoken.Core.Logging;

namespace Plainspoken.Core.Tests;

internal static class Fixtures
{
    /// <summary>Reads shared/test-fixtures/{name}; the Android tests use the same files.</summary>
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shared", "test-fixtures", name));

    public static string Shared(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shared", name));
}

/// <summary>A captured request (content is read eagerly because the request is disposed after sending).</summary>
internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body, byte[]? Bytes, string? ContentType);

/// <summary>Fake network: each request is answered by a handler function. Never touches the network.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, int, HttpResponseMessage> _respond;

    public FakeHttpHandler(Func<CapturedRequest, int, HttpResponseMessage> respond) => _respond = respond;

    public List<CapturedRequest> Requests { get; } = [];

    public static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (n, v) in headers)
        {
            r.Headers.TryAddWithoutValidation(n, v);
        }

        return r;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            headers[h.Key] = string.Join(",", h.Value);
        }

        byte[]? bytes = null;
        string? body = null;
        string? contentType = null;
        if (request.Content is not null)
        {
            bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            contentType = request.Content.Headers.ContentType?.MediaType;
            if (contentType == "application/json")
            {
                body = Encoding.UTF8.GetString(bytes);
            }
        }

        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers, body, bytes, contentType);
        Requests.Add(captured);
        return _respond(captured, Requests.Count - 1);
    }
}

/// <summary>Routes fake responses by method + path; the default is the verified happy path.</summary>
internal sealed class GeminiFake
{
    public const string UploadUrl = "https://generativelanguage.googleapis.com/upload/v1beta/files?upload_id=abc&upload_protocol=resumable";

    public Func<CapturedRequest, HttpResponseMessage?>? Override { get; set; }

    public FakeHttpHandler Handler { get; }

    public GeminiFake() => Handler = new FakeHttpHandler((r, _) => Override?.Invoke(r) ?? Default(r));

    public HttpClient Client() => new(Handler);

    public IEnumerable<CapturedRequest> To(string pathContains) =>
        Handler.Requests.Where(r => r.Uri.AbsoluteUri.Contains(pathContains, StringComparison.Ordinal));

    public static HttpResponseMessage Default(CapturedRequest r)
    {
        var path = r.Uri.AbsolutePath;
        if (r.Method == HttpMethod.Post && path == "/upload/v1beta/files" && r.Uri.Query.Length == 0)
        {
            return FakeHttpHandler.Json(HttpStatusCode.OK, "", ("X-Goog-Upload-URL", UploadUrl));
        }

        if (r.Method == HttpMethod.Post && path == "/upload/v1beta/files")
        {
            return FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("upload-response.json"));
        }

        if (r.Method == HttpMethod.Post && path == "/v1beta/interactions")
        {
            return FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("interactions-response.json"));
        }

        if (r.Method == HttpMethod.Post && path.EndsWith(":generateContent", StringComparison.Ordinal))
        {
            return FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("generate-response.json"));
        }

        if (r.Method == HttpMethod.Delete)
        {
            return FakeHttpHandler.Json(HttpStatusCode.OK, "{}");
        }

        if (r.Method == HttpMethod.Get && path.StartsWith("/v1beta/models/", StringComparison.Ordinal))
        {
            return FakeHttpHandler.Json(HttpStatusCode.OK, "{\"name\":\"models/gemini-3.5-transcribe\"}");
        }

        return FakeHttpHandler.Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":404,\"message\":\"no route in fake\"}}");
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir() => System.IO.Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plainspoken-tests-" + Guid.NewGuid().ToString("N"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class ListLog : ILog
{
    public List<string> Lines { get; } = [];

    public void Info(string message) => Add("INFO " + message);

    public void Warn(string message) => Add("WARN " + message);

    public void Error(string message, Exception? exception = null) => Add("ERROR " + message + (exception is null ? "" : " " + exception.GetType().Name));

    private void Add(string line)
    {
        lock (Lines)
        {
            Lines.Add(line);
        }
    }
}

internal sealed class FakeTime : TimeProvider
{
    public FakeTime(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        Now = now;
        Zone = zone ?? TimeZoneInfo.Utc;
    }

    public DateTimeOffset Now { get; set; }

    public TimeZoneInfo Zone { get; }

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => Zone;
}

internal static class NoDelay
{
    public static Task Delay(TimeSpan t, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
