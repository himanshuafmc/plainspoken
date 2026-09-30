package app.plainspoken.core.transcription

import app.plainspoken.core.logging.Log
import app.plainspoken.core.logging.Redactor
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.TranscriptionMode
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonObject
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.io.IOException
import java.net.URI
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.Duration
import java.time.Instant
import java.util.Base64
import java.util.concurrent.TimeUnit
import kotlin.coroutines.cancellation.CancellationException

/** One dictation streamed to the live model while the user speaks. */
interface LiveSession : AutoCloseable {
    /** Queues 16 kHz mono PCM16 samples. Safe to call from any thread; never blocks or throws. */
    fun push(samples: ShortArray)

    /** Ends the audio and waits for the final transcript. Throws [TranscriptionException] on failure. */
    suspend fun finish(): String

    /** Stops without a transcript (cancel, too short). */
    fun abort()

    override fun close() = abort()
}

interface LiveTranscriber {
    /** False while live streaming is paused after repeated failures; the normal engine is used then. */
    fun canStart(settings: PlainspokenSettings): Boolean = true

    /** Starts connecting immediately; audio pushed before the connection is ready is buffered. */
    fun start(settings: PlainspokenSettings): LiveSession
}

/** Minimal WebSocket abstraction so the protocol can be unit-tested without a network. */
interface LiveSocket {
    suspend fun connect(uri: URI, apiKey: String?)

    suspend fun send(json: String)

    /** Next message as text, or null once the server has closed the connection. */
    suspend fun receive(): String?

    /** Close status and reason sent by the server, if any. */
    val closeReason: String?

    fun close()
}

/** Real socket on OkHttp (keeps a 15 s ping going). */
class OkHttpLiveSocket(http: OkHttpClient) : LiveSocket {
    private val client = http.newBuilder().pingInterval(15, TimeUnit.SECONDS).build()
    private val incoming = Channel<String?>(Channel.UNLIMITED)
    private val opened = CompletableDeferred<Unit>()
    private var ws: WebSocket? = null

    @Volatile
    override var closeReason: String? = null
        private set

    override suspend fun connect(uri: URI, apiKey: String?) {
        val request = Request.Builder().url(uri.toString()).apply {
            if (!apiKey.isNullOrBlank()) header(GeminiClient.API_KEY_HEADER, apiKey.trim())
        }.build()
        ws = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                opened.complete(Unit)
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                incoming.trySend(text)
            }

            // The Live API sends JSON in text or binary frames; both are UTF-8.
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                incoming.trySend(bytes.utf8())
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                closeReason = "$code $reason".trim()
                webSocket.close(1000, null)
                incoming.trySend(null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (closeReason == null) closeReason = "$code $reason".trim()
                incoming.trySend(null)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (!opened.isCompleted) {
                    opened.completeExceptionally(IOException("connect failed: ${response?.code ?: t.javaClass.simpleName}", t))
                } else {
                    if (closeReason == null) closeReason = "failure ${t.javaClass.simpleName}"
                    incoming.trySend(null)
                }
            }
        })

        try {
            opened.await()
        } catch (e: CancellationException) {
            ws?.cancel()
            throw e
        }
    }

    override suspend fun send(json: String) {
        if (ws?.send(json) != true) throw IOException("socket closed")
    }

    override suspend fun receive(): String? = incoming.receive()

    override fun close() {
        ws?.close(1000, "done")
        incoming.trySend(null)
    }
}

/**
 * gemini-3.5-transcribe-live over the Live API (BidiGenerateContent WebSocket). The setup message for this
 * model isn't documented, so several shapes are tried in order and the accepted one is remembered. Every
 * server message shape is logged (never text). After [failuresBeforePause] failed dictations in a row live
 * streaming pauses for [pauseAfterFailures], so a network that blocks WebSockets doesn't slow every dictation.
 */
class GeminiLiveTranscriber(
    private val socketFactory: () -> LiveSocket,
    private val apiKey: () -> String?,
    internal val log: Log,
    internal val scope: CoroutineScope = BackgroundScope,
    /** Connection timeout, setup timeout (shortened in tests). */
    internal val stepTimeout: Duration = Duration.ofSeconds(10),
    internal val quietAfterEnd: Duration = Duration.ofMillis(1500),
    private val failuresBeforePause: Int = 2,
    private val pauseAfterFailures: Duration = Duration.ofMinutes(15),
    private val now: () -> Instant = { Instant.now() },
) : LiveTranscriber {
    private val lock = Any()

    @Volatile
    internal var preferred = 0 // index into candidates() of the last setup the server accepted
    private var failures = 0
    private var pausedUntil: Instant = Instant.EPOCH
    private var pausedFor: String? = null

    internal val key: String? get() = apiKey()

    internal fun newSocket() = socketFactory()

    override fun canStart(settings: PlainspokenSettings): Boolean = synchronized(lock) {
        val paused = pausedFor ?: return true
        if (paused != pauseKey(settings) || !now().isBefore(pausedUntil)) {
            pausedFor = null // pause over, or the key/model/address changed: try live again
            failures = 0
            return true
        }

        false
    }

    /** Records how a finished dictation went. Cancelled dictations are not reported. */
    internal fun report(settings: PlainspokenSettings, success: Boolean) {
        synchronized(lock) {
            if (success) {
                failures = 0
                return
            }

            if (++failures < failuresBeforePause) return
            failures = 0
            pausedFor = pauseKey(settings)
            pausedUntil = now().plus(pauseAfterFailures)
        }

        log.warn("live: $failuresBeforePause failures in a row; paused for ${pauseAfterFailures.toMinutes()} min (using the normal engine)")
    }

    // The key is only hashed in memory, never stored or logged.
    private fun pauseKey(settings: PlainspokenSettings) =
        "${settings.transcription.liveModel}|${settings.transcription.apiBaseUrl}|${apiKey().orEmpty().hashCode()}"

    override fun start(settings: PlainspokenSettings): LiveSession = GeminiLiveSession(this, settings)

    companion object {
        private val API_VERSIONS = listOf("v1beta", "v1alpha")

        internal fun endpoint(apiBaseUrl: String, version: String): URI {
            val base = URI(PlainspokenSettings.normalizeBaseUrl(apiBaseUrl))
            val scheme = if (base.scheme == "http") "ws" else "wss"
            return URI("$scheme://${base.rawAuthority}/ws/google.ai.generativelanguage.$version.GenerativeService.BidiGenerateContent")
        }

        /** (API version, setup name, setup message) to try, most likely first. */
        internal fun candidates(settings: PlainspokenSettings): List<Triple<String, String, JsonObject>> {
            val t = settings.transcription
            val model = "models/" + ModelId.normalize(t.liveModel)
            val request = TranscriptionRequest.from(ByteArray(0), 0.0, settings)
            val transcriptionConfig = buildJsonObject {
                put("mode", if (request.mode == TranscriptionMode.VERBATIM) "verbatim" else "smart")
                if (request.vocabulary.isNotEmpty()) put("customVocabulary", buildJsonArray { request.vocabulary.forEach { add(JsonPrimitive(it)) } })
                if (request.languageCodes.isNotEmpty()) put("languageCodes", buildJsonArray { request.languageCodes.forEach { add(JsonPrimitive(it)) } })
            }

            val shapes = listOf(
                "transcriptionConfig" to buildJsonObject {
                    put("model", model)
                    putJsonObject("generationConfig") { put("transcriptionConfig", transcriptionConfig) }
                },
                "inputAudioTranscription+text" to buildJsonObject {
                    put("model", model)
                    putJsonObject("generationConfig") { put("responseModalities", buildJsonArray { add(JsonPrimitive("TEXT")) }) }
                    putJsonObject("inputAudioTranscription") {}
                },
                "inputAudioTranscription" to buildJsonObject {
                    put("model", model)
                    putJsonObject("inputAudioTranscription") {}
                },
                "model-only" to buildJsonObject { put("model", model) },
            )

            return API_VERSIONS.flatMap { v -> shapes.map { (name, setup) -> Triple(v, name, buildJsonObject { put("setup", setup) }) } }
        }
    }
}

internal class GeminiLiveSession(private val owner: GeminiLiveTranscriber, private val settings: PlainspokenSettings) : LiveSession {
    private val audio = Channel<ShortArray>(Channel.UNLIMITED)

    @Volatile
    private var pushedSamples = 0L

    @Volatile
    private var aborted = false
    private val run: Deferred<String> = owner.scope.async { run() }

    private val log get() = owner.log

    override fun push(samples: ShortArray) {
        if (samples.isNotEmpty() && audio.trySend(samples).isSuccess) pushedSamples += samples.size
    }

    override suspend fun finish(): String {
        audio.close()
        // Streaming should finish quickly; allow a little extra for long dictations.
        val limit = minOf(30.0, 8 + pushedSamples / 16000.0 / 10)
        try {
            val text = withTimeout((limit * 1000).toLong()) { run.await() }
            owner.report(settings, success = text.isNotBlank())
            return text
        } catch (e: TimeoutCancellationException) {
            abort()
            owner.report(settings, success = false)
            throw TranscriptionException(TranscriptionErrorKind.TIMEOUT, "live: no final transcript in time", cause = e)
        } catch (e: CancellationException) {
            if (!aborted && run.isCancelled) owner.report(settings, success = false)
            throw e
        } catch (e: Exception) {
            owner.report(settings, success = false)
            throw e
        }
    }

    override fun abort() {
        aborted = true
        audio.close()
        run.cancel()
    }

    private suspend fun run(): String {
        val candidates = GeminiLiveTranscriber.candidates(settings)
        val order = candidates.indices.sortedBy { if (it == owner.preferred) 0 else 1 }
        val skipVersions = HashSet<String>()
        var last: TranscriptionException? = null
        for (index in order) {
            val (version, name, setup) = candidates[index]
            if (version in skipVersions) continue
            val socket = owner.newSocket()
            try {
                try {
                    withTimeout(owner.stepTimeout.toMillis()) {
                        socket.connect(GeminiLiveTranscriber.endpoint(settings.transcription.apiBaseUrl, version), owner.key)
                    }
                } catch (e: TimeoutCancellationException) {
                    log.warn("live: connect $version timed out")
                    last = TranscriptionException(TranscriptionErrorKind.NETWORK, "live: connect timed out", cause = e)
                    skipVersions.add(version)
                    continue
                } catch (e: IOException) {
                    log.warn("live: connect $version failed: ${Redactor.clean(e.message, 160)}")
                    last = TranscriptionException(TranscriptionErrorKind.NETWORK, "live: connect failed", cause = e)
                    skipVersions.add(version) // endpoint missing or unreachable: other shapes won't help
                    continue
                }

                val reply = withTimeout(owner.stepTimeout.toMillis()) {
                    socket.send(setup.toString())
                    socket.receive()
                }

                if (reply == null || !LiveTranscript.isSetupComplete(reply)) {
                    val reason = Redactor.clean(socket.closeReason ?: if (reply == null) "closed" else JsonShape.describe(reply), 300)
                    log.warn("live: setup '$name' on $version rejected: $reason")
                    val error = LiveTranscript.errorFor(reason)
                    last = error
                    if (error.kind == TranscriptionErrorKind.INVALID_KEY || error.isRateLimit) throw error // no other shape fixes these
                    continue
                }

                if (owner.preferred != index) {
                    owner.preferred = index
                    log.info("live: setup '$name' on $version accepted")
                }

                return stream(socket)
            } finally {
                socket.close()
            }
        }

        throw last ?: TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "live: no setup accepted")
    }

    private suspend fun stream(socket: LiveSocket): String = coroutineScope {
        val transcript = LiveTranscript(log)
        val endSent = CompletableDeferred<Unit>()
        val receive = async {
            while (true) {
                val message: String? = if (endSent.isCompleted) {
                    // After the end of audio, stop once the server finishes or goes quiet.
                    try {
                        withTimeout(owner.quietAfterEnd.toMillis()) { socket.receive() }
                    } catch (_: TimeoutCancellationException) {
                        if (transcript.hasText) {
                            log.info("live: server went quiet after end of audio; using the text received")
                            return@async
                        }

                        continue
                    }
                } else {
                    socket.receive()
                }

                if (message == null) {
                    val reason = socket.closeReason
                    if (!transcript.hasText && !reason.isNullOrEmpty() && !reason.startsWith("1000")) {
                        throw LiveTranscript.errorFor(Redactor.clean(reason, 300))
                    }

                    return@async
                }

                if (transcript.apply(message) && endSent.isCompleted) return@async // turn complete after the end of audio
            }
        }

        val buffer = ShortArrayBuilder()
        try {
            for (samples in audio) {
                buffer.add(samples)
                if (buffer.size >= CHUNK_SAMPLES) sendAudio(socket, buffer)
                if (receive.isCompleted) break // server closed early; the error (if any) surfaces below
            }

            if (buffer.size > 0 && !receive.isCompleted) sendAudio(socket, buffer)
            if (!receive.isCompleted) socket.send("""{"realtimeInput":{"audioStreamEnd":true}}""")
            endSent.complete(Unit)
            receive.await()
        } catch (e: IOException) {
            receive.cancel()
            throw TranscriptionException(TranscriptionErrorKind.NETWORK, "live: send failed", cause = e)
        }

        val text = transcript.finish()
        log.info("live: finished, ${text.length} chars (${transcript.messages} messages)")
        text
    }

    private suspend fun sendAudio(socket: LiveSocket, buffer: ShortArrayBuilder) {
        val bytes = ByteBuffer.allocate(buffer.size * 2).order(ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until buffer.size) bytes.putShort(buffer[i])
        buffer.clear()
        val message = buildJsonObject {
            putJsonObject("realtimeInput") {
                putJsonObject("audio") {
                    put("data", Base64.getEncoder().encodeToString(bytes.array()))
                    put("mimeType", "audio/pcm;rate=16000")
                }
            }
        }

        socket.send(message.toString())
    }

    private companion object {
        const val CHUNK_SAMPLES = 1600 // 100 ms at 16 kHz
    }
}

internal class ShortArrayBuilder {
    private var data = ShortArray(4096)
    var size = 0
        private set

    operator fun get(i: Int) = data[i]

    fun add(samples: ShortArray) {
        if (size + samples.size > data.size) data = data.copyOf(maxOf(data.size * 2, size + samples.size))
        samples.copyInto(data, size)
        size += samples.size
    }

    fun clear() {
        size = 0
    }
}

/**
 * Collects text from Live API server messages, tolerating several shapes:
 * serverContent.modelTurn.parts[].text (preferred: the model's formatted output),
 * serverContent.inputTranscription.text / transcription.text (speech-to-text chunks), joined with care.
 */
internal class LiveTranscript(private val log: Log) {
    private val model = ArrayList<String>()
    private val input = ArrayList<String>()
    private val loggedShapes = HashSet<String>()

    var messages = 0
        private set

    val hasText: Boolean get() = model.isNotEmpty() || input.isNotEmpty()

    /** The joined transcript (model text preferred); logs how the pieces arrived, never the text. */
    fun finish(): String {
        val (pieces, source) = if (model.isNotEmpty()) model to "model" else input to "input transcription"
        val (text, stats) = TranscriptJoiner.joinWithStats(pieces)
        log.info("live: $source text in $stats")
        return text.trim()
    }

    /** Returns true when the message marks the turn/generation as complete. */
    fun apply(message: String): Boolean {
        messages++
        val root = try {
            ApiJson.parse(message)
        } catch (_: Exception) {
            log.warn("live: non-JSON message (${message.length} chars)")
            return false
        }

        val shape = JsonShape.describe(root)
        if (loggedShapes.size < 12 && loggedShapes.add(shape)) log.info("live: message shape $shape")
        if (root !is JsonObject) return false
        if (root.containsKey("error")) throw errorFor(shape)

        var complete = false
        val content = root["serverContent"] as? JsonObject
        if (content != null) {
            addText(content, "inputTranscription", input)
            addText(content, "transcription", input)
            val parts = ((content["modelTurn"] as? JsonObject)?.get("parts") as? kotlinx.serialization.json.JsonArray)
            parts?.forEach { part ->
                if (part is JsonObject && (part["thought"] as? JsonPrimitive)?.content != "true") {
                    part.str("text")?.let { if (it.isNotEmpty()) model.add(it) }
                }
            }

            complete = isTrue(content, "turnComplete") || isTrue(content, "generationComplete")
        }

        // Tolerate a top-level transcription object too.
        addText(root, "inputTranscription", input)
        addText(root, "transcription", input)
        return complete
    }

    private fun addText(parent: JsonObject, name: String, target: MutableList<String>) {
        (parent[name] as? JsonObject)?.str("text")?.let { if (it.isNotEmpty()) target.add(it) }
    }

    private fun isTrue(e: JsonObject, name: String) = (e[name] as? JsonPrimitive)?.content == "true"

    companion object {
        fun isSetupComplete(message: String): Boolean = try {
            (ApiJson.parse(message) as? JsonObject)?.containsKey("setupComplete") == true
        } catch (_: Exception) {
            false
        }

        fun errorFor(reason: String): TranscriptionException {
            val r = reason.uppercase()
            val kind = when {
                "API KEY" in r || "PERMISSION" in r -> TranscriptionErrorKind.INVALID_KEY
                "QUOTA" in r || "RESOURCE_EXHAUSTED" in r || "RATE LIMIT" in r ->
                    GeminiErrors.classify429(reason, emptyList(), GeminiErrors.parseRetryFromMessage(reason))
                "NOT FOUND" in r || "NOT SUPPORTED" in r -> TranscriptionErrorKind.MODEL_NOT_FOUND
                else -> TranscriptionErrorKind.UNEXPECTED_RESPONSE
            }

            return TranscriptionException(kind, "live: $reason")
        }
    }
}
