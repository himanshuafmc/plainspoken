using System.Text.RegularExpressions;

namespace Plainspoken.Core.Logging;

/// <summary>
/// Minimal logger. Callers must never pass transcript text or the API key;
/// <see cref="Redactor"/> is a safety net for keys, not a licence to log secrets.
/// </summary>
public interface ILog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}

public static partial class Redactor
{
    /// <summary>Removes anything that looks like a Google API key and trims to a sane length.</summary>
    public static string Clean(string? text, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = GoogleKey().Replace(text, "[key]");
        cleaned = KeyParam().Replace(cleaned, "$1[key]");
        cleaned = cleaned.Replace('\r', ' ').Replace('\n', ' ');
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }

    [GeneratedRegex(@"AIza[0-9A-Za-z_\-]{20,}")]
    private static partial Regex GoogleKey();

    [GeneratedRegex(@"((?:key|api_key|x-goog-api-key)[=:]\s*)[^\s&""']+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyParam();
}
