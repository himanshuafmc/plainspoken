package app.plainspoken.core.transcription

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** What the Interactions API returned. [text] is null when no text field was found at all. */
data class InteractionResult(val id: String?, val status: String?, val text: String?, val hasOutputContainer: Boolean)

/**
 * Tolerant parsers for the two response formats. Verified shape (2026-09-29):
 * {"id":"…","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"…"}]}]}
 */
object ResponseParsers {
    fun parseInteraction(root: JsonElement): InteractionResult {
        if (root !is JsonObject) return InteractionResult(null, null, null, false)
        val id = root.str("id")
        val status = root.str("status")

        // 1. SDK-style convenience field, in case the REST API adds it.
        for (name in listOf("output_text", "outputText")) {
            root.str(name)?.let { return InteractionResult(id, status, it, true) }
        }

        val pieces = ArrayList<String>()
        var found = false
        var container = false

        // 2. "outputs": [{"type":"text","text":"…"}]
        (root["outputs"] as? JsonArray)?.let {
            container = true
            found = addTextItems(it, pieces) || found
        }

        // 3. "steps": [{"type":"model_output","content":[{"type":"text","text":"…"}]}]
        val steps = root["steps"] as? JsonArray
        if (!found && steps != null) {
            container = true
            for (step in steps) {
                if (step !is JsonObject) continue
                val type = step.str("type")
                if (type != null && type != "model_output") continue
                for (name in listOf("content", "outputs")) {
                    (step[name] as? JsonArray)?.let { found = addTextItems(it, pieces) || found }
                }

                step.str("text")?.let {
                    pieces.add(it)
                    found = true
                }
            }
        }

        // Several text items (e.g. one per spoken segment) may be trimmed, so join them with care.
        return InteractionResult(id, status, if (found) TranscriptJoiner.join(pieces) else null, container)
    }

    /** Returns the transcript, "" when the model produced no text, or throws for blocked/odd shapes. */
    fun parseGenerateContent(root: JsonElement): String {
        if (root !is JsonObject) throw unexpected(root)
        (root["promptFeedback"] as? JsonObject)?.str("blockReason")?.let {
            throw TranscriptionException(TranscriptionErrorKind.BLOCKED, "generateContent blocked: $it")
        }

        val candidates = root["candidates"] as? JsonArray
        if (candidates.isNullOrEmpty()) throw unexpected(root)
        val candidate = candidates[0]
        val finish = candidate.str("finishReason")
        val sb = StringBuilder()
        val parts = ((candidate as? JsonObject)?.get("content") as? JsonObject)?.get("parts") as? JsonArray
        parts?.forEach { part ->
            if (part !is JsonObject) return@forEach
            if ((part["thought"] as? JsonPrimitive)?.content == "true") return@forEach
            part.str("text")?.let { sb.append(it) }
        }

        if (sb.isEmpty() && finish in setOf("SAFETY", "RECITATION", "BLOCKLIST", "PROHIBITED_CONTENT", "SPII")) {
            throw TranscriptionException(TranscriptionErrorKind.BLOCKED, "generateContent finishReason $finish")
        }

        return sb.toString()
    }

    private fun addTextItems(array: JsonArray, pieces: MutableList<String>): Boolean {
        var found = false
        for (item in array) {
            if (item !is JsonObject) continue
            val type = item.str("type")
            val text = item.str("text")
            if ((type == null || type == "text") && text != null) {
                pieces.add(text)
                found = true
            }
        }

        return found
    }

    private fun unexpected(root: JsonElement) =
        TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "unexpected response shape: " + JsonShape.describe(root))
}
