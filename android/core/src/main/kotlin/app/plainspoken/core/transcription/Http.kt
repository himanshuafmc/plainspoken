package app.plainspoken.core.transcription

import app.plainspoken.core.logging.Log
import app.plainspoken.core.logging.Redactor
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SpeechService
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import java.io.IOException
import java.io.InterruptedIOException
import java.time.Duration
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** Status, body and lower-cased headers of a finished HTTP call. */
internal class RawResponse(val status: Int, val body: String, val headers: Map<String, String>)

/** Runs the call on OkHttp's threads; the body is read there too, so callers may be on the main thread. */
internal suspend fun Call.awaitRaw(log: Log, operation: String): RawResponse = suspendCancellableCoroutine { cont ->
    cont.invokeOnCancellation { cancel() }
    enqueue(object : Callback {
        override fun onFailure(call: Call, e: IOException) {
            if (cont.isActive) cont.resumeWithException(e)
        }

        override fun onResponse(call: Call, response: Response) {
            val raw = try {
                response.use { r ->
                    val headers = r.headers.names().associate { it.lowercase() to r.headers.values(it).joinToString(",") }
                    RawResponse(r.code, r.body.string(), headers)
                }
            } catch (e: IOException) {
                if (cont.isActive) cont.resumeWithException(e)
                return
            }

            if (cont.isActive) cont.resume(raw) else log.info("$operation: response after cancellation ignored")
        }
    })
}

/** Reads a Retry-After header: seconds or an HTTP date. */
internal fun parseRetryAfter(value: String?): Duration? {
    if (value.isNullOrBlank()) return null
    value.trim().toLongOrNull()?.let { return Duration.ofSeconds(it) }
    return try {
        val date = ZonedDateTime.parse(value.trim(), DateTimeFormatter.RFC_1123_DATE_TIME)
        val wait = Duration.between(java.time.Instant.now(), date.toInstant())
        if (wait.isNegative) Duration.ZERO else wait
    } catch (_: Exception) {
        null
    }
}

/**
 * HTTP for the non-Google speech services: adds the service's auth header (only ever to its own host),
 * applies the timeout, maps errors to [TranscriptionException] and retries 5xx with [retry].
 */
class ServiceClient(
    private val http: OkHttpClient,
    val service: SpeechService,
    baseUrl: String,
    private val apiKey: () -> String?,
    val log: Log,
    val retry: RetryPolicy = RetryPolicy.DEFAULT,
) {
    val base: HttpUrl = (PlainspokenSettings.normalizeBaseUrl(baseUrl, service.defaultBaseUrl) + "/").toHttpUrl()

    /** [relative] is resolved against the base address, keeping its path (for example /v1 of an OpenAI-style API). */
    fun url(relative: String): HttpUrl = base.resolve(relative.trimStart('/')) ?: throw IllegalArgumentException("Bad URL: $relative")

    /** Sends with 5xx retries; returns the parsed JSON body. */
    suspend fun sendJson(build: () -> Request, timeout: Duration, operation: String): JsonElement =
        retry.execute(operation, log) { parse(sendOnce(build, timeout, operation).body, operation) }

    internal suspend fun sendOnce(build: () -> Request, timeout: Duration, operation: String, throwOnError: Boolean = true): RawResponse {
        val key = apiKey()?.trim()
        if (key.isNullOrEmpty()) throw TranscriptionException(TranscriptionErrorKind.NO_KEY, "No ${service.id} key configured.", service = service)
        var request = build()
        if (request.url.host.equals(base.host, ignoreCase = true)) request = authorize(request, key)

        val started = System.nanoTime()
        val call = http.newCall(request)
        call.timeout().timeout(timeout.toMillis(), TimeUnit.MILLISECONDS)
        val r = try {
            call.awaitRaw(log, operation)
        } catch (e: InterruptedIOException) {
            log.warn("$operation: timed out after ${(System.nanoTime() - started) / 1_000_000} ms")
            throw TranscriptionException(TranscriptionErrorKind.TIMEOUT, "$operation: timeout after ${timeout.seconds}s", cause = e, service = service)
        } catch (e: IOException) {
            log.warn("$operation: network error (${e.javaClass.simpleName})")
            throw TranscriptionException(TranscriptionErrorKind.NETWORK, "$operation: ${e.javaClass.simpleName}", cause = e, service = service)
        }

        log.info("$operation: HTTP ${r.status} in ${(System.nanoTime() - started) / 1_000_000} ms (${request.body?.contentLength() ?: 0} bytes up, ${r.body.length} chars down)")
        if (throwOnError && r.status !in 200..299) {
            val ex = ServiceErrors.fromResponse(service, r.status, r.body, parseRetryAfter(r.headers["retry-after"]))
            log.warn("$operation: ${ex.kind} — ${ex.message}")
            throw ex
        }

        return r
    }

    private fun authorize(request: Request, key: String): Request = request.newBuilder().apply {
        when (service) {
            SpeechService.SARVAM -> header("api-subscription-key", key)
            SpeechService.DEEPGRAM -> header("Authorization", "Token $key")
            SpeechService.ELEVENLABS -> header("xi-api-key", key)
            else -> header("Authorization", "Bearer $key")
        }
    }.build()

    internal fun parse(body: String, operation: String): JsonElement = try {
        ApiJson.parse(body.ifBlank { "{}" })
    } catch (e: Exception) {
        throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "$operation: ${JsonShape.describe(body)}", cause = e, service = service)
    }

    companion object {
        /** The auth header a service expects, for its WebSocket handshake. */
        fun authHeaders(service: SpeechService, key: String): Map<String, String> = when (service) {
            SpeechService.SARVAM -> mapOf("Api-Subscription-Key" to key)
            SpeechService.DEEPGRAM -> mapOf("Authorization" to "Token $key")
            SpeechService.ELEVENLABS -> mapOf("xi-api-key" to key)
            else -> mapOf("Authorization" to "Bearer $key")
        }
    }
}

/**
 * Maps errors of the non-Google services. Body shapes seen in their SDKs:
 * OpenAI/Sarvam {"error":{"message","code"}}, Deepgram {"err_code","err_msg"}, ElevenLabs {"detail":{"status","message"}} or {"detail":"…"}.
 */
object ServiceErrors {
    private val creditWords = Regex("credit|balance|insufficient|quota|billing|payment|exceeded your", RegexOption.IGNORE_CASE)
    private val modelWords = Regex("\\bmodel", RegexOption.IGNORE_CASE)

    fun fromResponse(service: SpeechService, status: Int, body: String?, retryAfter: Duration?): TranscriptionException {
        val (message, code) = readError(body)
        val text = "$message $code"
        val kind = when (status) {
            401 -> TranscriptionErrorKind.INVALID_KEY
            402 -> TranscriptionErrorKind.NO_CREDITS
            403 -> if (creditWords.containsMatchIn(text)) TranscriptionErrorKind.NO_CREDITS else TranscriptionErrorKind.INVALID_KEY
            404 -> if (modelWords.containsMatchIn(text)) TranscriptionErrorKind.MODEL_NOT_FOUND else TranscriptionErrorKind.BAD_REQUEST
            408, 504 -> TranscriptionErrorKind.TIMEOUT
            429 -> if (creditWords.containsMatchIn(text)) TranscriptionErrorKind.NO_CREDITS else TranscriptionErrorKind.RATE_LIMITED
            in 500..599 -> TranscriptionErrorKind.SERVER
            else -> when {
                creditWords.containsMatchIn(text) -> TranscriptionErrorKind.NO_CREDITS
                modelWords.containsMatchIn(text) && (status == 400 || status == 422) -> TranscriptionErrorKind.MODEL_NOT_FOUND
                else -> TranscriptionErrorKind.BAD_REQUEST
            }
        }

        val detail = "HTTP $status ${code.ifEmpty { "-" }}: ${Redactor.clean(message, 200)}"
        return TranscriptionException(kind, detail, status, retryAfter, service = service)
    }

    /** (message, code) from any of the known error shapes; empty strings when absent. */
    internal fun readError(body: String?): Pair<String, String> {
        if (body.isNullOrBlank()) return "" to ""
        val root = try {
            ApiJson.parse(body) as? JsonObject
        } catch (_: Exception) {
            null
        } ?: return "" to ""

        (root["error"] as? JsonObject)?.let { e -> return (e.str("message") ?: "") to (codeOf(e["code"]) ?: e.str("type") ?: "") }
        root.str("error")?.let { return it to (codeOf(root["code"]) ?: "") }
        root.str("err_msg")?.let { return it to (root.str("err_code") ?: "") }
        when (val detail = root["detail"]) {
            is JsonObject -> return (detail.str("message") ?: "") to (detail.str("status") ?: "")
            is JsonPrimitive -> if (detail.isString) return detail.content to ""
            else -> Unit
        }

        root.str("message")?.let { return it to (codeOf(root["code"]) ?: "") }
        return "" to ""
    }

    private fun codeOf(e: JsonElement?): String? = (e as? JsonPrimitive)?.content
}
