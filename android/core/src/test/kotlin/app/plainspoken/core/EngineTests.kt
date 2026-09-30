package app.plainspoken.core

import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.settings.EngineKind
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.transcription.GeminiClient
import app.plainspoken.core.transcription.GeminiGenerateEngine
import app.plainspoken.core.transcription.GeminiTranscribeEngine
import app.plainspoken.core.transcription.KeyTester
import app.plainspoken.core.transcription.RetryPolicy
import app.plainspoken.core.transcription.TranscriptionEngine
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import app.plainspoken.core.transcription.TranscriptionRequest
import app.plainspoken.core.transcription.TranscriptionService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import java.io.IOException
import java.net.UnknownHostException
import java.time.Duration
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class EngineTests {
    private val settings = PlainspokenSettings().normalize()
    private val noSleep = RetryPolicy(delay = {})
    private val interactionOk = Fixtures.read("interactions-response.json")

    private fun request(bytes: Int = 1000) = TranscriptionRequest(
        WavEncoder.encode(ShortArray(bytes / 2)), 1.0, settings.transcription.mode, settings.transcription.languages, listOf("WhatsApp"),
    )

    private fun client(http: FakeHttp, key: String? = "test-key") =
        GeminiClient(http.client, settings.transcription.apiBaseUrl, { key }, ListLog(), noSleep)

    @Test
    fun `short clips go inline in one request with the key header`() = runBlocking {
        val http = FakeHttp { FakeResponse(body = interactionOk) }
        val text = GeminiTranscribeEngine(client(http), "gemini-3.5-transcribe", Duration.ofSeconds(60)).transcribe(request())
        assertTrue(text.startsWith("So, please add milk"))
        val r = http.requests.single()
        assertEquals("https://generativelanguage.googleapis.com/v1beta/interactions", r.url)
        assertEquals("test-key", r.headers["x-goog-api-key"])
        assertTrue("\"data\":" in r.body)
    }

    @Test
    fun `a rejected inline request falls back to upload, transcribe and delete`() = runBlocking {
        val http = FakeHttp { req ->
            when {
                req.url.endsWith("/v1beta/interactions") && "\"data\":" in req.body -> FakeResponse(400, """{"error":{"message":"inline not supported","code":"invalid_request"}}""")
                req.url.endsWith("/upload/v1beta/files") -> FakeResponse(headers = mapOf("X-Goog-Upload-URL" to "https://generativelanguage.googleapis.com/upload/v1beta/files?upload_id=abc"))
                "upload_id=abc" in req.url -> FakeResponse(body = Fixtures.read("upload-response.json"))
                req.url.endsWith("/v1beta/interactions") -> FakeResponse(body = interactionOk)
                req.method == "DELETE" -> FakeResponse(body = "{}")
                else -> FakeResponse(500)
            }
        }
        val engine = GeminiTranscribeEngine(client(http), "gemini-3.5-transcribe", Duration.ofSeconds(60))
        assertTrue(engine.transcribe(request()).startsWith("So, please"))
        engine.cleanupJob!!.join()
        val methods = http.requests.map { it.method + " " + it.url.substringAfter(".com/").substringBefore('?') }
        assertEquals(
            listOf("POST v1beta/interactions", "POST upload/v1beta/files", "POST upload/v1beta/files", "POST v1beta/interactions", "DELETE v1beta/files/1qp4xte94dib"),
            methods,
        )
        assertTrue("\"uri\":\"https://generativelanguage.googleapis.com/v1beta/files/1qp4xte94dib\"" in http.requests[3].body)
    }

    @Test
    fun `server errors are retried twice then reported`() = runBlocking {
        val http = FakeHttp { FakeResponse(503, """{"error":{"code":503,"status":"UNAVAILABLE","message":"busy"}}""") }
        val e = assertFailsWith<TranscriptionException> {
            GeminiTranscribeEngine(client(http), "gemini-3.5-transcribe", Duration.ofSeconds(60)).transcribe(request())
        }
        assertEquals(TranscriptionErrorKind.SERVER, e.kind)
        assertEquals(3, http.requests.size)
    }

    @Test
    fun `network and missing key errors are mapped`() = runBlocking {
        val offline = FakeHttp { FakeResponse(throwable = UnknownHostException("no dns")) }
        assertEquals(
            TranscriptionErrorKind.NETWORK,
            assertFailsWith<TranscriptionException> { GeminiTranscribeEngine(client(offline), "m", Duration.ofSeconds(5)).transcribe(request()) }.kind,
        )
        val http = FakeHttp { FakeResponse() }
        assertEquals(
            TranscriptionErrorKind.NO_KEY,
            assertFailsWith<TranscriptionException> { GeminiTranscribeEngine(client(http, key = " "), "m", Duration.ofSeconds(5)).transcribe(request()) }.kind,
        )
        assertTrue(http.requests.isEmpty())
    }

    @Test
    fun `the key is only sent to the configured host`() = runBlocking {
        val http = FakeHttp { FakeResponse() }
        val c = client(http)
        c.sendJsonOnce("GET", "https://elsewhere.example/v1/x", null, Duration.ofSeconds(5), "t", false)
        assertFalse(http.requests.single().headers.containsKey("x-goog-api-key"))
    }

    @Test
    fun `generate engine posts to generateContent`() = runBlocking {
        val http = FakeHttp { FakeResponse(body = Fixtures.read("generate-response.json")) }
        val text = GeminiGenerateEngine(client(http), "gemini-3.5-flash-lite", Duration.ofSeconds(60)).transcribe(request())
        assertTrue(text.startsWith("So, please"))
        assertEquals("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash-lite:generateContent", http.requests.single().url)
    }

    @Test
    fun `rate limits switch to the backup engine`() = runBlocking {
        val calls = ArrayList<EngineKind>()
        val service = TranscriptionService(
            { kind, _ ->
                object : TranscriptionEngine {
                    override val kind = kind
                    override suspend fun transcribe(request: TranscriptionRequest): String {
                        calls.add(kind)
                        if (kind == EngineKind.TRANSCRIBE) throw TranscriptionException(TranscriptionErrorKind.DAILY_QUOTA, "429", 429)
                        return "from backup"
                    }
                }
            },
            ListLog(),
            workDispatcher = Dispatchers.Unconfined,
        )
        val statuses = ArrayList<String>()
        val outcome = service.transcribe(request(), settings) { statuses.add(it) }
        assertEquals("from backup", outcome.text)
        assertEquals(EngineKind.GENERATE, outcome.engine)
        assertEquals(listOf(EngineKind.TRANSCRIBE, EngineKind.GENERATE), calls)
        assertTrue(statuses.single().contains("backup"))
    }

    @Test
    fun `without the backup a short per-minute limit waits and retries once`() = runBlocking {
        var attempts = 0
        val waits = ArrayList<Duration>()
        val s = PlainspokenSettings().apply { transcription.useBackupEngineWhenLimited = false }
        val service = TranscriptionService(
            { kind, _ ->
                object : TranscriptionEngine {
                    override val kind = kind
                    override suspend fun transcribe(request: TranscriptionRequest): String {
                        if (attempts++ == 0) throw TranscriptionException(TranscriptionErrorKind.RATE_LIMITED, "429", 429, Duration.ofSeconds(5))
                        return "ok"
                    }
                }
            },
            ListLog(),
            delay = { waits.add(it) },
            workDispatcher = Dispatchers.Unconfined,
        )
        assertEquals("ok", service.transcribe(request(), s, null).text)
        assertEquals(listOf(Duration.ofSeconds(6)), waits)
    }

    @Test
    fun `key tester explains the result`() = runBlocking {
        val ok = FakeHttp { FakeResponse(body = """{"name":"models/gemini-3.5-transcribe"}""") }
        assertTrue(KeyTester.test(ok.client, settings.transcription.apiBaseUrl, "gemini-3.5-transcribe", "k", ListLog()).ok)
        val bad = FakeHttp { FakeResponse(400, Fixtures.read("error-400-invalid-key.json")) }
        val result = KeyTester.test(bad.client, settings.transcription.apiBaseUrl, "gemini-3.5-transcribe", "k", ListLog())
        assertFalse(result.ok)
        assertTrue("did not accept" in result.message)
        assertFalse(KeyTester.test(ok.client, settings.transcription.apiBaseUrl, "m", "  ", ListLog()).ok)
    }

    @Test
    fun `logs never contain the key`() = runBlocking {
        val log = ListLog()
        val http = FakeHttp { FakeResponse(500, "oops key=${FakeKey.value}") }
        runCatching {
            GeminiTranscribeEngine(GeminiClient(http.client, settings.transcription.apiBaseUrl, { FakeKey.value }, log, noSleep), "m", Duration.ofSeconds(5))
                .transcribe(request())
        }
        assertTrue(log.lines.isNotEmpty())
        assertFalse(log.lines.any { FakeKey.value in it })
    }

    @Test
    fun `io failures while reading are network errors`() = runBlocking {
        val http = FakeHttp { FakeResponse(throwable = IOException("reset")) }
        val e = assertFailsWith<TranscriptionException> { GeminiGenerateEngine(client(http), "m", Duration.ofSeconds(5)).transcribe(request()) }
        assertEquals(TranscriptionErrorKind.NETWORK, e.kind)
    }
}
