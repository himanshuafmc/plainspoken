package app.plainspoken.core.transcription

import app.plainspoken.core.audio.AudioCompressor
import app.plainspoken.core.audio.EncodedAudio
import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.logging.Log
import app.plainspoken.core.settings.CleanupProvider
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.Snippet
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.settings.TranscriptionMode
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.supervisorScope
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import java.time.Duration
import java.time.Instant
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import kotlin.coroutines.cancellation.CancellationException

/** The finished text, which service heard it, and whether the clean-up step tidied it (null = not cleaned). */
/** [offline]: the text came from the model on this device ([OfflineTranscriber]). */
data class TranscriptionOutcome(val text: String, val service: SpeechService, val cleanedBy: String? = null, val offline: Boolean = false)

/**
 * Transcription on this device, the last resort: used when there is no internet, no usable service or every service
 * failed, and alongside the services when none has answered within [TranscriptionService.offlineAfter].
 */
interface OfflineTranscriber {
    /** True when offline fallback is on and its model is on this device. */
    fun isReady(settings: PlainspokenSettings): Boolean

    suspend fun transcribe(request: TranscriptionRequest): String
}

/** Turns a recording into finished text: the speech service(s), then clean-up and snippets. Used by the dictation controller. */
interface Transcriber {
    suspend fun transcribe(request: TranscriptionRequest, settings: PlainspokenSettings, status: ((String) -> Unit)?): TranscriptionOutcome

    /** For text that already came from a live stream: clean-up and snippets only. */
    suspend fun finish(
        raw: String,
        service: SpeechService,
        request: TranscriptionRequest,
        settings: PlainspokenSettings,
        status: ((String) -> Unit)?,
        listenMillis: Long,
    ): TranscriptionOutcome

    /**
     * Opens the HTTPS connection in the background (called when recording starts) so the real request doesn't pay
     * for DNS/TCP/TLS setup. Never throws; does nothing if called again within 20 s.
     */
    fun warmUp(settings: PlainspokenSettings)
}

/** One dictation, for the stats page. Never holds any text. */
class DictationStat(
    val utc: Instant,
    val service: SpeechService,
    val audioSeconds: Double,
    val listenMillis: Long,
    val cleanupMillis: Long,
    val ok: Boolean,
    val errorKind: String? = null,
    val streamed: Boolean = false,
    /** A backup service produced the text. */
    val usedBackup: Boolean = false,
    /** A second service was asked because the first was slow. */
    val hedged: Boolean = false,
    val cleanedBy: String? = null,
)

fun interface StatsRecorder {
    fun record(stat: DictationStat)
}

/**
 * Remembers which services are failing. After [failuresBeforePause] failures in a row (at once for "out of credits"
 * and "daily limit"), a service is skipped for [pause] while another one is available.
 */
class ServiceHealth(
    private val failuresBeforePause: Int = 2,
    private val pause: Duration = Duration.ofMinutes(15),
    private val now: () -> Instant = { Instant.now() },
) {
    private val failures = ConcurrentHashMap<SpeechService, Int>()
    private val pausedUntil = ConcurrentHashMap<SpeechService, Instant>()

    fun isAvailable(service: SpeechService): Boolean = pausedUntil[service]?.let { !now().isBefore(it) } ?: true

    fun success(service: SpeechService) {
        failures.remove(service)
        pausedUntil.remove(service)
    }

    /** Returns true if this failure paused the service. */
    fun failure(service: SpeechService, kind: TranscriptionErrorKind): Boolean {
        val immediate = kind == TranscriptionErrorKind.NO_CREDITS || kind == TranscriptionErrorKind.DAILY_QUOTA
        val count = failures.merge(service, 1, Int::plus) ?: 1
        if (!immediate && count < failuresBeforePause) return false
        failures.remove(service)
        pausedUntil[service] = now().plus(pause)
        return true
    }

    /** Forget everything about [service] (for example after its key was changed). */
    fun reset(service: SpeechService) = success(service)
}

/**
 * The router. Uses the services in the user's order (main first) that have a key, skipping ones [health] has paused.
 * If a service fails, the next is tried at once; if it is slow (see [hedgeDelay]), the next one is asked as well and
 * the first answer wins. Then the clean-up step tidies raw text (Smart mode), with a local filler filter as the
 * fallback, and snippets are expanded. Network work runs on [workDispatcher].
 */
class TranscriptionService(
    private val engineFactory: (service: SpeechService, settings: PlainspokenSettings, serverRetries: Int) -> TranscriptionEngine,
    private val hasKey: (SpeechService) -> Boolean,
    private val log: Log,
    private val cleaners: CleanerFactory? = null,
    private val stats: StatsRecorder? = null,
    private val compressor: AudioCompressor? = null,
    private val warmUpCall: (suspend (SpeechService, PlainspokenSettings) -> Unit)? = null,
    private val workDispatcher: CoroutineDispatcher = Dispatchers.IO,
    private val background: CoroutineScope = BackgroundScope,
    private val now: () -> Instant = { Instant.now() },
    val health: ServiceHealth = ServiceHealth(),
    private val offlineTranscriber: OfflineTranscriber? = null,
    private val isOnline: () -> Boolean = { true },
    /** How long the services get before offline transcription starts as well. */
    val offlineAfter: Duration = Duration.ofSeconds(6),
) : Transcriber {
    private val lastWarmUp = AtomicLong(0)

    override fun warmUp(settings: PlainspokenSettings) {
        val call = warmUpCall ?: return
        val service = usableServices(settings).firstOrNull() ?: return
        val nowNanos = System.nanoTime()
        val last = lastWarmUp.get()
        if ((last != 0L && nowNanos - last < WARM_UP_INTERVAL_NANOS) || !lastWarmUp.compareAndSet(last, nowNanos)) return
        background.launch {
            try {
                call(service, settings)
            } catch (e: Exception) {
                log.info("warm-up skipped: ${e.javaClass.simpleName}")
            }
        }
    }

    /** Services in the user's order that have a key, without paused ones (unless all are paused). */
    fun usableServices(settings: PlainspokenSettings): List<SpeechService> {
        val withKeys = settings.transcription.orderedServices().filter(hasKey)
        return withKeys.filter(health::isAvailable).ifEmpty { withKeys }
    }

    override suspend fun transcribe(request: TranscriptionRequest, settings: PlainspokenSettings, status: ((String) -> Unit)?): TranscriptionOutcome {
        val order = usableServices(settings)
        val first = settings.transcription.orderedServices().first()
        val offline = offlineTranscriber?.takeIf { it.isReady(settings) }
        if (offline != null && (order.isEmpty() || !isOnline())) {
            log.info(if (order.isEmpty()) "transcribe: no usable service; offline" else "transcribe: no internet; offline")
            val started = System.nanoTime()
            val text = offline.transcribe(request)
            return finishText(text, order.firstOrNull() ?: first, request, settings, status, elapsed(started), streamed = false, usedBackup = false, hedged = false, offline = true)
        }

        if (order.isEmpty()) throw TranscriptionException(TranscriptionErrorKind.NO_KEY, "No key for any chosen service.", service = first)
        log.info(
            "transcribe: services=${order.joinToString(",") { it.id }} wav=${request.wav.size} bytes " +
                "duration=${"%.1f".format(Locale.ROOT, request.durationSeconds)}s mode=${request.mode} languages=${request.languages} vocabulary=${request.vocabulary.size}",
        )

        val prepared = compressIfUseful(request, settings, order)
        val started = System.nanoTime()
        val heard = try {
            if (offline == null) listen(prepared, settings, order, status) else listenOrOffline(prepared, request, settings, order, offline, status)
        } catch (e: TranscriptionException) {
            stats?.record(DictationStat(now(), e.service ?: order.first(), request.durationSeconds, elapsed(started), 0, ok = false, errorKind = e.kind.name))
            throw e
        }

        val listenMs = elapsed(started)
        return finishText(
            heard.text, heard.service, request, settings, status, listenMs,
            streamed = false, usedBackup = heard.service != order.first(), hedged = heard.hedged, offline = heard.offline,
        )
    }

    override suspend fun finish(
        raw: String,
        service: SpeechService,
        request: TranscriptionRequest,
        settings: PlainspokenSettings,
        status: ((String) -> Unit)?,
        listenMillis: Long,
    ): TranscriptionOutcome = finishText(raw, service, request, settings, status, listenMillis, streamed = true, usedBackup = false, hedged = false)

    private class Heard(val text: String, val service: SpeechService, val hedged: Boolean, val offline: Boolean = false)

    /**
     * The services' text; offline transcription starts as soon as they have all failed, or alongside them once
     * [offlineAfter] has passed, and the first text wins. If offline fails too, the services' error stands. Offline runs
     * outside this scope, so a service that wins doesn't wait for the model to finish.
     */
    private suspend fun listenOrOffline(
        prepared: TranscriptionRequest,
        original: TranscriptionRequest,
        settings: PlainspokenSettings,
        order: List<SpeechService>,
        offline: OfflineTranscriber,
        status: ((String) -> Unit)?,
    ): Heard = supervisorScope {
        val online = async { listen(prepared, settings, order, status) }
        withTimeoutOrNull(offlineAfter.toMillis()) { runCatching { online.await() } }?.getOrNull()?.let { return@supervisorScope it }
        ensureActive()
        log.info(if (online.isCompleted) "transcribe: every service failed; offline" else "transcribe: no answer yet; offline as well")
        status?.invoke("Transcribing on this device…")
        val local = background.async(workDispatcher) { Heard(offline.transcribe(original), order.first(), hedged = false, offline = true) }
        val results = Channel<Pair<Boolean, Result<Heard>>>(2) // (from offline, result)
        val racers = buildList {
            add(launch { results.send(true to runCatching { local.await() }) })
            if (!online.isCompleted) add(launch { results.send(false to runCatching { online.await() }) })
        }

        repeat(racers.size) {
            val (fromOffline, result) = results.receive()
            result.getOrNull()?.let { heard ->
                local.cancel()
                online.cancel()
                racers.forEach { it.cancel() }
                return@supervisorScope heard
            }

            if (fromOffline) log.warn("offline: failed (${result.exceptionOrNull()?.javaClass?.simpleName})")
        }

        ensureActive()
        online.await() // throws the services' error
        throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "offline: no text", service = order.first())
    }

    private sealed class Attempt(val service: SpeechService) {
        class Done(service: SpeechService, val text: String) : Attempt(service)

        class Failed(service: SpeechService, val error: TranscriptionException) : Attempt(service)
    }

    /** Runs the services in order with fallback and hedging; returns the first transcript. */
    private suspend fun listen(request: TranscriptionRequest, settings: PlainspokenSettings, order: List<SpeechService>, status: ((String) -> Unit)?): Heard =
        coroutineScope {
            val results = Channel<Attempt>(Channel.UNLIMITED)
            val jobs = ArrayList<Job>()
            val errors = ArrayList<TranscriptionException>()
            val hedge = hedgeDelay(settings, request.durationSeconds)
            val serverRetries = if (order.size > 1) 0 else 2 // with a backup, a busy service is skipped rather than retried
            var next = 0
            var running = 0
            var hedged = false

            fun launchNext() {
                val service = order[next++]
                running++
                jobs.add(launch(workDispatcher) {
                    val started = System.nanoTime()
                    val attempt = try {
                        val text = engineFactory(service, settings, serverRetries).transcribe(request)
                        log.info("transcribe: ${service.id} ok in ${elapsed(started)} ms, ${text.length} chars")
                        Attempt.Done(service, text)
                    } catch (e: TranscriptionException) {
                        log.warn("transcribe: ${service.id} failed after ${elapsed(started)} ms: ${e.kind}")
                        Attempt.Failed(service, e.withService(service))
                    }

                    results.send(attempt)
                })
            }

            launchNext()
            var heard: Heard? = null
            while (heard == null) {
                val result = if (next < order.size && hedge != null) withTimeoutOrNull(hedge.toMillis()) { results.receive() } else results.receive()
                if (result == null) {
                    log.info("transcribe: ${order[next - 1].id} is slow; also asking ${order[next].id}")
                    status?.invoke("Still working — also asking ${order[next].shortName}…")
                    hedged = true
                    launchNext()
                    continue
                }

                running--
                when (result) {
                    is Attempt.Done -> {
                        health.success(result.service)
                        jobs.forEach { it.cancel() }
                        heard = Heard(result.text, result.service, hedged)
                    }
                    is Attempt.Failed -> {
                        errors.add(result.error)
                        if (health.failure(result.service, result.error.kind)) {
                            log.warn("transcribe: ${result.service.id} paused for 15 min after repeated failures")
                        }

                        if (next < order.size) {
                            if (running == 0) status?.invoke("Trying ${order[next].shortName}…")
                            if (running == 0 || hedge == null) launchNext()
                        } else if (running == 0) {
                            throw errors.first()
                        }
                    }
                }
            }

            heard
        }

    private suspend fun finishText(
        raw: String,
        service: SpeechService,
        request: TranscriptionRequest,
        settings: PlainspokenSettings,
        status: ((String) -> Unit)?,
        listenMillis: Long,
        streamed: Boolean,
        usedBackup: Boolean,
        hedged: Boolean,
        offline: Boolean = false,
    ): TranscriptionOutcome {
        val started = System.nanoTime()
        var text = raw.trim()
        var cleanedBy: String? = null
        // Offline text stays on the device: no clean-up service, only the local filler filter.
        if (!offline && shouldClean(settings, service, text)) {
            val cleaner = cleaners?.create(settings)
            if (cleaner != null) {
                status?.invoke("Tidying up…")
                try {
                    val out = withContext(workDispatcher) {
                        cleaner.clean(text, CleanupRequest(request.vocabulary, request.hindiScript, settings.snippets.map(Snippet::trigger)))
                    }

                    val accepted = CleanupGuard.accept(text, out)
                    if (accepted != null) {
                        text = accepted
                        cleanedBy = cleaner.name
                    } else {
                        log.warn("cleanup: result refused by the safety check; using the service's text")
                    }
                } catch (e: CancellationException) {
                    throw e
                } catch (e: TranscriptionException) {
                    log.warn("cleanup: ${cleaner.name} failed (${e.kind}); using the service's text")
                }
            }
        }

        if (cleanedBy == null && request.mode == TranscriptionMode.SMART) text = LocalFillerRemover.clean(text)
        text = SnippetExpander.expand(text, settings.snippets)
        val cleanupMs = elapsed(started)
        if (cleanedBy != null) log.info("cleanup: $cleanedBy in $cleanupMs ms")
        if (!offline) {
            stats?.record(DictationStat(now(), service, request.durationSeconds, listenMillis, cleanupMs, ok = true, streamed = streamed, usedBackup = usedBackup, hedged = hedged, cleanedBy = cleanedBy))
        }

        return TranscriptionOutcome(text, service, cleanedBy, offline)
    }

    private fun shouldClean(settings: PlainspokenSettings, service: SpeechService, raw: String): Boolean {
        val c = settings.transcription.cleanup
        return settings.transcription.mode == TranscriptionMode.SMART && c.provider != CleanupProvider.OFF &&
            (!service.cleansItself || c.alsoGemini) && CleanupGuard.words(raw) >= MIN_WORDS_TO_CLEAN
    }

    /** Compresses once, before any service is asked, if one of them accepts it. Failures just mean WAV. */
    private suspend fun compressIfUseful(request: TranscriptionRequest, settings: PlainspokenSettings, order: List<SpeechService>): TranscriptionRequest {
        val c = compressor ?: return request
        if (!settings.transcription.compressAudio || order.none { it.acceptsCompressedAudio }) return request
        val started = System.nanoTime()
        val encoded: EncodedAudio? = try {
            withContext(workDispatcher) {
                val (samples, rate) = WavEncoder.decode(request.wav)
                c.compress(samples, rate)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            log.warn("compress: failed (${e.javaClass.simpleName}); sending WAV")
            null
        }

        if (encoded == null || encoded.bytes.size >= request.wav.size) return request
        log.info("compress: ${request.wav.size} → ${encoded.bytes.size} bytes in ${elapsed(started)} ms")
        return request.withCompressed(encoded)
    }

    companion object {
        /** Very short dictations ("yes", "on my way") don't need the clean-up round trip. */
        const val MIN_WORDS_TO_CLEAN = 3
        private val WARM_UP_INTERVAL_NANOS = Duration.ofSeconds(20).toNanos()

        private fun elapsed(startNanos: Long) = (System.nanoTime() - startNanos) / 1_000_000

        /** When to also ask the next service: the user's seconds plus a bit for long recordings, at most 20 s. Null = never. */
        fun hedgeDelay(settings: PlainspokenSettings, durationSeconds: Double): Duration? {
            val base = settings.transcription.hedgeSeconds
            if (base <= 0) return null
            return Duration.ofMillis(((base + durationSeconds * 0.15).coerceAtMost(20.0) * 1000).toLong())
        }

        /** Factory for the real engines. */
        fun engines(http: OkHttpClient, keyFor: (SpeechService) -> String?, log: Log, requireGeminiKey: Boolean = true) =
            { service: SpeechService, settings: PlainspokenSettings, serverRetries: Int ->
                val t = settings.transcription
                val timeout = Duration.ofSeconds(t.requestTimeoutSeconds.toLong())
                val retry = if (serverRetries == RetryPolicy.DEFAULT.maxServerRetries) RetryPolicy.DEFAULT else RetryPolicy(maxServerRetries = serverRetries)
                fun client() = ServiceClient(http, service, t.baseUrlOf(service), { keyFor(service) }, log, retry)
                val config = t.services[service]
                when (service) {
                    SpeechService.GEMINI ->
                        GeminiTranscribeEngine(GeminiClient(http, t.apiBaseUrl, { keyFor(service) }, log, retry, requireGeminiKey), t.transcribeModel, timeout)
                    SpeechService.GEMINI_GENERAL ->
                        GeminiGenerateEngine(GeminiClient(http, t.apiBaseUrl, { keyFor(service) }, log, retry, requireGeminiKey), t.generateModel, timeout)
                    SpeechService.SARVAM -> SarvamEngine(client(), config, timeout)
                    SpeechService.OPENAI -> OpenAiEngine(client(), config, timeout)
                    SpeechService.DEEPGRAM -> DeepgramEngine(client(), config, timeout)
                    SpeechService.ELEVENLABS -> ElevenLabsEngine(client(), config, timeout)
                }
            }

        /**
         * Warm-up: Gemini gets a tiny GET of the model (costs no quota); the others an unauthenticated GET of the
         * API host, which only opens the connection for the real request.
         */
        fun warmUp(http: OkHttpClient, keyFor: (SpeechService) -> String?, log: Log): suspend (SpeechService, PlainspokenSettings) -> Unit = { service, settings ->
            val t = settings.transcription
            if (service.isGemini) {
                val client = GeminiClient(http, t.apiBaseUrl, { keyFor(service) }, log, RetryPolicy(maxServerRetries = 0))
                client.sendJsonOnce("GET", "v1beta/models/" + ModelId.segment(t.modelOf(service)), null, Duration.ofSeconds(10), "warm-up", true)
            } else {
                val base = PlainspokenSettings.normalizeBaseUrl(t.baseUrlOf(service), service.defaultBaseUrl)
                val call = http.newCall(Request.Builder().url("$base/").get().build())
                call.timeout().timeout(10, java.util.concurrent.TimeUnit.SECONDS)
                call.awaitRaw(log, "warm-up")
            }
        }
    }
}

/** Spoken shortcuts: a dictation that is only a trigger becomes its text; a trigger inside a sentence is replaced. */
object SnippetExpander {
    private val punctuation = Regex("[\\p{P}\\p{S}]")
    private val spaces = Regex("\\s+")

    fun expand(text: String, snippets: List<Snippet>): String {
        if (snippets.isEmpty() || text.isBlank()) return text
        val whole = normalize(text)
        snippets.firstOrNull { normalize(it.trigger) == whole }?.let { return it.text }
        var out = text
        for (s in snippets.sortedByDescending { it.trigger.length }) {
            val words = normalize(s.trigger).split(' ').filter { it.isNotEmpty() }
            if (words.isEmpty()) continue
            val pattern = Regex(
                "(?<![\\p{L}\\p{M}\\p{N}])" + words.joinToString("[\\s,]+") { Regex.escape(it) } + "(?![\\p{L}\\p{M}\\p{N}])",
                RegexOption.IGNORE_CASE,
            )
            out = pattern.replace(out) { s.text }
        }

        return out
    }

    private fun normalize(s: String) = spaces.replace(punctuation.replace(s.lowercase(Locale.ROOT), " "), " ").trim()
}
