package app.plainspoken

import android.content.Context
import app.plainspoken.core.logging.FileLog
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SeedVocabulary
import app.plainspoken.core.settings.SettingsJson
import app.plainspoken.core.settings.SettingsStore
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.storage.HistoryStore
import app.plainspoken.core.storage.PendingStore
import app.plainspoken.core.storage.StatsStore
import app.plainspoken.core.transcription.CleanerFactory
import app.plainspoken.core.transcription.DeepgramStream
import app.plainspoken.core.transcription.GeminiLiveTranscriber
import app.plainspoken.core.transcription.LiveRouter
import app.plainspoken.core.transcription.OkHttpLiveSocket
import app.plainspoken.core.transcription.SarvamStream
import app.plainspoken.core.transcription.StreamingTranscriber
import app.plainspoken.core.transcription.TranscriptionService
import app.plainspoken.platform.AacCompressor
import app.plainspoken.platform.KeyVault
import okhttp3.ConnectionPool
import okhttp3.OkHttpClient
import java.io.File
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

/**
 * Everything the keyboard and the app screens share, created once per process: settings (in memory, saved to
 * settings.json in the shared format), the encrypted keys, the HTTP client, history, saved recordings, stats,
 * the speech services and the log.
 */
class Services(context: Context) {
    private val files: File = context.filesDir

    val log = FileLog(File(files, "logs"))
    private val store = SettingsStore(File(files, "settings.json"), log)

    @Volatile
    private var current: PlainspokenSettings = store.load { defaults() }
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private val keyCache = ConcurrentHashMap<String, Pair<String, String?>>() // slot → (encrypted blob, plain key)

    /** Connections stay open for 5 minutes between dictations, so the next request skips the TLS handshake. */
    val http: OkHttpClient = OkHttpClient.Builder()
        .connectionPool(ConnectionPool(4, 5, TimeUnit.MINUTES))
        .build()

    val history = HistoryStore(File(files, "history.json"), log)
    val pending = PendingStore(File(files, "pending"), log)
    val stats = StatsStore(File(files, "stats.json"), log)

    val transcriber = TranscriptionService(
        engineFactory = TranscriptionService.engines(http, ::keyFor, log),
        hasKey = { !keyFor(it).isNullOrBlank() },
        log = log,
        cleaners = CleanerFactory.standard(http, { key(SpeechService.GEMINI_KEY) }, { key(SpeechService.CLEANUP_KEY) }, log),
        stats = stats,
        compressor = AacCompressor(context.cacheDir, log),
        warmUpCall = TranscriptionService.warmUp(http, ::keyFor, log),
    )

    /** Streaming for the main service when it can (Gemini, Sarvam or Deepgram). */
    val live = LiveRouter(
        mapOf(
            SpeechService.GEMINI to GeminiLiveTranscriber({ OkHttpLiveSocket(http) }, { key(SpeechService.GEMINI_KEY) }, log),
            SpeechService.SARVAM to StreamingTranscriber(SarvamStream, { OkHttpLiveSocket(http) }, ::keyFor, log),
            SpeechService.DEEPGRAM to StreamingTranscriber(DeepgramStream, { OkHttpLiveSocket(http) }, ::keyFor, log),
        ),
    ) { settings -> transcriber.usableServices(settings).firstOrNull() }

    val logDirectory: File get() = log.directory

    fun settings(): PlainspokenSettings = current

    /** Changes a copy of the settings, saves it and tells listeners (e.g. the keyboard). */
    fun update(change: (PlainspokenSettings) -> Unit) {
        val copy = SettingsJson.clone(current)
        change(copy)
        replace(copy)
    }

    fun replace(settings: PlainspokenSettings) {
        settings.normalize()
        current = settings
        try {
            store.save(settings)
        } catch (e: IOException) {
            log.error("could not save settings", e)
        }

        listeners.forEach { it() }
    }

    fun addListener(listener: () -> Unit) {
        listeners.add(listener)
    }

    fun removeListener(listener: () -> Unit) {
        listeners.remove(listener)
    }

    fun keyFor(service: SpeechService): String? = key(service.keySlot)

    /** The decrypted key of a slot ("gemini", "sarvam", … or the clean-up key), or null. */
    fun key(slot: String): String? {
        val blob = blobOf(slot) ?: return null
        val cached = keyCache[slot]
        if (cached != null && cached.first == blob) return cached.second
        val plain = KeyVault.unprotect(blob)
        if (plain == null) log.warn("stored $slot key could not be decrypted; it needs to be entered again")
        keyCache[slot] = blob to plain
        return plain
    }

    fun hasKey(slot: String): Boolean = !key(slot).isNullOrBlank()

    /** True when at least one service in the user's order has a key, so dictation can start. */
    fun hasApiKey(): Boolean = current.transcription.orderedServices().any { !keyFor(it).isNullOrBlank() }

    fun setKey(slot: String, key: String?) {
        val trimmed = key?.trim().orEmpty()
        val blob = if (trimmed.isEmpty()) null else KeyVault.protect(trimmed)
        update {
            if (slot == SpeechService.GEMINI_KEY) {
                it.local.apiKeyProtected = blob
            } else {
                it.local.apiKeysProtected = if (blob == null) it.local.apiKeysProtected - slot else it.local.apiKeysProtected + (slot to blob)
            }
        }

        SpeechService.entries.filter { it.keySlot == slot }.forEach(transcriber.health::reset)
    }

    private fun blobOf(slot: String): String? =
        if (slot == SpeechService.GEMINI_KEY) current.local.apiKeyProtected else current.local.apiKeysProtected[slot]

    private fun defaults() = PlainspokenSettings().apply { transcription.customVocabulary = SeedVocabulary.terms }
}
