package app.plainspoken.core.storage

import app.plainspoken.core.logging.Log
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json
import java.io.File
import java.io.IOException
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.math.roundToLong

object AtomicFile {
    /** Writes to a temp file then renames over the target, so a crash never leaves half a file. */
    fun writeText(file: File, text: String) = write(file) { it.writeText(text) }

    fun writeBytes(file: File, bytes: ByteArray) = write(file) { it.writeBytes(bytes) }

    private fun write(file: File, writeTemp: (File) -> Unit) {
        file.absoluteFile.parentFile?.mkdirs()
        val tmp = File(file.path + ".tmp")
        writeTemp(tmp)
        if (!tmp.renameTo(file)) {
            // Some file systems refuse to rename over an existing file.
            file.delete()
            if (!tmp.renameTo(file)) throw IOException("Could not replace ${file.name}")
        }
    }
}

private val storageJson = Json {
    ignoreUnknownKeys = true
    coerceInputValues = true
    prettyPrint = true
}

internal fun round1(value: Double): Double = (value * 10).roundToLong() / 10.0

@Serializable
data class HistoryEntry(val id: String, val createdUtc: String, val text: String, val durationSeconds: Double) {
    val created: Instant get() = runCatching { Instant.parse(createdUtc) }.getOrDefault(Instant.EPOCH)
}

/** The last N transcripts, newest first, persisted as JSON. Thread-safe. */
class HistoryStore(private val file: File, private val log: Log, private val capacity: Int = DEFAULT_CAPACITY) {
    private val lock = Any()
    private var entries: MutableList<HistoryEntry> = load()
    private val listeners = CopyOnWriteArrayList<() -> Unit>()

    fun addListener(listener: () -> Unit) {
        listeners.add(listener)
    }

    fun removeListener(listener: () -> Unit) {
        listeners.remove(listener)
    }

    val all: List<HistoryEntry> get() = synchronized(lock) { entries.toList() }

    fun add(text: String, durationSeconds: Double, now: Instant): HistoryEntry {
        val entry = HistoryEntry(UUID.randomUUID().toString(), now.toString(), text, round1(durationSeconds))
        synchronized(lock) {
            entries.add(0, entry)
            while (entries.size > capacity) entries.removeAt(entries.size - 1)
            persist()
        }

        listeners.forEach { it() }
        return entry
    }

    fun clear() {
        synchronized(lock) {
            entries.clear()
            if (file.exists() && !file.delete()) log.warn("Could not delete history file")
        }

        listeners.forEach { it() }
    }

    private fun persist() {
        try {
            AtomicFile.writeText(file, storageJson.encodeToString(ListSerializer(HistoryEntry.serializer()), entries))
        } catch (e: IOException) {
            log.warn("Could not save history: ${e.javaClass.simpleName}")
        }
    }

    private fun load(): MutableList<HistoryEntry> = try {
        if (!file.exists()) {
            mutableListOf()
        } else {
            storageJson.decodeFromString(ListSerializer(HistoryEntry.serializer()), file.readText())
                .filter { it.text.isNotEmpty() }
                .sortedByDescending { it.created }
                .take(capacity)
                .toMutableList()
        }
    } catch (e: Exception) {
        if (e !is IOException && e !is SerializationException && e !is IllegalArgumentException) throw e
        log.warn("History file unreadable (${e.javaClass.simpleName}); starting empty.")
        mutableListOf()
    }

    companion object {
        const val DEFAULT_CAPACITY = 20
    }
}

data class PendingRecording(
    val id: String,
    val audioFile: File,
    val created: Instant,
    val durationSeconds: Double,
    val lastError: String?,
    val attempts: Int,
)

/**
 * Recordings waiting for a transcript. Each is saved (WAV + small JSON) before any network call
 * and deleted only once a transcript has been obtained, so a failure never loses speech.
 */
class PendingStore(val directory: File, private val log: Log) {
    private val listeners = CopyOnWriteArrayList<() -> Unit>()

    fun addListener(listener: () -> Unit) {
        listeners.add(listener)
    }

    fun removeListener(listener: () -> Unit) {
        listeners.remove(listener)
    }

    fun save(wav: ByteArray, durationSeconds: Double, now: Instant): PendingRecording {
        directory.mkdirs()
        val id = ID_TIME.format(now) + "-" + UUID.randomUUID().toString().replace("-", "").take(6)
        val rec = PendingRecording(id, File(directory, "$id.wav"), now, round1(durationSeconds), null, 0)
        AtomicFile.writeBytes(rec.audioFile, wav)
        writeMeta(rec)
        log.info("Pending saved: ${wav.size} bytes, ${rec.durationSeconds}s")
        listeners.forEach { it() }
        return rec
    }

    /** Newest first. Recordings without readable metadata are still listed. */
    fun list(): List<PendingRecording> =
        (directory.listFiles { f -> f.name.endsWith(".wav") } ?: emptyArray())
            .map { readMeta(it) }
            .sortedWith(compareByDescending<PendingRecording> { it.created }.thenByDescending { it.id })

    fun latest(): PendingRecording? = list().firstOrNull()

    val count: Int get() = directory.listFiles { f -> f.name.endsWith(".wav") }?.size ?: 0

    fun readAudio(recording: PendingRecording): ByteArray {
        val full = recording.audioFile.canonicalFile
        if (!full.path.startsWith(directory.canonicalPath + File.separator)) {
            throw IllegalStateException("Pending recording is outside the pending folder.")
        }

        return full.readBytes()
    }

    fun markFailed(recording: PendingRecording, errorKind: String): PendingRecording {
        val updated = recording.copy(lastError = errorKind, attempts = recording.attempts + 1)
        try {
            writeMeta(updated)
        } catch (e: IOException) {
            log.warn("Could not update pending metadata: ${e.javaClass.simpleName}")
        }

        listeners.forEach { it() }
        return updated
    }

    fun delete(recording: PendingRecording) {
        for (f in listOf(recording.audioFile, metaFile(recording.audioFile))) {
            if (f.exists() && !f.delete()) log.warn("Could not delete pending file")
        }

        listeners.forEach { it() }
    }

    private fun metaFile(audio: File) = File(audio.parentFile, audio.nameWithoutExtension + ".json")

    private fun writeMeta(rec: PendingRecording) {
        val meta = Meta(rec.created.toString(), rec.durationSeconds, rec.lastError, rec.attempts)
        AtomicFile.writeText(metaFile(rec.audioFile), storageJson.encodeToString(Meta.serializer(), meta))
    }

    private fun readMeta(wav: File): PendingRecording {
        val id = wav.nameWithoutExtension
        try {
            val meta = metaFile(wav)
            if (meta.exists()) {
                val m = storageJson.decodeFromString(Meta.serializer(), meta.readText())
                return PendingRecording(id, wav, Instant.parse(m.createdUtc), m.durationSeconds, m.lastError, m.attempts)
            }
        } catch (e: Exception) {
            if (e !is IOException && e !is SerializationException && e !is IllegalArgumentException &&
                e !is java.time.format.DateTimeParseException
            ) {
                throw e
            }

            log.warn("Pending metadata unreadable (${e.javaClass.simpleName}); using file info.")
        }

        return PendingRecording(id, wav, Instant.ofEpochMilli(wav.lastModified()), 0.0, null, 0)
    }

    @Serializable
    private data class Meta(val createdUtc: String, val durationSeconds: Double, val lastError: String?, val attempts: Int)

    private companion object {
        val ID_TIME: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss-SSS").withZone(ZoneOffset.UTC)
    }
}
