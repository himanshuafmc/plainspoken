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

    /** This device only. Never exported. */
    var local: LocalSettings = LocalSettings()

    /** Clamps numbers, fills defaults and cleans the vocabulary. Returns this. */
    fun normalize(): PlainspokenSettings {
        schemaVersion = CURRENT_SCHEMA_VERSION
        val t = transcription
        t.transcribeModel = t.transcribeModel.trim().ifEmpty { TranscriptionSettings.DEFAULT_TRANSCRIBE_MODEL }
        t.generateModel = t.generateModel.trim().ifEmpty { TranscriptionSettings.DEFAULT_GENERATE_MODEL }
        t.liveModel = t.liveModel.trim().ifEmpty { TranscriptionSettings.DEFAULT_LIVE_MODEL }
        t.apiBaseUrl = normalizeBaseUrl(t.apiBaseUrl)
        t.requestTimeoutSeconds = t.requestTimeoutSeconds.coerceIn(10, 600)
        t.customVocabulary = VocabularyCleaner.clean(t.customVocabulary)
        recording.maxSeconds = recording.maxSeconds.coerceIn(30, 1800)
        hotkey = hotkey.trim().ifEmpty { DEFAULT_HOTKEY }
        return this
    }

    companion object {
        const val CURRENT_SCHEMA_VERSION = 1
        const val DEFAULT_HOTKEY = "Ctrl+Alt+Space"

        fun normalizeBaseUrl(url: String?): String {
            val raw = url?.trim().orEmpty()
            val uri = try {
                URI(raw)
            } catch (_: Exception) {
                null
            }

            if (uri == null || uri.host.isNullOrEmpty() || (uri.scheme != "https" && uri.scheme != "http")) {
                return TranscriptionSettings.DEFAULT_API_BASE_URL
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

    fun modelFor(engine: EngineKind): String = if (engine == EngineKind.TRANSCRIBE) transcribeModel else generateModel

    companion object {
        const val DEFAULT_TRANSCRIBE_MODEL = "gemini-3.5-transcribe"
        const val DEFAULT_GENERATE_MODEL = "gemini-3.5-flash-lite"
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
