namespace Plainspoken.Core.Settings;

/// <summary>Cleans the custom vocabulary list: trim, drop blanks/comments, de-duplicate, cap.</summary>
public static class VocabularyCleaner
{
    public const int MaxTerms = 1000;
    public const int MaxTermLength = 100;

    public static List<string> Clean(IEnumerable<string?> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in terms)
        {
            var term = CollapseSpaces(raw);
            if (term.Length == 0 || term.StartsWith('#') || term.Length > MaxTermLength)
            {
                continue;
            }

            if (seen.Add(term))
            {
                result.Add(term);
                if (result.Count == MaxTerms)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>One term per line (the format of shared/vocabulary.default.txt and the settings box).</summary>
    public static List<string> FromText(string? text) =>
        Clean((text ?? string.Empty).Split('\n'));

    public static string ToText(IEnumerable<string> terms) => string.Join(Environment.NewLine, terms);

    private static string CollapseSpaces(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
