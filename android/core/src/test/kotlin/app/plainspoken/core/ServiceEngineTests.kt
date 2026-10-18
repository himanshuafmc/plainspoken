package app.plainspoken.core

import app.plainspoken.core.audio.AudioChunker
import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.settings.HindiScript
import app.plainspoken.core.settings.LanguagePreset
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.settings.SpeechServiceSettings
import app.plainspoken.core.settings.TranscriptionMode
import app.plainspoken.core.transcription.DeepgramEngine
import app.plainspoken.core.transcription.ElevenLabsEngine
import app.plainspoken.core.transcription.KeyTester
import app.plainspoken.core.transcription.OpenAiEngine
import app.plainspoken.core.transcription.RetryPolicy
import app.plainspoken.core.transcription.SarvamEngine
import app.plainspoken.core.transcription.ServiceClient
import app.plainspoken.core.transcription.ServiceErrors
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import app.plainspoken.core.transcription.TranscriptionRequest
import kotlinx.coroutines.runBlocking
import java.time.Duration
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ServiceEngineTests {
    private val noSleep = RetryPolicy(maxServerRetries = 1, delay = {})
    private val timeout = Duration.ofSeconds(30)

    private fun request(
        seconds: Double = 2.0,
        languages: LanguagePreset = LanguagePreset.ENGLISH_HINDI,
        mode: TranscriptionMode = TranscriptionMode.SMART,
        script: HindiScript = HindiScript.ROMAN,
        vocabulary: List<String> = listOf("WhatsApp", "New Delhi"),
    ) = TranscriptionRequest(WavEncoder.encode(speech(seconds)), seconds, mode, languages, vocabulary, script)

    private fun client(http: FakeHttp, service: SpeechService, base: String = service.defaultBaseUrl) =
        ServiceClient(http.client, service, base, { "test-key" }, ListLog(), noSleep)

    private fun config(service: SpeechService, configure: SpeechServiceSettings.() -> Unit = {}) =
        SpeechServiceSettings(model = service.defaultModel, apiBaseUrl = service.defaultBaseUrl).apply(configure)

    @Test
    fun `sarvam posts multipart with its key header and reads the transcript`() = runBlocking {
        val http = FakeHttp { FakeResponse(body = """{"request_id":"r1","transcript":"kal meeting hai","language_code":"hi-IN"}""") }
        val text = SarvamEngine(client(http, SpeechService.SARVAM), config(SpeechService.SARVAM), timeout).transcribe(request())
        assertEquals("kal meeting hai", text)
        val r = http.requests.single()
        assertEquals("https://api.sarvam.ai/speech-to-text", r.url)
        assertEquals("test-key", r.headers["api-subscription-key"])
        assertFalse(r.headers.containsKey("authorization"))
        assertTrue("name=\"model\"" in r.body && "saaras:v3" in r.body, r.body.take(600))
        assertTrue("translit" in r.body) // Hindi in Roman letters
        assertTrue("unknown" in r.body) // language auto-detected for English + Hindi
        assertFalse("keyterms" in r.body) // only with saaras:v4
    }

    @Test
    fun `sarvam modes and key terms follow the settings`() {
        val sarvam = config(SpeechService.SARVAM)
        assertEquals("codemix", SarvamEngine.mode(request(script = HindiScript.DEVANAGARI), sarvam))
        assertEquals("verbatim", SarvamEngine.mode(request(mode = TranscriptionMode.VERBATIM), sarvam))
        assertEquals("transcribe", SarvamEngine.mode(request(languages = LanguagePreset.ENGLISH), sarvam))
        assertEquals("codemix", SarvamEngine.mode(request(), config(SpeechService.SARVAM) { option = "codemix" }))
        assertEquals("en-IN", SarvamEngine.language(LanguagePreset.ENGLISH))
        val v4 = config(SpeechService.SARVAM) { model = "saaras:v4" }
        val body = okio.Buffer().also { SarvamEngine.buildBody(request().audioFor(SpeechService.SARVAM), request(), v4).writeTo(it) }.readUtf8()
        assertTrue("[\"WhatsApp\",\"New Delhi\"]" in body)
    }

    @Test
    fun `long recordings are split for sarvam's 30 second limit and joined in order`() = runBlocking {
        var n = 0
        val http = FakeHttp { synchronized(this) { n++ }; FakeResponse(body = """{"transcript":"part"}""") }
        val text = SarvamEngine(client(http, SpeechService.SARVAM), config(SpeechService.SARVAM), timeout).transcribe(request(seconds = 70.0))
        assertEquals(3, http.requests.size)
        assertEquals("part part part", text)
    }

    @Test
    fun `the chunker cuts at the quietest moment before the limit`() {
        val loud = speech(40.0)
        for (i in (25 * 16000) until (25 * 16000 + 3200)) loud[i] = 0 // 200 ms of silence at 25 s
        val parts = AudioChunker.split(loud, 16000, maxSeconds = 28.0)
        assertEquals(2, parts.size)
        assertTrue(parts[0].size in (25 * 16000)..(25 * 16000 + 3200), "cut at ${parts[0].size / 16000.0}s")
        assertEquals(loud.size, parts.sumOf { it.size })
        assertEquals(1, AudioChunker.split(speech(10.0), 16000, maxSeconds = 28.0).size)
    }

    @Test
    fun `openai-compatible services get a bearer token, the model and a style prompt`() = runBlocking {
        val http = FakeHttp { FakeResponse(body = """{"text":"Kal meeting hai, please inform kar dena."}""") }
        val engine = OpenAiEngine(client(http, SpeechService.OPENAI, "https://api.groq.com/openai/v1"), config(SpeechService.OPENAI) { model = "whisper-large-v3-turbo" }, timeout)
        assertEquals("Kal meeting hai, please inform kar dena.", engine.transcribe(request()))
        val r = http.requests.single()
        assertEquals("https://api.groq.com/openai/v1/audio/transcriptions", r.url)
        assertEquals("Bearer test-key", r.headers["authorization"])
        assertTrue("whisper-large-v3-turbo" in r.body)
        assertTrue("Kal meeting hai" in r.body) // the Roman-script example in the prompt
        assertTrue("WhatsApp, New Delhi." in r.body)
        assertFalse("name=\"language\"" in r.body)
        val english = OpenAiEngine.prompt(request(languages = LanguagePreset.ENGLISH, vocabulary = emptyList()), config(SpeechService.OPENAI))
        assertEquals("", english)
    }

    @Test
    fun `deepgram sends raw audio with options in the query`() = runBlocking {
        val http = FakeHttp { FakeResponse(body = """{"results":{"channels":[{"alternatives":[{"transcript":"Hello there.","confidence":0.98}]}]}}""") }
        assertEquals("Hello there.", DeepgramEngine(client(http, SpeechService.DEEPGRAM), config(SpeechService.DEEPGRAM), timeout).transcribe(request()))
        val r = http.requests.single()
        assertTrue(r.url.startsWith("https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&punctuate=true&language=multi"), r.url)
        assertTrue("keyterm=WhatsApp" in r.url && "keyterm=New%20Delhi" in r.url, r.url)
        assertEquals("Token test-key", r.headers["authorization"])
        assertEquals("audio/wav", r.contentType)
        val verbatim = DeepgramEngine.query(request(mode = TranscriptionMode.VERBATIM, languages = LanguagePreset.ENGLISH), config(SpeechService.DEEPGRAM))
        assertTrue("filler_words" to "true" in verbatim && "language" to "en" in verbatim)
    }

    @Test
    fun `elevenlabs asks scribe_v2 to drop fillers and sends hints only when on`() = runBlocking {
        val http = FakeHttp { FakeResponse(body = """{"language_code":"hin","language_probability":0.9,"text":"Theek hai.","words":[]}""") }
        val engine = ElevenLabsEngine(client(http, SpeechService.ELEVENLABS), config(SpeechService.ELEVENLABS) { vocabularyHints = false }, timeout)
        assertEquals("Theek hai.", engine.transcribe(request()))
        val r = http.requests.single()
        assertEquals("https://api.elevenlabs.io/v1/speech-to-text", r.url)
        assertEquals("test-key", r.headers["xi-api-key"])
        assertTrue("scribe_v2" in r.body && "no_verbatim" in r.body)
        assertFalse("keyterms" in r.body)
        val withHints = okio.Buffer().also {
            ElevenLabsEngine.buildBody(request().audioFor(SpeechService.ELEVENLABS), request(), config(SpeechService.ELEVENLABS)).writeTo(it)
        }.readUtf8()
        assertEquals(2, Regex("name=\"keyterms\"").findAll(withHints).count())
    }

    @Test
    fun `service errors map to kinds the router and the user understand`() {
        fun kind(status: Int, body: String) = ServiceErrors.fromResponse(SpeechService.SARVAM, status, body, null).kind
        assertEquals(TranscriptionErrorKind.INVALID_KEY, kind(401, """{"error":{"message":"Invalid API subscription key"}}"""))
        assertEquals(TranscriptionErrorKind.NO_CREDITS, kind(402, "{}"))
        assertEquals(TranscriptionErrorKind.NO_CREDITS, kind(403, """{"error":{"message":"Insufficient credits"}}"""))
        assertEquals(TranscriptionErrorKind.NO_CREDITS, kind(429, """{"error":{"message":"You exceeded your current quota, please check your plan and billing details","type":"insufficient_quota"}}"""))
        assertEquals(TranscriptionErrorKind.RATE_LIMITED, kind(429, """{"err_code":"TOO_MANY_REQUESTS","err_msg":"slow down"}"""))
        assertEquals(TranscriptionErrorKind.MODEL_NOT_FOUND, kind(400, """{"detail":{"status":"invalid_model","message":"Model nova-9 not found"}}"""))
        assertEquals(TranscriptionErrorKind.SERVER, kind(503, "<html>busy</html>"))
        assertEquals(TranscriptionErrorKind.TIMEOUT, kind(504, ""))
        assertEquals(TranscriptionErrorKind.BAD_REQUEST, kind(413, """{"detail":"File too large"}"""))
        val e = ServiceErrors.fromResponse(SpeechService.DEEPGRAM, 401, """{"err_msg":"Invalid credentials"}""", Duration.ofSeconds(3))
        assertEquals(SpeechService.DEEPGRAM, e.service)
        assertEquals(Duration.ofSeconds(3), e.retryAfter)
    }

    @Test
    fun `server errors are retried and the key never leaves its host`() = runBlocking {
        var first = true
        val http = FakeHttp {
            if (first) {
                first = false
                FakeResponse(503, "{}")
            } else {
                FakeResponse(body = """{"text":"ok"}""")
            }
        }
        assertEquals("ok", OpenAiEngine(client(http, SpeechService.OPENAI), config(SpeechService.OPENAI), timeout).transcribe(request()))
        assertEquals(2, http.requests.size)

        val other = FakeHttp { FakeResponse(body = "{}") }
        val c = client(other, SpeechService.OPENAI)
        c.sendJson({ okhttp3.Request.Builder().url("https://elsewhere.example/x").build() }, timeout, "t")
        assertFalse(other.requests.single().headers.containsKey("authorization"))
    }

    @Test
    fun `missing keys and odd responses fail clearly`() = runBlocking {
        val http = FakeHttp { FakeResponse(body = """{"unexpected":true}""") }
        val noKey = ServiceClient(http.client, SpeechService.SARVAM, SpeechService.SARVAM.defaultBaseUrl, { "  " }, ListLog())
        val e = assertFailsWith<TranscriptionException> { SarvamEngine(noKey, config(SpeechService.SARVAM), timeout).transcribe(request()) }
        assertEquals(TranscriptionErrorKind.NO_KEY, e.kind)
        val odd = assertFailsWith<TranscriptionException> { SarvamEngine(client(http, SpeechService.SARVAM), config(SpeechService.SARVAM), timeout).transcribe(request()) }
        assertEquals(TranscriptionErrorKind.UNEXPECTED_RESPONSE, odd.kind)
    }

    @Test
    fun `logs never contain any service's key`() = runBlocking {
        val sarvamLike = "sk_" + "a1b2c3d4_" + "Q".repeat(24)
        val openAiLike = "sk-" + "proj-" + "Z".repeat(30)
        val deepgramLike = "0123456789abcdef".repeat(2) + "01234567"
        val log = ListLog()
        val http = FakeHttp { FakeResponse(401, """{"error":{"message":"bad key $sarvamLike, also Bearer $openAiLike and $deepgramLike"}}""") }
        runCatching {
            SarvamEngine(ServiceClient(http.client, SpeechService.SARVAM, SpeechService.SARVAM.defaultBaseUrl, { sarvamLike }, log, noSleep), config(SpeechService.SARVAM), timeout)
                .transcribe(request())
        }
        assertTrue(log.lines.isNotEmpty())
        assertFalse(log.lines.any { sarvamLike in it || openAiLike in it || deepgramLike in it }, log.lines.joinToString("\n"))
    }

    @Test
    fun `key tests use a free endpoint per service`() = runBlocking {
        val settings = PlainspokenSettings().normalize()
        for ((service, path) in listOf(
            SpeechService.SARVAM to "https://api.sarvam.ai/v2/models",
            SpeechService.OPENAI to "https://api.openai.com/v1/models",
            SpeechService.DEEPGRAM to "https://api.deepgram.com/v1/projects",
            SpeechService.ELEVENLABS to "https://api.elevenlabs.io/v1/models",
        )) {
            val http = FakeHttp { FakeResponse(body = "{}") }
            assertTrue(KeyTester.test(http.client, service, settings, "k", ListLog()).ok)
            assertEquals(path, http.requests.single().url)
        }

        val bad = FakeHttp { FakeResponse(401, """{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}""") }
        val result = KeyTester.test(bad.client, SpeechService.ELEVENLABS, settings, "k", ListLog())
        assertFalse(result.ok)
        assertTrue("ElevenLabs did not accept" in result.message, result.message)
        val noModels = FakeHttp { FakeResponse(404, "{}") }
        assertTrue(KeyTester.test(noModels.client, SpeechService.OPENAI, settings, "k", ListLog()).ok) // e.g. a local Whisper server
    }
}
