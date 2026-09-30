package app.plainspoken.core.dictation

import app.plainspoken.core.audio.ClipAnalyzer
import app.plainspoken.core.audio.ClipVerdict
import app.plainspoken.core.audio.WavEncoder
import app.plainspoken.core.logging.Log
import app.plainspoken.core.logging.Redactor
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.storage.HistoryStore
import app.plainspoken.core.storage.PendingRecording
import app.plainspoken.core.storage.PendingStore
import app.plainspoken.core.time.Clock
import app.plainspoken.core.time.SystemClock
import app.plainspoken.core.transcription.LiveSession
import app.plainspoken.core.transcription.LiveTranscriber
import app.plainspoken.core.transcription.TextPostProcessor
import app.plainspoken.core.transcription.Transcriber
import app.plainspoken.core.transcription.TranscriptionException
import app.plainspoken.core.transcription.TranscriptionRequest
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import java.io.IOException
import kotlin.coroutines.cancellation.CancellationException

/**
 * The dictation state machine (SPEC §2): Idle → Recording → Transcribing → Idle. A port of the Windows
 * DictationController. Platform-neutral: the app supplies recorder, inserter, view and sounds.
 * All public functions must be called on the UI thread; they never throw (except coroutine cancellation).
 */
class DictationController(
    private val settings: () -> PlainspokenSettings,
    private val hasApiKey: () -> Boolean,
    private val recorder: AudioRecorder,
    private val transcriber: Transcriber,
    private val inserter: TextInserter,
    private val view: DictationView,
    private val sounds: SoundPlayer,
    private val history: HistoryStore,
    private val pending: PendingStore,
    private val log: Log,
    private val clock: Clock = SystemClock,
    private val liveTranscriber: LiveTranscriber? = null,
    /** File work (saving and reading recordings) runs here, off the UI thread. */
    private val io: CoroutineDispatcher = Dispatchers.IO,
) {
    private var live: LiveSession? = null
    private var starting = false
    private var stopping = false
    private var warned = false
    private var transcribeJob: Deferred<String>? = null
    private var cancelRequested = false

    var state: DictationState = DictationState.IDLE
        private set

    val elapsedMillis: Long get() = recorder.elapsedMillis

    val level: Float get() = recorder.level

    /** Mic / ✓ button. */
    suspend fun toggle() {
        try {
            if (starting || stopping) return
            when (state) {
                DictationState.IDLE -> start()
                DictationState.RECORDING -> stopAndTranscribe()
                DictationState.TRANSCRIBING -> view.showHint(UserMessages.BUSY)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            recover(e)
        }
    }

    /** ✕. While recording: discard. While transcribing: abort and keep the audio for Retry. */
    suspend fun cancel() {
        try {
            if (state == DictationState.TRANSCRIBING) {
                cancelRequested = true
                transcribeJob?.cancel()
                return
            }

            if (state != DictationState.RECORDING || starting || stopping) return
            stopping = true
            try {
                recorder.cancel()
            } finally {
                stopping = false
                recorder.sampleSink = null
                dropLive()
            }

            log.info("recording cancelled")
            view.showHint(UserMessages.CANCELLED)
            setState(DictationState.IDLE)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            recover(e)
        }
    }

    /** Call a few times per second from a UI timer: handles the 30-second warning and auto-stop. */
    suspend fun tick() {
        if (state != DictationState.RECORDING || starting || stopping) return
        val maxMs = settings().recording.maxSeconds * 1000L
        val elapsed = recorder.elapsedMillis
        if (elapsed >= maxMs) {
            log.info("maximum recording length reached; auto-stopping")
            toggle()
            return
        }

        if (!warned && maxMs > WARN_BEFORE_END_MS * 2 && elapsed >= maxMs - WARN_BEFORE_END_MS) {
            warned = true
            view.showWarning(UserMessages.THIRTY_SECONDS_LEFT)
        }
    }

    /** Retry the newest saved recording: inserted at the cursor from the keyboard, copied from the app. */
    suspend fun retryLast(insertAtCursor: Boolean) {
        try {
            if (state != DictationState.IDLE || starting || stopping) {
                view.showHint(UserMessages.BUSY)
                return
            }

            val rec = pending.latest()
            if (rec == null) {
                view.showHint(UserMessages.NOTHING_TO_RETRY)
                return
            }

            if (!hasApiKey()) {
                view.showError(UserMessages.NO_KEY)
                return
            }

            val wav = withContext(io) { pending.readAudio(rec) }
            val duration = if (rec.durationSeconds > 0) {
                rec.durationSeconds
            } else {
                WavEncoder.durationSeconds((wav.size - WavEncoder.HEADER_SIZE) / 2)
            }

            log.info("retry: pending recording ${"%.1f".format(java.util.Locale.ROOT, duration)}s, attempt ${rec.attempts + 1}")
            cancelRequested = false
            setState(DictationState.TRANSCRIBING)
            view.showTranscribing("Retrying…")
            transcribeAndDeliver(wav, duration, rec, insertAtCursor, null)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            recover(e)
        }
    }

    private suspend fun start() {
        if (!hasApiKey()) {
            view.showError(UserMessages.NO_KEY)
            return
        }

        val s = settings()
        starting = true
        startLive(s)
        transcriber.warmUp(s)
        try {
            recorder.start()
        } catch (e: RecorderException) {
            log.warn("microphone start failed: ${e.kind} (${e.cause?.javaClass?.simpleName})")
            recorder.sampleSink = null
            dropLive()
            view.showError(UserMessages.forRecorder(e.kind))
            return
        } finally {
            starting = false
        }

        warned = false
        setState(DictationState.RECORDING)
        if (s.recording.sounds) sounds.playStart()
        view.showRecording()
        log.info("recording started")
    }

    private suspend fun stopAndTranscribe() {
        val s = settings()
        cancelRequested = false
        setState(DictationState.TRANSCRIBING)
        view.showTranscribing("Transcribing…")
        if (s.recording.sounds) sounds.playStop()

        stopping = true
        val session = live
        live = null
        val audio = try {
            recorder.stop()
        } catch (e: Exception) {
            if (e is CancellationException) throw e
            log.error("microphone stop failed", e)
            session?.close()
            view.showError(UserMessages.forRecorder(RecorderErrorKind.FAILED))
            setState(DictationState.IDLE)
            return
        } finally {
            stopping = false
            recorder.sampleSink = null
        }

        val verdict = ClipAnalyzer.analyze(audio.samples, audio.sampleRate)
        log.info("recording stopped: ${"%.1f".format(java.util.Locale.ROOT, audio.durationSeconds)}s, clip $verdict")
        if (verdict != ClipVerdict.OK) {
            session?.close()
            view.showHint(UserMessages.DIDNT_CATCH)
            setState(DictationState.IDLE)
            return
        }

        val wav = withContext(io) { WavEncoder.encode(audio.samples, audio.sampleRate) }
        val rec = try {
            withContext(io) { pending.save(wav, audio.durationSeconds, clock.now()) }
        } catch (e: IOException) {
            log.error("could not save the recording for retry; continuing", e)
            null
        }

        transcribeAndDeliver(wav, audio.durationSeconds, rec, insertAtCursor = true, session)
    }

    private suspend fun transcribeAndDeliver(
        wav: ByteArray,
        duration: Double,
        rec: PendingRecording?,
        insertAtCursor: Boolean,
        session: LiveSession?,
    ) {
        val s = settings()
        if (cancelRequested) {
            // ✕ was tapped while the recording was being saved.
            cancelled(rec)
            return
        }

        val text: String
        try {
            text = coroutineScope {
                val job = async {
                    val raw = session?.let { tryLive(it) } ?: transcriber.transcribe(TranscriptionRequest.from(wav, duration, s), s) { status ->
                        if (state == DictationState.TRANSCRIBING) view.showTranscribing(status)
                    }.text
                    TextPostProcessor.clean(raw)
                }

                transcribeJob = job
                job.await()
            }
        } catch (e: CancellationException) {
            if (!cancelRequested || !currentCoroutineContext().isActive) throw e
            cancelled(rec)
            return
        } catch (e: TranscriptionException) {
            log.warn("transcription failed: ${e.kind} — ${e.message}")
            rec?.let { pending.markFailed(it, e.kind.name) }
            view.showError(UserMessages.forError(e, clock.now(), clock.zone()))
            setState(DictationState.IDLE)
            return
        } catch (e: Exception) {
            log.error("transcription crashed", e)
            rec?.let { pending.markFailed(it, "Unexpected") }
            view.showError(UserMessages.UNEXPECTED)
            setState(DictationState.IDLE)
            return
        } finally {
            transcribeJob = null
            cancelRequested = false
        }

        // We have a transcript: the audio is no longer needed.
        rec?.let { pending.delete(it) }
        if (text.isEmpty()) {
            view.showHint(UserMessages.DIDNT_CATCH)
            setState(DictationState.IDLE)
            return
        }

        if (s.history.enabled) history.add(text, duration, clock.now())
        val toInsert = TextPostProcessor.forInsertion(text, s.insertion.trailingSpace)
        val result = if (insertAtCursor) {
            inserter.insert(toInsert)
        } else if (inserter.copy(toInsert)) {
            InsertOutcome.COPIED
        } else {
            InsertOutcome.FAILED
        }

        log.info("delivered: $result, ${text.length} chars")
        when (result) {
            InsertOutcome.INSERTED -> view.showIdle()
            InsertOutcome.COPIED -> view.showHint(UserMessages.COPIED)
            InsertOutcome.FAILED -> view.showError(UserMessages.INSERT_FAILED)
        }

        setState(DictationState.IDLE)
    }

    private fun cancelled(rec: PendingRecording?) {
        cancelRequested = false
        log.info("transcription cancelled by user")
        rec?.let { pending.markFailed(it, "Cancelled") }
        view.showHint(if (rec == null) UserMessages.CANCELLED else UserMessages.CANCELLED_SAVED)
        setState(DictationState.IDLE)
    }

    /** Starts a live (streaming) session if enabled. Any failure just means no live session. */
    private fun startLive(s: PlainspokenSettings) {
        dropLive()
        val lt = liveTranscriber
        if (!s.transcription.liveStreaming || lt == null || !lt.canStart(s)) return
        try {
            val session = lt.start(s)
            recorder.sampleSink = session::push
            live = session
        } catch (e: Exception) {
            log.warn("live: could not start (${e.javaClass.simpleName}); using the normal engine")
            recorder.sampleSink = null
        }
    }

    private fun dropLive() {
        val session = live
        live = null
        session?.close()
    }

    /** The live transcript, or null to fall back to the normal engine. User cancellation propagates. */
    private suspend fun tryLive(session: LiveSession): String? {
        try {
            val text = session.finish()
            if (text.isNotBlank()) return text
            log.warn("live: no text returned; using the normal engine")
        } catch (e: CancellationException) {
            if (cancelRequested || !currentCoroutineContext().isActive) throw e
            log.warn("live: stopped unexpectedly; using the normal engine")
        } catch (e: Exception) {
            val kind = (e as? TranscriptionException)?.kind?.name ?: e.javaClass.simpleName
            log.warn("live: failed ($kind: ${Redactor.clean(e.message, 200)}); using the normal engine")
        } finally {
            session.close()
        }

        return null
    }

    private suspend fun recover(e: Exception) {
        log.error("dictation controller error", e)
        starting = false
        stopping = false
        recorder.sampleSink = null
        dropLive()
        try {
            if (recorder.isRecording) recorder.cancel()
        } catch (inner: Exception) {
            if (inner is CancellationException) throw inner
            log.error("could not release the microphone", inner)
        }

        view.showError(UserMessages.GENERIC)
        setState(DictationState.IDLE)
    }

    private fun setState(newState: DictationState) {
        if (state == newState) return
        state = newState
        view.onStateChanged(newState)
    }

    private companion object {
        const val WARN_BEFORE_END_MS = 30_000L
    }
}
