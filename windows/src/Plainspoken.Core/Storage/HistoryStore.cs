using System.Text.Json;
using Plainspoken.Core.Logging;

namespace Plainspoken.Core.Storage;

public sealed record HistoryEntry(Guid Id, DateTimeOffset CreatedUtc, string Text, double DurationSeconds);

/// <summary>The last N transcripts, newest first, persisted as JSON. Thread-safe.</summary>
public sealed class HistoryStore
{
    public const int DefaultCapacity = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly int _capacity;
    private readonly ILog _log;
    private readonly object _gate = new();
    private List<HistoryEntry> _entries;

    public HistoryStore(string path, ILog log, int capacity = DefaultCapacity)
    {
        _path = path;
        _log = log;
        _capacity = capacity;
        _entries = Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public HistoryEntry Add(string text, double durationSeconds, DateTimeOffset nowUtc)
    {
        var entry = new HistoryEntry(Guid.NewGuid(), nowUtc, text, Math.Round(durationSeconds, 1));
        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > _capacity)
            {
                _entries.RemoveRange(_capacity, _entries.Count - _capacity);
            }

            Persist();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
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
                _log.Warn($"Could not delete history file: {ex.GetType().Name}");
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Persist()
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not save history: {ex.GetType().Name}");
        }
    }

    private List<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var list = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path), JsonOptions) ?? [];
            return list.Where(e => e is not null && !string.IsNullOrEmpty(e.Text))
                .OrderByDescending(e => e.CreatedUtc)
                .Take(_capacity)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warn($"History file unreadable ({ex.GetType().Name}); starting empty.");
            return [];
        }
    }
}
