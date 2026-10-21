package app.plainspoken.core.transcription

import app.plainspoken.core.logging.Log
import app.plainspoken.core.logging.Redactor
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SpeechService
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonObject
import java.io.IOException
import java.net.URI
import java.net.URLEncoder
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.Duration
import java.time.Instant
import java.util.Base64
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.cancellation.CancellationException

/** How one service streams: where to connect, how audio and the end are sent, and how replies are read. */
interface StreamProtocol {
    val service: SpeechService

    fun uri(settings: PlainspokenSettings): URI

    suspend fun sendAudio(socket: LiveSocket, pcm: ByteArray)

    /** Sent after the last audio, asking the service to finish. */
    val endMessage: String

    /** Sent (best effort) before closing, if the service wants one. */
    val closeMessage: String?

    /**
     * Reads one server message, adding finished text to [pieces] and what it says about speech to [progress]. Returns
     * true when the service has confirmed the end of the stream. Throws [TranscriptionException] for an error message.
     */
    fun apply(message: String, pieces: MutableList<String>, endSent: Boolean, progress: StreamProgress): Boolean
}

/**
 * What a service that reports when speech starts and ends (Sarvam's VAD signals) has said in one dictation, so the end
 * can be told apart from a slow reply. Each stretch of speech gets a transcript once it ends, or once the end of audio
 * ends it.
 */
class StreamProgress {
    private var reported = false
    private var open = false
    private var ended = 0 // stretches that ended and haven't been transcribed yet

    fun speechStarted() {
        reported = true
        open = true
    }

    fun speechEnded() {
        reported = true
        if (!open) return
        open = false
        ended++
    }

    /** A transcript arrived. Before the end of audio, one for a stretch still going on is only part of it. */
    fun transcribed(endSent: Boolean) {
        if (ended > 0) {
            ended--
        } else if (endSent) {
            open = false
        }
    }

    /** Transcripts still to come, or null when the service doesn't say. */
    val owed: Int? get() = if (reported) ended + (if (open) 1 else 0) else null
}

/**
 * Streaming for Sarvam and Deepgram (Gemini has its own, [GeminiLiveTranscriber]). Audio pushed before the socket is
 * open is buffered. After [failuresBeforePause] failed dictations in a row streaming pauses for [pauseAfterFailures]
 * and the normal request is used, so a network that blocks WebSockets doesn't add a delay every time.
 */
class StreamingTranscriber(
    internal val protocol: StreamProtocol,
    private val socketFactory: () -> LiveSocket,
    private val keyFor: (SpeechService) -> String?,
    internal val log: Log,
    internal val scope: CoroutineScope = BackgroundScope,
    internal val stepTimeout: Duration = Duration.ofSeconds(10),
    /** After the end of audio, stop once nothing more arrives for this long (services that don't say what's to come). */
    internal val quietAfterEnd: Duration = Duration.ofMillis(900),
    /** After the end of audio with no transcript owed, stop once nothing more arrives for this long. */
    internal val settleAfterEnd: Duration = Duration.ofMillis(300),
    /** After the end of audio, wait this long for each transcript the service still owes. */
    internal val owedAfterEnd: Duration = Duration.ofMillis(2500),
    private val failuresBeforePause: Int = 2,
    private val pauseAfterFailures: Duration = Duration.ofMinutes(15),
    private val now: () -> Instant = { Instant.now() },
) : LiveTranscriber {
    private val lock = Any()
    private var failures = 0
    private var pausedUntil: Instant = Instant.EPOCH

    internal val key: String? get() = keyFor(protocol.service)?.trim()?.ifEmpty { null }

    internal fun newSocket() = socketFactory()

    override fun canStart(settings: PlainspokenSettings): Boolean = key != null && synchronized(lock) { !now().isBefore(pausedUntil) }

    override fun start(settings: PlainspokenSettings): LiveSession = StreamingSession(this, settings)

    internal fun report(success: Boolean) {
        synchronized(lock) {
            if (success) {
                failures = 0
                return
            }

            if (++failures < failuresBeforePause) return
            failures = 0
            pausedUntil = now().plus(pauseAfterFailures)
        }

        log.warn("live ${protocol.service.id}: $failuresBeforePause failures in a row; paused for ${pauseAfterFailures.toMinutes()} min")
    }
}

internal class StreamingSession(private val owner: StreamingTranscriber, private val settings: PlainspokenSettings) : LiveSession {
    override val service: SpeechService = owner.protocol.service
    private val audio = Channel<ShortArray>(Channel.UNLIMITED)

    @Volatile
    private var pushedSamples = 0L

    @Volatile
    private var aborted = false
    private val run: Deferred<String> = owner.scope.async { run() }
    private val log get() = owner.log
    private val name get() = service.id

    override fun push(samples: ShortArray) {
        if (samples.isNotEmpty() && audio.trySend(samples).isSuccess) pushedSamples += samples.size
    }

    override suspend fun finish(): String {
        audio.close()
        val limit = minOf(30.0, 8 + pushedSamples / 16000.0 / 10)
        try {
            val text = withTimeout((limit * 1000).toLong()) { run.await() }
            owner.report(success = text.isNotBlank())
            return text
        } catch (e: TimeoutCancellationException) {
            abort()
            owner.report(success = false)
            throw TranscriptionException(TranscriptionErrorKind.TIMEOUT, "live $name: no final transcript in time", cause = e, service = service)
        } catch (e: CancellationException) {
            if (!aborted && run.isCancelled) owner.report(success = false)
            throw e
        } catch (e: Exception) {
            owner.report(success = false)
            throw e
        }
    }

    override fun abort() {
        aborted = true
        audio.close()
        run.cancel()
    }

    private suspend fun run(): String {
        val key = owner.key ?: throw TranscriptionException(TranscriptionErrorKind.NO_KEY, "live $name: no key", service = service)
        val socket = owner.newSocket()
        try {
            try {
                withTimeout(owner.stepTimeout.toMillis()) { socket.connect(owner.protocol.uri(settings), ServiceClient.authHeaders(service, key)) }
            } catch (e: TimeoutCancellationException) {
                throw TranscriptionException(TranscriptionErrorKind.NETWORK, "live $name: connect timed out", cause = e, service = service)
            } catch (e: IOException) {
                log.warn("live $name: connect failed: ${Redactor.clean(e.message, 160)}")
                throw TranscriptionException(TranscriptionErrorKind.NETWORK, "live $name: connect failed", cause = e, service = service)
            }

            return stream(socket)
        } finally {
            socket.close()
        }
    }

    private suspend fun stream(socket: LiveSocket): String = coroutineScope {
        val protocol = owner.protocol
        val pieces = ArrayList<String>()
        val progress = StreamProgress()
        val endSent = CompletableDeferred<Unit>()
        val ending = AtomicBoolean(false) // set before the end is sent, so a quick reply to it is read as one
        var messages = 0
        val receive = async {
            while (true) {
                val message: String? = if (endSent.isCompleted) {
                    val wait = when (progress.owed) {
                        null -> owner.quietAfterEnd
                        0 -> owner.settleAfterEnd
                        else -> owner.owedAfterEnd
                    }

                    try {
                        withTimeout(wait.toMillis()) { socket.receive() }
                    } catch (_: TimeoutCancellationException) {
                        return@async // finished: nothing more came after the end of audio
                    }
                } else {
                    // Wake up now and then, so the end of audio is noticed even if the last reply already came.
                    (withTimeoutOrNull(POLL_MS) { Received(socket.receive()) } ?: continue).message
                }

                if (message == null) {
                    val reason = socket.closeReason
                    if (pieces.isEmpty() && !reason.isNullOrEmpty() && !reason.startsWith("1000")) {
                        throw TranscriptionException(StreamErrors.kindFor(reason), "live $name: closed: ${Redactor.clean(reason, 200)}", service = service)
                    }

                    return@async
                }

                messages++
                if (protocol.apply(message, pieces, ending.get() || endSent.isCompleted, progress)) return@async
            }
        }

        val buffer = ShortArrayBuilder()
        try {
            for (samples in audio) {
                buffer.add(samples)
                if (buffer.size >= CHUNK_SAMPLES) protocol.sendAudio(socket, drain(buffer))
                if (receive.isCompleted) break
            }

            if (buffer.size > 0 && !receive.isCompleted) protocol.sendAudio(socket, drain(buffer))
            ending.set(true)
            if (!receive.isCompleted) socket.send(protocol.endMessage)
            endSent.complete(Unit)
            receive.await()
            protocol.closeMessage?.let { runCatching { socket.send(it) } }
        } catch (e: IOException) {
            receive.cancel()
            throw TranscriptionException(TranscriptionErrorKind.NETWORK, "live $name: send failed", cause = e, service = service)
        }

        val text = pieces.map { it.trim() }.filter { it.isNotEmpty() }.joinToString(" ")
        log.info("live $name: finished, ${text.length} chars in ${pieces.size} parts ($messages messages)")
        text
    }

    private fun drain(buffer: ShortArrayBuilder): ByteArray {
        val bytes = ByteBuffer.allocate(buffer.size * 2).order(ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until buffer.size) bytes.putShort(buffer[i])
        buffer.clear()
        return bytes.array()
    }

    private class Received(val message: String?)

    private companion object {
        const val CHUNK_SAMPLES = 1600 // 100 ms at 16 kHz
        const val POLL_MS = 200L
    }
}

internal object StreamErrors {
    fun kindFor(reason: String): TranscriptionErrorKind {
        val r = reason.lowercase()
        return when {
            "auth" in r || "key" in r || "401" in r || "403" in r || "credential" in r -> TranscriptionErrorKind.INVALID_KEY
            "credit" in r || "balance" in r || "quota" in r || "402" in r -> TranscriptionErrorKind.NO_CREDITS
            "rate" in r || "429" in r -> TranscriptionErrorKind.RATE_LIMITED
            else -> TranscriptionErrorKind.UNEXPECTED_RESPONSE
        }
    }
}

private fun wss(baseUrl: String, fallback: String, path: String, query: List<Pair<String, String>>): URI {
    val base = URI(PlainspokenSettings.normalizeBaseUrl(baseUrl, fallback))
    val scheme = if (base.scheme == "http") "ws" else "wss"
    val q = query.joinToString("&") { (k, v) -> "${enc(k)}=${enc(v)}" }
    return URI("$scheme://${base.rawAuthority}${base.rawPath.orEmpty().trimEnd('/')}/$path?$q")
}

private fun enc(s: String) = URLEncoder.encode(s, "UTF-8").replace("+", "%20")

/**
 * Sarvam streaming (wss://api.sarvam.ai/speech-to-text/ws): raw 16 kHz PCM sent base64 in JSON audio messages,
 * {"type":"flush"} at the end; replies {"type":"data","data":{"transcript":…}} per stretch of speech, and
 * {"type":"events","data":{"signal_type":"START_SPEECH"|"END_SPEECH"}} when each starts and ends (vad_signals), which is
 * how the end is recognised: Sarvam doesn't confirm the flush.
 */
object SarvamStream : StreamProtocol {
    override val service = SpeechService.SARVAM
    override val endMessage = """{"type":"flush"}"""
    override val closeMessage: String? = null

    override fun uri(settings: PlainspokenSettings): URI {
        val config = settings.transcription.services.sarvam
        val request = TranscriptionRequest.from(ByteArray(0), 0.0, settings)
        val q = arrayListOf(
            "language-code" to SarvamEngine.language(request.languages),
            "model" to config.model,
            "mode" to SarvamEngine.mode(request, config),
            "sample_rate" to "16000",
            "input_audio_codec" to "pcm_s16le",
            "flush_signal" to "true",
            "vad_signals" to "true",
        )
        if (config.vocabularyHints && "v4" in config.model) {
            val terms = hintTerms(request.vocabulary, max = 50, maxLength = 64)
            if (terms.isNotEmpty()) q.add("keyterms" to JsonArray(terms.map { JsonPrimitive(it) }).toString())
        }

        return wss(config.apiBaseUrl, service.defaultBaseUrl, "speech-to-text/ws", q)
    }

    override suspend fun sendAudio(socket: LiveSocket, pcm: ByteArray) {
        val message = buildJsonObject {
            putJsonObject("audio") {
                put("data", Base64.getEncoder().encodeToString(pcm))
                put("sample_rate", 16000)
                put("encoding", "audio/wav")
            }
        }

        socket.send(message.toString())
    }

    override fun apply(message: String, pieces: MutableList<String>, endSent: Boolean, progress: StreamProgress): Boolean {
        val root = try {
            ApiJson.parse(message) as? JsonObject
        } catch (_: Exception) {
            null
        } ?: return false
        val data = root["data"] as? JsonObject
        when (root.str("type")) {
            "data" -> {
                data.str("transcript")?.let { if (it.isNotBlank()) pieces.add(it) }
                progress.transcribed(endSent)
            }
            "events" -> when (data.str("signal_type")) {
                "START_SPEECH" -> progress.speechStarted()
                "END_SPEECH" -> progress.speechEnded()
            }
            "error" -> {
                val text = "${data.str("code").orEmpty()} ${data.str("error").orEmpty()}"
                throw TranscriptionException(StreamErrors.kindFor(text), "live sarvam: ${Redactor.clean(text, 200)}", service = service)
            }
        }

        return false // Sarvam doesn't confirm the flush; the session stops once no transcript is owed
    }
}

/**
 * Deepgram streaming (wss://api.deepgram.com/v1/listen): binary linear16 frames, {"type":"Finalize"} at the end;
 * final Results carry the text, and the one answering Finalize has from_finalize = true.
 */
object DeepgramStream : StreamProtocol {
    override val service = SpeechService.DEEPGRAM
    override val endMessage = """{"type":"Finalize"}"""
    override val closeMessage = """{"type":"CloseStream"}"""

    override fun uri(settings: PlainspokenSettings): URI {
        val config = settings.transcription.services.deepgram
        val request = TranscriptionRequest.from(ByteArray(0), 0.0, settings)
        val q = DeepgramEngine.query(request, config) + listOf(
            "encoding" to "linear16",
            "sample_rate" to "16000",
            "channels" to "1",
            "interim_results" to "false",
        )
        return wss(config.apiBaseUrl, service.defaultBaseUrl, "v1/listen", q)
    }

    override suspend fun sendAudio(socket: LiveSocket, pcm: ByteArray) = socket.sendBinary(pcm)

    override fun apply(message: String, pieces: MutableList<String>, endSent: Boolean, progress: StreamProgress): Boolean {
        val root = try {
            ApiJson.parse(message) as? JsonObject
        } catch (_: Exception) {
            null
        } ?: return false
        when (root.str("type")) {
            "Results" -> {
                val isFinal = (root["is_final"] as? JsonPrimitive)?.content == "true"
                val alternatives = (root["channel"] as? JsonObject)?.get("alternatives") as? JsonArray
                val text = (alternatives?.firstOrNull() as? JsonObject).str("transcript")
                if (isFinal && !text.isNullOrBlank()) pieces.add(text)
                return endSent && (root["from_finalize"] as? JsonPrimitive)?.content == "true"
            }
            "Error" -> {
                val text = "${root.str("err_code") ?: root.str("variant").orEmpty()} ${root.str("err_msg") ?: root.str("description").orEmpty()}"
                throw TranscriptionException(StreamErrors.kindFor(text), "live deepgram: ${Redactor.clean(text, 200)}", service = service)
            }
        }

        return false
    }
}

/**
 * Picks the streaming service for a dictation: the first usable service, if it can stream and streaming is on for it.
 * Its raw text then goes through the same clean-up as everything else.
 */
class LiveRouter(
    private val transcribers: Map<SpeechService, LiveTranscriber>,
    private val firstService: (PlainspokenSettings) -> SpeechService?,
) : LiveTranscriber {
    override fun canStart(settings: PlainspokenSettings): Boolean = pick(settings)?.canStart(settings) == true

    override fun start(settings: PlainspokenSettings): LiveSession =
        (pick(settings) ?: throw IllegalStateException("no streaming service")).start(settings)

    private fun pick(settings: PlainspokenSettings): LiveTranscriber? {
        val service = firstService(settings) ?: return null
        return if (streamingOn(settings, service)) transcribers[service] else null
    }

    companion object {
        fun streamingOn(settings: PlainspokenSettings, service: SpeechService): Boolean = when (service) {
            SpeechService.GEMINI -> settings.transcription.liveStreaming
            SpeechService.SARVAM, SpeechService.DEEPGRAM -> settings.transcription.services[service].streaming
            else -> false
        }
    }
}
