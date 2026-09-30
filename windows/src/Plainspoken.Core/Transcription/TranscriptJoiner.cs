using System.Globalization;
using System.Text;

namespace Plainspoken.Core.Transcription;

/// <summary>How the pieces of one transcript arrived. Counts only, never text, so it can be logged.</summary>
public readonly record struct JoinStats(int Pieces, int CarrySpaces, int MultiWord, int SpacesAdded, int WordJoins)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Pieces} pieces ({CarrySpaces} with their own boundary space, {MultiWord} multi-word); added {SpacesAdded} spaces, joined {WordJoins} word boundaries as is");
}

/// <summary>
/// Joins transcript text that arrives in pieces (live streaming, or several text items in one response).
/// Pieces are not always cut at a space: one service sends "tomorrow." then "Please…", another " trans" then
/// "cription". Gluing them blindly loses spaces; adding a space everywhere splits words. So:
/// <list type="bullet">
/// <item>existing whitespace at a boundary is kept as it is;</item>
/// <item>no space before closing punctuation or a combining mark, or after an opening bracket, quote or hyphen;</item>
/// <item>a space after sentence or clause punctuation (. , ! ? ; : … ।) before a word, except inside numbers
///   (2.5, 2,500, 10:30) and web or email addresses; after . ! ? … only before a capital or caseless letter
///   ("a.m.", "e.g." stay); with trimmed phrases also before a lower-case letter, unless the word so far is a
///   single-letter abbreviation ("a.", "e.g.");</item>
/// <item>between two word characters, a space only when the pieces are trimmed phrases (none carries its own
///   boundary space and some contain several words). Services that stream sub-word tokens carry their own
///   spaces, so there such a boundary is the middle of a word.</item>
/// </list>
/// </summary>
public static class TranscriptJoiner
{
    private const string ClosingPunctuation = ".,!?;:…)]}”’'%।॥";
    private const string OpeningPunctuation = "([{“‘/-–—";
    private const string SentenceEnd = ".!?…";
    private const string SpacedAfter = ".,!?;:…।॥";

    public static string Join(IReadOnlyList<string> pieces) => Join(pieces, out _);

    public static string Join(IReadOnlyList<string> pieces, out JoinStats stats)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        var parts = pieces.Where(p => !string.IsNullOrEmpty(p)).ToList();
        var carrySpaces = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            if ((i > 0 && char.IsWhiteSpace(parts[i][0])) || (i < parts.Count - 1 && char.IsWhiteSpace(parts[i][^1])))
            {
                carrySpaces++;
            }
        }

        var multiWord = parts.Count(p => p.Trim().Any(char.IsWhiteSpace));
        var trimmedPhrases = carrySpaces == 0 && multiWord > 0;

        var sb = new StringBuilder();
        int added = 0, wordJoins = 0;
        foreach (var part in parts)
        {
            if (sb.Length > 0)
            {
                switch (Boundary(sb, part, trimmedPhrases))
                {
                    case Gap.Space:
                        sb.Append(' ');
                        added++;
                        break;
                    case Gap.WordJoin:
                        wordJoins++;
                        break;
                }
            }

            sb.Append(part);
        }

        stats = new JoinStats(parts.Count, carrySpaces, multiWord, added, wordJoins);
        return sb.ToString();
    }

    private enum Gap
    {
        None,
        Space,
        WordJoin,
    }

    private static Gap Boundary(StringBuilder left, string right, bool trimmedPhrases)
    {
        var l = left[^1];
        var r = right[0];
        if (char.IsWhiteSpace(l) || char.IsWhiteSpace(r) || IsMark(r))
        {
            return Gap.None;
        }

        if (ClosingPunctuation.Contains(r, StringComparison.Ordinal))
        {
            return Gap.None;
        }

        if (r == '"')
        {
            return QuoteIsOpen(left) ? Gap.None : Gap.Space; // closing quote attaches; opening quote is a new word
        }

        if (l == '"')
        {
            return QuoteIsOpen(left) ? Gap.None : Gap.Space;
        }

        if (OpeningPunctuation.Contains(l, StringComparison.Ordinal))
        {
            return Gap.None;
        }

        if (SpacedAfter.Contains(l, StringComparison.Ordinal))
        {
            if (!IsWordChar(r) && !"([{“‘".Contains(r, StringComparison.Ordinal))
            {
                return Gap.None; // e.g. "http:" + "//…"
            }

            var numberInside = l is '.' or ',' or ':' && left.Length > 1 && char.IsDigit(left[^2]) && char.IsDigit(r);
            var abbreviation = SentenceEnd.Contains(l, StringComparison.Ordinal) && char.IsLower(r) &&
                               (!trimmedPhrases || IsLetterAbbreviation(LastWord(left)));
            return numberInside || abbreviation || InAddress(left) ? Gap.None : Gap.Space;
        }

        if (IsWordChar(l) && IsWordChar(r))
        {
            return trimmedPhrases ? Gap.Space : Gap.WordJoin;
        }

        return Gap.None;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || IsMark(c);

    private static bool IsMark(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool QuoteIsOpen(StringBuilder left)
    {
        var count = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] == '"')
            {
                count++;
            }
        }

        return count % 2 == 1;
    }

    private static string LastWord(StringBuilder left)
    {
        var start = left.Length - 1;
        while (start > 0 && !char.IsWhiteSpace(left[start - 1]))
        {
            start--;
        }

        return left.ToString(start, left.Length - start);
    }

    /// <summary>"a.", "e.g.", "i.e.": single letters each followed by a full stop.</summary>
    private static bool IsLetterAbbreviation(string word)
    {
        if (word.Length < 2 || word.Length % 2 != 0)
        {
            return false;
        }

        for (var i = 0; i < word.Length; i += 2)
        {
            if (!char.IsLetter(word[i]) || word[i + 1] != '.')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the last word so far looks like a web or email address ("www.", "name@site.").</summary>
    private static bool InAddress(StringBuilder left)
    {
        var word = LastWord(left);
        return word.Contains('@', StringComparison.Ordinal) || word.Contains("://", StringComparison.Ordinal) ||
               word.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }
}
