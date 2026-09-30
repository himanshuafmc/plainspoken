package app.plainspoken.core.transcription

import app.plainspoken.core.logging.Redactor
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import java.time.Duration

enum class TranscriptionErrorKind {
    NO_KEY,
    INVALID_KEY,
    RATE_LIMITED,
    DAILY_QUOTA,
    NETWORK,
    TIMEOUT,
    SERVER,
    MODEL_NOT_FOUND,
    BAD_REQUEST,
    UNEXPECTED_RESPONSE,
    BLOCKED,
}

/**
 * Any failure talking to the speech engine. [message] is a sanitised diagnostic for the log (status codes,
 * error codes, JSON shape); it never contains transcript text or the key and is never shown to the user
 * (see dictation.UserMessages for that).
 */
class TranscriptionException(
    val kind: TranscriptionErrorKind,
    message: String,
    val statusCode: Int? = null,
    val retryAfter: Duration? = null,
    cause: Throwable? = null,
) : Exception(message, cause) {
    val isRateLimit: Boolean get() = kind == TranscriptionErrorKind.RATE_LIMITED || kind == TranscriptionErrorKind.DAILY_QUOTA
}

/**
 * Maps Gemini HTTP errors to [TranscriptionErrorKind]. Two body shapes exist:
 * Interactions API: {"error":{"message":"…","code":"too_many_requests"}}
 * Classic APIs:     {"error":{"code":429,"message":"…","status":"RESOURCE_EXHAUSTED","details":[…]}}
 */
object GeminiErrors {
    /** A 429 whose retry delay is at most this long is treated as a per-minute limit. */
    val perMinuteThreshold: Duration = Duration.ofSeconds(120)

    private val retryIn = Regex("retry in\\s+(\\d+(?:\\.\\d+)?)\\s*(ms|s)\\b", RegexOption.IGNORE_CASE)

    fun fromResponse(status: Int, body: String?, retryAfterHeader: Duration?, modelCall: Boolean): TranscriptionException {
        val info = parseBody(body)
        val retryAfter = retryAfterHeader ?: info.retryDelay ?: parseRetryFromMessage(info.message)
        val detail = "HTTP $status ${info.code ?: "-"}/${info.status ?: "-"}${info.reason?.let { "/$it" } ?: ""}: " +
            Redactor.clean(info.message, 200)
        val msg = info.message.orEmpty()
        val kind = when (status) {
            400 -> if (info.reason == "API_KEY_INVALID" || msg.contains("API key", ignoreCase = true)) {
                TranscriptionErrorKind.INVALID_KEY
            } else {
                TranscriptionErrorKind.BAD_REQUEST
            }
            401 -> TranscriptionErrorKind.INVALID_KEY
            // A missing/foreign uploaded file also returns 403 ("…permission to access the File…").
            403 -> if (msg.contains("File")) TranscriptionErrorKind.BAD_REQUEST else TranscriptionErrorKind.INVALID_KEY
            404 -> if (modelCall || msg.contains("model", ignoreCase = true)) {
                TranscriptionErrorKind.MODEL_NOT_FOUND
            } else {
                TranscriptionErrorKind.BAD_REQUEST
            }
            408 -> TranscriptionErrorKind.TIMEOUT
            429 -> classify429(msg, info.quotaIds, retryAfter)
            in 500..599 -> TranscriptionErrorKind.SERVER
            else -> TranscriptionErrorKind.BAD_REQUEST
        }

        return TranscriptionException(kind, detail, status, retryAfter)
    }

    /**
     * Structured quota ids (classic APIs) are trusted as they are. The Interactions API only has a message,
     * and it names the model's daily limit even when a short per-minute window tripped (seen 2026-09-30:
     * "limit: 25 requests per day … Please retry in 17s", and a retry 30 s later succeeded), so there a
     * short retry delay wins over the "per day" wording.
     */
    fun classify429(message: String, quotaIds: List<String>, retryAfter: Duration?): TranscriptionErrorKind {
        val ids = normalise(quotaIds.joinToString(" "))
        if (mentionsDay(ids)) return TranscriptionErrorKind.DAILY_QUOTA
        val shortWait = retryAfter != null && retryAfter <= perMinuteThreshold
        if (mentionsMinute(ids) || mentionsMinute(normalise(message)) || shortWait) return TranscriptionErrorKind.RATE_LIMITED
        return TranscriptionErrorKind.DAILY_QUOTA
    }

    fun parseRetryFromMessage(message: String?): Duration? {
        val m = retryIn.find(message ?: return null) ?: return null
        val v = m.groupValues[1].toDoubleOrNull() ?: return null
        return if (m.groupValues[2].equals("ms", ignoreCase = true)) {
            Duration.ofMillis(v.toLong())
        } else {
            Duration.ofMillis((v * 1000).toLong())
        }
    }

    /** Reads "43s" / "1.5s" (protobuf Duration JSON). */
    fun parseDuration(s: String?): Duration? {
        if (s.isNullOrEmpty() || !s.endsWith('s')) return null
        val v = s.dropLast(1).toDoubleOrNull() ?: return null
        return Duration.ofMillis((v * 1000).toLong())
    }

    private fun normalise(s: String) = s.uppercase().replace('_', ' ')

    private fun mentionsDay(t: String) = "PER DAY" in t || "PERDAY" in t || "DAILY" in t

    private fun mentionsMinute(t: String) = "PER MINUTE" in t || "PERMINUTE" in t

    internal class ErrorInfo {
        var message: String? = null
        var code: String? = null
        var status: String? = null
        var reason: String? = null
        var retryDelay: Duration? = null
        val quotaIds = ArrayList<String>()
    }

    internal fun parseBody(body: String?): ErrorInfo {
        val info = ErrorInfo()
        if (body.isNullOrBlank()) return info
        var root = try {
            ApiJson.parse(body)
        } catch (_: Exception) {
            return info
        }

        if (root is JsonArray && root.isNotEmpty()) root = root[0] // some Google endpoints wrap errors in an array
        val err = (root as? JsonObject)?.get("error") as? JsonObject ?: return info
        info.message = err.str("message")
        info.status = err.str("status")
        info.code = when (val c = err["code"]) {
            null, JsonNull -> null
            is JsonPrimitive -> c.content
            else -> c.toString()
        }

        (err["details"] as? JsonArray)?.forEach { d ->
            if (d !is JsonObject) return@forEach
            val type = d.str("@type").orEmpty()
            when {
                type.endsWith("RetryInfo") -> info.retryDelay = parseDuration(d.str("retryDelay"))
                type.endsWith("ErrorInfo") -> info.reason = d.str("reason")
                type.endsWith("QuotaFailure") -> (d["violations"] as? JsonArray)?.forEach { v ->
                    (v as? JsonObject)?.str("quotaId")?.let { info.quotaIds.add(it) }
                }
            }
        }

        return info
    }
}

internal fun JsonElement?.str(name: String): String? =
    ((this as? JsonObject)?.get(name) as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull

/** Strict JSON parsing for API bodies: an HTML error page must not pass as JSON. */
internal object ApiJson {
    private val json = kotlinx.serialization.json.Json { ignoreUnknownKeys = true }
    private val literal = Regex("-?\\d+(\\.\\d+)?([eE][+-]?\\d+)?|true|false|null")

    /** Throws [IllegalArgumentException] for anything that isn't JSON (the parser accepts a bare word). */
    fun parse(text: String): JsonElement {
        val element = json.parseToJsonElement(text)
        if (element is JsonPrimitive && !element.isString && !literal.matches(element.content)) {
            throw IllegalArgumentException("not JSON")
        }

        return element
    }
}

/**
 * Describes the structure of a JSON document without its content, for logging unexpected responses
 * safely. Only short enum-like values of well-known keys (status, type, …) are kept.
 * Example: {id:str,status:"completed",steps:[1×{content:[1×{text:str(123),type:"text"}],type:"model_output"}]}
 */
object JsonShape {
    private val safeValueKeys = setOf(
        "status", "type", "object", "state", "code", "finishReason", "blockReason", "mimeType", "mime_type", "reason",
    )

    fun describe(body: String?): String {
        if (body.isNullOrBlank()) return "empty"
        return try {
            val s = describe(ApiJson.parse(body))
            if (s.length > 1500) s.take(1500) + "…" else s
        } catch (_: Exception) {
            "non-JSON (${body.length} chars)"
        }
    }

    fun describe(element: JsonElement): String = StringBuilder().also { describe(element, null, it, 0) }.toString()

    private fun describe(e: JsonElement, key: String?, sb: StringBuilder, depth: Int) {
        if (depth > 8) {
            sb.append('…')
            return
        }

        when (e) {
            is JsonObject -> {
                sb.append('{')
                var first = true
                for ((name, value) in e) {
                    if (!first) sb.append(',')
                    first = false
                    sb.append(name).append(':')
                    describe(value, name, sb, depth + 1)
                }

                sb.append('}')
            }
            is JsonArray -> {
                sb.append('[').append(e.size)
                if (e.isNotEmpty()) {
                    sb.append('×')
                    describe(e[0], null, sb, depth + 1)
                }

                sb.append(']')
            }
            JsonNull -> sb.append("null")
            is JsonPrimitive -> when {
                e.isString -> {
                    val s = e.content
                    if (key != null && key in safeValueKeys && s.length <= 40) {
                        sb.append('"').append(s).append('"')
                    } else {
                        sb.append("str(").append(s.length).append(')')
                    }
                }
                e.content == "true" || e.content == "false" -> sb.append("bool")
                else -> sb.append(if (key == "code") e.content else "num")
            }
        }
    }
}
