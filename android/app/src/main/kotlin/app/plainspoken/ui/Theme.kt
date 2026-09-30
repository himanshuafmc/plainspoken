package app.plainspoken.ui

import android.content.Context
import android.content.res.Configuration
import android.graphics.Color
import kotlin.math.roundToInt

/** Plainspoken colours (same teal as the Windows app), for light and dark mode. */
class Palette(val dark: Boolean) {
    val brand = if (dark) 0xFF4FC3B0.toInt() else 0xFF0B7A6B.toInt()
    val onBrand = if (dark) 0xFF00201B.toInt() else Color.WHITE
    val background = if (dark) 0xFF101614.toInt() else 0xFFF4F8F7.toInt()
    val surface = if (dark) 0xFF1A2320.toInt() else Color.WHITE
    val keyboard = if (dark) 0xFF141B19.toInt() else 0xFFE4ECE9.toInt()
    val key = if (dark) 0xFF26312E.toInt() else Color.WHITE
    val keyPressed = if (dark) 0xFF34423E.toInt() else 0xFFD2DDD9.toInt()
    val text = if (dark) 0xFFE4EDEA.toInt() else 0xFF10201D.toInt()
    val muted = if (dark) 0xFF9CB0AA.toInt() else 0xFF566A63.toInt()
    val divider = if (dark) 0xFF2C3835.toInt() else 0xFFD9E2DF.toInt()
    val danger = if (dark) 0xFFFF8A80.toInt() else 0xFFB3261E.toInt()
    val warning = if (dark) 0xFFFFC266.toInt() else 0xFFB45309.toInt()

    /** The ✓ "done" button while listening, as on Windows. */
    val done = if (dark) 0xFF4ADE80.toInt() else 0xFF16A34A.toInt()
    val listening = if (dark) 0xFFF87171.toInt() else 0xFFDC2626.toInt()

    companion object {
        fun of(context: Context): Palette =
            Palette((context.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES)
    }
}

fun Context.dp(value: Float): Int = (value * resources.displayMetrics.density).roundToInt()

fun Context.dp(value: Int): Int = dp(value.toFloat())

fun Context.dpf(value: Float): Float = value * resources.displayMetrics.density
