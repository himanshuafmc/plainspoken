namespace Plainspoken.Core.Dictation;

public static class TextPostProcessor
{
    /// <summary>Trims, normalises line endings to \n and removes stray code fences/quotes around the whole text.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) && text.Length >= 6)
        {
            text = text[3..^3];
            var nl = text.IndexOf('\n', StringComparison.Ordinal);
            if (nl >= 0 && nl < 12 && !text[..nl].Contains(' ', StringComparison.Ordinal))
            {
                text = text[(nl + 1)..]; // drop a language tag such as ```text
            }

            text = text.Trim();
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && text.Count(c => c == '"') == 2)
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    public static string ForInsertion(string cleaned, bool trailingSpace)
    {
        ArgumentNullException.ThrowIfNull(cleaned);
        if (trailingSpace && cleaned.Length > 0 && !char.IsWhiteSpace(cleaned[^1]))
        {
            return cleaned + " ";
        }

        return cleaned;
    }
}
