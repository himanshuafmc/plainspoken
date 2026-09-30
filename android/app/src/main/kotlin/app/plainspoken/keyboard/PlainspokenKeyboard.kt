package app.plainspoken.keyboard

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.inputmethodservice.InputMethodService
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.text.TextUtils
import android.util.TypedValue
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import app.plainspoken.MainActivity
import app.plainspoken.PlainspokenApp
import app.plainspoken.Services
import app.plainspoken.core.dictation.DictationController
import app.plainspoken.core.dictation.DictationState
import app.plainspoken.core.dictation.DictationView
import app.plainspoken.core.dictation.InsertOutcome
import app.plainspoken.core.dictation.TextInserter
import app.plainspoken.core.dictation.UserAction
import app.plainspoken.core.dictation.UserMessage
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SettingsJson
import app.plainspoken.core.time.SystemClock
import app.plainspoken.platform.AndroidRecorder
import app.plainspoken.platform.AndroidSounds
import app.plainspoken.ui.Icon
import app.plainspoken.ui.Palette
import app.plainspoken.ui.dp
import app.plainspoken.ui.dpf
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * Plainspoken as an Android keyboard: tap the mic, speak, tap ✓ and the text goes straight into the text box.
 * The dictation logic is the shared DictationController (same as Windows); this class is the Android view,
 * text inserter and glue. Voice typing is switched off in password fields.
 */
class PlainspokenKeyboard : InputMethodService(), DictationView, TextInserter {
    private val scope = MainScope()
    private val handler = Handler(Looper.getMainLooper())
    private lateinit var services: Services
    private lateinit var controller: DictationController
    private lateinit var palette: Palette

    private var root: View? = null
    private var status: TextView? = null
    private var action: TextView? = null
    private var mic: MicButton? = null
    private var cancelKey: KeyButton? = null
    private var retryKey: KeyButton? = null
    private var enterKey: KeyButton? = null

    private var privateField = false
    private var incognito = false
    private var pendingAction: UserAction = UserAction.NONE
    private var messageUntil = 0L
    private val settingsListener: () -> Unit = { handler.post { refreshIdle() } }
    private val pendingListener: () -> Unit = { handler.post { refreshRetry() } }

    private val ticker = object : Runnable {
        override fun run() {
            if (controller.state != DictationState.RECORDING) return
            mic?.level = controller.level
            if (System.currentTimeMillis() > messageUntil) setStatus("Listening  ${formatElapsed(controller.elapsedMillis)}", palette.text)
            scope.launch { controller.tick() }
            handler.postDelayed(this, 100)
        }
    }

    override fun onCreate() {
        super.onCreate()
        services = PlainspokenApp.services(this)
        palette = Palette.of(this)
        val recorder = AndroidRecorder(this, services.log)
        controller = DictationController(
            settings = ::currentSettings,
            hasApiKey = services::hasApiKey,
            recorder = recorder,
            transcriber = services.transcriber,
            inserter = this,
            view = this,
            sounds = AndroidSounds(services.log) { root?.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY) },
            history = services.history,
            pending = services.pending,
            log = services.log,
            clock = SystemClock,
            liveTranscriber = services.live,
        )
        services.addListener(settingsListener)
        services.pending.addListener(pendingListener)
    }

    override fun onDestroy() {
        services.removeListener(settingsListener)
        services.pending.removeListener(pendingListener)
        handler.removeCallbacksAndMessages(null)
        if (controller.state == DictationState.RECORDING) scope.launch { controller.cancel() }
        scope.cancel()
        super.onDestroy()
    }

    /** Never take over the whole screen in landscape; the keyboard is small. */
    override fun onEvaluateFullscreenMode(): Boolean = false

    override fun onCreateInputView(): View {
        palette = Palette.of(this)
        val view = buildView()
        root = view
        onStateChanged(controller.state)
        return view
    }

    override fun onStartInputView(info: EditorInfo, restarting: Boolean) {
        super.onStartInputView(info, restarting)
        privateField = isPrivate(info)
        incognito = info.imeOptions and EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING != 0
        updateEnterKey(info)
        if (controller.state == DictationState.IDLE) refreshIdle()
        refreshRetry()
    }

    override fun onFinishInputView(finishingInput: Boolean) {
        // Keyboard closed while listening: finish and transcribe (the text is copied if the box is gone).
        if (controller.state == DictationState.RECORDING) scope.launch { controller.toggle() }
        super.onFinishInputView(finishingInput)
    }

    // --- Layout ---

    private fun buildView(): View {
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(palette.keyboard)
            setPadding(dp(6), dp(4), dp(6), dp(6))
        }

        val statusRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            minimumHeight = dp(44)
            setPadding(dp(8), 0, 0, 0)
        }

        status = TextView(this).apply {
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
            setTextColor(palette.text)
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
        }

        statusRow.addView(status, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        action = TextView(this).apply {
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setTextColor(palette.onBrand)
            gravity = Gravity.CENTER
            setPadding(dp(14), dp(6), dp(14), dp(6))
            background = GradientDrawable().apply {
                setColor(palette.brand)
                cornerRadius = dpf(16f)
            }
            visibility = View.GONE
            isClickable = true
            setOnClickListener { runAction(pendingAction) }
        }

        statusRow.addView(action, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginEnd = dp(4) })
        statusRow.addView(
            KeyButton(this, palette, Icon.SETTINGS, null, "Plainspoken settings") { openApp(null) },
            LinearLayout.LayoutParams(dp(48), dp(44)),
        )
        column.addView(statusRow)

        val micRow = FrameLayout(this)
        retryKey = KeyButton(this, palette, Icon.RETRY, "Retry", "Retry the saved recording") {
            scope.launch { controller.retryLast(insertAtCursor = true) }
        }.also { it.accent = true }
        micRow.addView(retryKey, FrameLayout.LayoutParams(dp(112), dp(48), Gravity.START or Gravity.CENTER_VERTICAL))

        mic = MicButton(this, palette).apply { setOnClickListener { onMicPressed() } }
        micRow.addView(mic, FrameLayout.LayoutParams(dp(104), dp(104), Gravity.CENTER))

        cancelKey = KeyButton(this, palette, Icon.CLOSE, "Cancel", "Cancel dictation") { scope.launch { controller.cancel() } }
        micRow.addView(cancelKey, FrameLayout.LayoutParams(dp(112), dp(48), Gravity.END or Gravity.CENTER_VERTICAL))
        column.addView(micRow, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(108)))

        val keys = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        fun add(key: View, weight: Float) = keys.addView(key, LinearLayout.LayoutParams(0, dp(52), weight))
        val switcher = KeyButton(this, palette, Icon.SWITCH_KEYBOARD, null, "Switch keyboard") { switchKeyboard() }
        switcher.setOnLongClickListener {
            getSystemService(InputMethodManager::class.java)?.showInputMethodPicker()
            true
        }

        add(switcher, 1.2f)
        add(KeyButton(this, palette, null, ",", "Comma") { commit(",") }, 1f)
        add(KeyButton(this, palette, null, "space", "Space") { commit(" ") }, 3.6f)
        add(KeyButton(this, palette, null, ".", "Full stop") { commit(".") }, 1f)
        add(KeyButton(this, palette, Icon.BACKSPACE, null, "Delete", repeat = true) { backspace() }, 1.3f)
        enterKey = KeyButton(this, palette, Icon.ENTER, null, "Enter") { enter() }
        add(enterKey!!, 1.3f)
        column.addView(keys)

        // Keep the keys above the gesture bar / navigation buttons.
        column.setOnApplyWindowInsetsListener { v, insets ->
            val bottom = if (Build.VERSION.SDK_INT >= 30) {
                insets.getInsets(WindowInsets.Type.navigationBars()).bottom
            } else {
                @Suppress("DEPRECATION")
                insets.systemWindowInsetBottom
            }

            v.setPadding(v.paddingLeft, v.paddingTop, v.paddingRight, dp(6) + bottom)
            insets
        }

        return column
    }

    // --- Actions ---

    private fun onMicPressed() {
        if (privateField && controller.state == DictationState.IDLE) {
            showHint("Voice typing is off in password fields")
            return
        }

        mic?.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
        scope.launch { controller.toggle() }
    }

    private fun runAction(a: UserAction) {
        when (a) {
            UserAction.RETRY -> scope.launch { controller.retryLast(insertAtCursor = true) }
            UserAction.OPEN_SETTINGS -> openApp(null)
            UserAction.GRANT_MICROPHONE -> openApp(MainActivity.EXTRA_ASK_MICROPHONE)
            UserAction.NONE -> Unit
        }
    }

    private fun openApp(extra: String?) {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        if (extra != null) intent.putExtra(extra, true)
        startActivity(intent)
    }

    private fun switchKeyboard() {
        if (controller.state == DictationState.RECORDING) scope.launch { controller.toggle() }
        if (Build.VERSION.SDK_INT >= 28) {
            if (!switchToPreviousInputMethod()) getSystemService(InputMethodManager::class.java)?.showInputMethodPicker()
        } else {
            getSystemService(InputMethodManager::class.java)?.showInputMethodPicker()
        }
    }

    private fun commit(text: String) {
        currentInputConnection?.commitText(text, 1)
    }

    private fun backspace() {
        val ic = currentInputConnection ?: return
        val selected = ic.getSelectedText(0)
        if (!selected.isNullOrEmpty()) ic.commitText("", 1) else sendDownUpKeyEvents(KeyEvent.KEYCODE_DEL)
    }

    private fun enter() {
        val info = currentInputEditorInfo
        val ic = currentInputConnection ?: return
        val actionId = info?.imeOptions?.and(EditorInfo.IME_MASK_ACTION) ?: EditorInfo.IME_ACTION_NONE
        val noEnterAction = (info?.imeOptions ?: 0) and EditorInfo.IME_FLAG_NO_ENTER_ACTION != 0
        if (!noEnterAction && actionId != EditorInfo.IME_ACTION_NONE && actionId != EditorInfo.IME_ACTION_UNSPECIFIED) {
            ic.performEditorAction(actionId)
        } else {
            sendDownUpKeyEvents(KeyEvent.KEYCODE_ENTER)
        }
    }

    private fun updateEnterKey(info: EditorInfo) {
        val actionId = info.imeOptions and EditorInfo.IME_MASK_ACTION
        val noEnterAction = info.imeOptions and EditorInfo.IME_FLAG_NO_ENTER_ACTION != 0
        val label = if (noEnterAction) null else when (actionId) {
            EditorInfo.IME_ACTION_SEND -> "Send"
            EditorInfo.IME_ACTION_SEARCH -> "Search"
            EditorInfo.IME_ACTION_GO -> "Go"
            EditorInfo.IME_ACTION_NEXT -> "Next"
            EditorInfo.IME_ACTION_DONE -> "Done"
            else -> null
        }

        enterKey?.let {
            it.label = label
            it.icon = if (label == null) Icon.ENTER else null
            it.contentDescription = label ?: "Enter"
            it.invalidate()
        }
    }

    private fun isPrivate(info: EditorInfo): Boolean {
        val cls = info.inputType and InputType.TYPE_MASK_CLASS
        val variation = info.inputType and InputType.TYPE_MASK_VARIATION
        return (cls == InputType.TYPE_CLASS_TEXT &&
            (variation == InputType.TYPE_TEXT_VARIATION_PASSWORD || variation == InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD ||
                variation == InputType.TYPE_TEXT_VARIATION_WEB_PASSWORD)) ||
            (cls == InputType.TYPE_CLASS_NUMBER && variation == InputType.TYPE_NUMBER_VARIATION_PASSWORD)
    }

    /** Settings for this dictation; nothing is added to history in incognito text boxes. */
    private fun currentSettings(): PlainspokenSettings {
        val s = services.settings()
        if (!incognito || !s.history.enabled) return s
        return SettingsJson.clone(s).apply { history.enabled = false }
    }

    // --- DictationView ---

    override fun onStateChanged(state: DictationState) {
        handler.removeCallbacks(ticker)
        when (state) {
            DictationState.IDLE -> {
                mic?.look = if (privateField) MicLook.DISABLED else MicLook.IDLE
                cancelKey?.visibility = View.INVISIBLE
                refreshRetry()
                refreshIdle() // keeps a hint or error that was just shown
            }
            DictationState.RECORDING -> {
                mic?.look = MicLook.LISTENING
                cancelKey?.visibility = View.VISIBLE
                retryKey?.visibility = View.INVISIBLE
                handler.post(ticker)
            }
            DictationState.TRANSCRIBING -> {
                mic?.look = MicLook.TRANSCRIBING
                cancelKey?.visibility = View.VISIBLE
                retryKey?.visibility = View.INVISIBLE
            }
        }
    }

    override fun showRecording() {
        messageUntil = 0
        hideAction()
        setStatus("Listening  0:00", palette.text)
    }

    override fun showTranscribing(text: String) {
        messageUntil = 0
        hideAction()
        setStatus(text, palette.text)
    }

    override fun showWarning(text: String) {
        messageUntil = System.currentTimeMillis() + 4000
        setStatus(text, palette.warning)
    }

    override fun showHint(text: String) {
        hideAction()
        messageUntil = System.currentTimeMillis() + 3000
        setStatus(text, palette.text)
        handler.postDelayed({ if (System.currentTimeMillis() >= messageUntil) refreshIdle() }, 3100)
    }

    override fun showError(message: UserMessage) {
        messageUntil = System.currentTimeMillis() + 60_000
        setStatus(message.text, palette.danger)
        pendingAction = message.action
        val label = when (message.action) {
            UserAction.RETRY -> "Retry"
            UserAction.OPEN_SETTINGS -> "Open app"
            UserAction.GRANT_MICROPHONE -> "Allow"
            UserAction.NONE -> null
        }

        action?.let {
            it.text = label
            it.visibility = if (label == null) View.GONE else View.VISIBLE
        }
    }

    override fun showIdle() {
        messageUntil = 0
        refreshIdle()
    }

    private fun refreshIdle() {
        if (controller.state != DictationState.IDLE || System.currentTimeMillis() < messageUntil) return
        hideAction()
        when {
            privateField -> setStatus("Voice typing is off in password fields", palette.muted)
            !services.hasApiKey() -> {
                setStatus("Add your free Gemini key in the Plainspoken app to start.", palette.text)
                pendingAction = UserAction.OPEN_SETTINGS
                action?.let {
                    it.text = "Open app"
                    it.visibility = View.VISIBLE
                }
            }
            else -> setStatus("Tap the mic and speak", palette.muted)
        }

        mic?.look = if (privateField) MicLook.DISABLED else MicLook.IDLE
    }

    private fun refreshRetry() {
        val show = controller.state == DictationState.IDLE && services.pending.count > 0
        retryKey?.visibility = if (show) View.VISIBLE else View.INVISIBLE
    }

    private fun setStatus(text: String, color: Int) {
        status?.text = text
        status?.setTextColor(color)
    }

    private fun hideAction() {
        pendingAction = UserAction.NONE
        action?.visibility = View.GONE
    }

    private fun formatElapsed(ms: Long): String {
        val seconds = ms / 1000
        return "%d:%02d".format(seconds / 60, seconds % 60)
    }

    // --- TextInserter ---

    override suspend fun insert(text: String): InsertOutcome {
        val ic = currentInputConnection
        if (ic == null || privateField) return if (copy(text)) InsertOutcome.COPIED else InsertOutcome.FAILED
        var toInsert = text
        // Add a space when the cursor sits right after a word, so dictations join up naturally.
        val before = ic.getTextBeforeCursor(1, 0)
        if (!before.isNullOrEmpty() && !before.last().isWhitespace() && toInsert.firstOrNull()?.isLetterOrDigit() == true) {
            toInsert = " $toInsert"
        }

        return if (ic.commitText(toInsert, 1)) InsertOutcome.INSERTED else if (copy(text)) InsertOutcome.COPIED else InsertOutcome.FAILED
    }

    override suspend fun copy(text: String): Boolean = try {
        getSystemService(ClipboardManager::class.java)?.setPrimaryClip(ClipData.newPlainText("Plainspoken", text.trimEnd()))
        if (Build.VERSION.SDK_INT < 33) Toast.makeText(this, "Copied", Toast.LENGTH_SHORT).show()
        true
    } catch (e: Exception) {
        services.log.warn("copy failed: ${e.javaClass.simpleName}")
        false
    }
}
