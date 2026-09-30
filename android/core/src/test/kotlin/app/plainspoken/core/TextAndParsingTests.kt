package app.plainspoken.core

import app.plainspoken.core.audio.ClipAnalyzer
import app.plainspoken.core.audio.ClipVerdict
import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.logging.Redactor
import app.plainspoken.core.settings.LanguagePreset
import app.plainspoken.core.settings.TranscriptionMode
import app.plainspoken.core.time.QuotaReset
import app.plainspoken.core.transcription.GeminiErrors
import app.plainspoken.core.transcription.GeminiGenerateEngine
import app.plainspoken.core.transcription.GeminiTranscribeEngine
import app.plainspoken.core.transcription.JsonShape
import app.plainspoken.core.transcription.ResponseParsers
import app.plainspoken.core.transcription.TextPostProcessor
import app.plainspoken.core.transcription.TranscriptJoiner
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import app.plainspoken.core.transcription.TranscriptionRequest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.util.Base64
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SharedTextCaseTests {
    private val cases = Json.parseToJsonElement(Fixtures.read("text-cases.json")).jsonObject

    @Test
    fun `join cases match the Windows app`() {
        val failures = cases["join"]!!.jsonArray.mapNotNull { c ->
            val o = c.jsonObject
            val pieces = o["pieces"]!!.jsonArray.map { it.jsonPrimitive.content }
            val expected = o["expected"]!!.jsonPrimitive.content
            val actual = TranscriptJoiner.join(pieces)
            if (actual == expected) null else "${o["note"]}: expected <$expected> but was <$actual>"
        }

        assertTrue(failures.isEmpty(), failures.joinToString("\n"))
    }

    @Test
    fun `spacing cases match the Windows app`() {
        val failures = cases["spacing"]!!.jsonArray.mapNotNull { c ->
            val o = c.jsonObject
            val input = o["input"]!!.jsonPrimitive.content
            val expected = o["expected"]!!.jsonPrimitive.content
            val actual = TextPostProcessor.fixSpaceAfterPunctuation(input)
            if (actual == expected) null else "expected <$expected> but was <$actual>"
        }

        assertTrue(failures.isEmpty(), failures.joinToString("\n"))
    }

    @Test
    fun `clean trims fences and quotes and fixes spacing`() {
        assertEquals("Hello.", TextPostProcessor.clean("  Hello.  "))
        assertEquals("Line one\nLine two", TextPostProcessor.clean("Line one\r\nLine two"))
        assertEquals("Hello there", TextPostProcessor.clean("```text\nHello there\n```"))
        assertEquals("Quoted", TextPostProcessor.clean("\"Quoted\""))
        assertEquals("Done. Next.", TextPostProcessor.clean("Done.Next."))
        assertEquals("", TextPostProcessor.clean("   "))
        assertEquals("Hi. ", TextPostProcessor.forInsertion("Hi.", true))
        assertEquals("Hi.", TextPostProcessor.forInsertion("Hi.", false))
    }

    @Test
    fun `join stats never contain text`() {
        val (text, stats) = TranscriptJoiner.joinWithStats(listOf("See you", "tomorrow.", "Bye."))
        assertEquals("See you tomorrow. Bye.", text)
        assertEquals(3, stats.pieces)
        assertEquals(2, stats.spacesAdded)
        assertFalse("tomorrow" in stats.toString())
    }
}

class ParsingTests {
    private fun request(languages: LanguagePreset = LanguagePreset.ENGLISH_HINDI, mode: TranscriptionMode = TranscriptionMode.SMART, vararg vocab: String) =
        TranscriptionRequest(WavEncoder.encode(shortArrayOf(1, 2, 3)), 1.0, mode, languages, vocab.toList())

    @Test
    fun `interactions request matches the shared snapshot`() {
        val body = GeminiTranscribeEngine.buildRequest(
            "gemini-3.5-transcribe",
            "https://generativelanguage.googleapis.com/v1beta/files/1qp4xte94dib",
            request(vocab = arrayOf("WhatsApp", "Plainspoken")),
        )
        assertEquals(Json.parseToJsonElement(Fixtures.read("interactions-request.expected.json")), body)
    }

    @Test
    fun `auto-detect and empty vocabulary omit the fields`() {
        val body = GeminiTranscribeEngine.buildRequest("models/gemini-3.5-transcribe", "u", request(LanguagePreset.AUTO, TranscriptionMode.VERBATIM))
        val config = body["generation_config"]!!.jsonObject["transcription_config"]!!.jsonObject
        assertEquals("verbatim", config["mode"]!!.jsonPrimitive.content)
        assertFalse("language_codes" in config)
        assertFalse("custom_vocabulary" in config)
        assertEquals("gemini-3.5-transcribe", body["model"]!!.jsonPrimitive.content)
    }

    @Test
    fun `inline request carries the audio as base64`() {
        val req = request()
        val audio = GeminiTranscribeEngine.buildInlineRequest("gemini-3.5-transcribe", req)["input"]!!.jsonArray[0].jsonObject
        assertEquals(Base64.getEncoder().encodeToString(req.wav), audio["data"]!!.jsonPrimitive.content)
        assertEquals("audio/wav", audio["mime_type"]!!.jsonPrimitive.content)
    }

    @Test
    fun `generate request has inline audio, prompt and vocabulary`() {
        val req = request(vocab = arrayOf("PowerPoint"))
        val body = GeminiGenerateEngine.buildRequest(req, null)
        val parts = body["contents"]!!.jsonArray[0].jsonObject["parts"]!!.jsonArray
        assertEquals("audio/wav", parts[0].jsonObject["inlineData"]!!.jsonObject["mimeType"]!!.jsonPrimitive.content)
        assertTrue("PowerPoint" in parts[1].jsonObject["text"]!!.jsonPrimitive.content)
        assertTrue("filler" in GeminiGenerateEngine.systemPrompt(TranscriptionMode.SMART))
        assertFalse("Remove filler" in GeminiGenerateEngine.systemPrompt(TranscriptionMode.VERBATIM))
        val viaFile = GeminiGenerateEngine.buildRequest(req, "https://x/v1beta/files/f1")
        assertEquals(
            "https://x/v1beta/files/f1",
            viaFile["contents"]!!.jsonArray[0].jsonObject["parts"]!!.jsonArray[0].jsonObject["fileData"]!!.jsonObject["fileUri"]!!.jsonPrimitive.content,
        )
    }

    @Test
    fun `reads the verified interactions and generate shapes`() {
        val r = ResponseParsers.parseInteraction(Json.parseToJsonElement(Fixtures.read("interactions-response.json")))
        assertEquals("completed", r.status)
        assertEquals("So, please add milk, eggs, and bread to the shopping list. The total is 2,500 rupees.", r.text)
        assertEquals(
            "So, please add milk, eggs, and bread to the shopping list. The total is 2,500 rupees.",
            ResponseParsers.parseGenerateContent(Json.parseToJsonElement(Fixtures.read("generate-response.json"))),
        )
    }

    @Test
    fun `tolerates small shape differences`() {
        fun text(json: String) = ResponseParsers.parseInteraction(Json.parseToJsonElement(json)).text
        assertEquals("hi there", text("""{"status":"completed","output_text":"hi there"}"""))
        assertEquals("hi there", text("""{"status":"completed","outputs":[{"type":"text","text":"hi "},{"type":"thought","text":"x"},{"type":"text","text":"there"}]}"""))
        assertEquals("ab", text("""{"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"a"},{"type":"audio"},{"type":"text","text":"b"}]}]}"""))
        assertEquals(
            "See you tomorrow. Please call me.",
            text("""{"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"See you tomorrow."},{"type":"text","text":"Please call me."}]}]}"""),
        )
        val empty = ResponseParsers.parseInteraction(Json.parseToJsonElement("""{"status":"completed","steps":[]}"""))
        assertNull(empty.text)
        assertTrue(empty.hasOutputContainer)
    }

    @Test
    fun `blocked generate responses throw`() {
        val e = assertFailsWith<TranscriptionException> {
            ResponseParsers.parseGenerateContent(Json.parseToJsonElement("""{"promptFeedback":{"blockReason":"SAFETY"}}"""))
        }
        assertEquals(TranscriptionErrorKind.BLOCKED, e.kind)
    }

    @Test
    fun `json shape never includes text`() {
        val shape = JsonShape.describe(Fixtures.read("interactions-response.json"))
        assertFalse("milk" in shape)
        assertTrue("status:\"completed\"" in shape)
        assertEquals("non-JSON (8 chars)", JsonShape.describe("<html>hi"))
    }
}

class ErrorTests {
    @Test
    fun `per-minute and daily 429s`() {
        val perMinute = GeminiErrors.fromResponse(429, Fixtures.read("error-429-per-minute.json"), null, true)
        assertEquals(TranscriptionErrorKind.RATE_LIMITED, perMinute.kind)
        assertEquals(Duration.ofSeconds(43), perMinute.retryAfter)

        val daily = GeminiErrors.fromResponse(429, Fixtures.read("error-429-daily.json"), null, true)
        assertEquals(TranscriptionErrorKind.DAILY_QUOTA, daily.kind)
        assertEquals(Duration.ofSeconds(40104), daily.retryAfter)

        val wording = GeminiErrors.fromResponse(429, Fixtures.read("error-429-day-wording.json"), null, true)
        assertEquals(TranscriptionErrorKind.RATE_LIMITED, wording.kind)
        assertEquals(TranscriptionErrorKind.DAILY_QUOTA, GeminiErrors.fromResponse(429, "{}", null, true).kind)
        assertEquals(TranscriptionErrorKind.RATE_LIMITED, GeminiErrors.fromResponse(429, "{}", Duration.ofSeconds(20), true).kind)
    }

    @Test
    fun `status codes map like on Windows`() {
        assertEquals(TranscriptionErrorKind.INVALID_KEY, GeminiErrors.fromResponse(400, Fixtures.read("error-400-invalid-key.json"), null, true).kind)
        assertEquals(TranscriptionErrorKind.INVALID_KEY, GeminiErrors.fromResponse(401, "<html>", null, true).kind)
        assertEquals(TranscriptionErrorKind.TIMEOUT, GeminiErrors.fromResponse(408, null, null, true).kind)
        assertEquals(TranscriptionErrorKind.SERVER, GeminiErrors.fromResponse(503, null, null, true).kind)
        assertEquals(TranscriptionErrorKind.MODEL_NOT_FOUND, GeminiErrors.fromResponse(404, "{}", null, true).kind)
        assertEquals(TranscriptionErrorKind.BAD_REQUEST, GeminiErrors.fromResponse(418, null, null, true).kind)
    }

    @Test
    fun `keys are redacted from error details`() {
        val e = GeminiErrors.fromResponse(400, """{"error":{"message":"bad key=${FakeKey.value}","code":"invalid_request"}}""", null, true)
        assertFalse(FakeKey.value in e.message!!)
        assertFalse(FakeKey.value in Redactor.clean("x-goog-api-key: ${FakeKey.value}"))
    }

    @Test
    fun `retry delay parsing`() {
        assertEquals(Duration.ofSeconds(43), GeminiErrors.parseRetryFromMessage("Please retry in 43s or upgrade"))
        assertEquals(Duration.ofMillis(1500), GeminiErrors.parseRetryFromMessage("Please retry in 1.5s."))
        assertEquals(Duration.ofMillis(250), GeminiErrors.parseRetryFromMessage("retry in 250ms"))
        assertNull(GeminiErrors.parseRetryFromMessage("nothing"))
    }
}

class AudioAndTimeTests {
    @Test
    fun `wav round trip`() {
        val samples = shortArrayOf(0, 1, -1, 32767, -32768)
        val wav = WavEncoder.encode(samples)
        assertEquals(44 + 10, wav.size)
        val (decoded, rate) = WavEncoder.decode(wav)
        assertEquals(samples.toList(), decoded.toList())
        assertEquals(16000, rate)
    }

    @Test
    fun `clip checks`() {
        assertEquals(ClipVerdict.TOO_SHORT, ClipAnalyzer.analyze(speech(0.3)))
        assertEquals(ClipVerdict.SILENT, ClipAnalyzer.analyze(ShortArray(16000)))
        assertEquals(ClipVerdict.OK, ClipAnalyzer.analyze(speech(1.0)))
    }

    @Test
    fun `quota reset is shown in local time`() {
        val now = Instant.parse("2026-07-15T10:00:00Z") // 3 AM in Los Angeles (PDT)
        assertEquals(Instant.parse("2026-07-16T07:00:00Z"), QuotaReset.nextReset(now))
        assertEquals("12:30 PM IST tomorrow", QuotaReset.formatNextReset(now, ZoneId.of("Asia/Kolkata")))
    }
}
