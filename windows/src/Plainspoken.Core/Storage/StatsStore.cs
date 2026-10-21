using System.Globalization;
using System.Text.Json;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Storage;

/// <summary>One dictation as stored for the stats window: timings and which service, never any text.</summary>
public sealed record StatEntry(
    string Utc,
    string Service,
    double AudioSeconds,
    long ListenMs,
    long CleanupMs,
    bool Ok,
    string? Error = null,
    bool Streamed = false,
    bool UsedBackup = false,
    bool Hedged = false,
    string? CleanedBy = null)
{
    public DateTimeOffset Time =>
        DateTimeOffset.TryParse(Utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UnixEpoch;
}

/// <summary>Totals for one service. Times are medians, so one slow request doesn't distort them.</summary>
public sealed record ServiceStats(
    SpeechService Service,
    int Dictations,
    int Failures,
    long? MedianListenMs,
    long? MedianTotalMs,
    double StreamedShare,
    double MinutesThisMonth,
    double MinutesAllTime);

/// <summary>The last <c>capacity</c> dictations' timings, newest last, persisted as JSON. Thread-safe.</summary>
public sealed class StatsStore : IStatsRecorder
{
    public const int DefaultCapacity = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly ILog _log;
    private readonly int _capacity;
    private readonly Lock _gate = new();
    private readonly List<StatEntry> _entries;

    public StatsStore(string path, ILog log, int capacity = DefaultCapacity)
    {
        _path = path;
        _log = log;
        _capacity = capacity;
        _entries = Load();
    }

    public IReadOnlyList<StatEntry> All
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Record(DictationStat stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        var entry = new StatEntry(stat.Utc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), stat.Service.Id(), Math.Round(stat.AudioSeconds, 1),
            stat.ListenMs, stat.CleanupMs, stat.Ok, stat.ErrorKind, stat.Streamed, stat.UsedBackup, stat.Hedged, stat.CleanedBy);
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > _capacity)
            {
                _entries.RemoveRange(0, _entries.Count - _capacity);
            }

            Persist();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (IOException ex)
            {
                _log.Warn($"Could not delete stats file: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>Per-service totals, services with the most dictations first.</summary>
    public IReadOnlyList<ServiceStats> Summary(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var monthStartLocal = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var monthStart = new DateTimeOffset(monthStartLocal, zone.GetUtcOffset(monthStartLocal));
        return All
            .GroupBy(e => e.Service, StringComparer.Ordinal)
            .Select(g => (Service: SpeechServices.ById(g.Key), List: g.ToList()))
            .Where(x => x.Service is not null)
            .Select(x =>
            {
                var ok = x.List.Where(e => e.Ok).ToList();
                return new ServiceStats(
                    x.Service!.Value,
                    x.List.Count,
                    x.List.Count(e => !e.Ok),
                    Median(ok.Select(e => e.ListenMs)),
                    Median(ok.Select(e => e.ListenMs + e.CleanupMs)),
                    ok.Count == 0 ? 0 : (double)ok.Count(e => e.Streamed) / ok.Count,
                    x.List.Where(e => e.Time >= monthStart).Sum(e => e.AudioSeconds) / 60,
                    x.List.Sum(e => e.AudioSeconds) / 60);
            })
            .OrderByDescending(s => s.Dictations)
            .ToList();
    }

    internal static long? Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? null : sorted[sorted.Count / 2];
    }

    private void Persist()
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not save stats: {ex.GetType().Name}");
        }
    }

    private List<StatEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var list = JsonSerializer.Deserialize<List<StatEntry>>(File.ReadAllText(_path), JsonOptions) ?? [];
            return list.Where(e => e is not null).TakeLast(_capacity).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"Stats file unreadable ({ex.GetType().Name}); starting empty.");
            return [];
        }
    }
}
