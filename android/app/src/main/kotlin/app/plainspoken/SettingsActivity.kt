package app.plainspoken

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.View
import android.widget.LinearLayout
import android.widget.Toast
import app.plainspoken.core.AppInfo
import app.plainspoken.core.settings.EngineKind
import app.plainspoken.core.settings.LanguagePreset
import app.plainspoken.core.settings.LanguagePresets
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SeedVocabulary
import app.plainspoken.core.settings.SettingsJson
import app.plainspoken.core.settings.TranscriptionMode
import app.plainspoken.core.settings.TranscriptionSettings
import app.plainspoken.core.settings.VocabularyCleaner
import app.plainspoken.core.transcription.KeyTester
import app.plainspoken.ui.Ui
import app.plainspoken.ui.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** All settings, saved as soon as they change. Export/import uses the same file format as the Windows app. */
class SettingsActivity : Activity() {
    private val scope = MainScope()
    private lateinit var services: Services
    private lateinit var ui: Ui
    private lateinit var content: LinearLayout

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        services = PlainspokenApp.services(this)
        ui = Ui(this)
        val (root, column) = ui.screen(this, "Settings", showBack = true)
        content = column
        setContentView(root)
        render()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun s(): PlainspokenSettings = services.settings()

    private fun render() {
        content.removeAllViews()
        keySection()
        speakingSection()
        vocabularySection()
        optionsSection()
        advancedSection()
        backupSection()
        aboutSection()
    }

    private fun keySection() {
        val card = ui.card(content)
        ui.heading(card, "Gemini API key")
        val has = services.hasApiKey()
        ui.text(card, if (has) "A key is saved (encrypted on this phone). Paste a new one below to replace it." else "No key saved yet. Get a free key at aistudio.google.com/apikey.", muted = true)
        val field = ui.edit(card, "Paste a key", secret = true)
        val result = ui.text(card, "", muted = true, sizeSp = 14f).apply { visibility = View.GONE }
        ui.button(card, "Test and save") {
            val key = field.text.toString().trim()
            result.visibility = View.VISIBLE
            result.text = "Checking…"
            scope.launch {
                val t = s().transcription
                val test = KeyTester.test(services.http, t.apiBaseUrl, t.transcribeModel, key, services.log)
                result.text = test.message
                result.setTextColor(if (test.ok) ui.palette.brand else ui.palette.danger)
                if (test.ok) {
                    services.setApiKey(key)
                    field.setText("")
                }
            }
        }

        if (has) {
            ui.button(card, "Remove key", primary = false) {
                services.setApiKey(null)
                render()
            }
        }
    }

    private fun speakingSection() {
        val card = ui.card(content)
        ui.heading(card, "Languages")
        ui.choice(card, LanguagePreset.entries.map { it to LanguagePresets.displayName(it) }, s().transcription.languages) { v ->
            services.update { it.transcription.languages = v }
        }

        ui.divider(card)
        ui.heading(card, "Style")
        ui.choice(
            card,
            listOf(
                TranscriptionMode.SMART to "Smart — removes “um”s and repeats, adds punctuation",
                TranscriptionMode.VERBATIM to "Verbatim — every word exactly as spoken",
            ),
            s().transcription.mode,
        ) { v -> services.update { it.transcription.mode = v } }
    }

    private fun vocabularySection() {
        val card = ui.card(content)
        ui.heading(card, "Vocabulary")
        ui.text(card, "Names and special words to spell correctly, one per line (up to 1,000). Kept on this phone only.", muted = true)
        val box = ui.edit(card, "One word or name per line", VocabularyCleaner.toText(s().transcription.customVocabulary), multiLine = true)
        val row = ui.row()
        card.addView(row)
        ui.button(row, "Save words") {
            val terms = VocabularyCleaner.fromText(box.text.toString())
            services.update { it.transcription.customVocabulary = terms }
            box.setText(VocabularyCleaner.toText(terms))
            Toast.makeText(this, "${terms.size} words saved", Toast.LENGTH_SHORT).show()
        }
        row.addView(android.widget.Space(this), LinearLayout.LayoutParams(dp(8), 1))
        ui.button(row, "Reset to starter list", primary = false) {
            box.setText(VocabularyCleaner.toText(SeedVocabulary.terms))
        }
    }

    private fun optionsSection() {
        val card = ui.card(content)
        ui.heading(card, "Options")
        ui.toggle(card, "Add a space after the text", null, s().insertion.trailingSpace) { v -> services.update { it.insertion.trailingSpace = v } }
        ui.toggle(card, "Sounds and vibration", "A tap when listening starts and a soft chirp when it stops", s().recording.sounds) { v ->
            services.update { it.recording.sounds = v }
        }
        ui.toggle(card, "Keep recent transcripts", "The last 20, on this phone only. Never kept for incognito text boxes.", s().history.enabled) { v ->
            services.update { it.history.enabled = v }
            if (!v) services.history.clear()
        }

        ui.divider(card)
        ui.heading(card, "Longest recording")
        ui.choice(card, listOf(60 to "1 minute", 180 to "3 minutes", 300 to "5 minutes", 600 to "10 minutes", 1200 to "20 minutes"), nearest(s().recording.maxSeconds)) { v ->
            services.update { it.recording.maxSeconds = v }
        }
    }

    private fun nearest(seconds: Int): Int = listOf(60, 180, 300, 600, 1200).minBy { kotlin.math.abs(it - seconds) }

    private fun advancedSection() {
        val card = ui.card(content)
        ui.heading(card, "Advanced")
        ui.toggle(
            card,
            "Live streaming (faster)",
            "Sends your voice to Google while you speak, so the text is ready moments after you stop. If it fails, the normal way is used automatically.",
            s().transcription.liveStreaming,
        ) { v -> services.update { it.transcription.liveStreaming = v } }
        ui.toggle(
            card,
            "Use the backup engine when Google is busy",
            "When the free limit is reached, try the other model once (faster, a little less accurate).",
            s().transcription.useBackupEngineWhenLimited,
        ) { v -> services.update { it.transcription.useBackupEngineWhenLimited = v } }

        ui.divider(card)
        ui.heading(card, "Speech engine")
        ui.choice(
            card,
            listOf(EngineKind.TRANSCRIBE to "Transcribe model (recommended)", EngineKind.GENERATE to "General model (backup)"),
            s().transcription.engine,
        ) { v -> services.update { it.transcription.engine = v } }

        ui.text(card, "Model names and address — change only if Google renames a model.", muted = true, sizeSp = 13f)
        val transcribe = ui.edit(card, "Transcribe model", s().transcription.transcribeModel)
        val live = ui.edit(card, "Live model", s().transcription.liveModel)
        val generate = ui.edit(card, "General model", s().transcription.generateModel)
        val base = ui.edit(card, "API address", s().transcription.apiBaseUrl)
        val timeout = ui.edit(card, "Timeout in seconds", s().transcription.requestTimeoutSeconds.toString())
        ui.button(card, "Save advanced settings") {
            services.update {
                it.transcription.transcribeModel = transcribe.text.toString()
                it.transcription.liveModel = live.text.toString()
                it.transcription.generateModel = generate.text.toString()
                it.transcription.apiBaseUrl = base.text.toString()
                it.transcription.requestTimeoutSeconds = timeout.text.toString().trim().toIntOrNull() ?: 60
            }
            Toast.makeText(this, "Saved", Toast.LENGTH_SHORT).show()
            render()
        }
        ui.button(card, "Reset advanced settings", primary = false) {
            services.update {
                it.transcription.engine = EngineKind.TRANSCRIBE
                it.transcription.transcribeModel = TranscriptionSettings.DEFAULT_TRANSCRIBE_MODEL
                it.transcription.liveModel = TranscriptionSettings.DEFAULT_LIVE_MODEL
                it.transcription.generateModel = TranscriptionSettings.DEFAULT_GENERATE_MODEL
                it.transcription.apiBaseUrl = TranscriptionSettings.DEFAULT_API_BASE_URL
                it.transcription.requestTimeoutSeconds = 60
                it.transcription.liveStreaming = true
                it.transcription.useBackupEngineWhenLimited = true
            }
            render()
        }
    }

    private fun backupSection() {
        val card = ui.card(content)
        ui.heading(card, "Share settings")
        ui.text(card, "Export saves your settings and vocabulary to a file (never your key). The same file works in Plainspoken for Windows.", muted = true)
        ui.button(card, "Export settings", primary = false) {
            startActivityForResult(
                Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/json")
                    .putExtra(Intent.EXTRA_TITLE, "plainspoken-settings.json"),
                REQUEST_EXPORT,
            )
        }
        ui.button(card, "Import settings", primary = false) {
            startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*"), REQUEST_IMPORT)
        }
    }

    private fun aboutSection() {
        val card = ui.card(content)
        ui.heading(card, "Privacy and help")
        ui.text(
            card,
            "Your voice goes only to Google's Gemini API, using your own key. Plainspoken has no servers and no tracking. " +
                "On Google's free tier Google may use recordings to improve its products — don't dictate confidential information.",
            muted = true,
        )
        ui.button(card, "Export the log (for a bug report)", primary = false) {
            startActivityForResult(
                Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("text/plain")
                    .putExtra(Intent.EXTRA_TITLE, "plainspoken-log.txt"),
                REQUEST_LOG,
            )
        }
        ui.text(card, "Logs hold timings and error codes, never your words or your key.", muted = true, sizeSp = 13f)
        ui.button(card, "Source code and help", primary = false) {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://github.com/himanshuafmc/plainspoken")))
        }
        ui.text(card, "${AppInfo.NAME} ${AppInfo.version}. Free software under the GNU GPL v3.0, with no warranty.", muted = true, sizeSp = 13f)
        ui.text(card, "Uses OkHttp, Okio, Kotlin, kotlinx.coroutines and kotlinx.serialization (Apache License 2.0).", muted = true, sizeSp = 13f)
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        val uri = data?.data
        if (resultCode != RESULT_OK || uri == null) return
        scope.launch {
            val message = try {
                withContext(Dispatchers.IO) {
                    when (requestCode) {
                        REQUEST_EXPORT -> {
                            contentResolver.openOutputStream(uri, "wt")?.use { it.write(SettingsJson.export(s()).toByteArray()) }
                            "Settings exported"
                        }
                        REQUEST_IMPORT -> {
                            val text = contentResolver.openInputStream(uri)?.use { it.readBytes().toString(Charsets.UTF_8) } ?: ""
                            services.replace(SettingsJson.import(text, s()))
                            "Settings imported"
                        }
                        REQUEST_LOG -> {
                            val text = services.log.files().take(3).reversed().joinToString("\n") { it.readText() }
                            contentResolver.openOutputStream(uri, "wt")?.use { it.write(text.toByteArray()) }
                            "Log exported"
                        }
                        else -> null
                    }
                }
            } catch (e: Exception) {
                services.log.warn("file action $requestCode failed: ${e.javaClass.simpleName}")
                if (requestCode == REQUEST_IMPORT) "That file isn't a Plainspoken settings file" else "Couldn't save the file"
            }

            if (message != null) Toast.makeText(this@SettingsActivity, message, Toast.LENGTH_SHORT).show()
            if (requestCode == REQUEST_IMPORT) render()
        }
    }

    private companion object {
        const val REQUEST_EXPORT = 10
        const val REQUEST_IMPORT = 11
        const val REQUEST_LOG = 12
    }
}
