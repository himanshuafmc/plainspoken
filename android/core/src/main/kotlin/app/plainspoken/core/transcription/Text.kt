package app.plainspoken.core.transcription

/** How the pieces of one transcript arrived. Counts only, never text, so it can be logged. */
data class JoinStats(val pieces: Int, val carrySpaces: Int, val multiWord: Int, val spacesAdded: Int, val wordJoins: Int) {
    override fun toString(): String =
        "$pieces pieces ($carrySpaces with their own boundary space, $multiWord multi-word); " +
            "added $spacesAdded spaces, joined $wordJoins word boundaries as is"
}

/**
 * Joins transcript text that arrives in pieces (live streaming, or several text items in one response).
 * Same rules as the Windows app (docs/SPEC.md §4.1b), checked against shared/test-fixtures/text-cases.json:
 * - existing whitespace at a boundary is kept as it is;
 * - no space before closing punctuation or a combining mark, or after an opening bracket, quote or hyphen;
 * - a space after sentence or clause punctuation before a word, except inside numbers (2.5, 2,500, 10:30)
 *   and web or email addresses; after . ! ? … only before a capital or caseless letter ("a.m.", "e.g." stay);
 *   with trimmed phrases also before a lower-case letter, unless the word so far is a single-letter
 *   abbreviation ("a.", "e.g.");
 * - between two word characters, a space only when the pieces are trimmed phrases (none carries its own
 *   boundary space and some contain several words). Token streams carry their own spaces, so there such a
 *   boundary is the middle of a word.
 */
object TranscriptJoiner {
    private const val CLOSING = ".,!?;:…)]}”’'%।॥"
    private const val OPENING = "([{“‘/-–—"
    private const val SENTENCE_END = ".!?…"
    private const val SPACED_AFTER = ".,!?;:…।॥"

    fun join(pieces: List<String>): String = joinWithStats(pieces).first

    fun joinWithStats(pieces: List<String>): Pair<String, JoinStats> {
        val parts = pieces.filter { it.isNotEmpty() }
        var carrySpaces = 0
        for (i in parts.indices) {
            if ((i > 0 && parts[i].first().isWhitespace()) || (i < parts.size - 1 && parts[i].last().isWhitespace())) {
                carrySpaces++
            }
        }

        val multiWord = parts.count { p -> p.trim().any { it.isWhitespace() } }
        val trimmedPhrases = carrySpaces == 0 && multiWord > 0
        val sb = StringBuilder()
        var added = 0
        var wordJoins = 0
        for (part in parts) {
            if (sb.isNotEmpty()) {
                when (boundary(sb, part, trimmedPhrases)) {
                    Gap.SPACE -> {
                        sb.append(' ')
                        added++
                    }
                    Gap.WORD_JOIN -> wordJoins++
                    Gap.NONE -> Unit
                }
            }

            sb.append(part)
        }

        return sb.toString() to JoinStats(parts.size, carrySpaces, multiWord, added, wordJoins)
    }

    private enum class Gap { NONE, SPACE, WORD_JOIN }

    private fun boundary(left: StringBuilder, right: String, trimmedPhrases: Boolean): Gap {
        val l = left[left.length - 1]
        val r = right[0]
        if (l.isWhitespace() || r.isWhitespace() || isMark(r)) return Gap.NONE
        if (r in CLOSING) return Gap.NONE
        if (r == '"') return if (quoteIsOpen(left)) Gap.NONE else Gap.SPACE // closing quote attaches; opening starts a word
        if (l == '"') return if (quoteIsOpen(left)) Gap.NONE else Gap.SPACE
        if (l in OPENING) return Gap.NONE
        if (l in SPACED_AFTER) {
            if (!isWordChar(r) && r !in "([{“‘") return Gap.NONE // e.g. "http:" + "//…"
            val numberInside = (l == '.' || l == ',' || l == ':') && left.length > 1 && left[left.length - 2].isDigit() && r.isDigit()
            val abbreviation = l in SENTENCE_END && r.isLowerCase() &&
                (!trimmedPhrases || isLetterAbbreviation(lastWord(left)))
            return if (numberInside || abbreviation || inAddress(left)) Gap.NONE else Gap.SPACE
        }

        if (isWordChar(l) && isWordChar(r)) return if (trimmedPhrases) Gap.SPACE else Gap.WORD_JOIN
        return Gap.NONE
    }

    private fun isWordChar(c: Char) = c.isLetterOrDigit() || isMark(c)

    internal fun isMark(c: Char) = c.category == CharCategory.NON_SPACING_MARK ||
        c.category == CharCategory.COMBINING_SPACING_MARK || c.category == CharCategory.ENCLOSING_MARK

    private fun quoteIsOpen(left: CharSequence) = left.count { it == '"' } % 2 == 1

    private fun lastWord(left: CharSequence): String {
        var start = left.length - 1
        while (start > 0 && !left[start - 1].isWhitespace()) start--
        return left.substring(start)
    }

    /** "a.", "e.g.", "i.e.": single letters each followed by a full stop. */
    private fun isLetterAbbreviation(word: String): Boolean {
        if (word.length < 2 || word.length % 2 != 0) return false
        for (i in word.indices step 2) {
            if (!word[i].isLetter() || word[i + 1] != '.') return false
        }

        return true
    }

    /** True when the last word so far looks like a web or email address ("www.", "name@site."). */
    private fun inAddress(left: CharSequence): Boolean {
        val word = lastWord(left)
        return '@' in word || "://" in word || word.startsWith("www.", ignoreCase = true)
    }
}

/** Final clean-up of every transcript before it is inserted (SPEC §5). */
object TextPostProcessor {
    private val missingSpace = Regex(
        "(?<=[\\p{Ll}\\p{Lo}\\p{M}])[.!?…](?=[\\p{Lu}\\p{Lo}])|(?<=[\\p{L}\\p{M}])[,;](?=\\p{L})|" +
            "(?<=[\\p{Ll}\\p{Lo}\\p{M}]):(?=[\\p{Lu}\\p{Lo}])|[।॥](?=\\p{L})",
    )

    /** Trims, normalises line endings to \n, removes stray code fences/quotes around the whole text, fixes spacing. */
    fun clean(raw: String?): String {
        if (raw.isNullOrBlank()) return ""
        var text = raw.replace("\r\n", "\n").replace('\r', '\n').trim()
        if (text.startsWith("```") && text.endsWith("```") && text.length >= 6) {
            text = text.substring(3, text.length - 3)
            val nl = text.indexOf('\n')
            if (nl in 0..11 && ' ' !in text.substring(0, nl)) text = text.substring(nl + 1) // drop a language tag
            text = text.trim()
        }

        if (text.length >= 2 && text.first() == '"' && text.last() == '"' && text.count { it == '"' } == 2) {
            text = text.substring(1, text.length - 1).trim()
        }

        return fixSpaceAfterPunctuation(text)
    }

    /**
     * Adds the space that is sometimes missing after punctuation ("tomorrow.Please" → "tomorrow. Please"):
     * after . ! ? … between a lower-case or caseless letter and a capital or caseless letter; after , ; between
     * letters; after : before a capital; after । or ॥ before a letter. Numbers (2.5, 2,500, 10:30), abbreviations
     * such as U.S.A. or a.m., and words containing @, :// or www. are left alone.
     */
    fun fixSpaceAfterPunctuation(text: String): String =
        missingSpace.replace(text) { m -> if (inAddress(text, m.range.first)) m.value else m.value + " " }

    fun forInsertion(cleaned: String, trailingSpace: Boolean): String =
        if (trailingSpace && cleaned.isNotEmpty() && !cleaned.last().isWhitespace()) "$cleaned " else cleaned

    private fun inAddress(text: String, index: Int): Boolean {
        var start = index
        while (start > 0 && !text[start - 1].isWhitespace()) start--
        var end = index
        while (end < text.length && !text[end].isWhitespace()) end++
        val word = text.substring(start, end)
        return '@' in word || "://" in word || word.startsWith("www.", ignoreCase = true)
    }
}
