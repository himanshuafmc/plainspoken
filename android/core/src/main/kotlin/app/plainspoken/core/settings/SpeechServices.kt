package app.plainspoken.core.settings

import kotlinx.serialization.Serializable

/**
 * The speech-to-text services Plainspoken can use. [id] is what settings files store; [keySlot] says which saved
 * key a service uses (both Gemini services share the Gemini key).
 */
enum class SpeechService(
    val id: String,
    val displayName: String,
    val keySlot: String,
    val defaultModel: String,
    val defaultBaseUrl: String,
    /** Can stream audio while the user speaks. */
    val canStream: Boolean,
    /** Accepts compressed (AAC in MP4) audio as well as WAV. */
    val acceptsCompressedAudio: Boolean,
    /** Longest recording one request accepts; longer ones are split into parts. 0 = no practical limit. */
    val maxRequestSeconds: Int,
    /** Returns finished, tidied text itself (Smart mode), so the clean-up step is optional. */
    val cleansItself: Boolean,
    /** Where to get a key. */
    val keyUrl: String,
) {
    GEMINI(
        "gemini", "Gemini — transcribe model", "gemini", TranscriptionSettings.DEFAULT_TRANSCRIBE_MODEL,
        TranscriptionSettings.DEFAULT_API_BASE_URL, canStream = true, acceptsCompressedAudio = false, maxRequestSeconds = 0,
        cleansItself = true, keyUrl = "aistudio.google.com/apikey",
    ),
    GEMINI_GENERAL(
        "gemini-general", "Gemini — general model", "gemini", TranscriptionSettings.DEFAULT_GENERATE_MODEL,
        TranscriptionSettings.DEFAULT_API_BASE_URL, canStream = false, acceptsCompressedAudio = false, maxRequestSeconds = 0,
        cleansItself = true, keyUrl = "aistudio.google.com/apikey",
    ),
    SARVAM(
        "sarvam", "Sarvam (Saaras)", "sarvam", "saaras:v3", "https://api.sarvam.ai",
        canStream = true, acceptsCompressedAudio = true, maxRequestSeconds = 30, cleansItself = false, keyUrl = "dashboard.sarvam.ai",
    ),
    OPENAI(
        "openai", "OpenAI-compatible", "openai", "gpt-4o-mini-transcribe", "https://api.openai.com/v1",
        canStream = false, acceptsCompressedAudio = true, maxRequestSeconds = 600, cleansItself = false, keyUrl = "platform.openai.com/api-keys",
    ),
    DEEPGRAM(
        "deepgram", "Deepgram", "deepgram", "nova-3", "https://api.deepgram.com",
        canStream = true, acceptsCompressedAudio = true, maxRequestSeconds = 0, cleansItself = false, keyUrl = "console.deepgram.com",
    ),
    ELEVENLABS(
        "elevenlabs", "ElevenLabs Scribe", "elevenlabs", "scribe_v2", "https://api.elevenlabs.io",
        canStream = false, acceptsCompressedAudio = true, maxRequestSeconds = 0, cleansItself = false, keyUrl = "elevenlabs.io/app/settings/api-keys",
    ),
    ;

    val isGemini: Boolean get() = keySlot == GEMINI_KEY

    /** The name used in messages ("Sarvam took too long to answer"). */
    val shortName: String
        get() = when (this) {
            GEMINI, GEMINI_GENERAL -> "Gemini"
            SARVAM -> "Sarvam"
            OPENAI -> "the OpenAI-compatible service"
            DEEPGRAM -> "Deepgram"
            ELEVENLABS -> "ElevenLabs"
        }

    companion object {
        const val GEMINI_KEY = "gemini"

        /** Key slot of the OpenAI-compatible clean-up service (it may differ from the speech one). */
        const val CLEANUP_KEY = "cleanup-openai"

        /** Main service plus up to two backups. */
        const val MAX_SERVICES = 3

        fun byId(id: String?): SpeechService? = entries.firstOrNull { it.id == id?.trim() }

        /** Known ids only, no repeats, at most [MAX_SERVICES]; never empty. */
        fun cleanOrder(order: List<String>?): List<String> {
            val ids = order.orEmpty().mapNotNull { byId(it)?.id }.distinct().take(MAX_SERVICES)
            return ids.ifEmpty { listOf(GEMINI.id) }
        }
    }
}

/** Settings of one speech service. The Gemini services keep their models and address in [TranscriptionSettings]. */
@Serializable
class SpeechServiceSettings(
    var model: String = "",
    var apiBaseUrl: String = "",
    /** Stream audio while the user speaks (only for services that can). */
    var streaming: Boolean = true,
    /** Send the vocabulary as recognition hints (ElevenLabs charges 20 % more for it). */
    var vocabularyHints: Boolean = true,
    /** What one hour of audio costs on this service, in [TranscriptionSettings.currency]. 0 = not set. */
    var pricePerHour: Double = 0.0,
    /** A service-specific option; for Sarvam the mode (transcribe, codemix, translit, verbatim). Empty = automatic. */
    var option: String = "",
) {
    internal fun normalize(service: SpeechService) {
        if (!service.isGemini) {
            model = model.trim().ifEmpty { service.defaultModel }
            apiBaseUrl = PlainspokenSettings.normalizeBaseUrl(apiBaseUrl.ifBlank { service.defaultBaseUrl }, service.defaultBaseUrl)
        }

        if (pricePerHour.isNaN() || pricePerHour < 0) pricePerHour = 0.0
        pricePerHour = pricePerHour.coerceAtMost(100_000.0)
        option = option.trim().take(40)
    }
}

/** Per-service settings, one property per [SpeechService] so the file stays readable. */
@Serializable
class ServiceSettingsMap(
    var gemini: SpeechServiceSettings = SpeechServiceSettings(),
    var geminiGeneral: SpeechServiceSettings = SpeechServiceSettings(),
    var sarvam: SpeechServiceSettings = SpeechServiceSettings(pricePerHour = 30.0),
    var openai: SpeechServiceSettings = SpeechServiceSettings(),
    var deepgram: SpeechServiceSettings = SpeechServiceSettings(),
    var elevenlabs: SpeechServiceSettings = SpeechServiceSettings(vocabularyHints = false),
) {
    operator fun get(service: SpeechService): SpeechServiceSettings = when (service) {
        SpeechService.GEMINI -> gemini
        SpeechService.GEMINI_GENERAL -> geminiGeneral
        SpeechService.SARVAM -> sarvam
        SpeechService.OPENAI -> openai
        SpeechService.DEEPGRAM -> deepgram
        SpeechService.ELEVENLABS -> elevenlabs
    }

    internal fun normalize() = SpeechService.entries.forEach { this[it].normalize(it) }
}

/** The Smart-mode clean-up step for services that return raw text. */
@Serializable
class CleanupSettings(
    var provider: CleanupProvider = CleanupProvider.GEMINI,
    var geminiModel: String = DEFAULT_GEMINI_MODEL,
    var openaiModel: String = DEFAULT_OPENAI_MODEL,
    var openaiBaseUrl: String = DEFAULT_OPENAI_BASE_URL,
    /** Also clean the Gemini services' text (they already tidy it themselves). */
    var alsoGemini: Boolean = false,
) {
    internal fun normalize() {
        geminiModel = geminiModel.trim().removePrefix("models/").ifEmpty { DEFAULT_GEMINI_MODEL }
        openaiModel = openaiModel.trim().ifEmpty { DEFAULT_OPENAI_MODEL }
        openaiBaseUrl = PlainspokenSettings.normalizeBaseUrl(openaiBaseUrl, DEFAULT_OPENAI_BASE_URL)
    }

    companion object {
        const val DEFAULT_GEMINI_MODEL = "gemini-3.5-flash"
        const val DEFAULT_OPENAI_MODEL = "gpt-4o-mini"
        const val DEFAULT_OPENAI_BASE_URL = "https://api.openai.com/v1"
    }
}

/** A spoken shortcut: saying [trigger] types [text]. */
@Serializable
class Snippet(var trigger: String = "", var text: String = "")

object SnippetCleaner {
    const val MAX_SNIPPETS = 100
    const val MAX_TRIGGER_LENGTH = 60
    const val MAX_TEXT_LENGTH = 2000

    private val spaces = Regex("\\s+")

    /** Trims, drops empty or over-long entries and repeated triggers, caps the list. */
    fun clean(snippets: List<Snippet>?): List<Snippet> {
        val seen = HashSet<String>()
        val result = ArrayList<Snippet>()
        for (s in snippets.orEmpty()) {
            val trigger = s.trigger.trim().replace(spaces, " ")
            val text = s.text.trim()
            if (trigger.isEmpty() || text.isEmpty() || trigger.length > MAX_TRIGGER_LENGTH) continue
            if (!seen.add(trigger.lowercase())) continue
            result.add(Snippet(trigger, text.take(MAX_TEXT_LENGTH)))
            if (result.size == MAX_SNIPPETS) break
        }

        return result
    }
}
