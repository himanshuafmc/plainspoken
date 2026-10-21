package app.plainspoken.core.dictation

import java.util.Locale

/**
 * Spots words the user fixed by hand right after a dictation, so they can be offered for the vocabulary.
 * [candidates] compares the text Plainspoken inserted with the text now around the cursor: words that replaced
 * inserted words (or changed their capitals, like "whatsapp" → "WhatsApp") are candidates.
 */
object CorrectionLearner {
    private val word = Regex("[\\p{L}\\p{M}\\p{N}](?:[\\p{L}\\p{M}\\p{N}'’.-]*[\\p{L}\\p{M}\\p{N}])?")
    private val common = setOf(
        "the", "and", "for", "but", "with", "that", "this", "have", "will", "your", "from", "they", "what", "when", "then",
        "there", "their", "about", "would", "could", "should", "which", "were", "been", "into", "also", "just", "very",
        "hai", "hain", "nahi", "kya", "aur", "main", "mein", "tum", "aap", "yeh", "woh", "kar", "karo", "raha", "rahi",
        "accha", "achha", "acha", "theek", "thik", "abhi", "bahut", "bohot", "lekin", "kyunki", "kyun", "wala", "wali",
        "wale", "matlab", "haan", "nahin", "kaise", "kaisa", "kuch", "phir", "agar", "bhi", "toh", "mera", "meri", "apna",
        "apni", "karna", "karte", "karke", "hoga", "hogi", "kahan", "yahan", "wahan", "baat", "bilkul", "zaroor", "jaldi",
    )

    const val MAX_CANDIDATES = 3

    fun candidates(inserted: String, current: String, vocabulary: Collection<String>): List<String> {
        val a = word.findAll(inserted).map { it.value }.toList()
        val bMatches = word.findAll(current).toList()
        val b = bMatches.map { it.value }
        if (a.isEmpty() || b.isEmpty() || a.size > 400 || b.size > 1200) return emptyList()

        val matches = lcs(a.map { it.lowercase(Locale.ROOT) }, b.map { it.lowercase(Locale.ROOT) })
        if (matches.size < maxOf(1, (a.size * 0.5).toInt())) return emptyList() // rewritten, or a different text box

        val known = vocabulary.map { it.lowercase(Locale.ROOT) }.toHashSet()
        val insertedWords = a.map { it.lowercase(Locale.ROOT) }.toHashSet()
        // Positions in b of words the user changed: true = only the capitals changed (always a candidate).
        val changed = sortedMapOf<Int, Boolean>()

        // Same word, new capitals ("whatsapp" → "WhatsApp"), unless it just starts a sentence.
        for ((i, j) in matches) {
            if (a[i] == b[j] || b[j].none { it.isUpperCase() } || b[j].lowercase(Locale.ROOT) in known) continue
            val before = current.substring(0, bMatches[j].range.first).trimEnd()
            if (before.isEmpty() || before.last() in ".!?\n:") continue
            changed[j] = true
        }

        // Replacements: inserted words that the user swapped for others.
        val (firstI, firstJ) = matches.first()
        if (firstI in 1..3) (maxOf(0, firstJ - firstI) until firstJ).forEach { changed.putIfAbsent(it, false) }
        for (k in 0 until matches.size - 1) {
            val (i0, j0) = matches[k]
            val (i1, j1) = matches[k + 1]
            if (i1 - i0 - 1 in 1..3 && j1 - j0 - 1 in 1..3) (j0 + 1 until j1).forEach { changed.putIfAbsent(it, false) }
        }

        val (lastI, lastJ) = matches.last()
        val trailing = a.size - lastI - 1
        if (trailing in 1..3) (lastJ + 1 until minOf(b.size, lastJ + 1 + trailing)).forEach { changed.putIfAbsent(it, false) }

        // Neighbouring capitalised words are one name ("New Delhi"); others are judged one by one.
        val found = LinkedHashSet<String>()
        val runs = ArrayList<MutableList<Int>>()
        for (j in changed.keys) if (runs.isNotEmpty() && runs.last().last() == j - 1) runs.last().add(j) else runs.add(mutableListOf(j))
        for (run in runs) {
            val words = run.map { b[it] }
            val phrase = words.joinToString(" ")
            if (words.size > 1 && words.all { it.first().isUpperCase() }) {
                if (phrase.lowercase(Locale.ROOT) !in known) found.add(phrase)
                continue
            }

            for (j in run) {
                val w = b[j]
                val lower = w.lowercase(Locale.ROOT)
                if (lower in known || w.none { it.isLetter() } || w.length < 2) continue
                if (changed.getValue(j)) {
                    found.add(w)
                } else if (lower !in insertedWords && lower !in common && (w.any { it.isUpperCase() } || w.length >= 4)) {
                    found.add(w)
                }
            }
        }

        return found.take(MAX_CANDIDATES)
    }

    /** Longest common subsequence as matched index pairs (i in [a], j in [b]), in order. */
    private fun lcs(a: List<String>, b: List<String>): List<Pair<Int, Int>> {
        val n = a.size
        val m = b.size
        val dp = Array(n + 1) { IntArray(m + 1) }
        for (i in n - 1 downTo 0) {
            for (j in m - 1 downTo 0) {
                dp[i][j] = if (a[i] == b[j]) dp[i + 1][j + 1] + 1 else maxOf(dp[i + 1][j], dp[i][j + 1])
            }
        }

        val pairs = ArrayList<Pair<Int, Int>>()
        var i = 0
        var j = 0
        while (i < n && j < m) {
            when {
                a[i] == b[j] -> {
                    pairs.add(i to j)
                    i++
                    j++
                }
                dp[i + 1][j] >= dp[i][j + 1] -> i++
                else -> j++
            }
        }

        return pairs
    }
}
