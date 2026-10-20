package app.plainspoken.core.transcription

import app.plainspoken.core.audio.AudioChunker
import app.plainspoken.core.audio.EncodedAudio
import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.settings.HindiScript
import app.plainspoken.core.settings.LanguagePreset
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.settings.SpeechServiceSettings
import app.plainspoken.core.settings.TranscriptionMode
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.time.Duration

/** Time allowed for one request: the base timeout, plus a share of the audio length and upload size. */
internal fun requestTimeout(base: Duration, seconds: Double, bytes: Int): Duration =
    base.plusMillis((seconds * 250).toLong()).plusMillis((bytes / (64.0 * 1024) * 1000).toLong())

/** Vocabulary terms a service accepts as hints: short, without odd characters, capped. */
internal fun hintTerms(vocabulary: List<String>, max: Int, maxLength: Int, maxWords: Int = 5): List<String> =
    vocabulary.asSequence()
        .map { it.trim() }
        .filter { it.isNotEmpty() && it.length <= maxLength && it.split(' ').size <= maxWords && it.none { c -> c in "<>{}[]\\\"" } }
        .take(max)
        .toList()

/**
 * Sarvam Saaras over REST (POST /speech-to-text, multipart). One request accepts up to 30 s, so longer
 * recordings are split at quiet moments and the parts are transcribed in parallel (up to three at a time).
 */
class SarvamEngine(
    private val client: ServiceClient,
    private val config: SpeechServiceSettings,
    private val timeout: Duration,
) : TranscriptionEngine {
    override val service = SpeechService.SARVAM

    override suspend fun transcribe(request: TranscriptionRequest): String {
        if (request.durationSeconds <= SpeechService.SARVAM.maxRequestSeconds - 1) return transcribePart(request.audioFor(service), request)
        val (samples, rate) = WavEncoder.decode(request.wav)
        val parts = AudioChunker.split(samples, rate, maxSeconds = 28.0)
        client.log.info("sarvam: ${"%.1f".format(java.util.Locale.ROOT, request.durationSeconds)}s split into ${parts.size} parts")
        val permits = Semaphore(3)
        val texts = coroutineScope {
            parts.map { part ->
                async { permits.withPermit { transcribePart(EncodedAudio(WavEncoder.encode(part, rate), "audio/wav", "audio.wav"), request) } }
            }.awaitAll()
        }

        return texts.map { it.trim() }.filter { it.isNotEmpty() }.joinToString(" ")
    }

    private suspend fun transcribePart(audio: EncodedAudio, request: TranscriptionRequest): String {
        val body = buildBody(audio, request, config)
        val root = client.sendJson(
            { Request.Builder().url(client.url("speech-to-text")).post(body).build() },
            requestTimeout(timeout, minOf(request.durationSeconds, 30.0), audio.bytes.size),
            "sarvam",
        )
        return (root as? JsonObject).str("transcript")?.trim()
            ?: throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "sarvam: ${JsonShape.describe(root)}", service = service)
    }

    companion object {
        fun buildBody(audio: EncodedAudio, request: TranscriptionRequest, config: SpeechServiceSettings): MultipartBody {
            val b = MultipartBody.Builder().setType(MultipartBody.FORM)
                .addFormDataPart("file", audio.fileName, audio.bytes.toRequestBody(audio.mimeType.toMediaType()))
                .addFormDataPart("model", config.model)
                .addFormDataPart("mode", mode(request, config))
                .addFormDataPart("language_code", language(request.languages))
            // Key terms only work with saaras:v4 (Sarvam SDK docs): up to 50, each up to 64 characters.
            if (config.vocabularyHints && "v4" in config.model) {
                val terms = hintTerms(request.vocabulary, max = 50, maxLength = 64)
                if (terms.isNotEmpty()) b.addFormDataPart("keyterms", jsonArray(terms))
            }

            return b.build()
        }

        fun language(preset: LanguagePreset): String = if (preset == LanguagePreset.ENGLISH) "en-IN" else "unknown"

        /** translit = Hindi in Roman letters; codemix = English words in English, Hindi in Devanagari. */
        fun mode(request: TranscriptionRequest, config: SpeechServiceSettings): String = when {
            config.option.isNotEmpty() -> config.option
            request.mode == TranscriptionMode.VERBATIM -> "verbatim"
            request.languages == LanguagePreset.ENGLISH -> "transcribe"
            request.hindiScript == HindiScript.ROMAN -> "translit"
            else -> "codemix"
        }
    }
}

/**
 * Any OpenAI-style transcription API: POST {base}/audio/transcriptions (OpenAI, Groq, a self-hosted Whisper server…).
 * Vocabulary and the wanted style go in the prompt; recordings over the size limit are split.
 */
class OpenAiEngine(
    private val client: ServiceClient,
    private val config: SpeechServiceSettings,
    private val timeout: Duration,
) : TranscriptionEngine {
    override val service = SpeechService.OPENAI

    override suspend fun transcribe(request: TranscriptionRequest): String {
        val audio = request.audioFor(service)
        if (audio.bytes.size <= MAX_UPLOAD_BYTES) return transcribePart(audio, request.durationSeconds, request)
        val (samples, rate) = WavEncoder.decode(request.wav)
        val parts = AudioChunker.split(samples, rate, maxSeconds = SpeechService.OPENAI.maxRequestSeconds.toDouble(), searchSeconds = 20.0)
        val texts = ArrayList<String>()
        for (part in parts) {
            texts.add(transcribePart(EncodedAudio(WavEncoder.encode(part, rate), "audio/wav", "audio.wav"), WavEncoder.durationSeconds(part.size, rate), request))
        }

        return texts.map { it.trim() }.filter { it.isNotEmpty() }.joinToString(" ")
    }

    private suspend fun transcribePart(audio: EncodedAudio, seconds: Double, request: TranscriptionRequest): String {
        val body = buildBody(audio, request, config)
        val root = client.sendJson(
            { Request.Builder().url(client.url("audio/transcriptions")).post(body).build() },
            requestTimeout(timeout, seconds, audio.bytes.size),
            "openai",
        )
        return (root as? JsonObject).str("text")?.trim()
            ?: throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "openai: ${JsonShape.describe(root)}", service = service)
    }

    companion object {
        /** OpenAI's limit is 25 MB per file; stay under it. */
        const val MAX_UPLOAD_BYTES = 24 * 1024 * 1024

        fun buildBody(audio: EncodedAudio, request: TranscriptionRequest, config: SpeechServiceSettings): MultipartBody {
            val b = MultipartBody.Builder().setType(MultipartBody.FORM)
                .addFormDataPart("file", audio.fileName, audio.bytes.toRequestBody(audio.mimeType.toMediaType()))
                .addFormDataPart("model", config.model)
                .addFormDataPart("response_format", "json")
                .addFormDataPart("temperature", "0")
            if (request.languages == LanguagePreset.ENGLISH) b.addFormDataPart("language", "en")
            val prompt = prompt(request, config)
            if (prompt.isNotEmpty()) b.addFormDataPart("prompt", prompt)
            return b.build()
        }

        /**
         * Whisper-style models copy the style of the prompt, so it shows the wanted script (and fillers for Verbatim),
         * followed by names to spell correctly. Kept short (these prompts are limited to about 200 tokens).
         */
        fun prompt(request: TranscriptionRequest, config: SpeechServiceSettings): String {
            val parts = ArrayList<String>()
            if (request.mode == TranscriptionMode.VERBATIM) parts.add("Umm, so, uh, I was like, hmm, thinking.")
            if (request.languages != LanguagePreset.ENGLISH) {
                when (request.hindiScript) {
                    HindiScript.ROMAN -> parts.add("Kal meeting hai, please sabko inform kar dena.")
                    HindiScript.DEVANAGARI -> parts.add("कल meeting है, please सबको inform कर देना।")
                    HindiScript.AS_SPOKEN -> Unit
                }
            }

            if (config.vocabularyHints) {
                val terms = hintTerms(request.vocabulary, max = 40, maxLength = 40)
                if (terms.isNotEmpty()) parts.add(terms.joinToString(", ") + ".")
            }

            return parts.joinToString(" ").take(600)
        }
    }
}

/** Deepgram pre-recorded transcription: POST /v1/listen with the audio as the body and options in the query. */
class DeepgramEngine(
    private val client: ServiceClient,
    private val config: SpeechServiceSettings,
    private val timeout: Duration,
) : TranscriptionEngine {
    override val service = SpeechService.DEEPGRAM

    override suspend fun transcribe(request: TranscriptionRequest): String {
        val audio = request.audioFor(service)
        val url = client.url("v1/listen").newBuilder().apply { query(request, config).forEach { (k, v) -> addQueryParameter(k, v) } }.build()
        val root = client.sendJson(
            { Request.Builder().url(url).post(audio.bytes.toRequestBody(audio.mimeType.toMediaType())).build() },
            requestTimeout(timeout, request.durationSeconds, audio.bytes.size),
            "deepgram",
        )
        return parse(root) ?: throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "deepgram: ${JsonShape.describe(root)}", service = service)
    }

    companion object {
        /** Query options shared by the REST and streaming endpoints (keyterm may repeat). */
        fun query(request: TranscriptionRequest, config: SpeechServiceSettings): List<Pair<String, String>> {
            val q = arrayListOf(
                "model" to config.model,
                "smart_format" to "true",
                "punctuate" to "true",
                "language" to language(request.languages),
            )
            if (request.mode == TranscriptionMode.VERBATIM) q.add("filler_words" to "true")
            // Key term prompting works with Nova-3 only.
            if (config.vocabularyHints && config.model.startsWith("nova-3")) {
                hintTerms(request.vocabulary, max = 100, maxLength = 50).forEach { q.add("keyterm" to it) }
            }

            return q
        }

        /** "multi" lets Nova-3 follow English and Hindi switching mid-sentence. */
        fun language(preset: LanguagePreset): String = if (preset == LanguagePreset.ENGLISH) "en" else "multi"

        /** results.channels[0].alternatives[0].transcript */
        fun parse(root: JsonElement): String? {
            val channels = ((root as? JsonObject)?.get("results") as? JsonObject)?.get("channels") as? JsonArray ?: return null
            val alternatives = (channels.firstOrNull() as? JsonObject)?.get("alternatives") as? JsonArray ?: return null
            return (alternatives.firstOrNull() as? JsonObject).str("transcript")?.trim()
        }
    }
}

/** ElevenLabs Scribe: POST /v1/speech-to-text (multipart). With scribe_v2, Smart mode asks it to leave out fillers too. */
class ElevenLabsEngine(
    private val client: ServiceClient,
    private val config: SpeechServiceSettings,
    private val timeout: Duration,
) : TranscriptionEngine {
    override val service = SpeechService.ELEVENLABS

    override suspend fun transcribe(request: TranscriptionRequest): String {
        val audio = request.audioFor(service)
        val body = buildBody(audio, request, config)
        val root = client.sendJson(
            { Request.Builder().url(client.url("v1/speech-to-text")).post(body).build() },
            requestTimeout(timeout, request.durationSeconds, audio.bytes.size),
            "elevenlabs",
        )
        return (root as? JsonObject).str("text")?.trim()
            ?: throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "elevenlabs: ${JsonShape.describe(root)}", service = service)
    }

    companion object {
        fun buildBody(audio: EncodedAudio, request: TranscriptionRequest, config: SpeechServiceSettings): MultipartBody {
            val b = MultipartBody.Builder().setType(MultipartBody.FORM)
                .addFormDataPart("model_id", config.model)
                .addFormDataPart("file", audio.fileName, audio.bytes.toRequestBody(audio.mimeType.toMediaType()))
                .addFormDataPart("tag_audio_events", "false")
            if (request.languages == LanguagePreset.ENGLISH) b.addFormDataPart("language_code", "en")
            if (request.mode == TranscriptionMode.SMART && "v2" in config.model) b.addFormDataPart("no_verbatim", "true")
            // Key terms cost 20 % more on ElevenLabs, so they are off unless switched on; over 100 adds a minimum charge.
            if (config.vocabularyHints) hintTerms(request.vocabulary, max = 100, maxLength = 49).forEach { b.addFormDataPart("keyterms", it) }
            return b.build()
        }
    }
}

private fun jsonArray(items: List<String>): String = JsonArray(items.map { JsonPrimitive(it) }).toString()
