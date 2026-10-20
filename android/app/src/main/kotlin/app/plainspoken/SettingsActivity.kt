package app.plainspoken

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.View
import android.widget.LinearLayout
import android.widget.Toast
import app.plainspoken.core.AppInfo
import app.plainspoken.core.settings.CleanupProvider
import app.plainspoken.core.settings.CleanupSettings
import app.plainspoken.core.settings.HindiScript
import app.plainspoken.core.settings.LanguagePreset
import app.plainspoken.core.settings.LanguagePresets
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SeedVocabulary
import app.plainspoken.core.settings.SettingsJson
import app.plainspoken.core.settings.Snippet
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.settings.TranscriptionMode
import app.plainspoken.core.settings.VocabularyCleaner
import app.plainspoken.core.transcription.KeyTester
import app.plainspoken.dot.DotPrefs
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

    override fun onResume() {
        super.onResume()
        render() // a service screen may have changed keys or settings
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun s(): PlainspokenSettings = services.settings()

    private fun render() {
        content.removeAllViews()
        floatingMicSection()
        servicesSection()
        cleanupSection()
        speakingSection()
        vocabularySection()
        snippetsSection()
        optionsSection()
        advancedSection()
        statsSection()
        backupSection()
        aboutSection()
    }

    // --- Floating mic ---

    private fun floatingMicSection() {
        val card = ui.card(content)
        ui.heading(card, "Floating mic")
        val prefs = DotPrefs(this)
        if (DotPrefs.serviceEnabled(this)) {
            ui.text(card, "On: a small mic appears next to your keyboard in any app. Tap it to start and again to finish, or hold it " +
                "while you speak. Drag it to move it.", muted = true, sizeSp = 14f)
            ui.toggle(card, "Show the floating mic", "Turn off to hide it without changing Accessibility settings.", prefs.enabled) { v ->
                prefs.enabled = v
            }
            ui.button(card, "Move it back to the right side", primary = false) {
                prefs.resetPosition()
                Toast.makeText(this, "Moved back", Toast.LENGTH_SHORT).show()
            }
        } else {
            ui.text(card, "Off. Switch on “Plainspoken floating mic” in Accessibility settings to dictate with your usual keyboard, " +
                "without switching keyboards.", muted = true, sizeSp = 14f)
        }

        ui.button(card, "Accessibility settings", primary = false) {
            try {
                startActivity(Intent(android.provider.Settings.ACTION_ACCESSIBILITY_SETTINGS))
            } catch (_: Exception) {
                Toast.makeText(this, "Open Settings → Accessibility", Toast.LENGTH_LONG).show()
            }
        }
    }

    // --- Speech services ---

    private fun servicesSection() {
        val card = ui.card(content)
        ui.heading(card, "Speech services")
        ui.text(
            card,
            "Plainspoken uses the main service. If it fails, runs out of credit or is slow, the next one takes over by itself.",
            muted = true,
        )

        val order = s().transcription.orderedServices()
        order.forEachIndexed { index, service ->
            ui.divider(card)
            val label = if (index == 0) "Main" else "Backup $index"
            val keyState = if (services.keyFor(service).isNullOrBlank()) "no key yet — tap Set up" else "key saved"
            ui.text(card, "$label: ${service.displayName}")
            ui.text(card, keyState, muted = true, sizeSp = 13f)
            val row = ui.row()
            card.addView(row)
            ui.button(row, "Set up", primary = services.keyFor(service).isNullOrBlank()) { openService(service) }
            if (index > 0) {
                row.addView(android.widget.Space(this), LinearLayout.LayoutParams(dp(8), 1))
                ui.button(row, "Move up", primary = false) { move(service, -1) }
            }

            if (order.size > 1) {
                row.addView(android.widget.Space(this), LinearLayout.LayoutParams(dp(8), 1))
                ui.button(row, "Remove", primary = false) {
                    services.update { it.transcription.serviceOrder = it.transcription.serviceOrder - service.id }
                    render()
                }
            }
        }

        if (order.size < SpeechService.MAX_SERVICES) {
            ui.divider(card)
            ui.button(card, if (order.size == 1) "Add a backup service" else "Add another backup", primary = false) { pickService() }
        }
    }

    private fun pickService() {
        val available = SpeechService.entries.filter { it.id !in s().transcription.serviceOrder }
        AlertDialog.Builder(this)
            .setTitle("Add a service")
            .setItems(available.map { it.displayName }.toTypedArray()) { _, which ->
                val service = available[which]
                services.update { it.transcription.serviceOrder = it.transcription.serviceOrder + service.id }
                if (services.keyFor(service).isNullOrBlank()) openService(service) else render()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun move(service: SpeechService, by: Int) {
        services.update {
            val list = it.transcription.serviceOrder.toMutableList()
            val i = list.indexOf(service.id)
            val j = (i + by).coerceIn(0, list.size - 1)
            if (i >= 0 && i != j) {
                list.removeAt(i)
                list.add(j, service.id)
            }

            it.transcription.serviceOrder = list
        }
        render()
    }

    private fun openService(service: SpeechService) {
        startActivity(Intent(this, ServiceActivity::class.java).putExtra(ServiceActivity.EXTRA_SERVICE, service.id))
    }

    // --- Clean-up ---

    private fun cleanupSection() {
        val card = ui.card(content)
        val c = s().transcription.cleanup
        ui.heading(card, "Clean-up")
        ui.text(
            card,
            "In Smart style, raw text from Sarvam, OpenAI-compatible services, Deepgram or ElevenLabs is tidied up: " +
                "“um”s and repeats removed, punctuation and grammar fixed, your corrections applied. Your words and language stay as spoken.",
            muted = true,
        )
        ui.choice(
            card,
            listOf(
                CleanupProvider.GEMINI to "Gemini (recommended — uses your Gemini key)",
                CleanupProvider.OPENAI to "An OpenAI-compatible service",
                CleanupProvider.OFF to "Off — only remove “um”, “uh” and similar sounds",
            ),
            c.provider,
        ) { v ->
            services.update { it.transcription.cleanup.provider = v }
            render()
        }

        when (c.provider) {
            CleanupProvider.GEMINI -> {
                if (!services.hasKey(SpeechService.GEMINI_KEY)) {
                    ui.text(card, "Needs a Gemini key: add one under Speech services → Gemini (you don't have to use Gemini for speech).", muted = true, sizeSp = 13f)
                }

                val model = ui.edit(card, "Gemini model", c.geminiModel)
                ui.button(card, "Save model", primary = false) {
                    services.update { it.transcription.cleanup.geminiModel = model.text.toString() }
                    Toast.makeText(this, "Saved", Toast.LENGTH_SHORT).show()
                }
            }
            CleanupProvider.OPENAI -> openAiCleanup(card, c)
            CleanupProvider.OFF -> Unit
        }

        ui.toggle(
            card,
            "Also clean up Gemini's text",
            "Gemini already tidies its own text. Turn on for the same clean-up everywhere (a second or so slower).",
            c.alsoGemini,
        ) { v -> services.update { it.transcription.cleanup.alsoGemini = v } }
    }

    private fun openAiCleanup(card: LinearLayout, c: CleanupSettings) {
        val base = ui.edit(card, "Address (for example https://api.openai.com/v1)", c.openaiBaseUrl)
        val model = ui.edit(card, "Model", c.openaiModel)
        val has = services.hasKey(SpeechService.CLEANUP_KEY)
        ui.text(card, if (has) "A key is saved. Paste a new one to replace it." else "Paste the service's API key.", muted = true, sizeSp = 13f)
        val key = ui.edit(card, "API key", secret = true)
        val result = ui.text(card, "", muted = true, sizeSp = 14f).apply { visibility = View.GONE }
        ui.button(card, "Test and save") {
            services.update {
                it.transcription.cleanup.openaiBaseUrl = base.text.toString()
                it.transcription.cleanup.openaiModel = model.text.toString()
            }

            val typed = key.text.toString().trim()
            val toTest = typed.ifEmpty { services.key(SpeechService.CLEANUP_KEY).orEmpty() }
            result.visibility = View.VISIBLE
            result.text = "Checking…"
            scope.launch {
                val test = KeyTester.testCleanup(services.http, s(), toTest, services.log)
                result.text = test.message
                result.setTextColor(if (test.ok) ui.palette.brand else ui.palette.danger)
                if (test.ok && typed.isNotEmpty()) {
                    services.setKey(SpeechService.CLEANUP_KEY, typed)
                    key.setText("")
                }
            }
        }

        if (has) ui.button(card, "Remove key", primary = false) {
            services.setKey(SpeechService.CLEANUP_KEY, null)
            render()
        }
    }

    // --- Languages and style ---

    private fun speakingSection() {
        val card = ui.card(content)
        ui.heading(card, "Languages")
        ui.choice(card, LanguagePreset.entries.map { it to LanguagePresets.displayName(it) }, s().transcription.languages) { v ->
            services.update { it.transcription.languages = v }
        }

        ui.divider(card)
        ui.heading(card, "Hindi words written in")
        ui.choice(
            card,
            listOf(
                HindiScript.ROMAN to "Roman letters — “kal meeting hai” (Hinglish style)",
                HindiScript.DEVANAGARI to "Devanagari — “कल meeting है”",
                HindiScript.AS_SPOKEN to "Whatever the service gives",
            ),
            s().transcription.hindiScript,
        ) { v -> services.update { it.transcription.hindiScript = v } }

        ui.divider(card)
        ui.heading(card, "Style")
        ui.choice(
            card,
            listOf(
                TranscriptionMode.SMART to "Smart — removes “um”s and repeats, fixes punctuation and grammar",
                TranscriptionMode.VERBATIM to "Verbatim — every word exactly as spoken",
            ),
            s().transcription.mode,
        ) { v -> services.update { it.transcription.mode = v } }
    }

    // --- Vocabulary and snippets ---

    private fun vocabularySection() {
        val card = ui.card(content)
        ui.heading(card, "Vocabulary")
        ui.text(
            card,
            "Names and special words to spell correctly, one per line (up to 1,000). Kept on this phone only. " +
                "When you fix a word right after dictating, the keyboard offers to add it here.",
            muted = true,
        )
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

    private fun snippetsSection() {
        val card = ui.card(content)
        ui.heading(card, "Snippets")
        ui.text(
            card,
            "Say a phrase, get saved text. For example say “insert my address” to type your full address. " +
                "Pick phrases you wouldn't say otherwise.",
            muted = true,
        )

        for (snippet in s().snippets) {
            ui.divider(card)
            ui.text(card, "“${snippet.trigger}”")
            ui.text(card, snippet.text.take(140) + if (snippet.text.length > 140) "…" else "", muted = true, sizeSp = 13f)
            ui.button(card, "Delete", primary = false) {
                services.update { s -> s.snippets = s.snippets.filter { it.trigger != snippet.trigger } }
                render()
            }
        }

        ui.divider(card)
        val trigger = ui.edit(card, "When I say… (for example: insert my address)")
        val text = ui.edit(card, "…type this", multiLine = true)
        ui.button(card, "Add snippet", primary = false) {
            val t = trigger.text.toString().trim()
            val body = text.text.toString().trim()
            if (t.isEmpty() || body.isEmpty()) {
                Toast.makeText(this, "Fill in both boxes", Toast.LENGTH_SHORT).show()
                return@button
            }

            services.update { s -> s.snippets = s.snippets.filter { !it.trigger.equals(t, ignoreCase = true) } + Snippet(t, body) }
            render()
        }
    }

    // --- Options ---

    private fun optionsSection() {
        val card = ui.card(content)
        ui.heading(card, "Options")
        ui.text(card, "On the keyboard and the floating mic: tap to start and tap again to finish — or hold while you speak and let go.", muted = true, sizeSp = 13f)
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
        ui.heading(card, "Speed")
        ui.text(card, "If the service in use hasn't answered after…", muted = true)
        ui.choice(
            card,
            listOf(3 to "3 seconds — ask the backup as well", 4 to "4 seconds (recommended)", 6 to "6 seconds", 10 to "10 seconds", 0 to "Never — only switch after an error"),
            listOf(3, 4, 6, 10, 0).minBy { kotlin.math.abs(it - s().transcription.hedgeSeconds) },
        ) { v -> services.update { it.transcription.hedgeSeconds = v } }
        ui.text(card, "Asking two services costs a little more that time; the first answer is used. Long recordings get extra time.", muted = true, sizeSp = 13f)
        ui.toggle(
            card,
            "Compress audio before sending",
            "About eight times smaller uploads for Sarvam, OpenAI-compatible services, Deepgram and ElevenLabs. Gemini always gets the original.",
            s().transcription.compressAudio,
        ) { v -> services.update { it.transcription.compressAudio = v } }

        ui.divider(card)
        ui.heading(card, "Advanced")
        val timeout = ui.edit(card, "Longest wait for one request, in seconds", s().transcription.requestTimeoutSeconds.toString())
        val currency = ui.edit(card, "Currency for prices (for example ₹ or $)", s().transcription.currency)
        ui.button(card, "Save", primary = false) {
            services.update {
                it.transcription.requestTimeoutSeconds = timeout.text.toString().trim().toIntOrNull() ?: 60
                it.transcription.currency = currency.text.toString()
            }
            Toast.makeText(this, "Saved", Toast.LENGTH_SHORT).show()
        }
    }

    private fun statsSection() {
        val card = ui.card(content)
        ui.heading(card, "Speed and cost")
        ui.text(card, "How fast each service has been for you, how often it failed, and what it has cost this month.", muted = true)
        ui.button(card, "Open speed and cost", primary = false) { startActivity(Intent(this, StatsActivity::class.java)) }
    }

    private fun backupSection() {
        val card = ui.card(content)
        ui.heading(card, "Share settings")
        ui.text(card, "Export saves your settings, vocabulary and snippets to a file (never your keys). The file also works in Plainspoken for Windows.", muted = true)
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
        val used = (s().transcription.orderedServices().map { it.shortName.removePrefix("the ") } +
            if (s().transcription.cleanup.provider == CleanupProvider.OFF) emptyList() else listOf("the clean-up service")).distinct()
        ui.text(
            card,
            "Your voice goes only to the services you chose (${used.joinToString(", ")}), using your own keys. Plainspoken has no servers and no tracking. " +
                "Free tiers may let the provider use recordings to improve its products — don't dictate confidential information.",
            muted = true,
        )
        ui.button(card, "Export the log (for a bug report)", primary = false) {
            startActivityForResult(
                Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("text/plain")
                    .putExtra(Intent.EXTRA_TITLE, "plainspoken-log.txt"),
                REQUEST_LOG,
            )
        }
        ui.text(card, "Logs hold timings and error codes, never your words or your keys.", muted = true, sizeSp = 13f)
        ui.button(card, "Source code and help", primary = false) {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://github.com/himanshuafmc/plainspoken")))
        }
        ui.text(card, "${AppInfo.NAME} ${AppInfo.version}. Free software under the GNU GPL v3.0, with no warranty.", muted = true, sizeSp = 13f)
        ui.text(card, "Uses OkHttp, Okio, AndroidX Startup, Kotlin, kotlinx.coroutines and kotlinx.serialization (Apache License 2.0).", muted = true, sizeSp = 13f)
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
