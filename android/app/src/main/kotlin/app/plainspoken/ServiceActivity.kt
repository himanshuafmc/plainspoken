package app.plainspoken

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.text.InputType
import android.view.View
import android.widget.LinearLayout
import android.widget.Toast
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.settings.TranscriptionSettings
import app.plainspoken.core.transcription.KeyTester
import app.plainspoken.ui.Ui
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** Set up one speech service: its key (with a free test), model, address, streaming, vocabulary hints and price. */
class ServiceActivity : Activity() {
    private val scope = MainScope()
    private lateinit var services: Services
    private lateinit var ui: Ui
    private lateinit var content: LinearLayout
    private lateinit var service: SpeechService

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        services = PlainspokenApp.services(this)
        service = SpeechService.byId(intent.getStringExtra(EXTRA_SERVICE)) ?: SpeechService.GEMINI
        ui = Ui(this)
        val (root, column) = ui.screen(this, service.displayName, showBack = true)
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
        keyCard()
        modelCard()
        priceCard()
    }

    private fun keyCard() {
        val card = ui.card(content)
        ui.heading(card, "API key")
        ui.text(card, about(service), muted = true)
        val has = services.keyFor(service) != null
        val shared = if (service.isGemini) " Both Gemini choices use the same key." else ""
        ui.text(card, (if (has) "A key is saved (encrypted on this phone). Paste a new one to replace it." else "No key saved yet.") + shared, muted = true, sizeSp = 13f)
        ui.button(card, "Get a key at ${service.keyUrl}", primary = false) { open("https://${service.keyUrl}") }
        val field = ui.edit(card, "Paste the key", secret = true)
        val result = ui.text(card, "", muted = true, sizeSp = 14f).apply { visibility = View.GONE }
        ui.button(card, "Test and save") {
            val typed = field.text.toString().trim()
            val toTest = typed.ifEmpty { services.keyFor(service).orEmpty() }
            result.visibility = View.VISIBLE
            result.text = "Checking…"
            scope.launch {
                val test = KeyTester.test(services.http, service, s(), toTest, services.log)
                result.text = test.message
                result.setTextColor(if (test.ok) ui.palette.brand else ui.palette.danger)
                if (test.ok && typed.isNotEmpty()) {
                    services.setKey(service.keySlot, typed)
                    field.setText("")
                }
            }
        }

        if (has) {
            ui.button(card, "Remove key", primary = false) {
                services.setKey(service.keySlot, null)
                render()
            }
        }
    }

    private fun modelCard() {
        val card = ui.card(content)
        val t = s().transcription
        val config = t.services[service]
        ui.heading(card, "Model")
        ui.text(card, modelHint(service), muted = true, sizeSp = 13f)
        val model = ui.edit(card, "Model", t.modelOf(service))
        val live = if (service == SpeechService.GEMINI) ui.edit(card, "Live (streaming) model", t.liveModel) else null
        ui.text(card, "Address — change only for a different server or region.", muted = true, sizeSp = 13f)
        val base = ui.edit(card, "API address", t.baseUrlOf(service))

        if (service == SpeechService.SARVAM) {
            ui.divider(card)
            ui.heading(card, "Output mode")
            ui.choice(
                card,
                listOf(
                    "" to "Automatic (follows the Hindi script setting)",
                    "translit" to "translit — everything in Roman letters",
                    "codemix" to "codemix — English in English, Hindi in Devanagari",
                    "transcribe" to "transcribe — each language in its own script",
                ),
                config.option,
            ) { v -> services.update { it.transcription.services.sarvam.option = v } }
        }

        if (service.canStream) {
            ui.divider(card)
            val on = if (service == SpeechService.GEMINI) t.liveStreaming else config.streaming
            ui.toggle(
                card,
                "Stream while you speak (faster)",
                "Sends your voice as you speak, so the text is ready moments after you stop. Used when this is the main service; if streaming fails the normal way is used.",
                on,
            ) { v ->
                services.update {
                    if (service == SpeechService.GEMINI) it.transcription.liveStreaming = v else it.transcription.services[service].streaming = v
                }
            }
        }

        if (!service.isGemini) {
            ui.toggle(card, "Send my vocabulary as hints", hintNote(service), config.vocabularyHints) { v ->
                services.update { it.transcription.services[service].vocabularyHints = v }
            }
        }

        ui.button(card, "Save") {
            services.update {
                val tr = it.transcription
                when (service) {
                    SpeechService.GEMINI -> {
                        tr.transcribeModel = model.text.toString()
                        tr.liveModel = live?.text?.toString().orEmpty()
                        tr.apiBaseUrl = base.text.toString()
                    }
                    SpeechService.GEMINI_GENERAL -> {
                        tr.generateModel = model.text.toString()
                        tr.apiBaseUrl = base.text.toString()
                    }
                    else -> {
                        tr.services[service].model = model.text.toString()
                        tr.services[service].apiBaseUrl = base.text.toString()
                    }
                }
            }
            Toast.makeText(this, "Saved", Toast.LENGTH_SHORT).show()
            render()
        }

        ui.button(card, "Reset to defaults", primary = false) {
            services.update {
                val tr = it.transcription
                when (service) {
                    SpeechService.GEMINI -> {
                        tr.transcribeModel = TranscriptionSettings.DEFAULT_TRANSCRIBE_MODEL
                        tr.liveModel = TranscriptionSettings.DEFAULT_LIVE_MODEL
                        tr.apiBaseUrl = TranscriptionSettings.DEFAULT_API_BASE_URL
                    }
                    SpeechService.GEMINI_GENERAL -> {
                        tr.generateModel = TranscriptionSettings.DEFAULT_GENERATE_MODEL
                        tr.apiBaseUrl = TranscriptionSettings.DEFAULT_API_BASE_URL
                    }
                    else -> {
                        tr.services[service].model = service.defaultModel
                        tr.services[service].apiBaseUrl = service.defaultBaseUrl
                        tr.services[service].option = ""
                    }
                }
            }
            render()
        }
    }

    private fun priceCard() {
        val card = ui.card(content)
        val t = s().transcription
        ui.heading(card, "Price")
        ui.text(card, "What one hour of audio costs you on this service, in ${t.currency}. Used only for the cost estimate on the Speed and cost page.", muted = true, sizeSp = 13f)
        val price = ui.edit(card, "Price per hour of audio (${t.currency})", t.services[service].pricePerHour.takeIf { it > 0 }?.toString().orEmpty())
        price.inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_DECIMAL
        ui.button(card, "Save price", primary = false) {
            services.update { it.transcription.services[service].pricePerHour = price.text.toString().trim().toDoubleOrNull() ?: 0.0 }
            Toast.makeText(this, "Saved", Toast.LENGTH_SHORT).show()
        }
    }

    private fun open(url: String) {
        try {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
        } catch (_: Exception) {
            Toast.makeText(this, url, Toast.LENGTH_LONG).show()
        }
    }

    companion object {
        const val EXTRA_SERVICE = "service"

        fun about(service: SpeechService): String = when (service) {
            SpeechService.GEMINI -> "Google's speech model: accurate, tidies the text itself, can stream. The free tier is small and sometimes busy."
            SpeechService.GEMINI_GENERAL -> "A general Gemini model listening to the audio: fast and usually available; tidies the text itself."
            SpeechService.SARVAM -> "Built for Indian languages and Hinglish; can stream. Paid per second of audio."
            SpeechService.OPENAI -> "Any service with an OpenAI-style transcription API: OpenAI, Groq (address https://api.groq.com/openai/v1), or your own Whisper server."
            SpeechService.DEEPGRAM -> "Very fast, can stream; follows English and Hindi in the same sentence. Paid per minute."
            SpeechService.ELEVENLABS -> "Accurate transcription with Hindi support. Paid per hour of audio."
        }

        fun modelHint(service: SpeechService): String = when (service) {
            SpeechService.GEMINI -> "Default gemini-3.5-transcribe. Change only if Google renames it."
            SpeechService.GEMINI_GENERAL -> "Default gemini-3.5-flash (more accurate) — gemini-3.5-flash-lite is faster."
            SpeechService.SARVAM -> "saaras:v3 (default) or saaras:v4 (newer; also uses your vocabulary)."
            SpeechService.OPENAI -> "For example gpt-4o-mini-transcribe, gpt-4o-transcribe or whisper-1 (OpenAI), whisper-large-v3-turbo (Groq)."
            SpeechService.DEEPGRAM -> "nova-3 (default; uses your vocabulary)."
            SpeechService.ELEVENLABS -> "scribe_v2 (default; leaves out fillers in Smart style) or scribe_v1."
        }

        fun hintNote(service: SpeechService): String = when (service) {
            SpeechService.SARVAM -> "Works with saaras:v4 only (up to 50 words)."
            SpeechService.ELEVENLABS -> "ElevenLabs charges 20 % more when hints are sent."
            SpeechService.DEEPGRAM -> "Works with nova-3 (up to 100 words)."
            else -> "Names are added to the request so they're spelled right."
        }
    }
}
