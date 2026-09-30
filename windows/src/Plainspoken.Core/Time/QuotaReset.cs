using System.Globalization;

namespace Plainspoken.Core.Time;

/// <summary>
/// Gemini's free daily quota resets at midnight Pacific time. This computes that moment
/// (DST-aware, via the America/Los_Angeles zone) and formats it in the user's time zone.
/// </summary>
public static class QuotaReset
{
    public static TimeZoneInfo Pacific { get; } = FindZone("America/Los_Angeles", "Pacific Standard Time");

    public static DateTimeOffset NextResetUtc(DateTimeOffset now, TimeZoneInfo? pacific = null)
    {
        var zone = pacific ?? Pacific;
        var laNow = TimeZoneInfo.ConvertTime(now, zone);
        var nextMidnight = DateTime.SpecifyKind(laNow.Date.AddDays(1), DateTimeKind.Unspecified);
        // Midnight is never skipped or repeated in Los Angeles (DST switches at 2 AM).
        var offset = zone.GetUtcOffset(nextMidnight);
        return new DateTimeOffset(nextMidnight, offset).ToUniversalTime();
    }

    /// <summary>For example "12:30 PM IST" or "12:30 PM IST tomorrow".</summary>
    public static string FormatNextReset(DateTimeOffset now, TimeZoneInfo local, TimeZoneInfo? pacific = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        var reset = TimeZoneInfo.ConvertTime(NextResetUtc(now, pacific), local);
        var localNow = TimeZoneInfo.ConvertTime(now, local);
        var text = reset.ToString("h:mm tt", CultureInfo.InvariantCulture) + " " + Abbreviation(local, reset);
        if (reset.Date > localNow.Date)
        {
            text += " tomorrow";
        }

        return text;
    }

    public static string Abbreviation(TimeZoneInfo zone, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (zone.Id is "Asia/Kolkata" or "Asia/Calcutta" or "India Standard Time")
        {
            return "IST";
        }

        if (zone.Id is "UTC" or "Etc/UTC" or "Coordinated Universal Time")
        {
            return "UTC";
        }

        var name = zone.IsDaylightSavingTime(at) ? zone.DaylightName : zone.StandardName;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is >= 2 and <= 4 && words.All(w => char.IsAsciiLetterUpper(w[0])) &&
            words[^1] == "Time")
        {
            return string.Concat(words.Select(w => w[0]));
        }

        var offset = zone.GetUtcOffset(at);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{offset:hh\\:mm}");
    }

    public static TimeZoneInfo FindZone(string ianaId, string windowsId)
    {
        foreach (var id in new[] { ianaId, windowsId })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // try the next id
            }
        }

        // Last resort: fixed UTC-8 (ignores DST, off by one hour in summer).
        return TimeZoneInfo.CreateCustomTimeZone("Pacific-fallback", TimeSpan.FromHours(-8), "Pacific", "Pacific");
    }
}
