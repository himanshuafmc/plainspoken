package app.plainspoken.core

import app.plainspoken.core.audio.AudioCompressor
import app.plainspoken.core.audio.EncodedAudio
import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.settings.CleanupProvider
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.Snippet
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.settings.TranscriptionMode
import app.plainspoken.core.transcription.CleanerFactory
import app.plainspoken.core.transcription.CleanupRequest
import app.plainspoken.core.transcription.DictationStat
import app.plainspoken.core.transcription.ServiceHealth
import app.plainspoken.core.transcription.SnippetExpander
import app.plainspoken.core.transcription.TextCleaner
import app.plainspoken.core.transcription.TranscriptionEngine
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import app.plainspoken.core.transcription.TranscriptionRequest
import app.plainspoken.core.transcription.TranscriptionService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import java.time.Duration
import java.time.Instant
import java.util.Collections
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class RouterTests {
    /** A scripted engine: waits [delayMs], then returns [text] or throws [error]. */
    private class Script(val text: String = "ok", val error: TranscriptionErrorKind? = null, val delayMs: Long = 0)

    private val calls: MutableList<SpeechService> = Collections.synchronizedList(ArrayList())
    private val requests: MutableList<TranscriptionRequest> = Collections.synchronizedList(ArrayList())
    private val stats: MutableList<DictationStat> = Collections.synchronizedList(ArrayList())

    private fun settings(vararg order: SpeechService, hedge: Int = 0) = PlainspokenSettings().apply {
        transcription.serviceOrder = order.map { it.id }
        transcription.hedgeSeconds = hedge
        transcription.cleanup.provider = CleanupProvider.OFF
    }.normalize()

    private fun request(seconds: Double = 2.0, mode: TranscriptionMode = TranscriptionMode.SMART) = TranscriptionRequest(WavEncoder.encode(speech(seconds)), seconds, mode,
        app.plainspoken.core.settings.LanguagePreset.ENGLISH_HINDI, listOf("WhatsApp"))

    private fun router(
        scripts: Map<SpeechService, Script>,
        keys: Set<SpeechService> = scripts.keys,
        cleaner: TextCleaner? = null,
        compressor: AudioCompressor? = null,
        health: ServiceHealth = ServiceHealth(),
    ) = TranscriptionService(
        engineFactory = { service, _, _ ->
            object : TranscriptionEngine {
                override val service = service

                override suspend fun transcribe(request: TranscriptionRequest): String {
                    calls.add(service)
                    requests.add(request)
                    val s = scripts.getValue(service)
                    if (s.delayMs > 0) delay(s.delayMs)
                    s.error?.let { throw TranscriptionException(it, "scripted") }
                    return s.text
                }
            }
        },
        hasKey = { it in keys },
        log = ListLog(),
        cleaners = cleaner?.let { c -> CleanerFactory { c } },
        stats = { stats.add(it) },
        compressor = compressor,
        workDispatcher = Dispatchers.Default,
        health = health,
    )

    @Test
    fun `the main service is used when it works`() = runBlocking {
        val r = router(mapOf(SpeechService.SARVAM to Script("Main."), SpeechService.GEMINI to Script("Backup.")))
        val out = r.transcribe(request(), settings(SpeechService.SARVAM, SpeechService.GEMINI), null)
        assertEquals("Main.", out.text)
        assertEquals(SpeechService.SARVAM, out.service)
        assertEquals(listOf(SpeechService.SARVAM), calls)
        assertFalse(stats.single().usedBackup)
    }

    @Test
    fun `a failure moves straight to the next service`() = runBlocking {
        val r = router(mapOf(SpeechService.SARVAM to Script(error = TranscriptionErrorKind.SERVER), SpeechService.DEEPGRAM to Script("From Deepgram.")))
        val statuses = ArrayList<String>()
        val out = r.transcribe(request(), settings(SpeechService.SARVAM, SpeechService.DEEPGRAM), statuses::add)
        assertEquals("From Deepgram.", out.text)
        assertEquals(listOf(SpeechService.SARVAM, SpeechService.DEEPGRAM), calls)
        assertTrue(statuses.any { "Deepgram" in it })
        assertTrue(stats.single().usedBackup)
    }

    @Test
    fun `when every service fails the first error is reported with its service`() = runBlocking {
        val r = router(mapOf(SpeechService.SARVAM to Script(error = TranscriptionErrorKind.NO_CREDITS), SpeechService.GEMINI to Script(error = TranscriptionErrorKind.TIMEOUT)))
        val e = assertFailsWith<TranscriptionException> { r.transcribe(request(), settings(SpeechService.SARVAM, SpeechService.GEMINI), null) }
        assertEquals(TranscriptionErrorKind.NO_CREDITS, e.kind)
        assertEquals(SpeechService.SARVAM, e.service)
        assertFalse(stats.single().ok)
    }

    @Test
    fun `a slow service is hedged and the first answer wins`() = runBlocking {
        val r = router(mapOf(SpeechService.SARVAM to Script("Slow.", delayMs = 5_000), SpeechService.DEEPGRAM to Script("Fast.", delayMs = 50)))
        val started = System.nanoTime()
        val out = r.transcribe(request(seconds = 0.0), settings(SpeechService.SARVAM, SpeechService.DEEPGRAM, hedge = 1), null)
        assertEquals("Fast.", out.text)
        assertTrue((System.nanoTime() - started) / 1_000_000 < 3_000, "the slow service should have been cancelled")
        assertTrue(stats.single().hedged)
    }

    @Test
    fun `hedging is off when the setting is 0`() {
        assertNull(TranscriptionService.hedgeDelay(settings(SpeechService.GEMINI, hedge = 0), 10.0))
        assertEquals(Duration.ofMillis(5_500), TranscriptionService.hedgeDelay(settings(SpeechService.GEMINI, hedge = 4), 10.0))
        assertEquals(Duration.ofSeconds(20), TranscriptionService.hedgeDelay(settings(SpeechService.GEMINI, hedge = 4), 600.0))
    }

    @Test
    fun `services without a key are skipped and no key at all is an error`() = runBlocking {
        val r = router(mapOf(SpeechService.SARVAM to Script("x"), SpeechService.GEMINI to Script("From Gemini.")), keys = setOf(SpeechService.GEMINI))
        assertEquals("From Gemini.", r.transcribe(request(), settings(SpeechService.SARVAM, SpeechService.GEMINI), null).text)
        val none = router(mapOf(SpeechService.SARVAM to Script()), keys = emptySet())
        val e = assertFailsWith<TranscriptionException> { none.transcribe(request(), settings(SpeechService.SARVAM), null) }
        assertEquals(TranscriptionErrorKind.NO_KEY, e.kind)
    }

    @Test
    fun `a failing service is paused and skipped while a backup works`() = runBlocking {
        var now = Instant.parse("2026-10-18T10:00:00Z")
        val health = ServiceHealth(failuresBeforePause = 2, pause = Duration.ofMinutes(15), now = { now })
        val scripts = mapOf(SpeechService.SARVAM to Script(error = TranscriptionErrorKind.SERVER), SpeechService.GEMINI to Script("G."))
        val r = router(scripts, health = health)
        val s = settings(SpeechService.SARVAM, SpeechService.GEMINI)
        repeat(2) { r.transcribe(request(), s, null) }
        calls.clear()
        r.transcribe(request(), s, null)
        assertEquals(listOf(SpeechService.GEMINI), calls) // Sarvam paused
        now = now.plus(Duration.ofMinutes(16))
        calls.clear()
        r.transcribe(request(), s, null)
        assertEquals(listOf(SpeechService.SARVAM, SpeechService.GEMINI), calls) // pause over: tried again
    }

    @Test
    fun `out of credits pauses at once`() {
        val health = ServiceHealth()
        assertTrue(health.failure(SpeechService.SARVAM, TranscriptionErrorKind.NO_CREDITS))
        assertFalse(health.isAvailable(SpeechService.SARVAM))
        health.reset(SpeechService.SARVAM)
        assertTrue(health.isAvailable(SpeechService.SARVAM))
        assertFalse(health.failure(SpeechService.DEEPGRAM, TranscriptionErrorKind.TIMEOUT))
    }

    private class FakeCleaner(val reply: (String) -> String) : TextCleaner {
        val seen = ArrayList<Pair<String, CleanupRequest>>()
        override val name = "fake"

        override suspend fun clean(raw: String, request: CleanupRequest): String {
            seen.add(raw to request)
            return reply(raw)
        }
    }

    private fun withCleanup(s: PlainspokenSettings) = s.apply { transcription.cleanup.provider = CleanupProvider.GEMINI }

    @Test
    fun `raw text from a non-Gemini service is cleaned up`() = runBlocking {
        val cleaner = FakeCleaner { "So, I think we should go." }
        val r = router(mapOf(SpeechService.DEEPGRAM to Script("um so I I think we should go")), cleaner = cleaner)
        val s = withCleanup(settings(SpeechService.DEEPGRAM)).apply { snippets = listOf(Snippet("insert my address", "1 Example Road")) }
        val out = r.transcribe(request(), s, null)
        assertEquals("So, I think we should go.", out.text)
        assertEquals("fake", out.cleanedBy)
        assertEquals(listOf("WhatsApp"), cleaner.seen.single().second.vocabulary)
        assertEquals(listOf("insert my address"), cleaner.seen.single().second.keepPhrases)
    }

    @Test
    fun `Gemini text is not cleaned again unless asked`() = runBlocking {
        val cleaner = FakeCleaner { "Cleaned." }
        val r = router(mapOf(SpeechService.GEMINI to Script("Already tidy text here.")), cleaner = cleaner)
        assertEquals("Already tidy text here.", r.transcribe(request(), withCleanup(settings(SpeechService.GEMINI)), null).text)
        assertTrue(cleaner.seen.isEmpty())
        val again = withCleanup(settings(SpeechService.GEMINI)).apply { transcription.cleanup.alsoGemini = true }
        assertEquals("Cleaned.", r.transcribe(request(), again, null).text)
    }

    @Test
    fun `a refused or failed clean-up falls back to the local filler filter`() = runBlocking {
        val summarizer = FakeCleaner { "Summary." }
        val r = router(mapOf(SpeechService.SARVAM to Script("um so we need the report by Thursday and the sales numbers from finance")), cleaner = summarizer)
        assertEquals("so we need the report by Thursday and the sales numbers from finance", r.transcribe(request(), withCleanup(settings(SpeechService.SARVAM)), null).text)

        val failing = object : TextCleaner {
            override val name = "broken"
            override suspend fun clean(raw: String, request: CleanupRequest): String = throw TranscriptionException(TranscriptionErrorKind.SERVER, "503")
        }
        val r2 = router(mapOf(SpeechService.SARVAM to Script("uh please send it today")), cleaner = failing)
        val out = r2.transcribe(request(), withCleanup(settings(SpeechService.SARVAM)), null)
        assertEquals("please send it today", out.text)
        assertNull(out.cleanedBy)
    }

    @Test
    fun `verbatim and very short dictations skip the clean-up`() = runBlocking {
        val cleaner = FakeCleaner { "X." }
        val r = router(mapOf(SpeechService.SARVAM to Script("um yes")), cleaner = cleaner)
        assertEquals("yes", r.transcribe(request(), withCleanup(settings(SpeechService.SARVAM)), null).text)
        val verbatim = withCleanup(settings(SpeechService.SARVAM)).apply { transcription.mode = TranscriptionMode.VERBATIM }
        assertEquals("um yes", r.transcribe(request(mode = TranscriptionMode.VERBATIM), verbatim, null).text)
        assertTrue(cleaner.seen.isEmpty())
    }

    @Test
    fun `streamed text is finished the same way`() = runBlocking {
        val cleaner = FakeCleaner { "Kal meeting hai." }
        val r = router(emptyMap(), cleaner = cleaner)
        val out = r.finish("umm kal meeting hai na", SpeechService.SARVAM, request(), withCleanup(settings(SpeechService.SARVAM)), null, 400)
        assertEquals("Kal meeting hai.", out.text)
        assertTrue(stats.single().streamed)
        assertEquals(400, stats.single().listenMillis)
    }

    @Test
    fun `compressed audio goes only to services that accept it`() = runBlocking {
        val compressor = AudioCompressor { _, _ -> EncodedAudio(ByteArray(10), "audio/mp4", "audio.m4a") }
        val r = router(mapOf(SpeechService.SARVAM to Script("S.")), compressor = compressor)
        r.transcribe(request(), settings(SpeechService.SARVAM), null)
        assertNotNull(requests.single().compressed)
        assertEquals("audio.m4a", requests.single().audioFor(SpeechService.SARVAM).fileName)
        assertEquals("audio.wav", requests.single().audioFor(SpeechService.GEMINI).fileName)

        requests.clear()
        val off = settings(SpeechService.SARVAM).apply { transcription.compressAudio = false }
        r.transcribe(request(), off, null)
        assertNull(requests.single().compressed)

        requests.clear()
        router(mapOf(SpeechService.GEMINI to Script("G.")), compressor = compressor).transcribe(request(), settings(SpeechService.GEMINI), null)
        assertNull(requests.single().compressed) // Gemini alone: not worth compressing
    }

    @Test
    fun `snippets expand as a whole dictation or inside a sentence`() {
        val snippets = listOf(Snippet("insert my address", "1 Example Road, Pune"), Snippet("my email sign off", "Best regards"))
        assertEquals("1 Example Road, Pune", SnippetExpander.expand("Insert my address.", snippets))
        assertEquals("Please deliver to 1 Example Road, Pune tomorrow.", SnippetExpander.expand("Please deliver to insert my address tomorrow.", snippets))
        assertEquals("Thanks. Best regards", SnippetExpander.expand("Thanks. My email, sign off", snippets))
        assertEquals("My address is new.", SnippetExpander.expand("My address is new.", snippets))
    }
}
