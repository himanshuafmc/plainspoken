package app.plainspoken.core

import app.plainspoken.core.dictation.CorrectionLearner
import app.plainspoken.core.settings.HindiScript
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.storage.StatsStore
import app.plainspoken.core.transcription.CleanupGuard
import app.plainspoken.core.transcription.CleanupPrompt
import app.plainspoken.core.transcription.CleanupRequest
import app.plainspoken.core.transcription.DictationStat
import app.plainspoken.core.transcription.GeminiClient
import app.plainspoken.core.transcription.GeminiCleaner
import app.plainspoken.core.transcription.LocalFillerRemover
import app.plainspoken.core.transcription.OpenAiCleaner
import app.plainspoken.core.transcription.RetryPolicy
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import java.io.File
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CleanupTests {
    private val request = CleanupRequest(listOf("WhatsApp", "Sameer"), HindiScript.ROMAN, listOf("insert my address"))

    @Test
    fun `the prompt covers fillers, grammar, corrections and never translating or answering`() {
        val p = CleanupPrompt.system(HindiScript.ROMAN)
        listOf("um", "matlab", "grammar", "corrects themselves", "Translate", "Answer questions", "Roman letters").forEach {
            assertTrue(it in p, "prompt should mention $it")
        }

        assertTrue("Devanagari (for example" in CleanupPrompt.system(HindiScript.DEVANAGARI))
        val user = CleanupPrompt.user("umm hello", request)
        assertTrue(user.startsWith("Spelling list: WhatsApp; Sameer"))
        assertTrue("insert my address" in user)
        assertTrue(user.endsWith("<dictation>\numm hello\n</dictation>"))
    }

    @Test
    fun `gemini clean-up asks for minimal thinking on Gemini 3 models`() = runBlocking {
        val http = FakeHttp { FakeResponse(body = """{"candidates":[{"content":{"parts":[{"text":"Hello."}]}}]}""") }
        val cleaner = GeminiCleaner(GeminiClient(http.client, "https://generativelanguage.googleapis.com", { "k" }, ListLog(), RetryPolicy(delay = {})), "gemini-3.5-flash", Duration.ofSeconds(10))
        assertEquals("Hello.", cleaner.clean("umm hello", request))
        val r = http.requests.single()
        assertTrue(r.url.endsWith("/v1beta/models/gemini-3.5-flash:generateContent"))
        assertTrue("\"thinkingLevel\":\"minimal\"" in r.body, r.body)
        assertTrue("\"temperature\":0" in r.body)
        assertTrue("Never:" in r.body)
    }

    @Test
    fun `a model that rejects the thinking setting is asked again without it`() = runBlocking {
        val http = FakeHttp { req ->
            if ("thinking" in req.body) FakeResponse(400, """{"error":{"code":400,"message":"Unknown name thinkingLevel","status":"INVALID_ARGUMENT"}}""")
            else FakeResponse(body = """{"candidates":[{"content":{"parts":[{"text":"Fine."}]}}]}""")
        }
        val cleaner = GeminiCleaner(GeminiClient(http.client, "https://generativelanguage.googleapis.com", { "k" }, ListLog(), RetryPolicy(delay = {})), "gemini-3-flash-preview", Duration.ofSeconds(10))
        assertEquals("Fine.", cleaner.clean("uh fine", request))
        assertEquals(2, http.requests.size)
    }

    @Test
    fun `openai-compatible clean-up uses chat completions`() = runBlocking {
        val http = FakeHttp { FakeResponse(body = """{"choices":[{"message":{"role":"assistant","content":"Kal meeting hai."}}]}""") }
        val cleaner = OpenAiCleaner(http.client, "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", { "k" }, ListLog(), Duration.ofSeconds(10))
        assertEquals("Kal meeting hai.", cleaner.clean("umm kal meeting hai", request))
        val r = http.requests.single()
        assertEquals("https://api.groq.com/openai/v1/chat/completions", r.url)
        assertEquals("Bearer k", r.headers["authorization"])
        assertTrue("\"role\":\"system\"" in r.body && "llama-3.3-70b-versatile" in r.body)
    }

    @Test
    fun `the guard refuses summaries, answers and empty replies`() {
        val raw = "okay so the client called and they want the report by Thursday and the sales numbers from finance"
        assertEquals("The client called; they want the report by Thursday and the sales numbers from finance.",
            CleanupGuard.accept(raw, "<dictation>The client called; they want the report by Thursday and the sales numbers from finance.</dictation>"))
        assertNull(CleanupGuard.accept(raw, "Report due Thursday."))
        assertNull(CleanupGuard.accept("what is the capital of France", "The capital of France is Paris, a city known for the Eiffel Tower, museums and its long history as a centre of art."))
        assertNull(CleanupGuard.accept(raw, "  "))
        assertEquals("", CleanupGuard.accept("umm uh hmm", ""))
        assertNull(CleanupGuard.accept(raw, null))
    }

    @Test
    fun `the local filter removes only unambiguous filler sounds`() {
        assertEquals("So I wanted to check, are you free?", LocalFillerRemover.clean("Um, so I wanted to uh check, umm are you free?"))
        assertEquals("Aa jao, kal milte hain.", LocalFillerRemover.clean("Aa jao, aaa kal milte hain."))
        assertEquals("Hmm-free text stays: summer, umbrella, human.", LocalFillerRemover.clean("Hmm-free text stays: summer, umbrella, human."))
        assertEquals("please send it", LocalFillerRemover.clean("uh please send it"))
        assertEquals("Please send it", LocalFillerRemover.clean("Uh, please send it"))
        assertTrue(LocalFillerRemover.isOnlyFiller("umm, uh... hmm"))
        assertFalse(LocalFillerRemover.isOnlyFiller("umm yes"))
    }

    @Test
    fun `the shared clean-up cases file is valid`() {
        val cases = Json.parseToJsonElement(Fixtures.read("cleanup-cases.json")).jsonObject["cases"]!!.jsonArray
        assertTrue(cases.size >= 25)
        val ids = cases.map { it.jsonObject["id"].toString() }
        assertEquals(ids.size, ids.toSet().size)
        cases.forEach { assertTrue(it.jsonObject.containsKey("raw")) }
    }
}

class CorrectionLearnerTests {
    private val vocab = listOf("WhatsApp")

    @Test
    fun `a name the user typed over a misheard word is offered`() {
        val inserted = "Please send the report to Samir by Friday."
        val now = "Earlier text. Please send the report to Sameer by Friday."
        assertEquals(listOf("Sameer"), CorrectionLearner.candidates(inserted, now, vocab))
    }

    @Test
    fun `capital changes and two-word names are offered`() {
        assertEquals(listOf("GitHub"), CorrectionLearner.candidates("I pushed it to github today", "I pushed it to GitHub today", vocab))
        assertEquals(listOf("New Delhi"), CorrectionLearner.candidates("The office is in new deli now", "The office is in New Delhi now", vocab))
    }

    @Test
    fun `nothing is offered for unchanged, rewritten or already known text`() {
        assertTrue(CorrectionLearner.candidates("Hello there friend", "Hello there friend", vocab).isEmpty())
        assertTrue(CorrectionLearner.candidates("one two three four five six", "completely different words in this box now", vocab).isEmpty())
        assertTrue(CorrectionLearner.candidates("send it on whatsapp please", "send it on WhatsApp please", vocab).isEmpty())
        assertTrue(CorrectionLearner.candidates("we will go there", "We will go there", vocab).isEmpty()) // sentence start
        assertTrue(CorrectionLearner.candidates("it is a big deal", "it is the big deal", vocab).isEmpty()) // common word
        assertTrue(CorrectionLearner.candidates("haan main aa raha hoon", "accha main aa raha hoon", vocab).isEmpty()) // Hinglish words
        assertTrue(CorrectionLearner.candidates("main kal aa raha hoon", "main abhi aa raha hoon", vocab).isEmpty())
    }

    @Test
    fun `corrections at the start or end of the dictation are found`() {
        assertEquals(listOf("Kavita"), CorrectionLearner.candidates("kavitha will call you back", "Kavita will call you back", vocab))
        assertEquals(listOf("Bengaluru"), CorrectionLearner.candidates("We are flying to bangalore", "We are flying to Bengaluru", vocab))
    }
}

class StatsTests {
    @Test
    fun `stats keep timings per service and summarise them`() {
        val file = File(tempDir(), "stats.json")
        val store = StatsStore(file, ListLog(), capacity = 5)
        val t = Instant.parse("2026-10-18T10:00:00Z")
        store.record(DictationStat(t, SpeechService.SARVAM, 30.0, 800, 900, ok = true, streamed = true, cleanedBy = "gemini:x"))
        store.record(DictationStat(t, SpeechService.SARVAM, 30.0, 1200, 1100, ok = true))
        store.record(DictationStat(t, SpeechService.SARVAM, 10.0, 5000, 0, ok = false, errorKind = "TIMEOUT"))
        store.record(DictationStat(Instant.parse("2026-09-01T10:00:00Z"), SpeechService.GEMINI, 60.0, 3000, 0, ok = true))

        val summary = StatsStore(file, ListLog()).summary(t, ZoneId.of("Asia/Kolkata"))
        val sarvam = summary.first()
        assertEquals(SpeechService.SARVAM, sarvam.service)
        assertEquals(3, sarvam.dictations)
        assertEquals(1, sarvam.failures)
        assertEquals(1200, sarvam.medianListenMs)
        assertEquals(2300, sarvam.medianTotalMs)
        assertEquals(0.5, sarvam.streamedShare)
        assertEquals(70.0 / 60, sarvam.minutesThisMonth, 1e-9)
        assertEquals(0.0, summary.first { it.service == SpeechService.GEMINI }.minutesThisMonth)

        repeat(5) { store.record(DictationStat(t, SpeechService.DEEPGRAM, 1.0, 100, 0, ok = true)) }
        assertEquals(5, store.all.size) // capacity
        assertFalse(file.readText().contains("text\""))
    }
}
