using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

public sealed record KeyTestResult(bool Ok, string Message);

/// <summary>
/// The "Test" buttons: one small real request per service that costs nothing (a models or account listing),
/// so a wrong key or address shows up before the first dictation.
/// </summary>
public static class KeyTester
{
    public static async Task<KeyTestResult> TestAsync(HttpClient http, SpeechService service, PlainspokenSettings settings, string apiKey, ILog log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new KeyTestResult(false, "Paste your key first.");
        }

        var t = settings.Transcription;
        var key = apiKey.Trim();
        try
        {
            if (service.IsGemini())
            {
                var client = new GeminiClient(http, t.ApiBaseUrl, () => key, log, new RetryPolicy(maxServerRetries: 0));
                using var doc = await client.SendJsonOnceAsync(HttpMethod.Get, "v1beta/models/" + Uri.EscapeDataString(ModelId.Normalize(t.ModelOf(service))),
                    null, TimeSpan.FromSeconds(15), "key-test", true, ct).ConfigureAwait(false);
            }
            else
            {
                var client = new ServiceClient(http, service, t.BaseUrlOf(service), () => key, log, new RetryPolicy(maxServerRetries: 0));
                await client.SendOnceAsync(() => new HttpRequestMessage(HttpMethod.Get, client.Url(CheckPath(service))), TimeSpan.FromSeconds(15), "key-test", ct)
                    .ConfigureAwait(false);
            }

            return new KeyTestResult(true, "Key works. You're ready to dictate.");
        }
        catch (TranscriptionException ex)
        {
            return Explain(ex, service, t.ModelOf(service));
        }
    }

    /// <summary>The key of the OpenAI-compatible clean-up service: GET {base}/models.</summary>
    public static async Task<KeyTestResult> TestCleanupAsync(HttpClient http, PlainspokenSettings settings, string apiKey, ILog log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new KeyTestResult(false, "Paste your key first.");
        }

        var c = settings.Transcription.Cleanup;
        var key = apiKey.Trim();
        var client = new ServiceClient(http, SpeechService.OpenAI, c.OpenaiBaseUrl, () => key, log, new RetryPolicy(maxServerRetries: 0));
        try
        {
            await client.SendOnceAsync(() => new HttpRequestMessage(HttpMethod.Get, client.Url("models")), TimeSpan.FromSeconds(15), "key-test", ct)
                .ConfigureAwait(false);
            return new KeyTestResult(true, "Key works.");
        }
        catch (TranscriptionException ex)
        {
            return Explain(ex, SpeechService.OpenAI, c.OpenaiModel);
        }
    }

    /// <summary>A free, read-only endpoint for each service.</summary>
    internal static string CheckPath(SpeechService service) => service switch
    {
        SpeechService.Sarvam => "v2/models",
        SpeechService.Deepgram => "v1/projects",
        SpeechService.ElevenLabs => "v1/models",
        _ => "models",
    };

    private static KeyTestResult Explain(TranscriptionException ex, SpeechService service, string model)
    {
        var name = service.IsGemini() ? "Google" : Capitalise(service.ShortName());
        var notAccepted = new KeyTestResult(false, $"{name} did not accept this key. Copy it again from {service.Info().KeyUrl}.");
        return ex.Kind switch
        {
            TranscriptionErrorKind.RateLimited or TranscriptionErrorKind.DailyQuota =>
                new(true, $"The key works, but {name} says it is busy or over its limit right now."),
            TranscriptionErrorKind.NoCredits => new(true, "The key works, but the account seems to be out of credits."),
            TranscriptionErrorKind.InvalidKey when ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase) && !service.IsGemini() =>
                new(true, "The key was accepted, but it may not be allowed to read account details. It will be checked on your first dictation."),
            TranscriptionErrorKind.InvalidKey => notAccepted,
            TranscriptionErrorKind.BadRequest when ex.StatusCode == 404 && !service.IsGemini() =>
                new(true, "The address answered, but it can't list models, so the key will be checked on your first dictation."),
            TranscriptionErrorKind.BadRequest => notAccepted,
            TranscriptionErrorKind.ModelNotFound => new(false, $"The key works, but the model \"{model}\" isn't available to it."),
            TranscriptionErrorKind.Network => new(false, $"Couldn't reach {name}. Check your connection and the address."),
            TranscriptionErrorKind.Timeout => new(false, $"{name} took too long to answer. Try again."),
            _ => new(false, "Couldn't check the key right now. Try again in a minute."),
        };
    }

    internal static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
