using Plainspoken.Core.Logging;

namespace Plainspoken.Core.Transcription;

public sealed record KeyTestResult(bool Ok, string Message);

/// <summary>"Test key" button: a tiny real request (GET the model's metadata) that costs no quota.</summary>
public static class KeyTester
{
    public static async Task<KeyTestResult> TestAsync(HttpClient http, string baseUrl, string model, string apiKey, ILog log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new KeyTestResult(false, "Paste your key first.");
        }

        var client = new GeminiClient(http, baseUrl, () => apiKey, log, new RetryPolicy(maxServerRetries: 0));
        try
        {
            using var doc = await client.SendJsonAsync(HttpMethod.Get, "v1beta/models/" + Uri.EscapeDataString(ModelId.Normalize(model)),
                null, TimeSpan.FromSeconds(15), "key-test", true, ct).ConfigureAwait(false);
            return new KeyTestResult(true, "Key works. You're ready to dictate.");
        }
        catch (TranscriptionException ex) when (ex.IsRateLimit)
        {
            return new KeyTestResult(true, "The key works, but Google says it is busy or over its free limit right now.");
        }
        catch (TranscriptionException ex)
        {
            return new KeyTestResult(false, ex.Kind switch
            {
                TranscriptionErrorKind.InvalidKey or TranscriptionErrorKind.BadRequest => "Google did not accept this key. Copy it again from AI Studio.",
                TranscriptionErrorKind.ModelNotFound => $"The key works, but the model \"{model}\" isn't available to it. Check Settings → Advanced.",
                TranscriptionErrorKind.Network => "No internet connection. Check your connection and try again.",
                TranscriptionErrorKind.Timeout => "Google took too long to answer. Try again.",
                _ => "Couldn't check the key right now. Try again in a minute.",
            });
        }
    }
}
