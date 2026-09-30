using System.Globalization;
using System.Text.Json;
using Plainspoken.Core.Logging;

namespace Plainspoken.Core.Storage;

public sealed record PendingRecording(string Id, string AudioPath, DateTimeOffset CreatedUtc, double DurationSeconds, string? LastError, int Attempts);

/// <summary>
/// Recordings waiting for a transcript. Each is saved (WAV + small JSON) before any network call
/// and deleted only once a transcript has been obtained, so a failure never loses speech.
/// </summary>
public sealed class PendingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _dir;
    private readonly ILog _log;

    public PendingStore(string directory, ILog log)
    {
        _dir = directory;
        _log = log;
    }

    public string Directory => _dir;

    public event EventHandler? Changed;

    public PendingRecording Save(byte[] wav, double durationSeconds, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(wav);
        System.IO.Directory.CreateDirectory(_dir);
        var id = nowUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" +
                 Guid.NewGuid().ToString("N")[..6];
        var rec = new PendingRecording(id, Path.Combine(_dir, id + ".wav"), nowUtc, Math.Round(durationSeconds, 1), null, 0);
        AtomicFile.WriteAllBytes(rec.AudioPath, wav);
        WriteMeta(rec);
        _log.Info($"Pending saved: {wav.Length} bytes, {rec.DurationSeconds}s");
        Changed?.Invoke(this, EventArgs.Empty);
        return rec;
    }

    /// <summary>Newest first. Recordings without readable metadata are still listed.</summary>
    public IReadOnlyList<PendingRecording> List()
    {
        if (!System.IO.Directory.Exists(_dir))
        {
            return [];
        }

        var list = new List<PendingRecording>();
        foreach (var wav in System.IO.Directory.EnumerateFiles(_dir, "*.wav"))
        {
            list.Add(ReadMeta(wav));
        }

        return list.OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id, StringComparer.Ordinal).ToList();
    }

    public PendingRecording? Latest()
    {
        var list = List();
        return list.Count > 0 ? list[0] : null;
    }

    public int Count => System.IO.Directory.Exists(_dir) ? System.IO.Directory.EnumerateFiles(_dir, "*.wav").Count() : 0;

    public byte[] ReadAudio(PendingRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var full = Path.GetFullPath(recording.AudioPath);
        if (!full.StartsWith(Path.GetFullPath(_dir), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pending recording is outside the pending folder.");
        }

        return File.ReadAllBytes(full);
    }

    public PendingRecording MarkFailed(PendingRecording recording, string errorKind)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var updated = recording with { LastError = errorKind, Attempts = recording.Attempts + 1 };
        try
        {
            WriteMeta(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not update pending metadata: {ex.GetType().Name}");
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public void Delete(PendingRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        foreach (var path in new[] { recording.AudioPath, MetaPath(recording.AudioPath) })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not delete pending file: {ex.GetType().Name}");
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string MetaPath(string audioPath) => Path.ChangeExtension(audioPath, ".json");

    private static void WriteMeta(PendingRecording rec)
    {
        var meta = new Meta(rec.CreatedUtc, rec.DurationSeconds, rec.LastError, rec.Attempts);
        AtomicFile.WriteAllText(MetaPath(rec.AudioPath), JsonSerializer.Serialize(meta, JsonOptions));
    }

    private PendingRecording ReadMeta(string wavPath)
    {
        var id = Path.GetFileNameWithoutExtension(wavPath);
        try
        {
            var metaPath = MetaPath(wavPath);
            if (File.Exists(metaPath))
            {
                var m = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath), JsonOptions);
                if (m is not null)
                {
                    return new PendingRecording(id, wavPath, m.CreatedUtc, m.DurationSeconds, m.LastError, m.Attempts);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"Pending metadata unreadable ({ex.GetType().Name}); using file info.");
        }

        var created = new DateTimeOffset(File.GetCreationTimeUtc(wavPath), TimeSpan.Zero);
        return new PendingRecording(id, wavPath, created, 0, null, 0);
    }

    private sealed record Meta(DateTimeOffset CreatedUtc, double DurationSeconds, string? LastError, int Attempts);
}
