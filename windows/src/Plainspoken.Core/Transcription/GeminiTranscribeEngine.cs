using System.Text.Json;
using System.Text.Json.Nodes;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

/// <summary>
/// Default engine: gemini-3.5-transcribe through the Interactions API.
/// Clips up to <see cref="InlineLimitBytes"/> are sent inline in a single request (one round trip).
/// Longer clips, or if Google rejects inline audio, use the Files API:
/// upload → POST /v1beta/interactions → delete the file.
/// </summary>
public sealed class GeminiTranscribeEngine : ITranscriptionEngine
{
    /// <summary>About 5 minutes of 16 kHz mono PCM16. Verified working inline on 2026-09-30 (9.6 MB).</summary>
    public const int InlineLimitBytes = 10 * 1024 * 1024;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly GeminiClient _client;
    private readonly string _model;
    private readonly TimeSpan _timeout;

    public GeminiTranscribeEngine(GeminiClient client, string model, TimeSpan timeout)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _model = ModelId.Normalize(model);
        _timeout = timeout;
    }

    public EngineKind Kind => EngineKind.Transcribe;

    /// <summary>The background delete of the last uploaded file (exposed for tests).</summary>
    internal Task CleanupTask { get; private set; } = Task.CompletedTask;

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Wav.Length <= InlineLimitBytes)
        {
            try
            {
                var body = BuildInlineRequest(_model, request);
                var timeout = _timeout + TimeSpan.FromSeconds(request.DurationSeconds / 4) +
                              TimeSpan.FromSeconds(request.Wav.Length / (64.0 * 1024));
                return await PostAsync(body, timeout, "interactions-inline", ct).ConfigureAwait(false);
            }
            catch (TranscriptionException ex) when (ex.Kind == TranscriptionErrorKind.BadRequest)
            {
                // Inline audio isn't in the published docs; if Google ever refuses it, use the upload path.
                _client.Log.Warn($"inline audio rejected ({ex.StatusCode}); retrying with file upload");
            }
        }

        return await TranscribeViaFileAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<string> TranscribeViaFileAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var file = await _client.UploadAsync(request.Wav, "audio/wav", _timeout, ct).ConfigureAwait(false);
        try
        {
            file = await _client.WaitUntilActiveAsync(file, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var body = BuildRequest(_model, file.Uri, request);
            var timeout = _timeout + TimeSpan.FromSeconds(request.DurationSeconds / 4);
            return await PostAsync(body, timeout, "interactions-post", ct).ConfigureAwait(false);
        }
        finally
        {
            // Don't make the user wait for the delete; it never throws.
            CleanupTask = _client.DeleteFileAsync(file.Name);
        }
    }

    private Task<string> PostAsync(JsonObject body, TimeSpan timeout, string operation, CancellationToken ct) =>
        _client.Retry.ExecuteAsync("interactions", async c =>
        {
            using var doc = await _client.SendJsonOnceAsync(HttpMethod.Post, "v1beta/interactions", body, timeout, operation, true, c).ConfigureAwait(false);
            return await ReadResultAsync(doc.RootElement, timeout, c).ConfigureAwait(false);
        }, _client.Log, ct);

    /// <summary>Request body with the audio inline (base64), for clips up to <see cref="InlineLimitBytes"/>.</summary>
    public static JsonObject BuildInlineRequest(string model, TranscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Build(model, new JsonObject
        {
            ["type"] = "audio",
            ["data"] = Convert.ToBase64String(request.Wav),
            ["mime_type"] = "audio/wav",
        }, request);
    }

    /// <summary>The exact JSON body sent to /v1beta/interactions (snapshot-tested).</summary>
    public static JsonObject BuildRequest(string model, string fileUri, TranscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Build(model, new JsonObject
        {
            ["type"] = "audio",
            ["uri"] = fileUri,
            ["mime_type"] = "audio/wav",
        }, request);
    }

    private static JsonObject Build(string model, JsonObject audio, TranscriptionRequest request)
    {
        var config = new JsonObject
        {
            ["mode"] = request.Mode == TranscriptionMode.Verbatim ? "verbatim" : "smart",
        };
        if (request.Vocabulary.Count > 0)
        {
            config["custom_vocabulary"] = new JsonArray(request.Vocabulary.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        if (request.LanguageCodes.Count > 0)
        {
            config["language_codes"] = new JsonArray(request.LanguageCodes.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        return new JsonObject
        {
            ["model"] = ModelId.Normalize(model),
            ["input"] = new JsonArray(audio),
            ["generation_config"] = new JsonObject { ["transcription_config"] = config },
        };
    }

    private async Task<string> ReadResultAsync(JsonElement root, TimeSpan timeout, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var result = ResponseParsers.ParseInteraction(root);
        JsonDocument? polled = null;
        try
        {
            while (IsRunning(result.Status) && result.Id is not null)
            {
                if (DateTimeOffset.UtcNow - started > timeout)
                {
                    throw new TranscriptionException(TranscriptionErrorKind.Timeout, $"interaction still {result.Status}");
                }

                await _client.Retry.Delay(PollInterval, ct).ConfigureAwait(false);
                polled?.Dispose();
                polled = await _client.SendJsonOnceAsync(HttpMethod.Get, "v1beta/interactions/" + Uri.EscapeDataString(result.Id), null,
                    TimeSpan.FromSeconds(20), "interactions-get", true, ct).ConfigureAwait(false);
                root = polled.RootElement;
                result = ResponseParsers.ParseInteraction(root);
            }

            if (result.Status is "failed" or "cancelled" or "canceled")
            {
                // Treated as a server problem so the retry policy tries again.
                throw new TranscriptionException(TranscriptionErrorKind.Server, $"interaction {result.Status}: {JsonShape.Describe(root)}");
            }

            if (result.Text is not null)
            {
                return result.Text;
            }

            if (result.Status == "completed" && result.HasOutputContainer)
            {
                return string.Empty; // finished, but nothing was said
            }

            throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, "interaction: " + JsonShape.Describe(root));
        }
        finally
        {
            polled?.Dispose();
        }
    }

    private static bool IsRunning(string? status) => status is "in_progress" or "queued" or "pending" or "running";
}

internal static class ModelId
{
    /// <summary>Accepts "gemini-x" or "models/gemini-x"; returns "gemini-x".</summary>
    public static string Normalize(string model)
    {
        var m = (model ?? string.Empty).Trim();
        return m.StartsWith("models/", StringComparison.Ordinal) ? m["models/".Length..] : m;
    }
}
