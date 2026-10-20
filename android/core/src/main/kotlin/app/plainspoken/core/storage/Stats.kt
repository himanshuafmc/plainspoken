package app.plainspoken.core.storage

import app.plainspoken.core.logging.Log
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.transcription.DictationStat
import app.plainspoken.core.transcription.StatsRecorder
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.builtins.ListSerializer
import java.io.File
import java.io.IOException
import java.time.Instant
import java.time.ZoneId
import java.time.ZonedDateTime

/** One dictation as stored for the stats page: timings and which service, never any text. */
@Serializable
data class StatEntry(
    val utc: String,
    val service: String,
    val audioSeconds: Double,
    val listenMs: Long,
    val cleanupMs: Long,
    val ok: Boolean,
    val error: String? = null,
    val streamed: Boolean = false,
    val usedBackup: Boolean = false,
    val hedged: Boolean = false,
    val cleanedBy: String? = null,
) {
    val time: Instant get() = runCatching { Instant.parse(utc) }.getOrDefault(Instant.EPOCH)
}

/** Totals for one service. Times are medians, so one slow request doesn't distort them. */
data class ServiceStats(
    val service: SpeechService,
    val dictations: Int,
    val failures: Int,
    val medianListenMs: Long?,
    val medianTotalMs: Long?,
    val streamedShare: Double,
    val minutesThisMonth: Double,
    val minutesAllTime: Double,
)

/** The last [capacity] dictations' timings, newest last, persisted as JSON. Thread-safe. */
class StatsStore(private val file: File, private val log: Log, private val capacity: Int = DEFAULT_CAPACITY) : StatsRecorder {
    private val lock = Any()
    private var entries: MutableList<StatEntry> = load()

    val all: List<StatEntry> get() = synchronized(lock) { entries.toList() }

    override fun record(stat: DictationStat) {
        val entry = StatEntry(
            stat.utc.toString(), stat.service.id, round1(stat.audioSeconds), stat.listenMillis, stat.cleanupMillis, stat.ok,
            stat.errorKind, stat.streamed, stat.usedBackup, stat.hedged, stat.cleanedBy,
        )
        synchronized(lock) {
            entries.add(entry)
            while (entries.size > capacity) entries.removeAt(0)
            persist()
        }
    }

    fun clear() = synchronized(lock) {
        entries.clear()
        if (file.exists() && !file.delete()) log.warn("Could not delete stats file")
    }

    /** Per-service totals, services with the most dictations first. */
    fun summary(now: Instant, zone: ZoneId): List<ServiceStats> {
        val month = ZonedDateTime.ofInstant(now, zone).withDayOfMonth(1).toLocalDate().atStartOfDay(zone).toInstant()
        return all.groupBy { it.service }.mapNotNull { (id, list) ->
            val service = SpeechService.byId(id) ?: return@mapNotNull null
            val ok = list.filter { it.ok }
            ServiceStats(
                service,
                dictations = list.size,
                failures = list.count { !it.ok },
                medianListenMs = median(ok.map { it.listenMs }),
                medianTotalMs = median(ok.map { it.listenMs + it.cleanupMs }),
                streamedShare = if (ok.isEmpty()) 0.0 else ok.count { it.streamed }.toDouble() / ok.size,
                minutesThisMonth = list.filter { !it.time.isBefore(month) }.sumOf { it.audioSeconds } / 60,
                minutesAllTime = list.sumOf { it.audioSeconds } / 60,
            )
        }.sortedByDescending { it.dictations }
    }

    private fun persist() {
        try {
            AtomicFile.writeText(file, storageJson.encodeToString(ListSerializer(StatEntry.serializer()), entries))
        } catch (e: IOException) {
            log.warn("Could not save stats: ${e.javaClass.simpleName}")
        }
    }

    private fun load(): MutableList<StatEntry> = try {
        if (!file.exists()) {
            mutableListOf()
        } else {
            storageJson.decodeFromString(ListSerializer(StatEntry.serializer()), file.readText()).takeLast(capacity).toMutableList()
        }
    } catch (e: Exception) {
        if (e !is IOException && e !is SerializationException && e !is IllegalArgumentException) throw e
        log.warn("Stats file unreadable (${e.javaClass.simpleName}); starting empty.")
        mutableListOf()
    }

    companion object {
        const val DEFAULT_CAPACITY = 1000

        internal fun median(values: List<Long>): Long? {
            if (values.isEmpty()) return null
            val sorted = values.sorted()
            return sorted[sorted.size / 2]
        }
    }
}
