using Plainspoken.Core.Dictation;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Storage;
using Plainspoken.Core.Time;

namespace Plainspoken.Core.Tests;

public class QuotaResetTests
{
    private static readonly TimeZoneInfo Kolkata = QuotaReset.FindZone("Asia/Kolkata", "India Standard Time");

    [Fact]
    public void Us_summer_time_resets_at_12_30_pm_ist()
    {
        var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero); // 03:00 PDT
        Assert.Equal(new DateTimeOffset(2026, 7, 16, 7, 0, 0, TimeSpan.Zero), QuotaReset.NextResetUtc(now));
        Assert.Equal("12:30 PM IST tomorrow", QuotaReset.FormatNextReset(now, Kolkata));
    }

    [Fact]
    public void Us_winter_time_resets_at_1_30_pm_ist()
    {
        var now = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero); // 02:00 PST
        Assert.Equal(new DateTimeOffset(2026, 1, 16, 8, 0, 0, TimeSpan.Zero), QuotaReset.NextResetUtc(now));
        Assert.Equal("1:30 PM IST tomorrow", QuotaReset.FormatNextReset(now, Kolkata));
    }

    [Fact]
    public void Same_day_reset_has_no_tomorrow()
    {
        var now = new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero); // 06:30 IST, 18:00 PDT previous day
        Assert.Equal("12:30 PM IST", QuotaReset.FormatNextReset(now, Kolkata));
    }

    [Fact]
    public void Handles_the_dst_switch_day()
    {
        // 2026-03-08 is the US spring-forward day. At 20:00 UTC it's 13:00 PDT; next midnight is 00:00 PDT = 07:00 UTC.
        var now = new DateTimeOffset(2026, 3, 8, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 7, 0, 0, TimeSpan.Zero), QuotaReset.NextResetUtc(now));

        // Just before midnight PST on the switch day (07:59 UTC = 23:59 PST, Mar 7) → midnight PST = 08:00 UTC.
        var before = new DateTimeOffset(2026, 3, 8, 7, 59, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero), QuotaReset.NextResetUtc(before));
    }

    [Fact]
    public void Abbreviations_are_sensible()
    {
        var at = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("IST", QuotaReset.Abbreviation(Kolkata, at));
        Assert.Equal("UTC", QuotaReset.Abbreviation(TimeZoneInfo.Utc, at));
        var custom = TimeZoneInfo.CreateCustomTimeZone("x", TimeSpan.FromHours(5.75), "weird zone", "weird zone");
        Assert.Equal("UTC+05:45", QuotaReset.Abbreviation(custom, at));
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void Keeps_newest_20_and_persists()
    {
        using var dir = new TempDir();
        var path = dir.File("history.json");
        var store = new HistoryStore(path, NullLog.Instance);
        var t0 = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 25; i++)
        {
            store.Add($"text {i}", 1.23, t0.AddMinutes(i));
        }

        Assert.Equal(20, store.Entries.Count);
        Assert.Equal("text 24", store.Entries[0].Text);
        Assert.Equal(1.2, store.Entries[0].DurationSeconds);

        var reloaded = new HistoryStore(path, NullLog.Instance);
        Assert.Equal(store.Entries.Select(e => e.Text), reloaded.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Clear_removes_the_file_and_raises_changed()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(dir.File("history.json"), NullLog.Instance);
        var changes = 0;
        store.Changed += (_, _) => changes++;
        store.Add("x", 1, DateTimeOffset.UtcNow);
        store.Clear();
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(dir.File("history.json")));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Corrupt_file_starts_empty()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("history.json"), "[{ broken");
        Assert.Empty(new HistoryStore(dir.File("history.json"), NullLog.Instance).Entries);
    }
}

public class PendingStoreTests
{
    [Fact]
    public void Save_list_mark_and_delete()
    {
        using var dir = new TempDir();
        var store = new PendingStore(dir.File("pending"), NullLog.Instance);
        Assert.Null(store.Latest());

        var t = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var first = store.Save([1, 2, 3], 1.0, t);
        var second = store.Save([4, 5, 6], 2.5, t.AddSeconds(5));

        Assert.Equal(2, store.Count);
        Assert.Equal(second.Id, store.Latest()!.Id);
        Assert.Equal(new byte[] { 4, 5, 6 }, store.ReadAudio(store.Latest()!));

        var marked = store.MarkFailed(second, "Network");
        Assert.Equal(1, marked.Attempts);
        Assert.Equal("Network", store.Latest()!.LastError);
        Assert.Equal(2.5, store.Latest()!.DurationSeconds);

        store.Delete(second);
        Assert.Equal(first.Id, store.Latest()!.Id);
        store.Delete(first);
        Assert.Equal(0, store.Count);
        Assert.Empty(Directory.GetFiles(dir.File("pending")));
    }

    [Fact]
    public void Refuses_to_read_outside_its_folder()
    {
        using var dir = new TempDir();
        var store = new PendingStore(dir.File("pending"), NullLog.Instance);
        var outside = new PendingRecording("x", dir.File("elsewhere.wav"), DateTimeOffset.UtcNow, 1, null, 0);
        Assert.Throws<InvalidOperationException>(() => store.ReadAudio(outside));
    }
}

public class FileLogTests
{
    [Fact]
    public void Writes_daily_files_redacts_keys_and_prunes_old_logs()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.FromHours(5.5));
        File.WriteAllText(Path.Combine(dir.Path, "plainspoken-20260920.log"), "old");
        File.WriteAllText(Path.Combine(dir.Path, "plainspoken-20260923.log"), "kept");
        var log = new FileLog(dir.Path, keepDays: 7, now: () => now);

        log.Info("upload: HTTP 200 key=" + FakeKey.Value);
        log.Error("boom", new InvalidOperationException("x-goog-api-key: secret123"));

        var text = File.ReadAllText(log.CurrentFilePath);
        Assert.EndsWith("plainspoken-20260929.log", log.CurrentFilePath, StringComparison.Ordinal);
        Assert.Contains("INFO  upload: HTTP 200", text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey.Value, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret123", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir.Path, "plainspoken-20260920.log")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "plainspoken-20260923.log")));
    }
}

public class TextPostProcessorTests
{
    [Theory]
    [InlineData("  Hello.  ", "Hello.")]
    [InlineData("Line one\r\nLine two\rLine three", "Line one\nLine two\nLine three")]
    [InlineData("```\nHello\n```", "Hello")]
    [InlineData("```text\nHello there\n```", "Hello there")]
    [InlineData("\"Quoted whole thing\"", "Quoted whole thing")]
    [InlineData("He said \"hi\" and \"bye\"", "He said \"hi\" and \"bye\"")]
    [InlineData("   ", "")]
    public void Cleans(string raw, string expected) => Assert.Equal(expected, TextPostProcessor.Clean(raw));

    [Fact]
    public void Trailing_space_only_when_needed()
    {
        Assert.Equal("Hi. ", TextPostProcessor.ForInsertion("Hi.", true));
        Assert.Equal("Hi.", TextPostProcessor.ForInsertion("Hi.", false));
        Assert.Equal("List:\n", TextPostProcessor.ForInsertion("List:\n", true));
        Assert.Equal("", TextPostProcessor.ForInsertion("", true));
    }
}
