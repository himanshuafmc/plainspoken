package app.plainspoken.core.settings

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import java.net.URI

@Serializable
enum class EngineKind {
    /** gemini-3.5-transcribe via the Interactions API (default). */
    @SerialName("transcribe")
    TRANSCRIBE,

    /** Flash-Lite via generateContent with a clean-up prompt (fallback). */
    @SerialName("generate")
    GENERATE,
}

@Serializable
enum class LanguagePreset {
    @SerialName("englishHindi")
    ENGLISH_HINDI,

    @SerialName("auto")
    AUTO,

    @SerialName("english")
    ENGLISH,
}

@Serializable
enum class TranscriptionMode {
    @SerialName("smart")
    SMART,

    @SerialName("verbatim")
    VERBATIM,
}

/** How Hindi words are written in the final text. */
@Serializable
enum class HindiScript {
    /** Roman letters, the way people type Hinglish ("kal meeting hai"). */
    @SerialName("roman")
    ROMAN,

    @SerialName("devanagari")
    DEVANAGARI,

    /** Whatever the speech service returns. */
    @SerialName("asSpoken")
    AS_SPOKEN,
}

/** Who tidies up raw transcripts in Smart mode. */
@Serializable
enum class CleanupProvider {
    @SerialName("gemini")
    GEMINI,

    /** Any service with an OpenAI-style /chat/completions endpoint. */
    @SerialName("openai")
    OPENAI,

    @SerialName("off")
    OFF,
}

@Serializable
enum class InsertionMethod {
    @SerialName("paste")
    PASTE,

    @SerialName("type")
    TYPE,
}

/**
 * All user settings, in the shared format of docs/SPEC.md §6 and shared/settings.schema.json.
 * A file exported on Windows imports here and the other way round. Windows-only fields (hotkey,
 * mini button) are kept so they survive a round trip. Everything except [local] can be exported.
 */
@Serializable
class PlainspokenSettings {
    var schemaVersion: Int = CURRENT_SCHEMA_VERSION
    var transcription: TranscriptionSettings = TranscriptionSettings()
    var recording: RecordingSettings = RecordingSettings()
    var insertion: InsertionSettings = InsertionSettings()
    var history: HistorySettings = HistorySettings()

    /** Windows only. */
    var hotkey: String = DEFAULT_HOTKEY

    /** Windows only: the small on-screen button. */
    var showMiniButton: Boolean = true

    /** Spoken shortcuts: saying [Snippet.trigger] types [Snippet.text]. */
    var snippets: List<Snippet> = emptyList()

    /** This device only. Never exported. */
    var local: LocalSettings = LocalSettings()

    /** Clamps numbers, fills defaults and cleans the vocabulary. Returns this. */
    fun normalize(): PlainspokenSettings {
        val t = transcription
        if (schemaVersion < 2) migrateFromVersion1(t)
        schemaVersion = CURRENT_SCHEMA_VERSION
        t.transcribeModel = t.transcribeModel.trim().ifEmpty { TranscriptionSettings.DEFAULT_TRANSCRIBE_MODEL }
        t.generateModel = t.generateModel.trim().ifEmpty { TranscriptionSettings.DEFAULT_GENERATE_MODEL }
        t.liveModel = t.liveModel.trim().ifEmpty { TranscriptionSettings.DEFAULT_LIVE_MODEL }
        t.apiBaseUrl = normalizeBaseUrl(t.apiBaseUrl)
        t.requestTimeoutSeconds = t.requestTimeoutSeconds.coerceIn(10, 600)
        t.customVocabulary = VocabularyCleaner.clean(t.customVocabulary)
        t.serviceOrder = SpeechService.cleanOrder(t.serviceOrder)
        // Keep the Windows-only fields in step, so an exported file behaves sensibly there.
        t.engine = if (t.serviceOrder.firstOrNull { it.startsWith("gemini") } == SpeechService.GEMINI_GENERAL.id) EngineKind.GENERATE else EngineKind.TRANSCRIBE
        t.useBackupEngineWhenLimited = SpeechService.GEMINI.id in t.serviceOrder && SpeechService.GEMINI_GENERAL.id in t.serviceOrder
        t.services.normalize()
        t.cleanup.normalize()
        t.hedgeSeconds = t.hedgeSeconds.coerceIn(0, 30)
        t.currency = t.currency.trim().take(4).ifEmpty { "₹" }
        snippets = SnippetCleaner.clean(snippets)
        recording.maxSeconds = recording.maxSeconds.coerceIn(30, 1800)
        hotkey = hotkey.trim().ifEmpty { DEFAULT_HOTKEY }
        return this
    }

    /** Version 1 had two Gemini engines and a "use the backup when limited" switch; they become the service order. */
    private fun migrateFromVersion1(t: TranscriptionSettings) {
        val primary = if (t.engine == EngineKind.GENERATE) SpeechService.GEMINI_GENERAL else SpeechService.GEMINI
        val other = if (primary == SpeechService.GEMINI) SpeechService.GEMINI_GENERAL else SpeechService.GEMINI
        t.serviceOrder = if (t.useBackupEngineWhenLimited) listOf(primary.id, other.id) else listOf(primary.id)
        if (t.generateModel.trim() == OLD_GENERATE_MODEL) t.generateModel = TranscriptionSettings.DEFAULT_GENERATE_MODEL
    }

    companion object {
        const val CURRENT_SCHEMA_VERSION = 2
        const val DEFAULT_HOTKEY = "Ctrl+Alt+Space"

        /** The general model's default before 0.4.0; more accurate gemini-3.5-flash replaces it. */
        private const val OLD_GENERATE_MODEL = "gemini-3.5-flash-lite"

        /** An http(s) address without a trailing slash, or [fallback] when it isn't one. Paths (like /v1) are kept. */
        fun normalizeBaseUrl(url: String?, fallback: String = TranscriptionSettings.DEFAULT_API_BASE_URL): String {
            val raw = url?.trim().orEmpty()
            val uri = try {
                URI(raw)
            } catch (_: Exception) {
                null
            }

            if (uri == null || uri.host.isNullOrEmpty() || (uri.scheme != "https" && uri.scheme != "http")) {
                return fallback
            }

            val port = if (uri.port >= 0) ":${uri.port}" else ""
            return ("${uri.scheme}://${uri.host}$port${uri.rawPath.orEmpty()}").trimEnd('/')
        }
    }
}

@Serializable
class TranscriptionSettings {
    var engine: EngineKind = EngineKind.TRANSCRIBE
    var transcribeModel: String = DEFAULT_TRANSCRIBE_MODEL
    var generateModel: String = DEFAULT_GENERATE_MODEL
    var apiBaseUrl: String = DEFAULT_API_BASE_URL
    var requestTimeoutSeconds: Int = 60
    var languages: LanguagePreset = LanguagePreset.ENGLISH_HINDI
    var mode: TranscriptionMode = TranscriptionMode.SMART

    /** On a 429 from the selected engine, try the other engine once. */
    var useBackupEngineWhenLimited: Boolean = true
    var customVocabulary: List<String> = emptyList()

    /** Stream audio to the live model while the user speaks (on by default); falls back to the normal engine. */
    var liveStreaming: Boolean = true
    var liveModel: String = DEFAULT_LIVE_MODEL

    /** Speech services to use, main first, then up to two backups (ids of [SpeechService]). Unknown ids are dropped. */
    var serviceOrder: List<String> = listOf(SpeechService.GEMINI.id, SpeechService.GEMINI_GENERAL.id)
    var services: ServiceSettingsMap = ServiceSettingsMap()
    var cleanup: CleanupSettings = CleanupSettings()
    var hindiScript: HindiScript = HindiScript.ROMAN

    /**
     * When the service in use hasn't answered after about this many seconds (a little more for long recordings),
     * the next one is asked too and the first answer wins. 0 = only switch after a failure.
     */
    var hedgeSeconds: Int = 4

    /** Send compressed audio (AAC) to services that accept it: much smaller uploads. */
    var compressAudio: Boolean = true

    /** Shown with the cost estimate; prices per service are in this currency. */
    var currency: String = "₹"

    fun modelFor(engine: EngineKind): String = if (engine == EngineKind.TRANSCRIBE) transcribeModel else generateModel

    /** The model a service uses (the Gemini services keep theirs in [transcribeModel] / [generateModel]). */
    fun modelOf(service: SpeechService): String = when (service) {
        SpeechService.GEMINI -> transcribeModel
        SpeechService.GEMINI_GENERAL -> generateModel
        else -> services[service].model
    }

    fun baseUrlOf(service: SpeechService): String = if (service.isGemini) apiBaseUrl else services[service].apiBaseUrl

    /** The services in [serviceOrder], main first. */
    fun orderedServices(): List<SpeechService> = SpeechService.cleanOrder(serviceOrder).mapNotNull { SpeechService.byId(it) }

    companion object {
        const val DEFAULT_TRANSCRIBE_MODEL = "gemini-3.5-transcribe"
        const val DEFAULT_GENERATE_MODEL = "gemini-3.5-flash"
        const val DEFAULT_API_BASE_URL = "https://generativelanguage.googleapis.com"
        const val DEFAULT_LIVE_MODEL = "gemini-3.5-transcribe-live"
    }
}

@Serializable
class RecordingSettings {
    var maxSeconds: Int = 600

    /** Start/stop sounds (and, on Android, a short vibration). */
    var sounds: Boolean = true
}

@Serializable
class InsertionSettings {
    /** Windows only ("paste" or "type"); Android always inserts through the keyboard. */
    var method: InsertionMethod = InsertionMethod.PASTE
    var trailingSpace: Boolean = true
}

@Serializable
class HistorySettings {
    var enabled: Boolean = true
}

@Serializable
class LocalSettings {
    /** The API key encrypted by the platform (Android: an Android Keystore AES key), base64. Never plain text. */
    var apiKeyProtected: String? = null

    /** Keys for the other services, by key slot (see [SpeechService.keySlot]), encrypted like [apiKeyProtected]. */
    var apiKeysProtected: Map<String, String> = emptyMap()

    /** Windows only: capture device id. */
    var microphoneId: String? = null

    /** Windows only. */
    var startWithWindows: Boolean = false
    var firstRunCompleted: Boolean = false

    /** Windows only: where the mini button was dragged to. */
    var miniButtonPosition: String? = null
}

object LanguagePresets {
    /** BCP-47 codes sent as language_codes; empty means auto-detect (field omitted). */
    fun codes(preset: LanguagePreset): List<String> = when (preset) {
        LanguagePreset.ENGLISH_HINDI -> listOf("en-IN", "hi-IN")
        LanguagePreset.ENGLISH -> listOf("en-IN")
        LanguagePreset.AUTO -> emptyList()
    }

    fun displayName(preset: LanguagePreset): String = when (preset) {
        LanguagePreset.ENGLISH_HINDI -> "English + Hindi (recommended)"
        LanguagePreset.ENGLISH -> "English only"
        LanguagePreset.AUTO -> "Auto-detect"
    }
}

/** Cleans the custom vocabulary list: trim, drop blanks/comments, de-duplicate, cap. */
object VocabularyCleaner {
    const val MAX_TERMS = 1000
    const val MAX_TERM_LENGTH = 100

    private val spaces = Regex("\\s+")

    fun clean(terms: Iterable<String?>): List<String> {
        val seen = HashSet<String>()
        val result = ArrayList<String>()
        for (raw in terms) {
            val term = raw?.trim()?.replace(spaces, " ").orEmpty()
            if (term.isEmpty() || term.startsWith('#') || term.length > MAX_TERM_LENGTH) continue
            if (seen.add(term.lowercase())) {
                result.add(term)
                if (result.size == MAX_TERMS) break
            }
        }

        return result
    }

    /** One term per line (the format of shared/vocabulary.default.txt and the settings box). */
    fun fromText(text: String?): List<String> = clean((text ?: "").split('\n'))

    fun toText(terms: List<String>): String = terms.joinToString("\n")
}

/** The starting vocabulary for a new install; a test keeps it equal to shared/vocabulary.default.txt. */
object SeedVocabulary {
    val terms: List<String> = listOf(
        "WhatsApp", "YouTube", "Gmail", "PowerPoint", "Excel", "LinkedIn", "Google Meet", "Wi-Fi", "UPI", "Hinglish", "Plainspoken",
    )
}
