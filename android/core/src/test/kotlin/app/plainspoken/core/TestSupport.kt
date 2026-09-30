package app.plainspoken.core

import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.logging.Log
import app.plainspoken.core.time.Clock
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.time.Instant
import java.time.ZoneId
import java.util.Collections

/** Reads shared/test-fixtures/{name}; the Windows tests use the same files. */
object Fixtures {
    private val dir = File(System.getProperty("plainspoken.fixtures") ?: "../../shared/test-fixtures")
    private val shared = File(System.getProperty("plainspoken.shared") ?: "../../shared")

    fun read(name: String): String = File(dir, name).readText()

    fun shared(name: String): String = File(shared, name).readText()
}

/** A made-up value with the shape of a Gemini API key, built at run time so none is ever committed. */
object FakeKey {
    val value: String = "AIza" + "0".repeat(35)
}

class ListLog : Log {
    val lines: MutableList<String> = Collections.synchronizedList(ArrayList())

    override fun info(message: String) {
        lines.add("INFO $message")
    }

    override fun warn(message: String) {
        lines.add("WARN $message")
    }

    override fun error(message: String, throwable: Throwable?) {
        lines.add("ERROR $message ${throwable?.javaClass?.simpleName.orEmpty()}")
    }
}

class FixedClock(var instant: Instant = Instant.parse("2026-07-15T10:00:00Z"), private val zoneId: ZoneId = ZoneId.of("Asia/Kolkata")) : Clock {
    override fun now(): Instant = instant

    override fun zone(): ZoneId = zoneId
}

/** A captured request (the body is read eagerly). */
class Captured(val method: String, val url: String, val headers: Map<String, String>, val body: String, val bodyBytes: Int)

/** Fake network: each request is answered by [handler]. Never touches the network. */
class FakeHttp(private val handler: (Captured) -> FakeResponse) {
    val requests: MutableList<Captured> = Collections.synchronizedList(ArrayList())

    val client: OkHttpClient = OkHttpClient.Builder().addInterceptor(Interceptor { chain -> answer(chain.request()) }).build()

    private fun answer(request: Request): Response {
        val buffer = Buffer()
        request.body?.writeTo(buffer)
        val bytes = buffer.size.toInt()
        val captured = Captured(
            request.method,
            request.url.toString(),
            request.headers.names().associate { it.lowercase() to request.header(it).orEmpty() },
            if (bytes < 2_000_000) buffer.readUtf8() else "",
            bytes,
        )
        requests.add(captured)
        val r = handler(captured)
        r.throwable?.let { throw it }
        val builder = Response.Builder().request(request).protocol(Protocol.HTTP_1_1).code(r.status).message("x")
            .body(r.body.toResponseBody("application/json".toMediaType()))
        r.headers.forEach { (k, v) -> builder.header(k, v) }
        return builder.build()
    }
}

class FakeResponse(val status: Int = 200, val body: String = "{}", val headers: Map<String, String> = emptyMap(), val throwable: IOException? = null)

fun tempDir(): File = Files.createTempDirectory("plainspoken-test").toFile().apply { deleteOnExit() }

/** One second of a loud 440 Hz tone, as 16 kHz mono PCM16. */
fun speech(seconds: Double = 1.0): ShortArray = ShortArray((seconds * WavEncoder.TARGET_SAMPLE_RATE).toInt()) {
    (Math.sin(2 * Math.PI * 440 * it / 16000.0) * 8000).toInt().toShort()
}
