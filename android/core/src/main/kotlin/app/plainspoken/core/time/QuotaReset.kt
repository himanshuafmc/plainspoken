package app.plainspoken.core.time

import java.time.Instant
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle
import java.util.Locale

/** Where "now" comes from; injectable for tests. */
interface Clock {
    fun now(): Instant

    fun zone(): ZoneId
}

object SystemClock : Clock {
    override fun now(): Instant = Instant.now()

    override fun zone(): ZoneId = ZoneId.systemDefault()
}

/**
 * Gemini's free daily quota resets at midnight Pacific time. This computes that moment
 * (DST-aware, via the America/Los_Angeles zone) and formats it in the user's time zone.
 */
object QuotaReset {
    val pacific: ZoneId = ZoneId.of("America/Los_Angeles")

    fun nextReset(now: Instant): Instant {
        val la = now.atZone(pacific)
        return la.toLocalDate().plusDays(1).atStartOfDay(pacific).toInstant()
    }

    /** For example "12:30 PM IST" or "12:30 PM IST tomorrow". */
    fun formatNextReset(now: Instant, local: ZoneId): String {
        val reset = nextReset(now).atZone(local)
        val localNow = now.atZone(local)
        var text = TIME.format(reset) + " " + abbreviation(local, reset)
        if (reset.toLocalDate().isAfter(localNow.toLocalDate())) text += " tomorrow"
        return text
    }

    fun abbreviation(zone: ZoneId, at: ZonedDateTime): String {
        if (zone.id in setOf("Asia/Kolkata", "Asia/Calcutta")) return "IST"
        if (zone.id in setOf("UTC", "Etc/UTC", "Z", "GMT")) return "UTC"
        val short = zone.getDisplayName(TextStyle.SHORT, Locale.ENGLISH)
        if (short.length in 2..5 && short.all { it.isUpperCase() }) return short
        val seconds = at.offset.totalSeconds
        val sign = if (seconds < 0) "-" else "+"
        val abs = kotlin.math.abs(seconds)
        return "UTC$sign%02d:%02d".format(abs / 3600, (abs % 3600) / 60)
    }

    private val TIME: DateTimeFormatter = DateTimeFormatter.ofPattern("h:mm a", Locale.ENGLISH)
}
