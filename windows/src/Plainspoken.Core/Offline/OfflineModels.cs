using System.Security.Cryptography;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Offline;

/// <summary>
/// A Whisper model for offline transcription, in whisper.cpp's format. It is downloaded once from whisper.cpp's page on
/// Hugging Face and checked against <paramref name="Sha256"/> before it is used.
/// </summary>
public sealed record OfflineModel(string Id, string File, string Label, long SizeBytes, string Sha256)
{
    public string Url => OfflineModels.BaseUrl + File;

    /// <summary>Its size and checksum are known, so it can be downloaded and checked.</summary>
    public bool Pinned => SizeBytes > 0 && Sha256.Length == 64;
}

/// <summary>The models Plainspoken offers. Same list and checksums as Android.</summary>
public static class OfflineModels
{
    public const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    // Sizes and SHA-256s come from whisper.cpp's Hugging Face listing, printed by CI (job "offline-models").
    // A model without them can't be downloaded.
    public static readonly OfflineModel Base = new OfflineModel("base", "ggml-base-q5_1.bin", "Base · about 60 MB · fastest", 0, "");
    public static readonly OfflineModel Small = new OfflineModel("small", "ggml-small-q5_1.bin", "Small · about 190 MB · more accurate", 0, "");
    public static readonly OfflineModel Medium = new OfflineModel("medium", "ggml-medium-q5_0.bin", "Medium · about 515 MB · accurate", 0, "");
    public static readonly OfflineModel LargeTurbo = new OfflineModel("large-v3-turbo", "ggml-large-v3-turbo-q5_0.bin", "Large v3 Turbo · about 550 MB · most accurate", 0, "");

    /// <summary>On phones; the first is the default.</summary>
    public static IReadOnlyList<OfflineModel> Phone { get; } = [Small, Base];

    /// <summary>On Windows; the first is the default.</summary>
    public static IReadOnlyList<OfflineModel> Pc { get; } = [Medium, LargeTurbo, Small];

    public static OfflineModel Find(string? id, IReadOnlyList<OfflineModel> offered)
    {
        ArgumentNullException.ThrowIfNull(offered);
        return offered.FirstOrDefault(m => m.Id == id) ?? offered[0];
    }
}

/// <summary>What Whisper is told, from the dictation's settings.</summary>
public static class OfflineWhisper
{
    /// <summary>English presets → "en"; with Hindi, Whisper detects the language (forcing "en" would make it translate).</summary>
    public static string Language(TranscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Languages == LanguagePreset.English ? "en" : "auto";
    }

    /// <summary>The same prompt as for OpenAI-compatible Whisper services: the wanted script, then names to spell right.</summary>
    public static string Prompt(TranscriptionRequest request) => OpenAIEngine.Prompt(request, new SpeechServiceSettings());

    /// <summary>Whisper's text without its notes for non-speech ("[BLANK_AUDIO]", "[Music]") and extra spaces.</summary>
    public static string Tidy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]\n]{1,40}\]", " ", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));
        return string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Downloaded models in a folder: download (resuming a partial one, checked against the SHA-256), delete, find.</summary>
public sealed class ModelStore(string directory, HttpClient http, ILog log)
{
    private const long ReportEvery = 1 << 20;

    public string FileOf(OfflineModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(directory, model.File);
    }

    private string PartOf(OfflineModel model) => FileOf(model) + ".part";

    public bool IsDownloaded(OfflineModel model) => new FileInfo(FileOf(model)) is { Exists: true } f && f.Length == model.SizeBytes;

    /// <summary>Space used by models and partial downloads.</summary>
    public long BytesOnDisk() => Directory.Exists(directory) ? new DirectoryInfo(directory).GetFiles().Sum(f => f.Length) : 0;

    public void Delete(OfflineModel model)
    {
        File.Delete(FileOf(model));
        File.Delete(PartOf(model));
    }

    /// <summary>Downloads the model; <paramref name="progress"/> gets (bytes so far, total) every megabyte or so.</summary>
    /// <exception cref="IOException">The download failed or the file doesn't match.</exception>
    public async Task DownloadAsync(OfflineModel model, Action<long, long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Pinned)
        {
            throw new IOException($"model download: {model.Id} has no pinned checksum");
        }

        Directory.CreateDirectory(directory);
        var part = PartOf(model);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var have = File.Exists(part) && new FileInfo(part).Length <= model.SizeBytes ? new FileInfo(part).Length : 0;
        var buffer = new byte[64 * 1024];
        if (have > 0)
        {
            await using var existing = File.OpenRead(part);
            int n;
            while ((n = await existing.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, n);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
        if (have > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"model download: HTTP {(int)response.StatusCode}");
        }

        if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            have = 0; // the server sent the whole file
            sha.GetHashAndReset();
        }

        await using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write))
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var reported = have;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                sha.AppendData(buffer, 0, n);
                have += n;
                if (have - reported >= ReportEvery)
                {
                    reported = have;
                    progress?.Invoke(have, model.SizeBytes);
                }
            }
        }

        var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (have != model.SizeBytes || !actual.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new IOException($"model download: the file doesn't match (size {have}, sha256 {actual[..12]}…)");
        }

        File.Move(part, FileOf(model), overwrite: true);
        progress?.Invoke(have, model.SizeBytes);
        log.Info($"offline model {model.Id}: downloaded");
    }
}
