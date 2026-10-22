package app.plainspoken.core

import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.offline.ModelStore
import app.plainspoken.core.offline.OfflineModel
import app.plainspoken.core.offline.OfflineModels
import app.plainspoken.core.offline.OfflineWhisper
import app.plainspoken.core.settings.HindiScript
import app.plainspoken.core.settings.LanguagePreset
import app.plainspoken.core.settings.TranscriptionMode
import app.plainspoken.core.transcription.TranscriptionRequest
import kotlinx.coroutines.runBlocking
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class OfflineTests {
    private val content = "pretend whisper model bytes"
    private val model = OfflineModel("test", "ggml-test.bin", "Test", content.length.toLong(), sha256(content))

    private fun sha256(text: String) = MessageDigest.getInstance("SHA-256").digest(text.toByteArray()).joinToString("") { "%02x".format(it) }

    @Test
    fun `a model is downloaded, checked and saved`() = runBlocking {
        val dir = tempDir()
        val http = FakeHttp { FakeResponse(body = content) }
        val store = ModelStore(dir, http.client, ListLog())
        var last = 0L
        store.download(model) { have, _ -> last = have }
        assertTrue(store.isDownloaded(model))
        assertEquals(content, store.file(model).readText())
        assertEquals(content.length.toLong(), last)
        assertEquals(OfflineModels.BASE_URL + "ggml-test.bin", http.requests.single().url)
        store.delete(model)
        assertFalse(store.isDownloaded(model))
    }

    @Test
    fun `a partial download is resumed, and a full answer to the resume still works`() = runBlocking {
        val dir = tempDir()
        File(dir, "ggml-test.bin.part").writeText(content.take(8))
        val resumed = FakeHttp { r ->
            assertEquals("bytes=8-", r.headers["range"])
            FakeResponse(status = 206, body = content.drop(8))
        }
        ModelStore(dir, resumed.client, ListLog()).download(model) { _, _ -> }
        assertEquals(content, File(dir, "ggml-test.bin").readText())

        val dir2 = tempDir()
        File(dir2, "ggml-test.bin.part").writeText(content.take(8))
        ModelStore(dir2, FakeHttp { FakeResponse(body = content) }.client, ListLog()).download(model) { _, _ -> }
        assertEquals(content, File(dir2, "ggml-test.bin").readText())
    }

    @Test
    fun `a file that doesn't match, a failed request or an unpinned model is refused`() = runBlocking {
        val dir = tempDir()
        val store = ModelStore(dir, FakeHttp { FakeResponse(body = "something else entirely!!!") }.client, ListLog())
        assertFailsWith<IOException> { store.download(model) { _, _ -> } }
        assertFalse(store.isDownloaded(model))
        assertFalse(File(dir, "ggml-test.bin.part").exists())

        assertFailsWith<IOException> { ModelStore(dir, FakeHttp { FakeResponse(status = 404) }.client, ListLog()).download(model) { _, _ -> } }
        assertFailsWith<IOException> { store.download(OfflineModel("x", "x.bin", "X", 0, "")) { _, _ -> } }
    }

    @Test
    fun `whisper is told the language and the wanted script`() {
        fun request(languages: LanguagePreset, script: HindiScript = HindiScript.ROMAN) =
            TranscriptionRequest(WavEncoder.encode(speech(1.0)), 1.0, TranscriptionMode.SMART, languages, listOf("WhatsApp"), script)
        assertEquals("en", OfflineWhisper.language(request(LanguagePreset.ENGLISH)))
        assertEquals("auto", OfflineWhisper.language(request(LanguagePreset.ENGLISH_HINDI)))
        assertTrue("Kal meeting hai" in OfflineWhisper.prompt(request(LanguagePreset.ENGLISH_HINDI)))
        assertTrue("WhatsApp" in OfflineWhisper.prompt(request(LanguagePreset.ENGLISH_HINDI)))
        assertEquals(OfflineModels.SMALL, OfflineModels.find(null, OfflineModels.phone))
        assertEquals(OfflineModels.MEDIUM, OfflineModels.find("nope", OfflineModels.pc))
    }
}
