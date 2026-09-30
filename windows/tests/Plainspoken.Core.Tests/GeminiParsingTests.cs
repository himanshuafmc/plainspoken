using System.Text.Json;
using System.Text.Json.Nodes;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public class RequestSnapshotTests
{
    private static TranscriptionRequest Request(LanguagePreset lang = LanguagePreset.EnglishHindi,
        TranscriptionMode mode = TranscriptionMode.Smart, params string[] vocab) =>
        new(WavBytes(), 1.0, mode, lang, vocab);

    private static byte[] WavBytes() => Audio.WavEncoder.Encode(new short[] { 1, 2, 3 });

    [Fact]
    public void Interactions_request_matches_snapshot()
    {
        var body = GeminiTranscribeEngine.BuildRequest("gemini-3.5-transcribe",
            "https://generativelanguage.googleapis.com/v1beta/files/1qp4xte94dib",
            Request(vocab: ["WhatsApp", "Plainspoken"]));

        var expected = JsonNode.Parse(Fixtures.Read("interactions-request.expected.json"))!;
        Assert.True(JsonNode.DeepEquals(expected, body), body.ToJsonString());
    }

    [Fact]
    public void Auto_detect_and_empty_vocabulary_omit_the_fields()
    {
        var body = GeminiTranscribeEngine.BuildRequest("models/gemini-3.5-transcribe", "u", Request(LanguagePreset.Auto, TranscriptionMode.Verbatim));
        var config = body["generation_config"]!["transcription_config"]!.AsObject();
        Assert.Equal("verbatim", config["mode"]!.GetValue<string>());
        Assert.False(config.ContainsKey("language_codes"));
        Assert.False(config.ContainsKey("custom_vocabulary"));
        Assert.Equal("gemini-3.5-transcribe", body["model"]!.GetValue<string>());
    }

    [Fact]
    public void Generate_request_has_inline_audio_prompt_and_vocabulary()
    {
        var req = Request(vocab: ["PowerPoint"]);
        var body = GeminiGenerateEngine.BuildRequest(req, null);
        var parts = body["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal("audio/wav", parts[0]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(req.Wav), parts[0]!["inlineData"]!["data"]!.GetValue<string>());
        Assert.Contains("PowerPoint", parts[1]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("English, Hindi", parts[1]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("filler", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, body["generationConfig"]!["temperature"]!.GetValue<int>());

        var viaFile = GeminiGenerateEngine.BuildRequest(req, "https://x/v1beta/files/f1");
        Assert.Equal("https://x/v1beta/files/f1", viaFile["contents"]![0]!["parts"]![0]!["fileData"]!["fileUri"]!.GetValue<string>());
    }

    [Fact]
    public void Verbatim_prompt_keeps_fillers()
    {
        Assert.Contains("verbatim", GeminiGenerateEngine.SystemPrompt(TranscriptionMode.Verbatim), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove filler", GeminiGenerateEngine.SystemPrompt(TranscriptionMode.Verbatim), StringComparison.Ordinal);
    }
}

public class ResponseParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Reads_the_verified_interactions_shape()
    {
        var r = ResponseParsers.ParseInteraction(Parse(Fixtures.Read("interactions-response.json")));
        Assert.Equal("completed", r.Status);
        Assert.StartsWith("v1_", r.Id, StringComparison.Ordinal);
        Assert.Equal("So, please add milk, eggs, and bread to the shopping list. The total is 2,500 rupees.", r.Text);
    }

    [Theory]
    [InlineData("""{"status":"completed","output_text":"hi there"}""", "hi there")]
    [InlineData("""{"status":"completed","outputText":"hi there"}""", "hi there")]
    [InlineData("""{"status":"completed","outputs":[{"type":"text","text":"hi "},{"type":"thought","text":"x"},{"type":"text","text":"there"}]}""", "hi there")]
    [InlineData("""{"status":"completed","steps":[{"type":"thought","content":[{"type":"text","text":"x"}]},{"content":[{"type":"text","text":"hi there"}]}]}""", "hi there")]
    [InlineData("""{"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"a"},{"type":"audio"},{"type":"text","text":"b"}]}]}""", "ab")]
    public void Tolerates_small_shape_differences(string json, string expected) =>
        Assert.Equal(expected, ResponseParsers.ParseInteraction(Parse(json)).Text);

    [Fact]
    public void Completed_without_text_is_distinguished_from_unknown_shape()
    {
        var empty = ResponseParsers.ParseInteraction(Parse("""{"status":"completed","steps":[]}"""));
        Assert.Null(empty.Text);
        Assert.True(empty.HasOutputContainer);

        var odd = ResponseParsers.ParseInteraction(Parse("""{"status":"completed","result":{"transcript":"x"}}"""));
        Assert.Null(odd.Text);
        Assert.False(odd.HasOutputContainer);
    }

    [Fact]
    public void Reads_generate_content_and_skips_thoughts()
    {
        Assert.StartsWith("So, please add milk", ResponseParsers.ParseGenerateContent(Parse(Fixtures.Read("generate-response.json"))), StringComparison.Ordinal);
        Assert.Equal("final", ResponseParsers.ParseGenerateContent(Parse(
            """{"candidates":[{"content":{"parts":[{"text":"thinking…","thought":true},{"text":"final"}]},"finishReason":"STOP"}]}""")));
        Assert.Equal("", ResponseParsers.ParseGenerateContent(Parse("""{"candidates":[{"finishReason":"STOP"}]}""")));
    }

    [Fact]
    public void Generate_content_blocked_and_odd_shapes_throw()
    {
        var blocked = Assert.Throws<TranscriptionException>(() => ResponseParsers.ParseGenerateContent(Parse("""{"promptFeedback":{"blockReason":"SAFETY"}}""")));
        Assert.Equal(TranscriptionErrorKind.Blocked, blocked.Kind);

        var safety = Assert.Throws<TranscriptionException>(() => ResponseParsers.ParseGenerateContent(Parse("""{"candidates":[{"finishReason":"SAFETY"}]}""")));
        Assert.Equal(TranscriptionErrorKind.Blocked, safety.Kind);

        var odd = Assert.Throws<TranscriptionException>(() => ResponseParsers.ParseGenerateContent(Parse("""{"text":"secret words"}""")));
        Assert.Equal(TranscriptionErrorKind.UnexpectedResponse, odd.Kind);
        Assert.DoesNotContain("secret", odd.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_shape_never_includes_text_content()
    {
        var shape = JsonShape.Describe(Fixtures.Read("interactions-response.json"));
        Assert.DoesNotContain("milk", shape, StringComparison.Ordinal);
        Assert.Contains("status:\"completed\"", shape, StringComparison.Ordinal);
        Assert.Contains("text:str(", shape, StringComparison.Ordinal);
        Assert.Contains("type:\"model_output\"", shape, StringComparison.Ordinal);
        Assert.Equal("non-JSON (8 chars)", JsonShape.Describe("<html>hi"));
    }
}

public class GeminiErrorTests
{
    [Fact]
    public void Per_minute_429_from_interactions_api()
    {
        var ex = GeminiErrors.FromResponse(429, Fixtures.Read("error-429-per-minute.json"), TimeSpan.FromSeconds(43), modelCall: true);
        Assert.Equal(TranscriptionErrorKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(43), ex.RetryAfter);
        Assert.Contains("too_many_requests", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_delay_is_read_from_message_when_header_missing()
    {
        var ex = GeminiErrors.FromResponse(429, Fixtures.Read("error-429-per-minute.json"), null, modelCall: true);
        Assert.Equal(TimeSpan.FromSeconds(43), ex.RetryAfter);
        Assert.Equal(TranscriptionErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public void Daily_429_with_quota_failure_details()
    {
        var ex = GeminiErrors.FromResponse(429, Fixtures.Read("error-429-daily.json"), null, modelCall: true);
        Assert.Equal(TranscriptionErrorKind.DailyQuota, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(40104), ex.RetryAfter);
    }

    [Fact]
    public void Day_limit_wording_with_short_retry_is_per_minute()
    {
        // Real wording from 2026-09-30: the message names the daily limit, yet a retry 30 s later worked.
        var ex = GeminiErrors.FromResponse(429, Fixtures.Read("error-429-day-wording.json"), null, modelCall: true);
        Assert.Equal(TranscriptionErrorKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(17), ex.RetryAfter);
        Assert.Equal(TranscriptionErrorKind.DailyQuota,
            GeminiErrors.Classify429("limit: 25 requests per day on Free Tier", [], null));
        Assert.Equal(TranscriptionErrorKind.DailyQuota,
            GeminiErrors.Classify429("quota exceeded", ["GenerateRequestsPerDayPerProjectPerModel-FreeTier"], TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Unexplained_429_is_treated_as_daily()
    {
        Assert.Equal(TranscriptionErrorKind.DailyQuota, GeminiErrors.FromResponse(429, "{}", null, true).Kind);
        Assert.Equal(TranscriptionErrorKind.RateLimited, GeminiErrors.FromResponse(429, "{}", TimeSpan.FromSeconds(20), true).Kind);
    }

    [Theory]
    [InlineData(400, "error-400-invalid-key.json", TranscriptionErrorKind.InvalidKey)]
    [InlineData(401, null, TranscriptionErrorKind.InvalidKey)]
    [InlineData(408, null, TranscriptionErrorKind.Timeout)]
    [InlineData(500, null, TranscriptionErrorKind.Server)]
    [InlineData(503, null, TranscriptionErrorKind.Server)]
    [InlineData(418, null, TranscriptionErrorKind.BadRequest)]
    public void Maps_status_codes(int status, string? fixture, TranscriptionErrorKind expected)
    {
        var body = fixture is null ? "<html>oops</html>" : Fixtures.Read(fixture);
        Assert.Equal(expected, GeminiErrors.FromResponse(status, body, null, modelCall: true).Kind);
    }

    [Fact]
    public void Status_403_depends_on_whether_it_is_about_a_file()
    {
        Assert.Equal(TranscriptionErrorKind.InvalidKey, GeminiErrors.FromResponse(403,
            """{"error":{"code":403,"message":"Method doesn't allow unregistered callers (callers without established identity).","status":"PERMISSION_DENIED"}}""", null, true).Kind);
        Assert.Equal(TranscriptionErrorKind.BadRequest, GeminiErrors.FromResponse(403,
            """{"error":{"message":"You do not have permission to access the File doesnotexist1 or it may not exist.","code":"permission_denied"}}""", null, true).Kind);
    }

    [Fact]
    public void Status_404_on_a_model_call_means_model_not_found()
    {
        var body = """{"error":{"message":"Model 'gemini-9-nope' not found.","code":"not_found"}}""";
        Assert.Equal(TranscriptionErrorKind.ModelNotFound, GeminiErrors.FromResponse(404, body, null, true).Kind);
        Assert.Equal(TranscriptionErrorKind.BadRequest, GeminiErrors.FromResponse(404, "{}", null, false).Kind);
    }

    [Fact]
    public void Plain_400_is_bad_request_and_keys_are_redacted()
    {
        var ex = GeminiErrors.FromResponse(400,
            $$$"""{"error":{"message":"Invalid enum value 'weird' key={{{FakeKey.Value}}}","code":"invalid_request"}}""", null, true);
        Assert.Equal(TranscriptionErrorKind.BadRequest, ex.Kind);
        Assert.DoesNotContain(FakeKey.Value, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Please retry in 43s or upgrade", 43.0)]
    [InlineData("Please retry in 1.5s.", 1.5)]
    [InlineData("retry in 250ms", 0.25)]
    public void Parses_retry_hints(string message, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), GeminiErrors.ParseRetryFromMessage(message));
}
