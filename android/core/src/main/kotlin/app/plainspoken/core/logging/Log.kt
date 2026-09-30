package app.plainspoken.core.logging

import app.plainspoken.core.AppInfo
import java.io.File
import java.io.IOException
import java.time.LocalDate
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.format.DateTimeParseException

/**
 * Minimal logger. Callers must never pass transcript text or the API key;
 * [Redactor] is a safety net for keys, not a licence to log secrets.
 */
interface Log {
    fun info(message: String)

    fun warn(message: String)

    fun error(message: String, throwable: Throwable? = null)
}

object NullLog : Log {
    override fun info(message: String) = Unit

    override fun warn(message: String) = Unit

    override fun error(message: String, throwable: Throwable?) = Unit
}

object Redactor {
    private val googleKey = Regex("AIza[0-9A-Za-z_\\-]{20,}")
    private val keyParam = Regex("((?:key|api_key|x-goog-api-key)[=:]\\s*)[^\\s&\"']+", RegexOption.IGNORE_CASE)

    /** Removes anything that looks like a Google API key and trims to a sane length. */
    fun clean(text: String?, maxLength: Int = 300): String {
        if (text.isNullOrEmpty()) return ""
        var cleaned = googleKey.replace(text, "[key]")
        cleaned = keyParam.replace(cleaned) { it.groupValues[1] + "[key]" }
        cleaned = cleaned.replace('\r', ' ').replace('\n', ' ')
        return if (cleaned.length <= maxLength) cleaned else cleaned.take(maxLength) + "…"
    }
}

/**
 * One log file per day (plainspoken-yyyyMMdd.log); files older than [keepDays] are deleted.
 * Thread-safe. Logging failures are swallowed: logging must never crash the app.
 */
class FileLog(
    val directory: File,
    private val keepDays: Int = 7,
    private val now: () -> ZonedDateTime = { ZonedDateTime.now() },
) : Log {
    private val lock = Any()
    private var lastCleanup: LocalDate? = null

    override fun info(message: String) = write("INFO", message, null)

    override fun warn(message: String) = write("WARN", message, null)

    override fun error(message: String, throwable: Throwable?) = write("ERROR", message, throwable)

    val currentFile: File get() = File(directory, fileNameFor(now().toLocalDate()))

    /** Newest first. */
    fun files(): List<File> =
        directory.listFiles { f -> f.name.startsWith(PREFIX) && f.name.endsWith(".log") }
            ?.sortedByDescending { it.name } ?: emptyList()

    private fun write(level: String, message: String, t: Throwable?) {
        try {
            val time = now()
            val sb = StringBuilder()
            sb.append(STAMP.format(time)).append(' ').append(level.padEnd(5)).append(' ')
                .append(Redactor.clean(message, 2000))
            if (t != null) {
                // Exception type, sanitised message and stack. Our own exceptions never carry transcript text.
                sb.append('\n').append("    ").append(t.javaClass.name).append(": ").append(Redactor.clean(t.message))
                t.stackTrace.take(30).forEach { sb.append("\n        at ").append(it) }
            }

            synchronized(lock) {
                directory.mkdirs()
                val today = time.toLocalDate()
                if (today != lastCleanup) {
                    lastCleanup = today
                    cleanup(today)
                }

                File(directory, fileNameFor(today)).appendText(sb.append('\n').toString())
            }
        } catch (_: IOException) {
            // Never let logging take the app down.
        } catch (_: SecurityException) {
        }
    }

    private fun cleanup(today: LocalDate) {
        for (file in files()) {
            val day = try {
                LocalDate.parse(file.name.removePrefix(PREFIX).removeSuffix(".log"), DAY)
            } catch (_: DateTimeParseException) {
                continue
            }

            if (day.isBefore(today.minusDays((keepDays - 1).toLong()))) {
                file.delete()
            }
        }
    }

    private companion object {
        /** Log file names are "<app>-yyyyMMdd.log", e.g. "plainspoken-20260930.log". */
        val PREFIX = AppInfo.NAME.lowercase() + "-"
        val DAY: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyyMMdd")
        val STAMP: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSS xxx")

        fun fileNameFor(day: LocalDate) = PREFIX + DAY.format(day) + ".log"
    }
}
