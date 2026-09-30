using System.Globalization;

namespace Plainspoken.Core.Logging;

/// <summary>
/// One log file per day (plainspoken-yyyyMMdd.log); files older than <c>keepDays</c> are deleted.
/// Thread-safe. Logging failures are swallowed: logging must never crash the app.
/// </summary>
public sealed class FileLog : ILog
{
    /// <summary>Log file names are "&lt;app&gt;-yyyyMMdd.log", e.g. "plainspoken-20260930.log".</summary>
    private static readonly string Prefix = AppInfo.Name.ToLowerInvariant() + "-";

    private readonly string _dir;
    private readonly int _keepDays;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private DateOnly _lastCleanup;

    public FileLog(string directory, int keepDays = 7, Func<DateTimeOffset>? now = null)
    {
        _dir = directory;
        _keepDays = keepDays;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string Directory => _dir;

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public string CurrentFilePath => Path.Combine(_dir, FileNameFor(DateOnly.FromDateTime(_now().DateTime)));

    private static string FileNameFor(DateOnly day) => $"{Prefix}{day:yyyyMMdd}.log";

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            var now = _now();
            var line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} {level,-5} {Redactor.Clean(message, 2000)}");
            if (ex is not null)
            {
                // Exception type, sanitised message and stack. Our own exceptions never carry transcript text.
                line += Environment.NewLine + "    " + ex.GetType().FullName + ": " + Redactor.Clean(ex.Message) +
                        Environment.NewLine + ex.StackTrace;
            }

            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_dir);
                var today = DateOnly.FromDateTime(now.DateTime);
                if (today != _lastCleanup)
                {
                    _lastCleanup = today;
                    Cleanup(today);
                }

                File.AppendAllText(Path.Combine(_dir, FileNameFor(today)), line + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Ignore: never let logging take the app down.
        }
    }

    private void Cleanup(DateOnly today)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, Prefix + "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(name.AsSpan(Prefix.Length), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
                day < today.AddDays(-(_keepDays - 1)))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Try again tomorrow.
                }
            }
        }
    }
}
