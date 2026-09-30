package app.plainspoken

import android.content.Context
import app.plainspoken.core.logging.FileLog
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SeedVocabulary
import app.plainspoken.core.settings.SettingsJson
import app.plainspoken.core.settings.SettingsStore
import app.plainspoken.core.storage.HistoryStore
import app.plainspoken.core.storage.PendingStore
import app.plainspoken.core.transcription.GeminiLiveTranscriber
import app.plainspoken.core.transcription.OkHttpLiveSocket
import app.plainspoken.core.transcription.TranscriptionService
import app.plainspoken.platform.KeyVault
import okhttp3.ConnectionPool
import okhttp3.OkHttpClient
import java.io.File
import java.io.IOException
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

/**
 * Everything the keyboard and the app screens share, created once per process: settings (in memory, saved to
 * settings.json in the shared format), the encrypted API key, the HTTP client, history, saved recordings,
 * the speech engines and the log.
 */
class Services(context: Context) {
    private val files: File = context.filesDir

    val log = FileLog(File(files, "logs"))
    private val store = SettingsStore(File(files, "settings.json"), log)

    @Volatile
    private var current: PlainspokenSettings = store.load { defaults() }
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private var cachedBlob: String? = null
    private var cachedKey: String? = null

    /** Connections stay open for 5 minutes between dictations, so the next request skips the TLS handshake. */
    val http: OkHttpClient = OkHttpClient.Builder()
        .connectionPool(ConnectionPool(2, 5, TimeUnit.MINUTES))
        .build()

    val history = HistoryStore(File(files, "history.json"), log)
    val pending = PendingStore(File(files, "pending"), log)

    val transcriber = TranscriptionService(
        TranscriptionService.geminiEngines(http, ::apiKey, log),
        log,
        warmUpCall = TranscriptionService.geminiWarmUp(http, ::apiKey, log),
    )

    val live = GeminiLiveTranscriber({ OkHttpLiveSocket(http) }, ::apiKey, log)

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

    @Synchronized
    fun apiKey(): String? {
        val blob = current.local.apiKeyProtected ?: return null
        if (blob != cachedBlob) {
            cachedKey = KeyVault.unprotect(blob)
            cachedBlob = blob
            if (cachedKey == null) log.warn("stored API key could not be decrypted; it needs to be entered again")
        }

        return cachedKey
    }

    fun hasApiKey(): Boolean = !apiKey().isNullOrBlank()

    fun setApiKey(key: String?) {
        val trimmed = key?.trim().orEmpty()
        update { it.local.apiKeyProtected = if (trimmed.isEmpty()) null else KeyVault.protect(trimmed) }
    }

    private fun defaults() = PlainspokenSettings().apply { transcription.customVocabulary = SeedVocabulary.terms }
}
