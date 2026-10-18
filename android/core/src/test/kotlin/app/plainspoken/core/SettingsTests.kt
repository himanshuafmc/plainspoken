package app.plainspoken.core

import app.plainspoken.core.settings.CleanupProvider
import app.plainspoken.core.settings.EngineKind
import app.plainspoken.core.settings.HindiScript
import app.plainspoken.core.settings.Snippet
import app.plainspoken.core.settings.SpeechService
import app.plainspoken.core.settings.InsertionMethod
import app.plainspoken.core.settings.LanguagePreset
import app.plainspoken.core.settings.LanguagePresets
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.settings.SettingsJson
import app.plainspoken.core.settings.SettingsStore
import app.plainspoken.core.settings.TranscriptionMode
import app.plainspoken.core.settings.TranscriptionSettings
import app.plainspoken.core.settings.VocabularyCleaner
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SettingsTests {
    @Test
    fun `defaults match the shared spec`() {
        val s = PlainspokenSettings().normalize()
        assertEquals(EngineKind.TRANSCRIBE, s.transcription.engine)
        assertEquals("gemini-3.5-transcribe", s.transcription.transcribeModel)
        assertEquals("gemini-3.5-transcribe-live", s.transcription.liveModel)
        assertTrue(s.transcription.liveStreaming)
        assertTrue(s.transcription.useBackupEngineWhenLimited)
        assertEquals(LanguagePreset.ENGLISH_HINDI, s.transcription.languages)
        assertEquals(600, s.recording.maxSeconds)
        assertTrue(s.insertion.trailingSpace)
    }

    @Test
    fun `json uses the same camelCase names and enum strings as Windows`() {
        val s = PlainspokenSettings().apply {
            transcription.languages = LanguagePreset.ENGLISH_HINDI
            transcription.mode = TranscriptionMode.VERBATIM
            insertion.method = InsertionMethod.TYPE
        }
        val json = SettingsJson.serialize(s)
        assertTrue("\"languages\": \"englishHindi\"" in json, json)
        assertTrue("\"mode\": \"verbatim\"" in json)
        assertTrue("\"engine\": \"transcribe\"" in json)
        assertTrue("\"useBackupEngineWhenLimited\": true" in json)
        assertTrue("\"method\": \"type\"" in json)
    }

    @Test
    fun `a file exported on Windows imports here and keeps local values`() {
        val current = PlainspokenSettings().apply { local.apiKeyProtected = "secret-blob" }
        val imported = SettingsJson.import(Fixtures.read("settings-export.json"), current)
        assertEquals(LanguagePreset.ENGLISH, imported.transcription.languages)
        assertEquals(TranscriptionMode.VERBATIM, imported.transcription.mode)
        assertEquals(90, imported.transcription.requestTimeoutSeconds)
        assertFalse(imported.transcription.useBackupEngineWhenLimited)
        assertEquals(listOf("WhatsApp", "Plainspoken", "Hinglish"), imported.transcription.customVocabulary)
        assertEquals(300, imported.recording.maxSeconds)
        assertFalse(imported.recording.sounds)
        assertFalse(imported.insertion.trailingSpace)
        assertFalse(imported.history.enabled)
        assertEquals("Ctrl+Shift+D", imported.hotkey) // Windows-only values survive a round trip
        assertEquals("secret-blob", imported.local.apiKeyProtected)
    }

    @Test
    fun `export never contains the key or the local section`() {
        val s = PlainspokenSettings().apply { local.apiKeyProtected = FakeKey.value }
        val exported = SettingsJson.export(s)
        assertFalse("local" in exported)
        assertFalse(FakeKey.value in exported)
        assertFalse("apiKeyProtected" in exported)
    }

    @Test
    fun `an import file cannot overwrite the local section`() {
        val current = PlainspokenSettings().apply { local.apiKeyProtected = "mine" }
        val imported = SettingsJson.import("""{"local":{"apiKeyProtected":"theirs"}}""", current)
        assertEquals("mine", imported.local.apiKeyProtected)
    }

    @Test
    fun `missing fields get defaults and unknown values are tolerated`() {
        val s = SettingsJson.deserialize(
            """{"transcription":{"engine":"quantum","languages":"klingon","requestTimeoutSeconds":5,"surprise":1},
               "recording":{"maxSeconds":99999},"brandNew":{"x":1}}""",
        )
        assertEquals(EngineKind.TRANSCRIBE, s.transcription.engine)
        assertEquals(LanguagePreset.ENGLISH_HINDI, s.transcription.languages)
        assertEquals(10, s.transcription.requestTimeoutSeconds)
        assertEquals(1800, s.recording.maxSeconds)
    }

    @Test
    fun `null sections and bad urls are repaired`() {
        val s = SettingsJson.deserialize("""{"transcription":{"apiBaseUrl":"ftp://x","transcribeModel":null},"recording":null}""")
        assertEquals(TranscriptionSettings.DEFAULT_API_BASE_URL, s.transcription.apiBaseUrl)
        assertEquals("gemini-3.5-transcribe", s.transcription.transcribeModel)
        assertEquals(600, s.recording.maxSeconds)
        assertEquals("https://example.com/proxy", PlainspokenSettings.normalizeBaseUrl(" https://example.com/proxy/ "))
    }

    @Test
    fun `exported properties are all in the shared schema`() {
        val schema = Json.parseToJsonElement(Fixtures.shared("settings.schema.json")).jsonObject
        val exported = Json.parseToJsonElement(SettingsJson.export(PlainspokenSettings())).jsonObject
        fun check(obj: JsonObject, schemaNode: JsonObject, path: String) {
            val props = schemaNode["properties"]?.jsonObject ?: return
            for ((key, value) in obj) {
                assertTrue(key in props, "$path$key is not in shared/settings.schema.json")
                if (value is JsonObject) check(value, props.getValue(key).jsonObject, "$path$key.")
            }
        }

        check(exported, schema, "")
    }

    @Test
    fun `store saves and quarantines a corrupt file`() {
        val dir = tempDir()
        val file = File(dir, "settings.json")
        val store = SettingsStore(file, ListLog())
        store.save(PlainspokenSettings().apply { transcription.mode = TranscriptionMode.VERBATIM })
        assertEquals(TranscriptionMode.VERBATIM, store.load { PlainspokenSettings() }.transcription.mode)

        file.writeText("{ not json")
        val loaded = store.load { PlainspokenSettings() }
        assertEquals(TranscriptionMode.SMART, loaded.transcription.mode)
        assertTrue(dir.listFiles()!!.any { it.name.startsWith("settings.json.bad-") })
        assertFalse(file.exists())
    }

    @Test
    fun `vocabulary is cleaned like on Windows`() {
        assertEquals(listOf("a b", "WhatsApp"), VocabularyCleaner.clean(listOf("  a   b ", "", "# comment", "WhatsApp", "whatsapp", null)))
        assertEquals(1000, VocabularyCleaner.clean((1..1500).map { "t$it" }).size)
        assertTrue(VocabularyCleaner.clean(listOf("x".repeat(101))).isEmpty())
        val seed = VocabularyCleaner.fromText(Fixtures.shared("vocabulary.default.txt"))
        assertTrue("WhatsApp" in seed && "Plainspoken" in seed && "Hinglish" in seed)
    }

    @Test
    fun `language presets map to codes`() {
        assertEquals(listOf("en-IN", "hi-IN"), LanguagePresets.codes(LanguagePreset.ENGLISH_HINDI))
        assertEquals(listOf("en-IN"), LanguagePresets.codes(LanguagePreset.ENGLISH))
        assertTrue(LanguagePresets.codes(LanguagePreset.AUTO).isEmpty())
    }

    @Test
    fun `schema enum values match what we write`() {
        val schema = Json.parseToJsonElement(Fixtures.shared("settings.schema.json")).jsonObject
        val t = schema["properties"]!!.jsonObject["transcription"]!!.jsonObject["properties"]!!.jsonObject
        val languages = t["languages"]!!.jsonObject["enum"]!!.jsonArray.map { it.jsonPrimitive.content }.toSet()
        assertEquals(setOf("englishHindi", "auto", "english"), languages)
        assertNull(PlainspokenSettings().local.apiKeyProtected)
    }
}

class SettingsVersion2Tests {
    @Test
    fun `version 1 files become a service order`() {
        val generateFirst = SettingsJson.deserialize("""{"schemaVersion":1,"transcription":{"engine":"generate","useBackupEngineWhenLimited":true,"generateModel":"gemini-3.5-flash-lite"}}""")
        assertEquals(listOf("gemini-general", "gemini"), generateFirst.transcription.serviceOrder)
        assertEquals("gemini-3.5-flash", generateFirst.transcription.generateModel)
        assertEquals(2, generateFirst.schemaVersion)

        val noBackup = SettingsJson.deserialize("""{"schemaVersion":1,"transcription":{"engine":"transcribe","useBackupEngineWhenLimited":false,"generateModel":"my-model"}}""")
        assertEquals(listOf("gemini"), noBackup.transcription.serviceOrder)
        assertEquals("my-model", noBackup.transcription.generateModel)
        assertFalse(noBackup.transcription.useBackupEngineWhenLimited)
    }

    @Test
    fun `the service order drops unknown ids and repeats and keeps at most three`() {
        val s = SettingsJson.deserialize("""{"schemaVersion":2,"transcription":{"serviceOrder":["quantum","sarvam","sarvam","deepgram","gemini","openai"]}}""")
        assertEquals(listOf("sarvam", "deepgram", "gemini"), s.transcription.serviceOrder)
        assertEquals(EngineKind.TRANSCRIBE, s.transcription.engine) // Windows fields follow the Gemini choice
        assertFalse(s.transcription.useBackupEngineWhenLimited)
        assertEquals(listOf("gemini"), SettingsJson.deserialize("""{"schemaVersion":2,"transcription":{"serviceOrder":[]}}""").transcription.serviceOrder)
    }

    @Test
    fun `per-service settings get their defaults and bad values are repaired`() {
        val s = SettingsJson.deserialize("""{"schemaVersion":2,"transcription":{"services":{"sarvam":{"model":" ","apiBaseUrl":"nope","pricePerHour":-5},"openai":{"apiBaseUrl":"https://api.groq.com/openai/v1/"}}}}""")
        val t = s.transcription
        assertEquals("saaras:v3", t.services.sarvam.model)
        assertEquals("https://api.sarvam.ai", t.services.sarvam.apiBaseUrl)
        assertEquals(0.0, t.services.sarvam.pricePerHour)
        assertEquals("https://api.groq.com/openai/v1", t.baseUrlOf(SpeechService.OPENAI))
        assertEquals("nova-3", t.modelOf(SpeechService.DEEPGRAM))
        assertEquals("gemini-3.5-transcribe", t.modelOf(SpeechService.GEMINI))
        assertFalse(PlainspokenSettings().normalize().transcription.services.elevenlabs.vocabularyHints)
        assertEquals(30.0, PlainspokenSettings().normalize().transcription.services.sarvam.pricePerHour)
    }

    @Test
    fun `snippets are cleaned and exported, keys never are`() {
        val s = PlainspokenSettings().apply {
            snippets = listOf(Snippet(" my  address ", " 1 Road "), Snippet("My address", "dup"), Snippet("", "x"), Snippet("x".repeat(61), "y"))
            local.apiKeysProtected = mapOf("sarvam" to "blob-" + FakeKey.value)
        }.normalize()
        assertEquals(1, s.snippets.size)
        assertEquals("my address", s.snippets[0].trigger)
        assertEquals("1 Road", s.snippets[0].text)
        val exported = SettingsJson.export(s)
        assertTrue("my address" in exported)
        assertFalse("blob-" in exported || "apiKeysProtected" in exported)
    }

    @Test
    fun `defaults use Gemini with clean-up on and Roman Hindi`() {
        val t = PlainspokenSettings().normalize().transcription
        assertEquals(listOf(SpeechService.GEMINI, SpeechService.GEMINI_GENERAL), t.orderedServices())
        assertEquals(CleanupProvider.GEMINI, t.cleanup.provider)
        assertEquals(HindiScript.ROMAN, t.hindiScript)
        assertEquals(4, t.hedgeSeconds)
        assertTrue(t.compressAudio)
    }
}

class SeedVocabularyTests {
    @Test
    fun `seed vocabulary matches the shared file`() {
        assertEquals(
            VocabularyCleaner.fromText(Fixtures.shared("vocabulary.default.txt")),
            app.plainspoken.core.settings.SeedVocabulary.terms,
        )
    }
}
