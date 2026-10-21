using System.Text.RegularExpressions;

namespace Plainspoken.Core.Dictation;

/// <summary>
/// Spots words the user fixed by hand after a dictation, so they can be offered for the vocabulary.
/// <see cref="Candidates"/> compares the text Plainspoken inserted with the text now in the text box: words that replaced
/// inserted words (or changed their capitals, like "whatsapp" → "WhatsApp") are candidates.
/// </summary>
public static partial class CorrectionLearner
{
    public const int MaxCandidates = 3;

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "but", "with", "that", "this", "have", "will", "your", "from", "they", "what", "when", "then",
        "there", "their", "about", "would", "could", "should", "which", "were", "been", "into", "also", "just", "very",
        "hai", "hain", "nahi", "kya", "aur", "main", "mein", "tum", "aap", "yeh", "woh", "kar", "karo", "raha", "rahi",
        "accha", "achha", "acha", "theek", "thik", "abhi", "bahut", "bohot", "lekin", "kyunki", "kyun", "wala", "wali",
        "wale", "matlab", "haan", "nahin", "kaise", "kaisa", "kuch", "phir", "agar", "bhi", "toh", "mera", "meri", "apna",
        "apni", "karna", "karte", "karke", "hoga", "hogi", "kahan", "yahan", "wahan", "baat", "bilkul", "zaroor", "jaldi",
    };

    public static IReadOnlyList<string> Candidates(string inserted, string current, IEnumerable<string> vocabulary)
    {
        ArgumentNullException.ThrowIfNull(inserted);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(vocabulary);
        var a = Word().Matches(inserted).Select(m => m.Value).ToList();
        var bMatches = Word().Matches(current).ToList();
        var b = bMatches.Select(m => m.Value).ToList();
        if (a.Count == 0 || b.Count == 0 || a.Count > 400 || b.Count > 1200)
        {
            return [];
        }

        var matches = Lcs(a.Select(Lower).ToList(), b.Select(Lower).ToList());
        if (matches.Count < Math.Max(1, (int)(a.Count * 0.5)))
        {
            return []; // rewritten, or a different text box
        }

        var known = vocabulary.Select(Lower).ToHashSet(StringComparer.Ordinal);
        var insertedWords = a.Select(Lower).ToHashSet(StringComparer.Ordinal);
        // Positions in b of words the user changed: true = only the capitals changed (always a candidate).
        var changed = new SortedDictionary<int, bool>();

        // Same word, new capitals ("whatsapp" → "WhatsApp"), unless it just starts a sentence.
        foreach (var (i, j) in matches)
        {
            if (a[i] == b[j] || !b[j].Any(char.IsUpper) || known.Contains(Lower(b[j])))
            {
                continue;
            }

            var before = current[..bMatches[j].Index].TrimEnd();
            if (before.Length == 0 || ".!?\n:".Contains(before[^1], StringComparison.Ordinal))
            {
                continue;
            }

            changed[j] = true;
        }

        // Replacements: inserted words that the user swapped for others.
        var (firstI, firstJ) = matches[0];
        if (firstI is >= 1 and <= 3)
        {
            for (var j = Math.Max(0, firstJ - firstI); j < firstJ; j++)
            {
                changed.TryAdd(j, false);
            }
        }

        for (var k = 0; k < matches.Count - 1; k++)
        {
            var (i0, j0) = matches[k];
            var (i1, j1) = matches[k + 1];
            if (i1 - i0 - 1 is >= 1 and <= 3 && j1 - j0 - 1 is >= 1 and <= 3)
            {
                for (var j = j0 + 1; j < j1; j++)
                {
                    changed.TryAdd(j, false);
                }
            }
        }

        var (lastI, lastJ) = matches[^1];
        var trailing = a.Count - lastI - 1;
        if (trailing is >= 1 and <= 3)
        {
            for (var j = lastJ + 1; j < Math.Min(b.Count, lastJ + 1 + trailing); j++)
            {
                changed.TryAdd(j, false);
            }
        }

        // Neighbouring capitalised words are one name ("New Delhi"); others are judged one by one.
        var runs = new List<List<int>>();
        foreach (var j in changed.Keys)
        {
            if (runs.Count > 0 && runs[^1][^1] == j - 1)
            {
                runs[^1].Add(j);
            }
            else
            {
                runs.Add([j]);
            }
        }

        var found = new List<string>();
        void Add(string w)
        {
            if (!found.Contains(w, StringComparer.Ordinal))
            {
                found.Add(w);
            }
        }

        foreach (var run in runs)
        {
            var words = run.Select(j => b[j]).ToList();
            if (words.Count > 1 && words.All(w => char.IsUpper(w[0])))
            {
                var phrase = string.Join(" ", words);
                if (!known.Contains(Lower(phrase)))
                {
                    Add(phrase);
                }

                continue;
            }

            foreach (var j in run)
            {
                var w = b[j];
                var lower = Lower(w);
                if (known.Contains(lower) || !w.Any(char.IsLetter) || w.Length < 2)
                {
                    continue;
                }

                if (changed[j])
                {
                    Add(w);
                }
                else if (!insertedWords.Contains(lower) && !Common.Contains(lower) && (w.Any(char.IsUpper) || w.Length >= 4))
                {
                    Add(w);
                }
            }
        }

        return found.Take(MaxCandidates).ToList();
    }

    /// <summary>
    /// The part of a long text box around where <paramref name="inserted"/> probably went, found by its longest words,
    /// so a long document can be compared too. Short texts are returned whole; null when nothing matches.
    /// </summary>
    public static string? Around(string inserted, string current, int maxChars = 4000)
    {
        ArgumentNullException.ThrowIfNull(inserted);
        ArgumentNullException.ThrowIfNull(current);
        if (current.Length <= maxChars)
        {
            return current;
        }

        var anchors = Word().Matches(inserted).Select(m => m.Value).Where(w => w.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(w => w.Length).Take(3)
            .Select(w => current.LastIndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).Order().ToList();
        if (anchors.Count == 0)
        {
            return null;
        }

        var centre = anchors[anchors.Count / 2];
        var half = Math.Min(maxChars / 2, inserted.Length + 400);
        var start = Math.Max(0, centre - half);
        var end = Math.Min(current.Length, centre + half);
        return current[start..end];
    }

    private static string Lower(string s) => s.ToLowerInvariant();

    /// <summary>Longest common subsequence as matched index pairs (i in <paramref name="a"/>, j in <paramref name="b"/>), in order.</summary>
    private static List<(int I, int J)> Lcs(List<string> a, List<string> b)
    {
        int n = a.Count, m = b.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var pairs = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                pairs.Add((x, y));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return pairs;
    }

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}](?:[\p{L}\p{M}\p{N}'’.-]*[\p{L}\p{M}\p{N}])?")]
    private static partial Regex Word();
}

/// <summary>
/// Remembers the last text inserted into an app, so that at the next dictation in the same app the text box can be
/// read again and compared (<see cref="CorrectionLearner"/>). Holds the text in memory only, for at most <see cref="Window"/>.
/// </summary>
public sealed class CorrectionWatch(TimeProvider? time = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private (string Text, string App, DateTimeOffset At)? _last;

    /// <summary>Called after text was typed into <paramref name="app"/> (a window or process identity).</summary>
    public void Inserted(string text, string app)
    {
        lock (_gate)
        {
            _last = string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(app) ? null : (text, app, _time.GetUtcNow());
        }
    }

    /// <summary>The inserted text if the user is back in the same app soon enough; consumed by the call.</summary>
    public string? Take(string app)
    {
        lock (_gate)
        {
            var last = _last;
            _last = null;
            return last is { } l && string.Equals(l.App, app, StringComparison.Ordinal) && _time.GetUtcNow() - l.At <= Window ? l.Text : null;
        }
    }

    public void Forget()
    {
        lock (_gate)
        {
            _last = null;
        }
    }
}
