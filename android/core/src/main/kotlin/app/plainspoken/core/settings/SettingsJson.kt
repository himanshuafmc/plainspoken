package app.plainspoken.core.settings

import app.plainspoken.core.logging.Log
import app.plainspoken.core.storage.AtomicFile
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import java.io.File
import java.io.IOException
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

/** JSON for settings, plus export/import without the local section. Same format as the Windows app. */
object SettingsJson {
    @OptIn(ExperimentalSerializationApi::class)
    val json = Json {
        ignoreUnknownKeys = true
        coerceInputValues = true
        encodeDefaults = true
        explicitNulls = true
        isLenient = true
        allowTrailingComma = true
        allowComments = true
        prettyPrint = true
        prettyPrintIndent = "  "
    }

    fun serialize(settings: PlainspokenSettings): String =
        json.encodeToString(PlainspokenSettings.serializer(), settings)

    /** Tolerant: unknown fields ignored, unknown enum values and nulls fall back to defaults. */
    fun deserialize(text: String): PlainspokenSettings =
        json.decodeFromString(PlainspokenSettings.serializer(), text).normalize()

    /** Shareable JSON: everything except `local` (so never the API key). */
    fun export(settings: PlainspokenSettings): String {
        val obj = json.parseToJsonElement(serialize(settings)).jsonObject
        return json.encodeToString(JsonObject.serializer(), JsonObject(obj - "local"))
    }

    /** Applies an exported file on top of [current], keeping this device's local values. */
    fun import(text: String, current: PlainspokenSettings): PlainspokenSettings {
        val element = json.parseToJsonElement(text)
        if (element !is JsonObject) throw SerializationException("Settings file must be a JSON object.")
        val imported = json.decodeFromJsonElement(PlainspokenSettings.serializer(), JsonObject(element - "local"))
        imported.local = clone(current).local
        return imported.normalize()
    }

    fun clone(settings: PlainspokenSettings): PlainspokenSettings = deserialize(serialize(settings))
}

/** Loads and saves settings.json. A corrupt file is kept aside and defaults are used. */
class SettingsStore(val file: File, private val log: Log) {
    val exists: Boolean get() = file.exists()

    fun load(createDefaults: () -> PlainspokenSettings): PlainspokenSettings {
        if (!file.exists()) return createDefaults().normalize()
        return try {
            SettingsJson.deserialize(file.readText())
        } catch (e: Exception) {
            if (e !is IOException && e !is SerializationException && e !is IllegalArgumentException) throw e
            val aside = File(file.path + ".bad-" + LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMddHHmmss")))
            log.error("Settings file unreadable (${e.javaClass.simpleName}); moved to ${aside.name} and using defaults.")
            file.renameTo(aside)
            createDefaults().normalize()
        }
    }

    fun save(settings: PlainspokenSettings) = AtomicFile.writeText(file, SettingsJson.serialize(settings.normalize()))
}
