package app.plainspoken.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.view.View

/** Simple original line icons, drawn on a 24-unit grid so they stay sharp at any size. */
enum class Icon {
    MIC, CHECK, CLOSE, BACKSPACE, ENTER, SWITCH_KEYBOARD, SETTINGS, RETRY, HISTORY, BACK, COPY;

    fun draw(canvas: Canvas, cx: Float, cy: Float, size: Float, color: Int) {
        val s = size / 24f
        val p = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            this.color = color
            style = Paint.Style.STROKE
            strokeWidth = 2f * s
            strokeCap = Paint.Cap.ROUND
            strokeJoin = Paint.Join.ROUND
        }

        canvas.save()
        canvas.translate(cx - 12 * s, cy - 12 * s)
        canvas.scale(s, s)
        p.strokeWidth = 2f
        when (this) {
            MIC -> {
                canvas.drawRoundRect(RectF(9f, 3f, 15f, 14f), 3f, 3f, p)
                canvas.drawArc(RectF(5.5f, 6f, 18.5f, 17.5f), 0f, 180f, false, p)
                canvas.drawLine(12f, 17.5f, 12f, 21f, p)
                canvas.drawLine(8.5f, 21f, 15.5f, 21f, p)
            }
            CHECK -> canvas.drawPath(Path().apply { moveTo(5f, 12.5f); lineTo(10f, 17.5f); lineTo(19.5f, 7f) }, p)
            CLOSE -> {
                canvas.drawLine(6.5f, 6.5f, 17.5f, 17.5f, p)
                canvas.drawLine(17.5f, 6.5f, 6.5f, 17.5f, p)
            }
            BACKSPACE -> {
                canvas.drawPath(Path().apply { moveTo(8f, 5f); lineTo(21f, 5f); lineTo(21f, 19f); lineTo(8f, 19f); lineTo(2.5f, 12f); close() }, p)
                canvas.drawLine(11.5f, 9f, 17.5f, 15f, p)
                canvas.drawLine(17.5f, 9f, 11.5f, 15f, p)
            }
            ENTER -> {
                canvas.drawPath(Path().apply { moveTo(19f, 5f); lineTo(19f, 14f); lineTo(5f, 14f) }, p)
                canvas.drawPath(Path().apply { moveTo(9f, 10f); lineTo(5f, 14f); lineTo(9f, 18f) }, p)
            }
            SWITCH_KEYBOARD -> {
                canvas.drawCircle(12f, 12f, 9f, p)
                canvas.drawOval(RectF(8f, 3f, 16f, 21f), p)
                canvas.drawLine(3f, 12f, 21f, 12f, p)
            }
            SETTINGS -> {
                canvas.drawLine(4f, 7f, 20f, 7f, p)
                canvas.drawLine(4f, 12f, 20f, 12f, p)
                canvas.drawLine(4f, 17f, 20f, 17f, p)
                val fill = Paint(p).apply { style = Paint.Style.FILL }
                canvas.drawCircle(9f, 7f, 2.2f, fill)
                canvas.drawCircle(15f, 12f, 2.2f, fill)
                canvas.drawCircle(8f, 17f, 2.2f, fill)
            }
            RETRY -> {
                canvas.drawArc(RectF(4.5f, 4.5f, 19.5f, 19.5f), -60f, 300f, false, p)
                canvas.drawPath(Path().apply { moveTo(15.5f, 3.5f); lineTo(16.2f, 7.8f); lineTo(20.2f, 6.2f) }, p)
            }
            HISTORY -> {
                canvas.drawCircle(12f, 12f, 8.5f, p)
                canvas.drawPath(Path().apply { moveTo(12f, 7.5f); lineTo(12f, 12f); lineTo(15f, 14f) }, p)
            }
            BACK -> {
                canvas.drawLine(19f, 12f, 5f, 12f, p)
                canvas.drawPath(Path().apply { moveTo(11f, 6f); lineTo(5f, 12f); lineTo(11f, 18f) }, p)
            }
            COPY -> {
                canvas.drawRoundRect(RectF(8f, 8f, 19f, 20f), 2f, 2f, p)
                canvas.drawPath(Path().apply { moveTo(16f, 5f); lineTo(16f, 4f); lineTo(5f, 4f); lineTo(5f, 16f); lineTo(6f, 16f) }, p)
            }
        }

        canvas.restore()
    }
}

/** A view that just shows one icon, centred. */
class IconView(context: Context, var icon: Icon, var color: Int, private val sizeDp: Float = 24f) : View(context) {
    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        icon.draw(canvas, width / 2f, height / 2f, context.dpf(sizeDp), color)
    }
}
