package app.plainspoken.core.dictation

import app.plainspoken.core.time.QuotaReset
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import java.time.Instant
import java.time.ZoneId

enum class DictationState { IDLE, RECORDING, TRANSCRIBING }

/** 16 kHz mono PCM16 audio. */
class RecordedAudio(val samples: ShortArray, val sampleRate: Int) {
    val durationSeconds: Double get() = if (sampleRate <= 0) 0.0 else samples.size.toDouble() / sampleRate
}

enum class RecorderErrorKind { ACCESS_DENIED, NO_DEVICE, DEVICE_BUSY, FAILED }

class RecorderException(val kind: RecorderErrorKind, message: String, cause: Throwable? = null) : Exception(message, cause)

/** Microphone capture, implemented per platform (Android: AudioRecord at 16 kHz mono). */
interface AudioRecorder {
    /** Starts capturing. Throws [RecorderException] on failure. */
    suspend fun start()

    /** Stops, releases the microphone and returns the audio. */
    suspend fun stop(): RecordedAudio

    /** Stops, releases the microphone and discards the audio. */
    suspend fun cancel()

    val isRecording: Boolean

    val elapsedMillis: Long

    /** 0..1, safe to read from the UI thread. */
    val level: Float

    /**
     * Optional: receives each new block of 16 kHz mono samples as it is captured (on the audio thread),
     * to stream audio while the user is still speaking. Set before [start].
     */
    var sampleSink: ((ShortArray) -> Unit)?
}

enum class InsertOutcome { INSERTED, COPIED, FAILED }

/** Puts text where the user's cursor is (Android: the keyboard's input connection). */
interface TextInserter {
    suspend fun insert(text: String): InsertOutcome

    suspend fun copy(text: String): Boolean
}

interface SoundPlayer {
    fun playStart()

    fun playStop()
}

enum class UserAction { NONE, OPEN_SETTINGS, RETRY, GRANT_MICROPHONE }

data class UserMessage(val text: String, val action: UserAction = UserAction.NONE)

/** Everything the user sees during dictation. All members are called on the UI thread. */
interface DictationView {
    fun onStateChanged(state: DictationState)

    fun showRecording()

    fun showTranscribing(text: String)

    fun showWarning(text: String)

    /** Short, self-dismissing hint (e.g. "Didn't catch that"). */
    fun showHint(text: String)

    /** Error the user needs to read, optionally with an action button. */
    fun showError(message: UserMessage)

    /** Back to the normal idle look. */
    fun showIdle()
}

/** Friendly wording for everything that can go wrong (SPEC §4.3), worded for the Android keyboard. */
object UserMessages {
    const val DIDNT_CATCH = "Didn't catch that"
    const val BUSY = "Busy — still transcribing"
    const val COPIED = "Copied — long-press the text box and tap Paste"
    const val CANCELLED = "Cancelled"
    const val CANCELLED_SAVED = "Stopped — recording saved. Tap Retry to try again."
    const val NOTHING_TO_RETRY = "Nothing to retry"
    const val THIRTY_SECONDS_LEFT = "30 s left"

    val NO_KEY = UserMessage("Add your free Gemini API key in the Plainspoken app to start.", UserAction.OPEN_SETTINGS)
    val UNEXPECTED = UserMessage("Something went wrong. Your recording is saved — tap Retry.", UserAction.RETRY)
    val INSERT_FAILED = UserMessage("Couldn't insert the text. It is in History in the Plainspoken app.")
    val GENERIC = UserMessage("Something went wrong. Please try again.")

    fun forError(e: TranscriptionException, now: Instant, zone: ZoneId): UserMessage = when (e.kind) {
        TranscriptionErrorKind.NO_KEY -> NO_KEY
        TranscriptionErrorKind.INVALID_KEY ->
            UserMessage("Your Gemini API key was not accepted. Check it in Plainspoken settings. Your recording is saved.", UserAction.OPEN_SETTINGS)
        TranscriptionErrorKind.DAILY_QUOTA ->
            UserMessage("Free daily limit reached. Resets at ${QuotaReset.formatNextReset(now, zone)}. Your recording is saved.", UserAction.RETRY)
        TranscriptionErrorKind.RATE_LIMITED ->
            UserMessage("Too many requests right now. Your recording is saved — tap Retry in a minute.", UserAction.RETRY)
        TranscriptionErrorKind.NETWORK ->
            UserMessage("No internet connection. Your recording is saved — tap Retry when you're back online.", UserAction.RETRY)
        TranscriptionErrorKind.TIMEOUT -> UserMessage("Google took too long to answer. Your recording is saved — tap Retry.", UserAction.RETRY)
        TranscriptionErrorKind.SERVER -> UserMessage("Google's service had a problem. Your recording is saved — tap Retry.", UserAction.RETRY)
        TranscriptionErrorKind.MODEL_NOT_FOUND ->
            UserMessage("The speech model isn't available. Check the model name in Settings → Advanced.", UserAction.OPEN_SETTINGS)
        TranscriptionErrorKind.BLOCKED -> UserMessage("Google declined to transcribe this recording. It is saved — you can tap Retry.", UserAction.RETRY)
        else -> UserMessage("Something unexpected came back from Google. Your recording is saved — tap Retry.", UserAction.RETRY)
    }

    fun forRecorder(kind: RecorderErrorKind): UserMessage = when (kind) {
        RecorderErrorKind.ACCESS_DENIED -> UserMessage("Plainspoken needs permission to use the microphone.", UserAction.GRANT_MICROPHONE)
        RecorderErrorKind.NO_DEVICE -> UserMessage("No microphone is available.")
        RecorderErrorKind.DEVICE_BUSY -> UserMessage("Another app (for example a call) is using the microphone. Try again when it's free.")
        RecorderErrorKind.FAILED -> UserMessage("Couldn't start the microphone. Try again.")
    }
}
