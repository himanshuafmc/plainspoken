package app.plainspoken.dot

import android.accessibilityservice.AccessibilityService
import android.annotation.SuppressLint
import android.content.ClipData
import android.content.ClipboardManager
import android.content.ComponentName
import android.content.Intent
import android.content.res.Configuration
import android.graphics.PixelFormat
import android.graphics.Rect
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityWindowInfo
import android.widget.TextView
import android.widget.Toast
import app.plainspoken.MainActivity
import app.plainspoken.PlainspokenApp
import app.plainspoken.Services
import app.plainspoken.core.dictation.CorrectionLearner
import app.plainspoken.core.dictation.DictationController
import app.plainspoken.core.dictation.DictationState
import app.plainspoken.core.dictation.DictationView
import app.plainspoken.core.dictation.InsertOutcome
import app.plainspoken.core.dictation.TextInserter
import app.plainspoken.core.dictation.UserAction
import app.plainspoken.core.dictation.UserMessage
import app.plainspoken.core.dictation.UserMessages
import app.plainspoken.core.settings.VocabularyCleaner
import app.plainspoken.core.time.SystemClock
import app.plainspoken.keyboard.MicButton
import app.plainspoken.keyboard.MicLook
import app.plainspoken.keyboard.PlainspokenKeyboard
import app.plainspoken.platform.AndroidRecorder
import app.plainspoken.platform.AndroidSounds
import app.plainspoken.ui.Palette
import app.plainspoken.ui.dp
import app.plainspoken.ui.dpf
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlin.math.abs

/**
 * The floating mic: a small dot that appears next to whatever keyboard the user has open, in any app. Tap it to start
 * and tap again to finish, or hold it while speaking and let go; drag it to move it. The text goes into the text box
 * through accessibility actions (see [FieldWriter]), so no keyboard switching is needed.
 *
 * It is an accessibility service because only those can see which text box has focus and type into another app's
 * text box. It looks only at the focused text box (never password boxes), and the dot itself is an accessibility
 * overlay, so no "display over other apps" permission is needed. The dictation logic is the same DictationController
 * the keyboard uses.
 */
class DictationDot : AccessibilityService(), DictationView, TextInserter {
    private val scope = MainScope()
    private val handler = Handler(Looper.getMainLooper())
    private lateinit var services: Services
    private lateinit var controller: DictationController
    private lateinit var recorder: AndroidRecorder
    private lateinit var prefs: DotPrefs
    private lateinit var palette: Palette
    private var prefsToken: Any? = null
    private var windowManager: WindowManager? = null
    private var startJob: kotlinx.coroutines.Job? = null
    private var ready = false

    private var dot: MicButton? = null
    private var dotParams: WindowManager.LayoutParams? = null
    private var dotShown = false
    private var label: TextView? = null
    private var labelParams: WindowManager.LayoutParams? = null
    private var labelShown = false
    private var labelAction: (() -> Unit)? = null

    /** The text box the current dictation goes into, captured when it started. */
    private var target: AccessibilityNodeInfo? = null

    // Touch handling: tap, hold-to-talk or drag.
    private var downAtX = 0f
    private var downAtY = 0f
    private var downTime = 0L
    private var startX = 0
    private var startY = 0
    private var dragging = false
    private var pressStartedDictation = false
    private var stopWhenStarted = false
    private var quietCancel = false

    // Learning: the last text inserted, to spot words the user corrects afterwards.
    private var lastInserted: String? = null
    private var lastInsertedPackage: String? = null
    private var lastInsertedAt = 0L
    private var offer: List<String> = emptyList()

    private val settingsListener: () -> Unit = { handler.post { scheduleUpdate() } }
    private val updater = Runnable { update() }
    private val ticker = object : Runnable {
        override fun run() {
            if (controller.state != DictationState.RECORDING) return
            dot?.level = controller.level
            scope.launch { controller.tick() }
            handler.postDelayed(this, 100)
        }
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        services = PlainspokenApp.services(this)
        prefs = DotPrefs(this)
        palette = Palette.of(this)
        windowManager = getSystemService(WindowManager::class.java)
        recorder = AndroidRecorder(this, services.log)
        controller = DictationController(
            settings = services::settings,
            hasApiKey = services::hasApiKey,
            recorder = recorder,
            transcriber = services.transcriber,
            inserter = this,
            view = this,
            sounds = AndroidSounds(services.log) { dot?.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY) },
            history = services.history,
            pending = services.pending,
            log = services.log,
            clock = SystemClock,
            liveTranscriber = services.live,
        )
        services.addListener(settingsListener)
        prefsToken = prefs.addListener { handler.post { scheduleUpdate() } }
        ready = true
        services.log.info("floating mic: on")
        scheduleUpdate()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        if (ready) scheduleUpdate()
    }

    override fun onInterrupt() = Unit

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        if (!ready) return
        palette = Palette.of(this)
        removeDot()
        hideLabel()
        scheduleUpdate()
    }

    override fun onDestroy() {
        if (ready) {
            services.removeListener(settingsListener)
            prefsToken?.let(prefs::removeListener)
            if (controller.state == DictationState.RECORDING) scope.launch { controller.cancel() }
            services.log.info("floating mic: off")
        }

        handler.removeCallbacksAndMessages(null)
        removeDot()
        hideLabel()
        scope.cancel()
        super.onDestroy()
    }

    // --- When and where the dot shows ---

    private fun scheduleUpdate() {
        handler.removeCallbacks(updater)
        handler.postDelayed(updater, 120)
    }

    /** Shows the dot while a text box is being typed in (keyboard open), hides it otherwise. Never moves it mid-dictation. */
    private fun update() {
        if (!ready || controller.state != DictationState.IDLE || dragging) return
        val ime = keyboardBounds()
        if (!prefs.enabled || ime == null || ourKeyboardIsActive() || focusedTextBox() == null) {
            removeDot()
            return
        }

        showDot(ime)
    }

    /** The text box with input focus, if it is an editable, non-password box. */
    private fun focusedTextBox(): AccessibilityNodeInfo? = try {
        val focused = findFocus(AccessibilityNodeInfo.FOCUS_INPUT) ?: rootInActiveWindow?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
        when {
            focused == null -> rootInActiveWindow?.let { textBoxInside(it, onlyIfFocused = true) }
            focused.isEditable -> focused.takeUnless { it.isPassword }
            // Apps built with Jetpack Compose (ChatGPT and Claude among them) report their whole screen as the focused
            // view, not the text box in it: look inside it for the box.
            else -> textBoxInside(focused, onlyIfFocused = false)
        }
    } catch (_: Exception) {
        null
    }

    /**
     * The editable box under [root] that has focus (null if that is a password box). Unless [onlyIfFocused], a box that
     * doesn't say it has focus is taken when it is the only one on screen.
     */
    private fun textBoxInside(root: AccessibilityNodeInfo, onlyIfFocused: Boolean): AccessibilityNodeInfo? {
        val queue = ArrayDeque<AccessibilityNodeInfo>().apply { add(root) }
        val boxes = mutableListOf<AccessibilityNodeInfo>()
        var seen = 0
        while (seen++ < MAX_NODES) {
            val node = queue.removeFirstOrNull() ?: break
            if (node.isEditable && node.isVisibleToUser) {
                if (node.isFocused) return node.takeUnless { it.isPassword }
                boxes += node
            }

            for (i in 0 until node.childCount) node.getChild(i)?.let(queue::add)
        }

        return boxes.singleOrNull()?.takeUnless { onlyIfFocused || it.isPassword }
    }

    /** Where the on-screen keyboard is, or null when none is showing. */
    private fun keyboardBounds(): Rect? = try {
        windows.orEmpty().firstOrNull { it.type == AccessibilityWindowInfo.TYPE_INPUT_METHOD }?.let { w ->
            Rect().also(w::getBoundsInScreen).takeIf { !it.isEmpty }
        }
    } catch (_: Exception) {
        null
    }

    /** The Plainspoken keyboard has its own mic, so the dot stays out of its way. */
    private fun ourKeyboardIsActive(): Boolean {
        val current = Settings.Secure.getString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD) ?: return false
        return ComponentName.unflattenFromString(current)?.className == PlainspokenKeyboard::class.java.name
    }

    private val dotSize get() = dp(DOT_DP)

    private fun showDot(ime: Rect) {
        val wm = windowManager ?: return
        val view = dot ?: createDot()
        val params = dotParams ?: overlayParams(dotSize, dotSize).also { dotParams = it }
        val screen = resources.displayMetrics
        val x = if (prefs.onRight) screen.widthPixels - dotSize - dp(EDGE_DP) else dp(EDGE_DP)
        val y = (ime.top - dotSize - dp(prefs.gapDp)).coerceAtLeast(dp(TOP_DP))
        // Moving a window raises a window event, which calls this again: only touch it when something changed.
        if (dotShown && params.x == x && params.y == y) return
        params.x = x
        params.y = y
        try {
            if (dotShown) wm.updateViewLayout(view, params) else wm.addView(view, params)
            dotShown = true
        } catch (e: Exception) {
            services.log.warn("floating mic: could not show (${e.javaClass.simpleName})")
            dotShown = false
        }
    }

    private fun removeDot() {
        val view = dot ?: return
        if (dotShown) {
            try {
                windowManager?.removeView(view)
            } catch (_: Exception) {
            }
        }

        dotShown = false
    }

    private fun overlayParams(width: Int, height: Int) = WindowManager.LayoutParams(
        width,
        height,
        WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
        WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
            WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
        PixelFormat.TRANSLUCENT,
    ).apply { gravity = Gravity.TOP or Gravity.START }

    @SuppressLint("ClickableViewAccessibility") // the dot also keeps a click listener for screen readers
    private fun createDot(): MicButton = MicButton(this, palette).apply {
        contentDescription = "Plainspoken: start dictation"
        setOnTouchListener { _, event -> onDotTouch(event) }
        setOnClickListener { onTap() }
        dot = this
    }

    // --- Touch: tap, hold-to-talk, drag ---

    private fun onDotTouch(event: MotionEvent): Boolean {
        val view = dot ?: return false
        val params = dotParams ?: return false
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                view.isPressed = true
                downAtX = event.rawX
                downAtY = event.rawY
                downTime = event.eventTime
                startX = params.x
                startY = params.y
                dragging = false
                stopWhenStarted = false
                quietCancel = false
                pressStartedDictation = controller.state == DictationState.IDLE
                if (pressStartedDictation) startDictation() // starts at once, so holding works without a delay
            }
            MotionEvent.ACTION_MOVE -> {
                val dx = event.rawX - downAtX
                val dy = event.rawY - downAtY
                val slop = ViewConfiguration.get(this).scaledTouchSlop * 2
                if (!dragging && pressStartedDictation && event.eventTime - downTime < DRAG_WINDOW_MS && (abs(dx) > slop || abs(dy) > slop)) {
                    // It's a drag, not a dictation: drop the recording this press just started.
                    dragging = true
                    view.isPressed = false
                    quietCancel = true
                    val starting = startJob
                    scope.launch {
                        starting?.join() // the microphone may still be starting
                        controller.cancel()
                    }
                    hideLabel()
                }

                if (dragging) {
                    params.x = (startX + dx).toInt()
                    params.y = (startY + dy).toInt()
                    try {
                        windowManager?.updateViewLayout(view, params)
                    } catch (_: Exception) {
                    }
                }
            }
            MotionEvent.ACTION_UP -> {
                view.isPressed = false
                if (dragging) {
                    finishDrag(params)
                } else if (!pressStartedDictation) {
                    onTap() // a tap while listening finishes; while transcribing it shows "busy"
                } else if (event.eventTime - downTime >= HOLD_MS) {
                    // Hold-to-talk: letting go finishes (as soon as the microphone has started).
                    if (controller.state == DictationState.RECORDING) scope.launch { controller.toggle() } else stopWhenStarted = true
                }
            }
            MotionEvent.ACTION_CANCEL -> {
                view.isPressed = false
                if (dragging) finishDrag(params)
            }
        }

        return true
    }

    /** Snaps the dot to the nearer screen edge and remembers its height above the keyboard. */
    private fun finishDrag(params: WindowManager.LayoutParams) {
        dragging = false
        val screen = resources.displayMetrics
        prefs.onRight = params.x + dotSize / 2 > screen.widthPixels / 2
        keyboardBounds()?.let { ime ->
            val gapPx = ime.top - dotSize - params.y
            prefs.gapDp = (gapPx / resources.displayMetrics.density).toInt().coerceIn(0, 600)
        }

        scheduleUpdate()
    }

    /** Taps from touch handling and from screen readers (which click instead of touching). */
    private fun onTap() {
        when (controller.state) {
            DictationState.IDLE -> startDictation()
            else -> scope.launch { controller.toggle() }
        }
    }

    private fun startDictation() {
        val box = focusedTextBox()
        if (box == null) {
            showLabel("Tap a text box first", palette.text, null, 3000)
            return
        }

        target = box
        lookForCorrections(box)
        startJob = scope.launch { controller.toggle() }
    }

    // --- Learning from corrections ---

    /**
     * At the start of a dictation in the app that got the last one (within 10 minutes), compares the text box with what
     * was inserted and remembers words the user typed in its place; they are offered once this dictation is done.
     */
    private fun lookForCorrections(box: AccessibilityNodeInfo) {
        val inserted = lastInserted ?: return
        lastInserted = null
        if (box.packageName?.toString() != lastInsertedPackage || System.currentTimeMillis() - lastInsertedAt > LEARN_WINDOW_MS) return
        val text = FieldWriter.textOf(box)
        if (text.contains(inserted)) return // nothing was changed
        val cursor = box.textSelectionStart.takeIf { it in 0..text.length } ?: text.length
        val around = if (text.length <= 4000) text else text.substring(maxOf(0, cursor - inserted.length - 600), minOf(text.length, cursor + 600))
        offer = CorrectionLearner.candidates(inserted, around, services.settings().transcription.customVocabulary)
    }

    private fun showOfferIfAny() {
        val words = offer
        if (words.isEmpty()) return
        offer = emptyList()
        showLabel("Add ${words.joinToString(", ") { "“$it”" }} to your vocabulary? Tap to add", palette.text, {
            services.update { it.transcription.customVocabulary = VocabularyCleaner.clean(it.transcription.customVocabulary + words) }
            services.log.info("vocabulary: ${words.size} corrected words added (floating mic)")
            showLabel("Added to your vocabulary", palette.text, null, 2500)
        }, 10_000)
    }

    // --- The label next to the dot (hints, errors, offers) ---

    private fun showLabel(text: String, color: Int, action: (() -> Unit)?, millis: Long) {
        val wm = windowManager ?: return
        val anchor = dotParams ?: return
        val view = label ?: createLabel()
        view.text = text
        view.setTextColor(color)
        labelAction = action
        val params = labelParams ?: overlayParams(WindowManager.LayoutParams.WRAP_CONTENT, WindowManager.LayoutParams.WRAP_CONTENT)
            .also { labelParams = it }
        view.measure(View.MeasureSpec.makeMeasureSpec(dp(LABEL_MAX_DP), View.MeasureSpec.AT_MOST), View.MeasureSpec.UNSPECIFIED)
        val screen = resources.displayMetrics
        params.x = if (prefs.onRight) (anchor.x - view.measuredWidth - dp(6)).coerceAtLeast(dp(EDGE_DP)) else anchor.x + dotSize + dp(6)
        params.x = params.x.coerceAtMost(screen.widthPixels - view.measuredWidth - dp(EDGE_DP)).coerceAtLeast(0)
        params.y = (anchor.y + (dotSize - view.measuredHeight) / 2).coerceAtLeast(0)
        try {
            if (labelShown) wm.updateViewLayout(view, params) else wm.addView(view, params)
            labelShown = true
        } catch (e: Exception) {
            services.log.warn("floating mic: could not show a message (${e.javaClass.simpleName})")
            labelShown = false
            if (action == null) Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
        }

        handler.removeCallbacks(labelHider)
        handler.postDelayed(labelHider, millis)
    }

    private val labelHider = Runnable { hideLabel() }

    private fun hideLabel() {
        labelAction = null
        val view = label ?: return
        if (labelShown) {
            try {
                windowManager?.removeView(view)
            } catch (_: Exception) {
            }
        }

        labelShown = false
    }

    private fun createLabel(): TextView = TextView(this).apply {
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        maxWidth = dp(LABEL_MAX_DP)
        setPadding(dp(12), dp(8), dp(12), dp(8))
        background = GradientDrawable().apply {
            setColor(palette.surface)
            setStroke(dp(1), palette.divider)
            cornerRadius = dpf(16f)
        }
        setOnClickListener {
            val action = labelAction
            hideLabel()
            action?.invoke()
        }
        label = this
    }

    // --- DictationView ---

    override fun onStateChanged(state: DictationState) {
        handler.removeCallbacks(ticker)
        val view = dot
        when (state) {
            DictationState.IDLE -> {
                view?.look = MicLook.IDLE
                view?.contentDescription = "Plainspoken: start dictation"
                handler.postDelayed({ showOfferIfAny() }, 300)
                scheduleUpdate()
            }
            DictationState.RECORDING -> {
                view?.look = MicLook.LISTENING
                hideLabel()
                handler.post(ticker)
                if (stopWhenStarted) {
                    stopWhenStarted = false
                    scope.launch { controller.toggle() }
                }
            }
            DictationState.TRANSCRIBING -> view?.look = MicLook.TRANSCRIBING
        }
    }

    override fun showRecording() = Unit

    override fun showTranscribing(text: String) = Unit

    override fun showWarning(text: String) = showLabel(text, palette.warning, null, 4000)

    override fun showHint(text: String) {
        if (quietCancel && text == UserMessages.CANCELLED) {
            quietCancel = false
            return
        }

        if (text == UserMessages.DIDNT_CATCH && recorder.silenced) {
            showLabel("Android didn't let the floating mic use the microphone. Use the Plainspoken keyboard for now.", palette.danger, null, 8000)
            return
        }

        showLabel(text, palette.text, null, 3000)
    }

    override fun showError(message: UserMessage) {
        val action: (() -> Unit)? = when (message.action) {
            UserAction.RETRY -> {
                {
                    target = focusedTextBox()
                    scope.launch { controller.retryLast(insertAtCursor = true) }
                }
            }
            UserAction.OPEN_SETTINGS -> ({ openApp(null) })
            UserAction.GRANT_MICROPHONE -> ({ openApp(MainActivity.EXTRA_ASK_MICROPHONE) })
            UserAction.NONE -> null
        }

        val suffix = when (message.action) {
            UserAction.RETRY -> " Tap to retry."
            UserAction.OPEN_SETTINGS, UserAction.GRANT_MICROPHONE -> " Tap to open Plainspoken."
            UserAction.NONE -> ""
        }

        showLabel(message.text + suffix, palette.danger, action, 10_000)
    }

    override fun showIdle() = Unit

    private fun openApp(extra: String?) {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        if (extra != null) intent.putExtra(extra, true)
        startActivity(intent)
    }

    // --- TextInserter ---

    override suspend fun insert(text: String): InsertOutcome {
        val box = target?.takeIf { it.refresh() && it.isEditable && !it.isPassword } ?: focusedTextBox()
        target = null
        if (box == null) return if (copy(text)) InsertOutcome.COPIED else InsertOutcome.FAILED
        val result = try {
            FieldWriter.insert(box, text, getSystemService(ClipboardManager::class.java))
        } catch (e: Exception) {
            services.log.warn("floating mic: insert failed (${e.javaClass.simpleName})")
            FieldWriter.Result.FAILED
        }

        services.log.info("floating mic: text ${result.name.lowercase()}")
        if (result == FieldWriter.Result.FAILED) return if (copy(text)) InsertOutcome.COPIED else InsertOutcome.FAILED
        lastInserted = text.trim()
        lastInsertedPackage = box.packageName?.toString()
        lastInsertedAt = System.currentTimeMillis()
        return InsertOutcome.INSERTED
    }

    override suspend fun copy(text: String): Boolean = try {
        getSystemService(ClipboardManager::class.java)?.setPrimaryClip(ClipData.newPlainText("Plainspoken", text.trimEnd()))
        true
    } catch (e: Exception) {
        services.log.warn("copy failed: ${e.javaClass.simpleName}")
        false
    }

    private companion object {
        const val DOT_DP = 60
        const val EDGE_DP = 4
        const val TOP_DP = 48
        const val LABEL_MAX_DP = 260

        /** How many nodes of an app's screen to look through for its text box. */
        const val MAX_NODES = 1500

        /** A press held at least this long is hold-to-talk: letting go finishes the dictation. */
        const val HOLD_MS = 500L

        /** Moving the finger this soon after touching the dot moves it instead of dictating. */
        const val DRAG_WINDOW_MS = 1200L

        const val LEARN_WINDOW_MS = 10 * 60 * 1000L
    }
}
