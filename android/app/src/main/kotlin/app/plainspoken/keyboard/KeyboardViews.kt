package app.plainspoken.keyboard

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.accessibility.AccessibilityNodeInfo
import app.plainspoken.ui.Icon
import app.plainspoken.ui.Palette
import app.plainspoken.ui.dpf

enum class MicLook { IDLE, LISTENING, TRANSCRIBING, DISABLED }

/** The big round button: mic when idle, green ✓ with a sound-level halo while listening, spinner while transcribing. */
class MicButton(context: Context, private val palette: Palette) : View(context) {
    var look: MicLook = MicLook.IDLE
        set(value) {
            field = value
            contentDescription = when (value) {
                MicLook.IDLE -> "Start dictation"
                MicLook.LISTENING -> "Finish and insert the text"
                MicLook.TRANSCRIBING -> "Transcribing"
                MicLook.DISABLED -> "Dictation not available here"
            }
            invalidate()
        }

    /** 0..1 microphone level, smoothed. */
    var level: Float = 0f
        set(value) {
            field = field * 0.6f + value.coerceIn(0f, 1f) * 0.4f
        }

    private val fill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val arc = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
    }

    init {
        isClickable = true
        isFocusable = true
        look = MicLook.IDLE
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val cx = width / 2f
        val cy = height / 2f
        val radius = minOf(width, height) / 2f - context.dpf(10f)
        val pressedScale = if (isPressed) 0.94f else 1f
        val r = radius * pressedScale
        when (look) {
            MicLook.LISTENING -> {
                fill.color = palette.done
                fill.alpha = 60
                canvas.drawCircle(cx, cy, r + context.dpf(3f) + level * context.dpf(9f), fill)
                fill.alpha = 255
                canvas.drawCircle(cx, cy, r, fill)
                Icon.CHECK.draw(canvas, cx, cy, r * 1.05f, palette.onBrand.takeIf { !palette.dark } ?: 0xFF06210F.toInt())
                postInvalidateOnAnimation()
            }
            MicLook.TRANSCRIBING -> {
                fill.color = palette.brand
                fill.alpha = 200
                canvas.drawCircle(cx, cy, r, fill)
                fill.alpha = 255
                arc.color = palette.onBrand
                arc.strokeWidth = context.dpf(3f)
                val inset = r * 0.45f
                val start = (SystemClock.uptimeMillis() % 1000L) * 0.36f
                canvas.drawArc(RectF(cx - inset, cy - inset, cx + inset, cy + inset), start, 270f, false, arc)
                postInvalidateOnAnimation()
            }
            MicLook.DISABLED -> {
                fill.color = palette.keyPressed
                canvas.drawCircle(cx, cy, r, fill)
                Icon.MIC.draw(canvas, cx, cy, r * 0.95f, palette.muted)
            }
            MicLook.IDLE -> {
                fill.color = palette.brand
                canvas.drawCircle(cx, cy, r, fill)
                Icon.MIC.draw(canvas, cx, cy, r * 0.95f, palette.onBrand)
            }
        }
    }

    override fun onInitializeAccessibilityNodeInfo(info: AccessibilityNodeInfo) {
        super.onInitializeAccessibilityNodeInfo(info)
        info.className = android.widget.Button::class.java.name
    }

    override fun drawableStateChanged() {
        super.drawableStateChanged()
        invalidate()
    }
}

/** A keyboard key with an icon or a short label. Optional auto-repeat while held (backspace). */
@SuppressLint("ViewConstructor")
class KeyButton(
    context: Context,
    private val palette: Palette,
    var icon: Icon?,
    var label: String?,
    description: String,
    private val repeat: Boolean = false,
    private val onPress: () -> Unit,
) : View(context) {
    private val shape = Paint(Paint.ANTI_ALIAS_FLAG)
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.CENTER
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
    }

    private val handler = Handler(Looper.getMainLooper())
    private var repeating = false
    private val repeater = object : Runnable {
        override fun run() {
            if (!repeating) return
            onPress()
            handler.postDelayed(this, 60)
        }
    }

    /** Draws the key in the accent colour (e.g. a Retry key that needs attention). */
    var accent = false
        set(value) {
            field = value
            invalidate()
        }

    init {
        contentDescription = description
        isClickable = true
        isFocusable = true
        if (!repeat) {
            setOnClickListener {
                performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                onPress()
            }
        }
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (!repeat) return super.onTouchEvent(event)
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                isPressed = true
                performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                onPress()
                repeating = true
                handler.postDelayed(repeater, 400)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                isPressed = false
                repeating = false
                handler.removeCallbacks(repeater)
                if (event.actionMasked == MotionEvent.ACTION_UP) performClick()
            }
        }

        return true
    }

    override fun performClick(): Boolean {
        super.performClick()
        return true
    }

    override fun onDetachedFromWindow() {
        repeating = false
        handler.removeCallbacks(repeater)
        super.onDetachedFromWindow()
    }

    override fun drawableStateChanged() {
        super.drawableStateChanged()
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val inset = context.dpf(3f)
        shape.color = when {
            isPressed -> palette.keyPressed
            accent -> palette.brand
            else -> palette.key
        }

        canvas.drawRoundRect(RectF(inset, inset, width - inset, height - inset), context.dpf(8f), context.dpf(8f), shape)
        val color = if (accent && !isPressed) palette.onBrand else palette.text
        val cx = width / 2f
        val cy = height / 2f
        val text = label
        val ic = icon
        if (ic != null && text != null) {
            val size = context.dpf(20f)
            textPaint.textSize = context.dpf(14f)
            textPaint.color = color
            val textWidth = textPaint.measureText(text)
            val total = size + context.dpf(6f) + textWidth
            val left = cx - total / 2
            ic.draw(canvas, left + size / 2, cy, size, color)
            canvas.drawText(text, left + size + context.dpf(6f) + textWidth / 2, cy - (textPaint.ascent() + textPaint.descent()) / 2, textPaint)
        } else if (ic != null) {
            ic.draw(canvas, cx, cy, context.dpf(22f), color)
        } else if (text != null) {
            textPaint.textSize = context.dpf(if (text.length == 1) 22f else 15f)
            textPaint.color = color
            canvas.drawText(text, cx, cy - (textPaint.ascent() + textPaint.descent()) / 2, textPaint)
        }
    }
}
