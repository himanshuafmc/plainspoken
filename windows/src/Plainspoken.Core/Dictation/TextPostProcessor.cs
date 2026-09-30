using System.Text.RegularExpressions;

namespace Plainspoken.Core.Dictation;

public static partial class TextPostProcessor
{
    /// <summary>
    /// Trims, normalises line endings to \n, removes stray code fences/quotes around the whole text and adds a
    /// missing space after punctuation (see <see cref="FixSpaceAfterPunctuation"/>).
    /// </summary>
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

        return FixSpaceAfterPunctuation(text);
    }

    /// <summary>
    /// Adds the space that is sometimes missing after punctuation ("tomorrow.Please" → "tomorrow. Please"):
    /// after . ! ? … between a lower-case or caseless letter and a capital or caseless letter; after , ; between
    /// letters; after : before a capital; after । or ॥ before a letter. Numbers (2.5, 2,500, 10:30), abbreviations
    /// such as U.S.A. or a.m., and words containing @, :// or www. are left alone.
    /// </summary>
    public static string FixSpaceAfterPunctuation(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MissingSpace().Replace(text, m => InAddress(text, m.Index) ? m.Value : m.Value + " ");
    }

    [GeneratedRegex(@"(?<=[\p{Ll}\p{Lo}\p{M}])[.!?…](?=[\p{Lu}\p{Lo}])|(?<=[\p{L}\p{M}])[,;](?=\p{L})|(?<=[\p{Ll}\p{Lo}\p{M}]):(?=[\p{Lu}\p{Lo}])|[।॥](?=\p{L})")]
    private static partial Regex MissingSpace();

    private static bool InAddress(string text, int index)
    {
        var start = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        var end = index;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        var word = text.AsSpan(start, end - start);
        return word.Contains('@') || word.Contains("://", StringComparison.Ordinal) ||
               word.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
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
