package app.plainspoken.core.transcription

import app.plainspoken.core.logging.Log
import app.plainspoken.core.settings.PlainspokenSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonObject
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.io.InterruptedIOException
import java.time.Duration
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlin.random.Random

data class GeminiFile(val name: String, val uri: String, val state: String, val mimeType: String)

/**
 * Retries a step on server errors (HTTP 5xx) with exponential backoff and jitter: by default 2 retries,
 * waiting about 1 s then 2 s (each ±50 %). Other errors are not retried here.
 */
class RetryPolicy(
    val maxServerRetries: Int = 2,
    val baseDelay: Duration = Duration.ofSeconds(1),
    jitter: Double = 0.5,
    /** Injectable so tests don't sleep. */
    val delay: suspend (Duration) -> Unit = { kotlinx.coroutines.delay(it.toMillis()) },
    private val random: Random = Random.Default,
) {
    val jitter = jitter.coerceIn(0.0, 1.0)

    /** Delay before retry number [retry] (1-based). */
    fun backoffFor(retry: Int): Duration {
        val baseMs = baseDelay.toMillis() * Math.pow(2.0, (retry - 1).coerceAtLeast(0).toDouble())
        val factor = 1 + jitter * (2 * random.nextDouble() - 1)
        return Duration.ofMillis((baseMs * factor).toLong())
    }

    suspend fun <T> execute(operation: String, log: Log, action: suspend () -> T): T {
        var retry = 0
        while (true) {
            try {
                return action()
            } catch (e: TranscriptionException) {
                if (e.kind != TranscriptionErrorKind.SERVER || retry >= maxServerRetries) throw e
                retry++
                val wait = backoffFor(retry)
                log.warn("$operation: server error (${e.statusCode}); retry $retry/$maxServerRetries in ${wait.toMillis()} ms")
                delay(wait)
            }
        }
    }

    companion object {
        val DEFAULT = RetryPolicy()
    }
}

/**
 * Thin HTTP layer shared by both engines: auth header, timeouts, error mapping, 5xx retries,
 * and the Files API (resumable upload, wait-until-active, delete).
 */
class GeminiClient(
    private val http: OkHttpClient,
    baseUrl: String,
    private val apiKey: () -> String?,
    val log: Log,
    val retry: RetryPolicy = RetryPolicy.DEFAULT,
    /** False only for the smoke test behind a proxy that injects the key. */
    private val requireApiKey: Boolean = true,
    /** Where best-effort background work (file deletes) runs. */
    private val background: CoroutineScope = BackgroundScope,
) {
    private val base: HttpUrl = (PlainspokenSettings.normalizeBaseUrl(baseUrl) + "/").toHttpUrl()

    fun url(relative: String): HttpUrl = base.resolve(relative) ?: throw IllegalArgumentException("Bad URL: $relative")

    /** POST/GET JSON with 5xx retries. Throws TranscriptionException on non-2xx. */
    suspend fun sendJson(method: String, relative: String, body: JsonElement?, timeout: Duration, operation: String, modelCall: Boolean): JsonElement =
        retry.execute(operation, log) { sendJsonOnce(method, relative, body, timeout, operation, modelCall) }

    /** Single attempt, for callers that wrap their own retry around several steps. */
    suspend fun sendJsonOnce(method: String, relative: String, body: JsonElement?, timeout: Duration, operation: String, modelCall: Boolean): JsonElement {
        val r = sendOnce({ build(method, url(relative), body) }, timeout, operation, modelCall)
        return parseJson(r.body, operation)
    }

    /** Resumable upload (start + upload/finalize). Retries the whole upload on 5xx. */
    suspend fun upload(data: ByteArray, mimeType: String, timeout: Duration): GeminiFile = retry.execute("upload", log) {
        val start = sendOnce({
            build("POST", url("upload/v1beta/files"), buildJsonObject { putJsonObject("file") { put("display_name", "plainspoken-dictation") } })
                .newBuilder()
                .header("X-Goog-Upload-Protocol", "resumable")
                .header("X-Goog-Upload-Command", "start")
                .header("X-Goog-Upload-Header-Content-Length", data.size.toString())
                .header("X-Goog-Upload-Header-Content-Type", mimeType)
                .build()
        }, timeout, "upload-start", false)

        val uploadUrl = start.headers["x-goog-upload-url"]?.let { runCatching { it.toHttpUrl() }.getOrNull() }
            ?: throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "upload-start: no X-Goog-Upload-URL header")

        val perByte = Duration.ofMillis((data.size / (64.0 * 1024) * 1000).toLong())
        val done = sendOnce({
            Request.Builder().url(uploadUrl)
                .post(data.toRequestBody(mimeType.toMediaType()))
                .header("X-Goog-Upload-Offset", "0")
                .header("X-Goog-Upload-Command", "upload, finalize")
                .build()
        }, timeout.plus(perByte), "upload-bytes", false)

        val doc = parseJson(done.body, "upload-bytes")
        readFile((doc as? JsonObject)?.get("file") ?: doc, "upload-bytes")
    }

    /** Polls a PROCESSING file until ACTIVE (every 500 ms, up to [maxWait]). */
    suspend fun waitUntilActive(file: GeminiFile, maxWait: Duration): GeminiFile {
        val started = System.nanoTime()
        var current = file
        while (true) {
            if (current.state.equals("ACTIVE", ignoreCase = true) || current.state.isEmpty()) return current
            if (current.state.equals("FAILED", ignoreCase = true)) {
                throw TranscriptionException(TranscriptionErrorKind.SERVER, "file processing FAILED")
            }

            if (Duration.ofNanos(System.nanoTime() - started) > maxWait) {
                throw TranscriptionException(TranscriptionErrorKind.TIMEOUT, "file still ${current.state} after ${maxWait.seconds}s")
            }

            retry.delay(Duration.ofMillis(500))
            current = readFile(sendJson("GET", "v1beta/" + current.name, null, Duration.ofSeconds(15), "file-get", false), "file-get")
        }
    }

    /** Best effort in the background; never throws. 403/404 mean the file is already gone. */
    fun deleteFileLater(name: String): Job = background.launch {
        try {
            val r = sendOnce({ build("DELETE", url("v1beta/$name"), null) }, Duration.ofSeconds(20), "file-delete", false, throwOnError = false)
            if (r.status !in setOf(200, 204, 403, 404)) {
                log.warn("file-delete: HTTP ${r.status}; Google deletes it automatically after ~48 h")
            }
        } catch (e: TranscriptionException) {
            log.warn("file-delete failed: ${e.kind}")
        }
    }

    internal class RawResponse(val status: Int, val body: String, val headers: Map<String, String>)

    internal suspend fun sendOnce(
        build: () -> Request,
        timeout: Duration,
        operation: String,
        modelCall: Boolean,
        throwOnError: Boolean = true,
    ): RawResponse {
        val key = apiKey()
        if (key.isNullOrBlank() && requireApiKey) {
            throw TranscriptionException(TranscriptionErrorKind.NO_KEY, "No API key configured.")
        }

        var request = build()
        // Only ever send the key to the configured API host.
        if (!key.isNullOrBlank() && request.url.host.equals(base.host, ignoreCase = true)) {
            request = request.newBuilder().header(API_KEY_HEADER, key.trim()).build()
        }

        val started = System.nanoTime()
        val call = http.newCall(request)
        call.timeout().timeout(timeout.toMillis(), TimeUnit.MILLISECONDS)
        val r = try {
            call.execute(operation)
        } catch (e: InterruptedIOException) {
            log.warn("$operation: timed out after ${elapsedMs(started)} ms")
            throw TranscriptionException(TranscriptionErrorKind.TIMEOUT, "$operation: timeout after ${timeout.seconds}s", cause = e)
        } catch (e: IOException) {
            log.warn("$operation: network error (${e.javaClass.simpleName})")
            throw TranscriptionException(TranscriptionErrorKind.NETWORK, "$operation: ${e.javaClass.simpleName}", cause = e)
        }

        log.info("$operation: HTTP ${r.status} in ${elapsedMs(started)} ms (${request.body?.contentLength() ?: 0} bytes up, ${r.body.length} chars down)")
        if (throwOnError && r.status !in 200..299) {
            val ex = GeminiErrors.fromResponse(r.status, r.body, retryAfter(r.headers["retry-after"]), modelCall)
            log.warn("$operation: ${ex.kind} — ${ex.message}")
            throw ex
        }

        return r
    }

    private fun elapsedMs(startNanos: Long) = (System.nanoTime() - startNanos) / 1_000_000

    /** Runs the call on OkHttp's threads; the body is read there too, so callers may be on the main thread. */
    private suspend fun Call.execute(operation: String): RawResponse = suspendCancellableCoroutine { cont ->
        cont.invokeOnCancellation { cancel() }
        enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                if (cont.isActive) cont.resumeWithException(e)
            }

            override fun onResponse(call: Call, response: Response) {
                val raw = try {
                    response.use { r ->
                        val headers = r.headers.names().associate { it.lowercase() to r.headers.values(it).joinToString(",") }
                        RawResponse(r.code, r.body?.string().orEmpty(), headers)
                    }
                } catch (e: IOException) {
                    if (cont.isActive) cont.resumeWithException(e)
                    return
                }

                if (cont.isActive) cont.resume(raw) else log.info("$operation: response after cancellation ignored")
            }
        })
    }

    private fun retryAfter(value: String?): Duration? {
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

    private fun build(method: String, url: HttpUrl, body: JsonElement?): Request {
        val b = Request.Builder().url(url)
        val requestBody = body?.toString()?.toRequestBody(JSON)
        return when (method) {
            "GET" -> b.get()
            "DELETE" -> b.delete(requestBody)
            else -> b.method(method, requestBody ?: ByteArray(0).toRequestBody(null))
        }.build()
    }

    internal fun parseJson(body: String, operation: String): JsonElement = try {
        ApiJson.parse(body.ifBlank { "{}" })
    } catch (e: Exception) {
        throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "$operation: ${JsonShape.describe(body)}", cause = e)
    }

    private fun readFile(e: JsonElement, operation: String): GeminiFile {
        val name = e.str("name")
        val uri = e.str("uri")
        if (name.isNullOrEmpty() || uri.isNullOrEmpty() || !name.startsWith("files/")) {
            throw TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "$operation: ${JsonShape.describe(e)}")
        }

        return GeminiFile(name, uri, e.str("state").orEmpty(), e.str("mimeType").orEmpty())
    }

    companion object {
        const val API_KEY_HEADER = "x-goog-api-key"
        private val JSON = "application/json; charset=utf-8".toMediaType()
    }
}

/** Long-lived scope for best-effort background work that must not block the user. */
object BackgroundScope : CoroutineScope by CoroutineScope(SupervisorJob() + Dispatchers.IO)

/** Accepts "gemini-x" or "models/gemini-x"; returns "gemini-x". */
internal object ModelId {
    fun normalize(model: String?): String = model.orEmpty().trim().removePrefix("models/")

    /** Percent-encodes a path segment (model ids are normally plain). */
    fun segment(model: String?): String = java.net.URLEncoder.encode(normalize(model), "UTF-8").replace("+", "%20")
}
