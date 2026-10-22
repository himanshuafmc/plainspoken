package app.plainspoken.core.offline

import app.plainspoken.core.logging.Log
import app.plainspoken.core.settings.LanguagePreset
import app.plainspoken.core.settings.SpeechServiceSettings
import app.plainspoken.core.transcription.OpenAiEngine
import app.plainspoken.core.transcription.TranscriptionRequest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.security.MessageDigest

/**
 * A Whisper model for offline transcription, in whisper.cpp's format. It is downloaded once from whisper.cpp's page on
 * Hugging Face and checked against [sha256] before it is used.
 */
class OfflineModel(val id: String, val file: String, val label: String, val sizeBytes: Long, val sha256: String) {
    val url: String get() = OfflineModels.BASE_URL + file

    /** Its size and checksum are known, so it can be downloaded and checked. */
    val pinned: Boolean get() = sizeBytes > 0 && sha256.length == 64
}

/** The models Plainspoken offers. Same list and checksums as Windows. */
object OfflineModels {
    const val BASE_URL = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/"

    // Sizes and SHA-256s come from whisper.cpp's Hugging Face listing, printed by CI (job "offline-models").
    // A model without them can't be downloaded.
    val BASE = OfflineModel("base", "ggml-base-q5_1.bin", "Base · about 60 MB · fastest", 0, "")
    val SMALL = OfflineModel("small", "ggml-small-q5_1.bin", "Small · about 190 MB · more accurate", 0, "")
    val MEDIUM = OfflineModel("medium", "ggml-medium-q5_0.bin", "Medium · about 515 MB · accurate", 0, "")
    val LARGE_TURBO = OfflineModel("large-v3-turbo", "ggml-large-v3-turbo-q5_0.bin", "Large v3 Turbo · about 550 MB · most accurate", 0, "")

    /** On phones; the first is the default. */
    val phone = listOf(SMALL, BASE)

    /** On Windows; the first is the default. */
    val pc = listOf(MEDIUM, LARGE_TURBO, SMALL)

    fun find(id: String?, offered: List<OfflineModel>): OfflineModel = offered.firstOrNull { it.id == id } ?: offered.first()
}

/** What Whisper is told, from the dictation's settings. */
object OfflineWhisper {
    /** English presets → "en"; with Hindi, Whisper detects the language (forcing "en" would make it translate). */
    fun language(request: TranscriptionRequest): String = if (request.languages == LanguagePreset.ENGLISH) "en" else "auto"

    /** The same prompt as for OpenAI-compatible Whisper services: the wanted script, then names to spell right. */
    fun prompt(request: TranscriptionRequest): String = OpenAiEngine.prompt(request, SpeechServiceSettings())
}

/** Downloaded models in [dir]: download (resuming a partial one, checked against the SHA-256), delete, find. */
class ModelStore(private val dir: File, private val http: OkHttpClient, private val log: Log) {
    fun file(model: OfflineModel): File = File(dir, model.file)

    private fun partFile(model: OfflineModel): File = File(dir, model.file + ".part")

    fun isDownloaded(model: OfflineModel): Boolean = file(model).let { it.isFile && it.length() == model.sizeBytes }

    /** Space used by models and partial downloads. */
    fun bytesOnDisk(): Long = dir.listFiles()?.sumOf { it.length() } ?: 0L

    fun delete(model: OfflineModel) {
        file(model).delete()
        partFile(model).delete()
    }

    /** Downloads [model]; [progress] gets (bytes so far, total) every megabyte or so. Throws [IOException] on failure. */
    suspend fun download(model: OfflineModel, progress: (Long, Long) -> Unit) = withContext(Dispatchers.IO) {
        if (!model.pinned) throw IOException("model download: ${model.id} has no pinned checksum")
        dir.mkdirs()
        val part = partFile(model)
        val digest = MessageDigest.getInstance("SHA-256")
        var have = if (part.isFile && part.length() <= model.sizeBytes) part.length() else 0L
        if (have > 0) part.inputStream().use { input -> copy(input, null, digest) }
        val request = Request.Builder().url(model.url).apply { if (have > 0) header("Range", "bytes=$have-") }.build()
        http.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("model download: HTTP ${response.code}")
            if (have > 0 && response.code != 206) { // the server sent the whole file
                have = 0
                digest.reset()
            }

            val body = response.body
            FileOutputStream(part, have > 0).use { out ->
                body.byteStream().use { input ->
                    val buffer = ByteArray(64 * 1024)
                    var reported = have
                    while (true) {
                        ensureActive()
                        val n = input.read(buffer)
                        if (n < 0) break
                        out.write(buffer, 0, n)
                        digest.update(buffer, 0, n)
                        have += n
                        if (have - reported >= REPORT_EVERY) {
                            reported = have
                            progress(have, model.sizeBytes)
                        }
                    }
                }
            }
        }

        val actual = digest.digest().joinToString("") { "%02x".format(it) }
        if (have != model.sizeBytes || !actual.equals(model.sha256, ignoreCase = true)) {
            part.delete()
            throw IOException("model download: the file doesn't match (size $have, sha256 ${actual.take(12)}…)")
        }

        if (!part.renameTo(file(model))) throw IOException("model download: could not save the file")
        progress(have, model.sizeBytes)
        log.info("offline model ${model.id}: downloaded")
    }

    private fun copy(input: java.io.InputStream, out: java.io.OutputStream?, digest: MessageDigest) {
        val buffer = ByteArray(64 * 1024)
        while (true) {
            val n = input.read(buffer)
            if (n < 0) return
            out?.write(buffer, 0, n)
            digest.update(buffer, 0, n)
        }
    }

    private companion object {
        const val REPORT_EVERY = 1L shl 20
    }
}
