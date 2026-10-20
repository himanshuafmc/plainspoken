package app.plainspoken.dot

import android.content.ClipData
import android.content.ClipboardManager
import android.os.Bundle
import android.view.accessibility.AccessibilityNodeInfo
import kotlinx.coroutines.delay

/**
 * Puts dictated text into another app's text box through accessibility actions, at the cursor:
 * first by setting the box's text (the text before the cursor + the dictation + the text after it), checked afterwards;
 * if the app doesn't support that, by pasting it. Never touches password boxes.
 */
object FieldWriter {
    enum class Result { SET, PASTED, FAILED }

    /** The box's text, without its grey placeholder ("Type a message") when it is empty. */
    fun textOf(node: AccessibilityNodeInfo): String =
        if (node.isShowingHintText) "" else node.text?.toString().orEmpty()

    suspend fun insert(node: AccessibilityNodeInfo, text: String, clipboard: ClipboardManager?): Result {
        if (node.isPassword || !node.isEditable) return Result.FAILED
        val current = textOf(node)
        var start = node.textSelectionStart
        var end = node.textSelectionEnd
        if (start < 0 || end < 0 || start > current.length || end > current.length) {
            start = current.length // cursor unknown: add to the end
            end = current.length
        }

        if (start > end) start = end.also { end = start }
        var piece = text
        // Add a space when the cursor sits right after a word, so dictations join up naturally.
        if (start > 0 && !current[start - 1].isWhitespace() && piece.firstOrNull()?.isLetterOrDigit() == true) piece = " $piece"
        val updated = current.substring(0, start) + piece + current.substring(end)

        val supportsSetText = node.actionList.any { it.id == AccessibilityNodeInfo.ACTION_SET_TEXT }
        if (supportsSetText) {
            val args = Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, updated) }
            // Some apps accept the action but ignore it; check before trusting it. The cursor is moved only after the new
            // text shows, because Jetpack Compose apps apply a cursor move to the text they had before.
            if (node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args) && showsNewText(node, current, piece.trim())) {
                val caret = start + piece.length
                node.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, Bundle().apply {
                    putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, caret)
                    putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, caret)
                })
                return Result.SET
            }
        }

        if (clipboard == null) return Result.FAILED
        clipboard.setPrimaryClip(ClipData.newPlainText("Plainspoken", piece))
        return if (node.performAction(AccessibilityNodeInfo.ACTION_PASTE)) Result.PASTED else Result.FAILED
    }

    /** Whether the box now holds the dictation, waiting briefly for apps (Jetpack Compose ones) that show it a frame later. */
    private suspend fun showsNewText(node: AccessibilityNodeInfo, before: String, piece: String): Boolean {
        repeat(CHECKS) { attempt ->
            if (attempt > 0) delay(CHECK_EVERY_MS)
            if (!node.refresh()) return true // the box can't be read back; trust the app
            val now = textOf(node)
            if (now != before && now.contains(piece)) return true
        }

        return false
    }

    private const val CHECKS = 6
    private const val CHECK_EVERY_MS = 60L
}
