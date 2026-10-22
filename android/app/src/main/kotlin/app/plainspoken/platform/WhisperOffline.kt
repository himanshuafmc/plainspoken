package app.plainspoken.platform

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.logging.Log
import app.plainspoken.core.offline.ModelStore
import app.plainspoken.core.offline.OfflineModel
import app.plainspoken.core.offline.OfflineModels
import app.plainspoken.core.offline.OfflineWhisper
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.transcription.OfflineTranscriber
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import app.plainspoken.core.transcription.TranscriptionRequest
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.IOException
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.coroutines.cancellation.CancellationException

/** whisper.cpp through JNI: libplainspoken_whisper.so, built from src/main/cpp. */
internal object WhisperNative {
    @Volatile
    private var loaded: Boolean? = null

    /** Loads the library and ggml's CPU backends (the best one this phone's processor supports); false if it can't. */
    fun available(context: Context): Boolean = loaded ?: synchronized(this) {
        loaded?.let { return it }
        val ok = try {
            System.loadLibrary("plainspoken_whisper")
            loadBackends(context.applicationInfo.nativeLibraryDir)
            true
        } catch (_: Throwable) {
            false
        }
        loaded = ok
        ok
    }

    external fun loadBackends(dir: String)

    /** A handle, or 0 if the model couldn't be loaded. */
    external fun open(path: String): Long

    /** UTF-8 text, or null if transcription failed. */
    external fun transcribe(handle: Long, samples: FloatArray, language: String, prompt: String, threads: Int): ByteArray?

    external fun close(handle: Long)
}

/**
 * Offline transcription with whisper.cpp and the model downloaded in Settings → Offline fallback. The model is loaded
 * on first use and kept while it stays the chosen one; one dictation is transcribed at a time.
 */
class WhisperOffline(
    private val context: Context,
    private val store: ModelStore,
    private val settings: () -> PlainspokenSettings,
    private val log: Log,
) : OfflineTranscriber {
    private val lock = Mutex()
    private var handle = 0L
    private var loadedPath: String? = null

    fun model(s: PlainspokenSettings): OfflineModel = OfflineModels.find(s.local.offlineModel, OfflineModels.phone)

    /** The engine works on this phone (its native library loads). */
    fun supported(): Boolean = WhisperNative.available(context)

    override fun isReady(settings: PlainspokenSettings): Boolean =
        settings.local.offlineEnabled && store.isDownloaded(model(settings)) && supported()

    override suspend fun transcribe(request: TranscriptionRequest): String = lock.withLock {
        withContext(Dispatchers.Default) {
            val model = model(settings())
            val path = store.file(model).absolutePath
            val started = System.nanoTime()
            if (handle == 0L || loadedPath != path) {
                if (handle != 0L) WhisperNative.close(handle)
                handle = WhisperNative.open(path)
                loadedPath = if (handle != 0L) path else null
            }

            if (handle == 0L) throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "offline: the model couldn't be loaded")
            val (samples, _) = WavEncoder.decode(request.wav)
            val floats = FloatArray(samples.size) { samples[it] / 32768f }
            val bytes = WhisperNative.transcribe(handle, floats, OfflineWhisper.language(request), OfflineWhisper.prompt(request), threads())
                ?: throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "offline: transcription failed")
            log.info("offline: ${model.id} took ${(System.nanoTime() - started) / 1_000_000} ms for ${"%.1f".format(java.util.Locale.ROOT, request.durationSeconds)}s")
            OfflineWhisper.tidy(String(bytes, Charsets.UTF_8))
        }
    }

    /** Phones are fastest with the big cores only: up to 4 threads. */
    private fun threads(): Int = (Runtime.getRuntime().availableProcessors() - 2).coerceIn(2, 4)

    companion object {
        /** True when Android says there is a network that reaches the internet. */
        fun online(context: Context): Boolean {
            val connectivity = context.getSystemService(ConnectivityManager::class.java) ?: return true
            val capabilities = connectivity.getNetworkCapabilities(connectivity.activeNetwork) ?: return false
            return capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        }
    }
}

/** The offline model's download, kept outside Settings so it carries on when the screen is closed. */
class ModelDownload(private val store: ModelStore, private val log: Log) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private var job: Job? = null

    /** (bytes so far, total) while downloading. */
    @Volatile
    var progress: Pair<Long, Long>? = null
        private set

    /** Why the last download stopped, if it didn't finish. */
    @Volatile
    var problem: String? = null
        private set

    val running: Boolean get() = job?.isActive == true

    fun start(model: OfflineModel) {
        if (running) return
        problem = null
        progress = 0L to model.sizeBytes
        job = scope.launch {
            try {
                store.download(model) { have, total ->
                    progress = have to total
                    notifyListeners()
                }
            } catch (_: CancellationException) {
                problem = "Download stopped. Tap Download to carry on where it stopped."
            } catch (e: IOException) {
                log.warn("offline model ${model.id}: download failed (${e.javaClass.simpleName})")
                problem = "Couldn't download the model. Check the internet connection and try again."
            } finally {
                progress = null
                notifyListeners()
            }
        }

        notifyListeners()
    }

    fun cancel() {
        job?.cancel()
    }

    fun addListener(listener: () -> Unit) {
        listeners.add(listener)
    }

    fun removeListener(listener: () -> Unit) {
        listeners.remove(listener)
    }

    private fun notifyListeners() = listeners.forEach { it() }
}
