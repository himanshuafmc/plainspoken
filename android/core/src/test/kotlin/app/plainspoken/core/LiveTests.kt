package app.plainspoken.core

import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.transcription.GeminiLiveTranscriber
import app.plainspoken.core.transcription.LiveSocket
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.IOException
import java.net.URI
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.Duration
import java.time.Instant
import java.util.Base64
import java.util.Collections
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Scripted Live API server for tests. */
class FakeLiveServer(
    var onSetup: (JsonObject) -> Pair<Boolean, String?> = { true to null },
    var onAudioEnd: () -> List<String> = { listOf("""{"serverContent":{"turnComplete":true}}""") },
    var failConnect: Boolean = false,
) {
    val sockets: MutableList<FakeLiveSocket> = Collections.synchronizedList(ArrayList())

    fun create(): LiveSocket = FakeLiveSocket(this).also { sockets.add(it) }
}

class FakeLiveSocket(private val server: FakeLiveServer) : LiveSocket {
    private val incoming = Channel<String?>(Channel.UNLIMITED)
    val sent: MutableList<String> = Collections.synchronizedList(ArrayList())
    var uri: URI? = null
    var key: String? = null

    override var closeReason: String? = null
        private set

    override suspend fun connect(uri: URI, apiKey: String?) {
        this.uri = uri
        key = apiKey
        if (server.failConnect) throw IOException("404")
    }

    override suspend fun send(json: String) {
        sent.add(json)
        val node = Json.parseToJsonElement(json).jsonObject
        val setup = node["setup"]
        if (setup != null) {
            val (accept, reason) = server.onSetup(setup.jsonObject)
            if (accept) {
                incoming.trySend("""{"setupComplete":{}}""")
            } else {
                closeReason = reason
                incoming.trySend(null)
            }
        } else if (node["realtimeInput"]?.jsonObject?.get("audioStreamEnd") != null) {
            server.onAudioEnd().forEach { incoming.trySend(it) }
        }
    }

    override suspend fun receive(): String? = incoming.receive()

    override fun close() {
        incoming.trySend(null)
    }
}

class LiveTests {
    private fun settings() = PlainspokenSettings().apply {
        transcription.liveStreaming = true
        transcription.customVocabulary = listOf("WhatsApp")
    }.normalize()

    private var now = Instant.parse("2026-09-30T06:00:00Z")

    private fun transcriber(server: FakeLiveServer, log: ListLog = ListLog()) = GeminiLiveTranscriber(
        server::create, { "test-key" }, log, CoroutineScope(SupervisorJob() + Dispatchers.Default),
        stepTimeout = Duration.ofSeconds(5), quietAfterEnd = Duration.ofMillis(200), now = { now },
    )

    private fun samples(n: Int, value: Short = 1000) = ShortArray(n) { value }

    @Test
    fun `streams audio and returns the model text`() = runBlocking {
        val server = FakeLiveServer(onAudioEnd = {
            listOf(
                """{"serverContent":{"inputTranscription":{"text":"hello world"}}}""",
                """{"serverContent":{"modelTurn":{"parts":[{"text":"Hello, "},{"text":"thinking","thought":true},{"text":"world."}]}}}""",
                """{"serverContent":{"turnComplete":true}}""",
            )
        })
        val session = transcriber(server).start(settings())
        session.push(samples(1000))
        session.push(samples(1000))
        session.push(samples(500))
        assertEquals("Hello, world.", session.finish())

        val socket = server.sockets.single()
        assertEquals("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent", socket.uri.toString())
        assertEquals("test-key", socket.key)
        val setup = Json.parseToJsonElement(socket.sent[0]).jsonObject["setup"]!!.jsonObject
        assertEquals("models/gemini-3.5-transcribe-live", setup["model"]!!.jsonPrimitive.content)
        val config = setup["generationConfig"]!!.jsonObject["transcriptionConfig"]!!.jsonObject
        assertEquals("smart", config["mode"]!!.jsonPrimitive.content)
        assertEquals("WhatsApp", config["customVocabulary"]!!.jsonArray[0].jsonPrimitive.content)

        var total = 0
        socket.sent.drop(1).filter { "\"audio\"" in it }.forEach { m ->
            val audio = Json.parseToJsonElement(m).jsonObject["realtimeInput"]!!.jsonObject["audio"]!!.jsonObject
            assertEquals("audio/pcm;rate=16000", audio["mimeType"]!!.jsonPrimitive.content)
            val bytes = Base64.getDecoder().decode(audio["data"]!!.jsonPrimitive.content)
            assertEquals(1000.toShort(), ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getShort(0))
            total += bytes.size / 2
        }

        assertEquals(2500, total)
        assertTrue("audioStreamEnd" in socket.sent.last())
    }

    @Test
    fun `a rejected setup shape falls back to the next and is remembered`() = runBlocking {
        val server = FakeLiveServer(
            onSetup = { setup ->
                if (setup["generationConfig"]?.jsonObject?.get("transcriptionConfig") == null) true to null
                else false to "1007 Invalid JSON payload received. Unknown name \"transcriptionConfig\""
            },
            onAudioEnd = { listOf("""{"serverContent":{"inputTranscription":{"text":" hi"}}}""", """{"serverContent":{"turnComplete":true}}""") },
        )
        val log = ListLog()
        val t = transcriber(server, log)
        t.start(settings()).let { it.push(samples(1600)); assertEquals("hi", it.finish()) }
        assertEquals(2, server.sockets.size)
        assertTrue(log.lines.any { "rejected" in it && "Unknown name" in it })
        t.start(settings()).let { it.push(samples(1600)); assertEquals("hi", it.finish()) }
        assertEquals(3, server.sockets.size) // the accepted shape was tried first
    }

    @Test
    fun `trimmed input transcription chunks get their spaces back`() = runBlocking {
        val server = FakeLiveServer(onAudioEnd = {
            listOf(
                """{"serverContent":{"inputTranscription":{"text":"I will call you tomorrow"}}}""",
                """{"serverContent":{"inputTranscription":{"text":"morning."}}}""",
                """{"serverContent":{"inputTranscription":{"text":"Please wait."}}}""",
                """{"serverContent":{"generationComplete":true}}""",
            )
        })
        val session = transcriber(server).start(settings())
        session.push(samples(3200))
        assertEquals("I will call you tomorrow morning. Please wait.", session.finish())
    }

    @Test
    fun `invalid key stops immediately`() = runBlocking {
        val server = FakeLiveServer(onSetup = { false to "1008 API key not valid. Please pass a valid API key." })
        val session = transcriber(server).start(settings())
        session.push(samples(1600))
        assertEquals(TranscriptionErrorKind.INVALID_KEY, assertFailsWith<TranscriptionException> { session.finish() }.kind)
        assertEquals(1, server.sockets.size)
    }

    @Test
    fun `an unreachable endpoint fails once per API version`() = runBlocking {
        val server = FakeLiveServer(failConnect = true)
        val session = transcriber(server).start(settings())
        session.push(samples(1600))
        assertEquals(TranscriptionErrorKind.NETWORK, assertFailsWith<TranscriptionException> { session.finish() }.kind)
        assertEquals(2, server.sockets.size)
    }

    @Test
    fun `repeated failures pause live streaming for a while`() = runBlocking {
        val server = FakeLiveServer(failConnect = true)
        val t = transcriber(server)
        val s = settings()
        suspend fun dictate() {
            val session = t.start(s)
            session.push(samples(1600))
            runCatching { session.finish() }
        }

        dictate()
        assertTrue(t.canStart(s))
        dictate()
        assertFalse(t.canStart(s))
        assertTrue(t.canStart(settings().apply { transcription.liveModel = "gemini-other-live" }))
        dictate()
        dictate()
        assertFalse(t.canStart(s))
        now = now.plus(Duration.ofMinutes(16))
        assertTrue(t.canStart(s))
    }

    @Test
    fun `the log never contains transcript text`() = runBlocking {
        val log = ListLog()
        val server = FakeLiveServer(onAudioEnd = {
            listOf("""{"serverContent":{"inputTranscription":{"text":"secret words"}}}""", """{"serverContent":{"turnComplete":true}}""")
        })
        val session = transcriber(server, log).start(settings())
        session.push(samples(1600))
        assertEquals("secret words", session.finish())
        assertFalse(log.lines.any { "secret" in it })
    }

    @Test
    fun `endpoint follows the base url`() {
        assertEquals(
            "ws://localhost:8080/ws/google.ai.generativelanguage.v1alpha.GenerativeService.BidiGenerateContent",
            GeminiLiveTranscriber.endpoint("http://localhost:8080", "v1alpha").toString(),
        )
    }
}
