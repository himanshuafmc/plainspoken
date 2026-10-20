package app.plainspoken.core.transcription

import app.plainspoken.core.logging.Log
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SpeechService
import okhttp3.OkHttpClient
import okhttp3.Request
import java.time.Duration

data class KeyTestResult(val ok: Boolean, val message: String)

/**
 * The "Test" buttons: one small real request per service that costs nothing (a models or account listing),
 * so a wrong key or address shows up before the first dictation.
 */
object KeyTester {
    suspend fun test(http: OkHttpClient, service: SpeechService, settings: PlainspokenSettings, apiKey: String, log: Log): KeyTestResult {
        if (apiKey.isBlank()) return KeyTestResult(false, "Paste your key first.")
        val t = settings.transcription
        return try {
            if (service.isGemini) {
                val client = GeminiClient(http, t.apiBaseUrl, { apiKey }, log, RetryPolicy(maxServerRetries = 0))
                client.sendJsonOnce("GET", "v1beta/models/" + ModelId.segment(t.modelOf(service)), null, Duration.ofSeconds(15), "key-test", true)
            } else {
                val client = ServiceClient(http, service, t.baseUrlOf(service), { apiKey }, log, RetryPolicy(maxServerRetries = 0))
                client.sendOnce({ Request.Builder().url(client.url(checkPath(service))).get().build() }, Duration.ofSeconds(15), "key-test")
            }

            KeyTestResult(true, "Key works. You're ready to dictate.")
        } catch (e: TranscriptionException) {
            explain(e, service, t.modelOf(service))
        }
    }

    /** The key of the OpenAI-compatible clean-up service: GET {base}/models. */
    suspend fun testCleanup(http: OkHttpClient, settings: PlainspokenSettings, apiKey: String, log: Log): KeyTestResult {
        if (apiKey.isBlank()) return KeyTestResult(false, "Paste your key first.")
        val c = settings.transcription.cleanup
        val client = ServiceClient(http, SpeechService.OPENAI, c.openaiBaseUrl, { apiKey }, log, RetryPolicy(maxServerRetries = 0))
        return try {
            client.sendOnce({ Request.Builder().url(client.url("models")).get().build() }, Duration.ofSeconds(15), "key-test")
            KeyTestResult(true, "Key works.")
        } catch (e: TranscriptionException) {
            explain(e, SpeechService.OPENAI, c.openaiModel)
        }
    }

    /** A free, read-only endpoint for each service. */
    internal fun checkPath(service: SpeechService): String = when (service) {
        SpeechService.SARVAM -> "v2/models"
        SpeechService.DEEPGRAM -> "v1/projects"
        SpeechService.ELEVENLABS -> "v1/models"
        else -> "models"
    }

    private fun explain(e: TranscriptionException, service: SpeechService, model: String): KeyTestResult {
        val name = service.shortName.replaceFirstChar { it.uppercase() }
        return when (e.kind) {
            TranscriptionErrorKind.RATE_LIMITED, TranscriptionErrorKind.DAILY_QUOTA ->
                KeyTestResult(true, "The key works, but $name says it is busy or over its limit right now.")
            TranscriptionErrorKind.NO_CREDITS -> KeyTestResult(true, "The key works, but the account seems to be out of credits.")
            TranscriptionErrorKind.INVALID_KEY ->
                if ((e.message ?: "").contains("permission", ignoreCase = true)) {
                    KeyTestResult(true, "The key was accepted, but it may not be allowed to read account details. It will be checked on your first dictation.")
                } else {
                    KeyTestResult(false, "$name did not accept this key. Copy it again from ${service.keyUrl}.")
                }
            TranscriptionErrorKind.BAD_REQUEST ->
                if (e.statusCode == 404 && !service.isGemini) {
                    KeyTestResult(true, "The address answered, but it can't list models, so the key will be checked on your first dictation.")
                } else {
                    KeyTestResult(false, "$name did not accept this key. Copy it again from ${service.keyUrl}.")
                }
            TranscriptionErrorKind.MODEL_NOT_FOUND -> KeyTestResult(false, "The key works, but the model \"$model\" isn't available to it.")
            TranscriptionErrorKind.NETWORK -> KeyTestResult(false, "Couldn't reach $name. Check your connection and the address.")
            TranscriptionErrorKind.TIMEOUT -> KeyTestResult(false, "$name took too long to answer. Try again.")
            else -> KeyTestResult(false, "Couldn't check the key right now. Try again in a minute.")
        }
    }
}
