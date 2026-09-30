package app.plainspoken

import android.Manifest
import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.InputMethodManager
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import app.plainspoken.core.AppInfo
import app.plainspoken.core.transcription.KeyTester
import app.plainspoken.keyboard.PlainspokenKeyboard
import app.plainspoken.ui.Icon
import app.plainspoken.ui.IconView
import app.plainspoken.ui.Ui
import app.plainspoken.ui.dp
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * Home and first-run screen: a short checklist (key, notice, microphone, keyboard on, keyboard chosen)
 * that ticks itself off, then a box to try dictation and links to History and Settings.
 */
class MainActivity : Activity() {
    private val scope = MainScope()
    private lateinit var services: Services
    private lateinit var ui: Ui
    private lateinit var content: LinearLayout
    private var shownState: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        services = PlainspokenApp.services(this)
        ui = Ui(this)
        val (root, column) = ui.screen(this, AppInfo.NAME, showBack = false)
        content = column
        setContentView(root)
        handleIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // The keyboard picker is a dialog over this screen: refresh when it closes.
        if (hasFocus) refresh()
    }

    /** Rebuilds the screen only when a setup step changed, so the "Try it" box keeps its text. */
    private fun refresh() {
        val state = listOf(
            services.hasApiKey(),
            services.settings().local.firstRunCompleted,
            checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED,
            keyboardEnabled(),
            keyboardChosen(),
            services.pending.count,
        ).joinToString()
        if (state != shownState) render()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun handleIntent(intent: Intent?) {
        if (intent?.getBooleanExtra(EXTRA_ASK_MICROPHONE, false) == true) askMicrophone()
    }

    private fun render() {
        content.removeAllViews()
        shownState = null
        header()
        val keyOk = services.hasApiKey()
        val noticeOk = services.settings().local.firstRunCompleted
        val micOk = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        val enabledOk = keyboardEnabled()
        val chosenOk = keyboardChosen()
        val allOk = keyOk && noticeOk && micOk && enabledOk && chosenOk

        if (allOk) {
            val card = ui.card(content)
            ui.heading(card, "You're all set ✓")
            ui.text(card, "In any app, tap a text box, make sure the Plainspoken keyboard is showing, tap the mic and speak. Tap ✓ when you're done.", muted = true)
        } else {
            val card = ui.card(content)
            ui.heading(card, "Set up in a minute")
            step(card, 1, "Get a free Gemini API key", "Sign in at Google AI Studio and tap Create API key, then copy it.", keyOk) {
                ui.button(it, "Open Google AI Studio") { open("https://aistudio.google.com/apikey") }
            }
            step(card, 2, "Paste your key", "It is stored encrypted on this phone only.", keyOk) { keyEditor(it) }
            step(card, 3, "Read this once", null, noticeOk) { notice(it) }
            step(card, 4, "Allow the microphone", "Only used while you dictate.", micOk) {
                ui.button(it, "Allow microphone") { askMicrophone() }
            }
            step(card, 5, "Turn on the Plainspoken keyboard", "In the list, switch on Plainspoken. Android shows a standard warning for every keyboard — Plainspoken only sends your voice to Google when you tap the mic.", enabledOk) {
                ui.button(it, "Open keyboard settings") { startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)) }
            }
            step(card, 6, "Choose it", "Pick Plainspoken. Later, switch keyboards with the 🌐 key or the keyboard button at the bottom of the screen.", chosenOk) {
                ui.button(it, "Choose keyboard") { getSystemService(InputMethodManager::class.java)?.showInputMethodPicker() }
            }
        }

        val tryCard = ui.card(content)
        ui.heading(tryCard, "Try it here")
        ui.text(tryCard, "Tap the box, then the mic on the Plainspoken keyboard. Say something like “Please add milk, eggs and bread to the list.”", muted = true)
        ui.edit(tryCard, "Your words appear here", multiLine = true).apply { minLines = 3 }

        val more = ui.card(content)
        val pending = services.pending.count
        if (pending > 0) ui.text(more, "$pending recording${if (pending == 1) "" else "s"} waiting to be transcribed — open Recent transcripts to retry.")
        ui.button(more, "Recent transcripts", primary = false) { startActivity(Intent(this, HistoryActivity::class.java)) }
        ui.button(more, "Settings", primary = false) { startActivity(Intent(this, SettingsActivity::class.java)) }
        ui.text(more, "Plainspoken ${AppInfo.version} · free, open-source software (GPL-3.0) · github.com/himanshuafmc/plainspoken", muted = true, sizeSp = 12f)
        shownState = listOf(keyOk, noticeOk, micOk, enabledOk, chosenOk, pending).joinToString()
    }

    private fun header() {
        val row = ui.row().apply { setPadding(0, 0, 0, dp(12)) }
        row.addView(IconView(this, Icon.MIC, ui.palette.brand, 28f), LinearLayout.LayoutParams(dp(44), dp(44)))
        val texts = ui.column()
        texts.addView(TextView(this).apply {
            text = "Speak naturally. Get clean text anywhere."
            setTextColor(ui.palette.text)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 17f)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        })
        texts.addView(TextView(this).apply {
            text = "English, Hindi and Hinglish — in any app."
            setTextColor(ui.palette.muted)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
        })
        row.addView(texts, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = dp(8) })
        content.addView(row)
    }

    /** One checklist step: a number (or ✓), title, note and, while not done, its controls. */
    private fun step(parent: LinearLayout, number: Int, title: String, note: String?, done: Boolean, controls: (LinearLayout) -> Unit) {
        val row = ui.row().apply {
            gravity = Gravity.TOP
            setPadding(0, dp(8), 0, dp(8))
        }

        row.addView(TextView(this).apply {
            text = if (done) "✓" else number.toString()
            gravity = Gravity.CENTER
            setTextColor(if (done) ui.palette.onBrand else ui.palette.brand)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            typeface = Typeface.DEFAULT_BOLD
            background = android.graphics.drawable.GradientDrawable().apply {
                shape = android.graphics.drawable.GradientDrawable.OVAL
                if (done) setColor(ui.palette.brand) else setStroke(dp(2), ui.palette.brand)
            }
        }, LinearLayout.LayoutParams(dp(28), dp(28)).apply { marginEnd = dp(12) })

        val body = ui.column()
        body.addView(TextView(this).apply {
            text = title
            setTextColor(if (done) ui.palette.muted else ui.palette.text)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
        })

        if (!done) {
            if (note != null) ui.text(body, note, muted = true, sizeSp = 14f)
            controls(body)
        }

        row.addView(body, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        parent.addView(row)
    }

    private fun keyEditor(parent: LinearLayout) {
        val field: EditText = ui.edit(parent, "Paste the key here", secret = true)
        val result = ui.text(parent, "", muted = true, sizeSp = 14f).apply { visibility = View.GONE }
        ui.button(parent, "Test and save") {
            val key = field.text.toString().trim()
            result.visibility = View.VISIBLE
            result.text = "Checking…"
            scope.launch {
                val s = services.settings().transcription
                val test = KeyTester.test(services.http, s.apiBaseUrl, s.transcribeModel, key, services.log)
                result.text = test.message
                result.setTextColor(if (test.ok) ui.palette.brand else ui.palette.danger)
                if (test.ok) {
                    services.setApiKey(key)
                    Toast.makeText(this@MainActivity, "Key saved", Toast.LENGTH_SHORT).show()
                    render()
                }
            }
        }
    }

    private fun notice(parent: LinearLayout) {
        ui.text(parent, NOTICE)
        val box = CheckBox(this).apply {
            text = "I understand"
            setTextColor(ui.palette.text)
            buttonTintList = android.content.res.ColorStateList.valueOf(ui.palette.brand)
        }

        parent.addView(box)
        ui.button(parent, "Continue") {
            if (!box.isChecked) {
                Toast.makeText(this, "Please tick “I understand” first", Toast.LENGTH_SHORT).show()
            } else {
                services.update { it.local.firstRunCompleted = true }
                render()
            }
        }
    }

    private fun askMicrophone() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) return
        if (shouldShowRequestPermissionRationale(Manifest.permission.RECORD_AUDIO) || !askedBefore()) {
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQUEST_MIC)
        } else {
            // Android won't ask again after "Don't allow" twice: send the user to the app's settings page.
            Toast.makeText(this, "Turn on Microphone under Permissions", Toast.LENGTH_LONG).show()
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
        }

        getPreferences(MODE_PRIVATE).edit().putBoolean("askedMic", true).apply()
    }

    private fun askedBefore() = getPreferences(MODE_PRIVATE).getBoolean("askedMic", false)

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        render()
    }

    private fun keyboardEnabled(): Boolean {
        val imm = getSystemService(InputMethodManager::class.java) ?: return false
        return imm.enabledInputMethodList.any { it.packageName == packageName }
    }

    private fun keyboardChosen(): Boolean {
        val current = Settings.Secure.getString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD) ?: return false
        return ComponentName.unflattenFromString(current)?.className == PlainspokenKeyboard::class.java.name
    }

    private fun open(url: String) {
        try {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
        } catch (_: Exception) {
            Toast.makeText(this, url, Toast.LENGTH_LONG).show()
        }
    }

    companion object {
        const val EXTRA_ASK_MICROPHONE = "askMicrophone"
        private const val REQUEST_MIC = 1

        const val NOTICE = "This app uses Google's free tier. Google may use your recordings to improve its products. " +
            "Do not dictate official, confidential or classified information."
    }
}
