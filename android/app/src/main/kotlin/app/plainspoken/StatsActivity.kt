package app.plainspoken

import android.app.Activity
import android.os.Bundle
import android.widget.LinearLayout
import app.plainspoken.core.storage.ServiceStats
import app.plainspoken.ui.Ui
import java.time.Instant
import java.time.ZoneId
import java.util.Locale

/** Speed, reliability and estimated cost per speech service, from the last 1,000 dictations on this phone. */
class StatsActivity : Activity() {
    private lateinit var services: Services
    private lateinit var ui: Ui
    private lateinit var content: LinearLayout

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        services = PlainspokenApp.services(this)
        ui = Ui(this)
        val (root, column) = ui.screen(this, "Speed and cost", showBack = true)
        content = column
        setContentView(root)
        render()
    }

    private fun render() {
        content.removeAllViews()
        val summary = services.stats.summary(Instant.now(), ZoneId.systemDefault())
        val intro = ui.card(content)
        ui.text(
            intro,
            "From your last ${services.stats.all.size} dictations on this phone. “Time to text” is from when you stop speaking " +
                "to when the text is ready. Costs are estimates from the prices you entered; the clean-up step isn't included.",
            muted = true,
        )

        if (summary.isEmpty()) {
            ui.text(intro, "Nothing yet — dictate something first.")
            return
        }

        summary.forEach { serviceCard(it) }
        val actions = ui.card(content)
        ui.button(actions, "Clear these numbers", primary = false) {
            services.stats.clear()
            render()
        }
    }

    private fun serviceCard(s: ServiceStats) {
        val card = ui.card(content)
        val t = services.settings().transcription
        ui.heading(card, s.service.displayName)
        val ok = s.dictations - s.failures
        ui.text(card, "$ok dictations" + if (s.failures > 0) ", ${s.failures} failed" else "")
        s.medianTotalMs?.let { total ->
            val listen = s.medianListenMs ?: total
            ui.text(card, "Typical time to text: ${seconds(total)} (speech ${seconds(listen)}, clean-up ${seconds(total - listen)})")
        }

        if (s.streamedShare > 0) ui.text(card, "Streamed: ${(s.streamedShare * 100).toInt()} % of dictations", muted = true, sizeSp = 13f)
        val price = t.services[s.service].pricePerHour
        val minutes = "%.1f".format(Locale.ROOT, s.minutesThisMonth)
        if (price > 0) {
            val cost = s.minutesThisMonth / 60 * price
            val total = s.minutesAllTime / 60 * price
            ui.text(card, "This month: $minutes min of audio ≈ ${t.currency}${"%.2f".format(Locale.ROOT, cost)} (all recorded: ${t.currency}${"%.2f".format(Locale.ROOT, total)})")
        } else {
            ui.text(card, "This month: $minutes min of audio. Enter its price in the service's settings to see the cost.", muted = true, sizeSp = 13f)
        }
    }

    private fun seconds(ms: Long) = "%.1f s".format(Locale.ROOT, ms / 1000.0)
}
