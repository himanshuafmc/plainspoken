package app.plainspoken.core

import app.plainspoken.core.logging.Log
import app.plainspoken.core.settings.HindiScript
import app.plainspoken.core.transcription.CleanupGuard
import app.plainspoken.core.transcription.CleanupRequest
import app.plainspoken.core.transcription.GeminiClient
import app.plainspoken.core.transcription.GeminiCleaner
import app.plainspoken.core.transcription.RetryPolicy
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.OkHttpClient
import java.time.Duration

/**
 * Developer tool (not a unit test): sends every case in cleanup-cases.json through the Gemini clean-up step and
 * checks the result. Prints raw → cleaned for reading, then a score. Run with ./gradlew -p core cleanupEval.
 */
fun main(args: Array<String>) = runBlocking {
    val model = args.getOrElse(0) { "gemini-3.5-flash" }
    val filters = args.getOrElse(1) { "" }.split(',').map { it.trim() }.filter { it.isNotEmpty() }
    val pause = args.getOrElse(2) { "6500" }.toLong()
    val key = System.getenv("GEMINI_API_KEY")
    val log = object : Log {
        override fun info(message: String) = Unit
        override fun warn(message: String) = println("  [warn] $message")
        override fun error(message: String, throwable: Throwable?) = println("  [error] $message")
    }

    val client = GeminiClient(OkHttpClient(), "https://generativelanguage.googleapis.com", { key }, log, RetryPolicy(maxServerRetries = 2), requireApiKey = key != null)
    val cleaner = GeminiCleaner(client, model, Duration.ofSeconds(30))
    val fixtures = java.io.File(System.getProperty("plainspoken.fixtures") ?: "../../shared/test-fixtures")
    val cases = Json.parseToJsonElement(java.io.File(fixtures, "cleanup-cases.json").readText()).jsonObject["cases"]!!.jsonArray
        .map { it.jsonObject }.filter { c -> filters.isEmpty() || filters.any { c.str("id").startsWith(it) } }
    var passed = 0
    val times = ArrayList<Long>()
    for (c in cases) {
        val raw = c.str("raw")
        val script = when (c.str("script")) {
            "devanagari" -> HindiScript.DEVANAGARI
            "asSpoken" -> HindiScript.AS_SPOKEN
            else -> HindiScript.ROMAN
        }

        val request = CleanupRequest(c.list("vocabulary"), script, c.list("keepPhrases"))
        val started = System.nanoTime()
        var out = ""
        for (attempt in 1..4) {
            out = try {
                CleanupGuard.accept(raw, cleaner.clean(raw, request)) ?: "<<guard refused>>"
            } catch (e: TranscriptionException) {
                if ((e.kind == TranscriptionErrorKind.RATE_LIMITED || e.kind == TranscriptionErrorKind.DAILY_QUOTA) && attempt < 4) {
                    val wait = (e.retryAfter?.toMillis() ?: 20_000L).coerceIn(5_000L, 60_000L)
                    println("   (free-tier limit; waiting ${wait / 1000} s)")
                    delay(wait)
                    continue
                }

                "<<error ${e.kind}: ${e.message}>>"
            }

            break
        }

        times.add((System.nanoTime() - started) / 1_000_000)
        val problems = check(c, raw, out)
        if (problems.isEmpty()) passed++
        println("${if (problems.isEmpty()) "PASS" else "FAIL"} ${c.str("id")} (${times.last()} ms)")
        println("   raw: $raw")
        println("   out: ${out.replace("\n", " ⏎ ")}")
        problems.forEach { println("   ✗ $it") }
        delay(pause)
    }

    times.sort()
    println("\n$passed / ${cases.size} passed with $model; median ${times.getOrNull(times.size / 2)} ms, slowest ${times.lastOrNull()} ms")
}

private val devanagari = Regex("[\\u0900-\\u097F]")
private val sentenceEnd = Regex("[.!?।]\\s")

private fun check(c: JsonObject, raw: String, out: String): List<String> {
    val p = ArrayList<String>()
    if (out.startsWith("<<")) return listOf(out)
    if (c["expectEmpty"]?.jsonPrimitive?.booleanOrNull == true) {
        if (out.isNotBlank()) p.add("expected empty")
        return p
    }

    c.list("mustContain").filter { !out.contains(it, ignoreCase = true) }.forEach { p.add("missing \"$it\"") }
    val any = c.list("mustContainAny")
    if (any.isNotEmpty() && any.none { out.contains(it, ignoreCase = true) }) p.add("missing any of $any")
    c.list("mustNotContain").filter { out.contains(it, ignoreCase = true) }.forEach { p.add("still contains \"$it\"") }
    c.list("mustNotContainWords").filter { Regex("(?<![\\p{L}\\p{N}])${Regex.escape(it)}(?![\\p{L}\\p{N}])", RegexOption.IGNORE_CASE).containsMatchIn(out) }
        .forEach { p.add("still has word \"$it\"") }
    c.list("notTranslated").filter { out.contains(it, ignoreCase = true) }.forEach { p.add("translated? found \"$it\"") }
    (c["maxCount"] as? JsonObject)?.forEach { (word, limit) ->
        val n = Regex("(?<![\\p{L}])${Regex.escape(word)}(?![\\p{L}])", RegexOption.IGNORE_CASE).findAll(out).count()
        if (n > (limit.jsonPrimitive.intOrNull ?: 0)) p.add("\"$word\" appears $n times")
    }
    if (c["noDevanagari"]?.jsonPrimitive?.booleanOrNull == true && devanagari.containsMatchIn(out)) p.add("has Devanagari")
    if (c["needsDevanagari"]?.jsonPrimitive?.booleanOrNull == true && !devanagari.containsMatchIn(out)) p.add("no Devanagari")
    c["maxWordRatio"]?.jsonPrimitive?.doubleOrNull?.let { if (CleanupGuard.words(out) > CleanupGuard.words(raw) * it) p.add("too long") }
    c["minSentences"]?.jsonPrimitive?.intOrNull?.let { if (sentenceEnd.findAll("$out ").count() < it) p.add("fewer than $it sentences") }
    return p
}

private fun JsonObject.str(name: String): String = (this[name] as? JsonPrimitive)?.content.orEmpty()

private fun JsonObject.list(name: String): List<String> = (this[name] as? JsonArray)?.map { it.jsonPrimitive.content }.orEmpty()
