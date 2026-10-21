package app.plainspoken.dot

import android.content.ComponentName
import android.content.Context
import android.provider.Settings

/**
 * This phone's floating-mic preferences: whether to show it, and where the user dragged it (the screen edge and the
 * height above the keyboard). Device-only, so they are not part of the shared settings file.
 */
class DotPrefs(context: Context) {
    private val prefs = context.getSharedPreferences("floating_mic", Context.MODE_PRIVATE)

    var enabled: Boolean
        get() = prefs.getBoolean("enabled", true)
        set(value) = prefs.edit().putBoolean("enabled", value).apply()

    /** True = right edge (the default), false = left edge. */
    var onRight: Boolean
        get() = prefs.getBoolean("onRight", true)
        set(value) = prefs.edit().putBoolean("onRight", value).apply()

    /** Gap between the dot and the top of the keyboard, in dp. */
    var gapDp: Int
        get() = prefs.getInt("gapDp", DEFAULT_GAP_DP)
        set(value) = prefs.edit().putInt("gapDp", value).apply()

    fun resetPosition() = prefs.edit().remove("onRight").remove("gapDp").apply()

    fun addListener(listener: () -> Unit): Any {
        val l = android.content.SharedPreferences.OnSharedPreferenceChangeListener { _, _ -> listener() }
        prefs.registerOnSharedPreferenceChangeListener(l)
        return l
    }

    fun removeListener(token: Any) {
        prefs.unregisterOnSharedPreferenceChangeListener(token as android.content.SharedPreferences.OnSharedPreferenceChangeListener)
    }

    companion object {
        /** Above a typical message box, so the dot doesn't cover a Send button. */
        const val DEFAULT_GAP_DP = 72

        /** Whether this is the floating-mic edition (only it declares the accessibility service). */
        fun available(context: Context): Boolean = try {
            context.packageManager.getServiceInfo(ComponentName(context, DictationDot::class.java), 0)
            true
        } catch (_: android.content.pm.PackageManager.NameNotFoundException) {
            false
        }

        /** Whether the user has switched on the floating mic in Android's Accessibility settings. */
        fun serviceEnabled(context: Context): Boolean {
            val list = Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
            val ours = ComponentName(context, DictationDot::class.java)
            return list.split(':').any { ComponentName.unflattenFromString(it) == ours }
        }
    }
}
