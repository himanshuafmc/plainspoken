package app.plainspoken

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.os.Build
import android.os.Bundle
import android.text.format.DateUtils
import android.util.TypedValue
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import app.plainspoken.core.dictation.AudioRecorder
import app.plainspoken.core.dictation.DictationController
import app.plainspoken.core.dictation.DictationState
import app.plainspoken.core.dictation.DictationView
import app.plainspoken.core.dictation.InsertOutcome
import app.plainspoken.core.dictation.RecordedAudio
import app.plainspoken.core.dictation.SoundPlayer
import app.plainspoken.core.dictation.TextInserter
import app.plainspoken.core.dictation.UserMessage
import app.plainspoken.ui.Icon
import app.plainspoken.ui.IconView
import app.plainspoken.ui.Ui
import app.plainspoken.ui.dp
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** The last 20 transcripts (tap to copy) and any recordings still waiting, with Retry. */
class HistoryActivity : Activity() {
    private val scope = MainScope()
    private lateinit var services: Services
    private lateinit var ui: Ui
    private lateinit var content: LinearLayout
    private lateinit var retryController: DictationController
    private val refresh: () -> Unit = { runOnUiThread { render() } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        services = PlainspokenApp.services(this)
        ui = Ui(this)
        val (root, column) = ui.screen(this, "Recent transcripts", showBack = true)
        content = column
        setContentView(root)
        retryController = DictationController(
            settings = services::settings,
            hasApiKey = services::hasApiKey,
            recorder = NoRecorder,
            transcriber = services.transcriber,
            inserter = ClipboardOnly(),
            view = ToastView(),
            sounds = NoSounds,
            history = services.history,
            pending = services.pending,
            log = services.log,
            liveTranscriber = null,
        )
    }

    override fun onStart() {
        super.onStart()
        services.history.addListener(refresh)
        services.pending.addListener(refresh)
        render()
    }

    override fun onStop() {
        services.history.removeListener(refresh)
        services.pending.removeListener(refresh)
        super.onStop()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun render() {
        content.removeAllViews()
        val waiting = services.pending.count
        if (waiting > 0) {
            val card = ui.card(content)
            ui.heading(card, "$waiting recording${if (waiting == 1) "" else "s"} waiting")
            ui.text(card, "Something went wrong earlier (for example no internet). Retry transcribes the newest one and copies the text.", muted = true)
            ui.button(card, if (retryController.state == DictationState.IDLE) "Retry now" else "Transcribing…") {
                scope.launch { retryController.retryLast(insertAtCursor = false) }
                render()
            }
        }

        val entries = services.history.all
        if (entries.isEmpty()) {
            val card = ui.card(content)
            ui.text(card, if (services.settings().history.enabled) "Nothing yet. Your last 20 dictations will appear here." else "History is turned off in Settings.", muted = true)
            return
        }

        ui.text(content, "Tap a transcript to copy it.", muted = true, sizeSp = 14f)
        for (entry in entries) {
            val card = ui.card(content)
            val row = ui.row()
            val texts = ui.column()
            texts.addView(TextView(this).apply {
                text = entry.text
                setTextColor(ui.palette.text)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
                setLineSpacing(0f, 1.15f)
            })
            texts.addView(TextView(this).apply {
                text = DateUtils.getRelativeTimeSpanString(entry.created.toEpochMilli(), System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS)
                setTextColor(ui.palette.muted)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
                setPadding(0, dp(4), 0, 0)
            })
            row.addView(texts, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            row.addView(IconView(this, Icon.COPY, ui.palette.muted, 20f), LinearLayout.LayoutParams(dp(36), dp(36)))
            card.addView(row)
            card.setOnClickListener { copy(entry.text) }
        }

        ui.button(content, "Clear history", primary = false) {
            services.history.clear()
            render()
        }
    }

    private fun copy(text: String): Boolean = try {
        getSystemService(ClipboardManager::class.java)?.setPrimaryClip(ClipData.newPlainText("Plainspoken", text.trimEnd()))
        if (Build.VERSION.SDK_INT < 33) Toast.makeText(this, "Copied", Toast.LENGTH_SHORT).show()
        true
    } catch (e: Exception) {
        false
    }

    private inner class ClipboardOnly : TextInserter {
        override suspend fun insert(text: String) = if (copy(text)) InsertOutcome.COPIED else InsertOutcome.FAILED

        override suspend fun copy(text: String) = this@HistoryActivity.copy(text)
    }

    private inner class ToastView : DictationView {
        override fun onStateChanged(state: DictationState) = render()

        override fun showRecording() = Unit

        override fun showTranscribing(text: String) = Unit

        override fun showWarning(text: String) = Unit

        override fun showHint(text: String) {
            val shown = if (text.startsWith("Copied")) "Copied — paste it where you need it" else text
            Toast.makeText(this@HistoryActivity, shown, Toast.LENGTH_SHORT).show()
        }

        override fun showError(message: UserMessage) {
            Toast.makeText(this@HistoryActivity, message.text, Toast.LENGTH_LONG).show()
        }

        override fun showIdle() = Unit
    }

    /** Retry never records. */
    private object NoRecorder : AudioRecorder {
        override suspend fun start() = Unit

        override suspend fun stop() = RecordedAudio(ShortArray(0), 16000)

        override suspend fun cancel() = Unit

        override val isRecording = false
        override val elapsedMillis = 0L
        override val level = 0f
        override var sampleSink: ((ShortArray) -> Unit)? = null
    }

    private object NoSounds : SoundPlayer {
        override fun playStart() = Unit

        override fun playStop() = Unit
    }
}
