package app.plainspoken.core

import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.transcription.DeepgramStream
import app.plainspoken.core.transcription.LiveRouter
import app.plainspoken.core.transcription.LiveSession
import app.plainspoken.core.transcription.LiveSocket
import app.plainspoken.core.transcription.LiveTranscriber
import app.plainspoken.core.transcription.SarvamStream
import app.plainspoken.core.transcription.StreamProgress
import app.plainspoken.core.transcription.StreamProtocol
import app.plainspoken.core.transcription.StreamingTranscriber
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import java.io.IOException
import java.net.URI
import java.time.Duration
import java.time.Instant
import java.util.Collections
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** A scripted streaming server: [onText] answers each text frame, [onBinary] each audio frame. */
class FakeStreamSocket(
    private val onText: (String) -> List<String> = { emptyList() },
    private val failConnect: Boolean = false,
    private val closeWith: String? = null,
) : LiveSocket {
    private val incoming = Channel<String?>(Channel.UNLIMITED)
    val sentText: MutableList<String> = Collections.synchronizedList(ArrayList())
    var binaryFrames = 0
    var uri: URI? = null
    var headers: Map<String, String> = emptyMap()

    override var closeReason: String? = null
        private set

    override suspend fun connect(uri: URI, headers: Map<String, String>) {
        this.uri = uri
        this.headers = headers
        if (failConnect) throw IOException("refused")
        if (closeWith != null) {
            closeReason = closeWith
            incoming.trySend(null)
        }
    }

    override suspend fun send(json: String) {
        sentText.add(json)
        onText(json).forEach { incoming.trySend(it) }
    }

    override suspend fun sendBinary(bytes: ByteArray) {
        binaryFrames++
    }

    override suspend fun receive(): String? = incoming.receive()

    /** A message the server sends on its own, not in answer to a frame. */
    fun serverSends(message: String) {
        incoming.trySend(message)
    }

    override fun close() {
        incoming.trySend(null)
    }
}

class StreamingTests {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private fun settings() = PlainspokenSettings().apply {
        transcription.serviceOrder = listOf("sarvam", "deepgram")
        transcription.customVocabulary = listOf("WhatsApp")
    }.normalize()

    private fun transcriber(protocol: StreamProtocol, socket: FakeStreamSocket, now: () -> Instant = { Instant.EPOCH.plusSeconds(1) }) =
        StreamingTranscriber(protocol, { socket }, { "test-key" }, ListLog(), scope, Duration.ofSeconds(5), Duration.ofMillis(300), now = now)

    private fun push(session: LiveSession, seconds: Double) {
        repeat((seconds * 10).toInt()) { session.push(ShortArray(1600) { 1000 }) }
    }

    @Test
    fun `sarvam streams base64 audio, flushes and joins the parts`() = runBlocking {
        val socket = FakeStreamSocket(onText = { msg ->
            if ("\"flush\"" in msg) listOf("""{"type":"data","data":{"request_id":"a","transcript":"kal meeting hai","metrics":{}}}""") else emptyList()
        })
        val session = transcriber(SarvamStream, socket).start(settings())
        push(session, 1.0)
        assertEquals("kal meeting hai", session.finish())
        assertEquals(SpeechService.SARVAM, session.service)
        val uri = socket.uri.toString()
        assertTrue(uri.startsWith("wss://api.sarvam.ai/speech-to-text/ws?language-code=unknown&model=saaras%3Av3&mode=translit"), uri)
        assertTrue("input_audio_codec=pcm_s16le" in uri && "flush_signal=true" in uri)
        assertEquals("test-key", socket.headers["Api-Subscription-Key"])
        assertTrue(socket.sentText.first().startsWith("""{"audio":{"data":"""))
        assertEquals("""{"type":"flush"}""", socket.sentText.last())
    }

    @Test
    fun `sarvam waits for a transcript it still owes after the flush`() = runBlocking {
        // The transcript comes after quietAfterEnd (300 ms here); the speech signals say it is still owed.
        lateinit var socket: FakeStreamSocket
        var started = false
        socket = FakeStreamSocket(onText = { msg ->
            when {
                "\"flush\"" in msg -> {
                    scope.launch {
                        delay(700)
                        socket.serverSends(transcript("kal meeting hai"))
                    }
                    emptyList()
                }
                started -> emptyList()
                else -> {
                    started = true
                    listOf(SPEECH_STARTED)
                }
            }
        })
        val session = transcriber(SarvamStream, socket).start(settings())
        push(session, 1.0)
        assertEquals("kal meeting hai", session.finish())
        assertTrue("vad_signals=true" in socket.uri.toString())
    }

    @Test
    fun `sarvam stops as soon as no transcript is owed`() = runBlocking {
        val socket = FakeStreamSocket(onText = { msg ->
            if ("\"flush\"" in msg) listOf(SPEECH_STARTED, SPEECH_ENDED, transcript("theek hai")) else emptyList()
        })
        val t = StreamingTranscriber(
            SarvamStream, { socket }, { "test-key" }, ListLog(), scope,
            quietAfterEnd = Duration.ofSeconds(3), settleAfterEnd = Duration.ofMillis(100),
        )
        val session = t.start(settings())
        push(session, 0.5)
        val started = System.nanoTime()
        assertEquals("theek hai", session.finish())
        val waited = (System.nanoTime() - started) / 1_000_000
        assertTrue(waited < 1500, "waited $waited ms for quiet instead of stopping")
    }

    @Test
    fun `sarvam's speech signals say how many transcripts are still to come`() {
        val p = StreamProgress()
        val pieces = ArrayList<String>()
        assertNull(p.owed)
        SarvamStream.apply(SPEECH_STARTED, pieces, endSent = false, progress = p)
        assertEquals(1, p.owed)
        SarvamStream.apply(transcript("part"), pieces, endSent = false, progress = p) // a long stretch may come in parts
        assertEquals(1, p.owed)
        SarvamStream.apply(SPEECH_ENDED, pieces, endSent = false, progress = p)
        SarvamStream.apply(SPEECH_STARTED, pieces, endSent = false, progress = p)
        assertEquals(2, p.owed)
        SarvamStream.apply(transcript("one"), pieces, endSent = false, progress = p)
        assertEquals(1, p.owed)
        SarvamStream.apply(transcript("two"), pieces, endSent = true, progress = p) // the flush ends the stretch still going on
        assertEquals(0, p.owed)
        assertEquals(listOf("part", "one", "two"), pieces)
    }

    @Test
    fun `deepgram streams binary audio and stops when Finalize is answered`() = runBlocking {
        val socket = FakeStreamSocket(onText = { msg ->
            if ("Finalize" in msg) {
                listOf(
                    """{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"Hello there.","confidence":0.9}]}}""",
                    """{"type":"Results","is_final":true,"from_finalize":true,"channel":{"alternatives":[{"transcript":"How are you?","confidence":0.9}]}}""",
                )
            } else {
                emptyList()
            }
        })
        val session = transcriber(DeepgramStream, socket).start(settings())
        push(session, 0.5)
        assertEquals("Hello there. How are you?", session.finish())
        assertTrue(socket.binaryFrames >= 4)
        assertEquals("Token test-key", socket.headers["Authorization"])
        val uri = socket.uri.toString()
        assertTrue(uri.startsWith("wss://api.deepgram.com/v1/listen?model=nova-3") && "encoding=linear16" in uri && "keyterm=WhatsApp" in uri, uri)
        assertEquals("""{"type":"CloseStream"}""", socket.sentText.last())
    }

    @Test
    fun `an error message or a refused connection fails the stream`() = runBlocking {
        val bad = FakeStreamSocket(onText = { msg ->
            if ("flush" in msg) listOf("""{"type":"error","data":{"error":"Invalid API key","code":"auth"}}""") else emptyList()
        })
        val s1 = transcriber(SarvamStream, bad).start(settings())
        push(s1, 0.3)
        assertEquals(TranscriptionErrorKind.INVALID_KEY, assertFailsWith<TranscriptionException> { s1.finish() }.kind)

        val refused = FakeStreamSocket(failConnect = true)
        val s2 = transcriber(DeepgramStream, refused).start(settings())
        push(s2, 0.3)
        assertEquals(TranscriptionErrorKind.NETWORK, assertFailsWith<TranscriptionException> { s2.finish() }.kind)
    }

    @Test
    fun `repeated failures pause streaming for a while`() = runBlocking {
        var now = Instant.parse("2026-10-18T10:00:00Z")
        val t = transcriber(DeepgramStream, FakeStreamSocket(failConnect = true)) { now }
        repeat(2) {
            val s = t.start(settings())
            runCatching { s.finish() }
        }
        assertFalse(t.canStart(settings()))
        now = now.plus(Duration.ofMinutes(16))
        assertTrue(t.canStart(settings()))
    }

    @Test
    fun `the live router streams only with the main service when it can`() {
        val fake = object : LiveTranscriber {
            override fun start(settings: PlainspokenSettings): LiveSession = throw UnsupportedOperationException()
        }
        var first: SpeechService? = SpeechService.SARVAM
        val router = LiveRouter(mapOf(SpeechService.SARVAM to fake, SpeechService.GEMINI to fake)) { first }
        val s = settings()
        assertTrue(router.canStart(s))
        s.transcription.services.sarvam.streaming = false
        assertFalse(router.canStart(s))
        first = SpeechService.ELEVENLABS
        assertFalse(router.canStart(settings()))
        first = SpeechService.GEMINI
        assertTrue(router.canStart(settings()))
        assertFalse(router.canStart(settings().apply { transcription.liveStreaming = false }))
        first = null
        assertFalse(router.canStart(settings()))
    }

    private companion object {
        const val SPEECH_STARTED = """{"type":"events","data":{"signal_type":"START_SPEECH","occured_at":0.4}}"""
        const val SPEECH_ENDED = """{"type":"events","data":{"signal_type":"END_SPEECH","occured_at":2.1}}"""

        fun transcript(text: String) = """{"type":"data","data":{"request_id":"a","transcript":"$text","metrics":{}}}"""
    }
}
