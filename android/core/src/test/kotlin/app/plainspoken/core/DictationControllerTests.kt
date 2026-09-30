package app.plainspoken.core

import app.plainspoken.core.dictation.AudioRecorder
import app.plainspoken.core.dictation.DictationController
import app.plainspoken.core.dictation.DictationState
import app.plainspoken.core.dictation.DictationView
import app.plainspoken.core.dictation.InsertOutcome
import app.plainspoken.core.dictation.RecordedAudio
import app.plainspoken.core.dictation.RecorderErrorKind
import app.plainspoken.core.dictation.RecorderException
import app.plainspoken.core.dictation.SoundPlayer
import app.plainspoken.core.dictation.TextInserter
import app.plainspoken.core.dictation.UserAction
import app.plainspoken.core.dictation.UserMessage
import app.plainspoken.core.dictation.UserMessages
import app.plainspoken.core.settings.EngineKind
import app.plainspoken.core.settings.PlainspokenSettings
import app.plainspoken.core.storage.HistoryStore
import app.plainspoken.core.storage.PendingStore
import app.plainspoken.core.transcription.LiveSession
import app.plainspoken.core.transcription.LiveTranscriber
import app.plainspoken.core.transcription.Transcriber
import app.plainspoken.core.transcription.TranscriptionErrorKind
import app.plainspoken.core.transcription.TranscriptionException
import app.plainspoken.core.transcription.TranscriptionOutcome
import app.plainspoken.core.transcription.TranscriptionRequest
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.yield
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class DictationControllerTests {
    private val dir = tempDir()
    private val log = ListLog()
    private val settings = PlainspokenSettings().apply { transcription.liveStreaming = false }.normalize()
    private var hasKey = true
    private val recorder = FakeRecorder()
    private val transcriber = FakeTranscriber()
    private val inserter = FakeInserter()
    private val view = FakeView()
    private val sounds = FakeSounds()
    private val history = HistoryStore(File(dir, "history.json"), log)
    private val pending = PendingStore(File(dir, "pending"), log)
    private val live = FakeLiveTranscriber()

    private fun controller() = DictationController(
        { settings }, { hasKey }, recorder, transcriber, inserter, view, sounds, history, pending, log, FixedClock(), live,
        io = Dispatchers.Unconfined,
    )

    @Test
    fun `happy path inserts the cleaned text with a trailing space and keeps history`() = runTest {
        val c = controller()
        recorder.audio = speech(1.0)
        transcriber.result = "Hello.World"
        c.toggle()
        assertEquals(DictationState.RECORDING, c.state)
        assertEquals(1, sounds.starts)
        assertEquals(1, transcriber.warmUps)
        c.toggle()
        assertEquals(DictationState.IDLE, c.state)
        assertEquals(listOf("Hello. World "), inserter.inserted)
        assertEquals("Hello. World", history.all.single().text)
        assertEquals(0, pending.count)
        assertTrue(view.events.contains("idle"))
    }

    @Test
    fun `short or silent clips are not sent`() = runTest {
        val c = controller()
        recorder.audio = speech(0.3)
        c.toggle()
        c.toggle()
        recorder.audio = ShortArray(32000)
        c.toggle()
        c.toggle()
        assertEquals(0, transcriber.calls)
        assertEquals(listOf(UserMessages.DIDNT_CATCH, UserMessages.DIDNT_CATCH), view.hints)
    }

    @Test
    fun `no key shows the settings action and never records`() = runTest {
        hasKey = false
        controller().toggle()
        assertFalse(recorder.isRecording)
        assertEquals(UserAction.OPEN_SETTINGS, view.errors.single().action)
    }

    @Test
    fun `microphone errors are explained`() = runTest {
        recorder.startError = RecorderException(RecorderErrorKind.ACCESS_DENIED, "denied")
        val c = controller()
        c.toggle()
        assertEquals(DictationState.IDLE, c.state)
        assertEquals(UserAction.GRANT_MICROPHONE, view.errors.single().action)
    }

    @Test
    fun `failures keep the recording and retry delivers it later`() = runTest {
        val c = controller()
        recorder.audio = speech(1.0)
        transcriber.error = TranscriptionException(TranscriptionErrorKind.NETWORK, "offline")
        c.toggle()
        c.toggle()
        assertEquals(1, pending.count)
        assertEquals(UserAction.RETRY, view.errors.single().action)
        assertEquals("NETWORK", pending.latest()!!.lastError)

        transcriber.error = null
        transcriber.result = "Recovered text."
        c.retryLast(insertAtCursor = true)
        assertEquals(listOf("Recovered text. "), inserter.inserted)
        assertEquals(0, pending.count)
    }

    @Test
    fun `retry from the app copies instead of inserting`() = runTest {
        val c = controller()
        recorder.audio = speech(1.0)
        transcriber.error = TranscriptionException(TranscriptionErrorKind.TIMEOUT, "slow")
        c.toggle()
        c.toggle()
        transcriber.error = null
        c.retryLast(insertAtCursor = false)
        assertEquals(listOf("Text. "), inserter.copied)
        assertEquals(UserMessages.COPIED, view.hints.last())
        c.retryLast(insertAtCursor = false)
        assertEquals(UserMessages.NOTHING_TO_RETRY, view.hints.last())
    }

    @Test
    fun `cancel while recording discards, cancel while transcribing keeps the audio`() = runTest {
        val c = controller()
        recorder.audio = speech(1.0)
        c.toggle()
        c.cancel()
        assertEquals(DictationState.IDLE, c.state)
        assertEquals(0, pending.count)
        assertEquals(UserMessages.CANCELLED, view.hints.last())

        transcriber.gate = CompletableDeferred()
        c.toggle()
        val stopping = async(start = CoroutineStart.UNDISPATCHED) { c.toggle() }
        yield()
        assertEquals(DictationState.TRANSCRIBING, c.state)
        c.cancel()
        stopping.await()
        assertEquals(DictationState.IDLE, c.state)
        assertEquals(1, pending.count)
        assertEquals(UserMessages.CANCELLED_SAVED, view.hints.last())
        assertTrue(inserter.inserted.isEmpty())
    }

    @Test
    fun `cancel during the save still cancels and keeps the audio`() = runTest {
        val slowPending = PendingStore(File(dir, "slow"), log)
        val gate = CompletableDeferred<Unit>()
        val c = DictationController(
            { settings }, { hasKey }, recorder, transcriber, inserter, view, sounds, history, slowPending, log, FixedClock(), live,
            io = kotlinx.coroutines.test.StandardTestDispatcher(testScheduler),
        )
        recorder.audio = speech(1.0)
        c.toggle()
        val stopping = async(start = CoroutineStart.UNDISPATCHED) { c.toggle() }
        assertEquals(DictationState.TRANSCRIBING, c.state) // suspended in the save
        c.cancel()
        gate.complete(Unit)
        stopping.await()
        assertEquals(DictationState.IDLE, c.state)
        assertEquals(0, transcriber.calls)
        assertEquals(1, slowPending.count)
        assertEquals(UserMessages.CANCELLED_SAVED, view.hints.last())
    }

    @Test
    fun `live text is used and the normal engine is not called`() = runTest {
        settings.transcription.liveStreaming = true
        live.result = "From the live model."
        val c = controller()
        recorder.audio = speech(1.0)
        c.toggle()
        assertTrue(recorder.sampleSink != null)
        c.toggle()
        assertEquals(0, transcriber.calls)
        assertEquals(listOf("From the live model. "), inserter.inserted)
        assertTrue(live.sessions.single().closed)
        assertNull(recorder.sampleSink)
    }

    @Test
    fun `a live failure or empty text falls back to the normal engine`() = runTest {
        settings.transcription.liveStreaming = true
        val c = controller()
        recorder.audio = speech(1.0)
        transcriber.result = "Batch text."
        live.error = TranscriptionException(TranscriptionErrorKind.UNEXPECTED_RESPONSE, "live: bad setup")
        c.toggle()
        c.toggle()
        live.error = null
        live.result = "   "
        c.toggle()
        c.toggle()
        assertEquals(2, transcriber.calls)
        assertEquals(listOf("Batch text. ", "Batch text. "), inserter.inserted)
        assertTrue(log.lines.any { "using the normal engine" in it })
    }

    @Test
    fun `paused live streaming goes straight to the normal engine`() = runTest {
        settings.transcription.liveStreaming = true
        live.available = false
        val c = controller()
        recorder.audio = speech(1.0)
        c.toggle()
        c.toggle()
        assertTrue(live.sessions.isEmpty())
        assertEquals(1, transcriber.calls)
    }

    @Test
    fun `the tick warns 30 s before the limit and stops at the limit`() = runTest {
        settings.recording.maxSeconds = 90
        val c = controller()
        recorder.audio = speech(1.0)
        c.toggle()
        recorder.elapsed = 61_000
        c.tick()
        assertEquals(UserMessages.THIRTY_SECONDS_LEFT, view.warnings.single())
        recorder.elapsed = 90_000
        c.tick()
        assertEquals(DictationState.IDLE, c.state)
        assertEquals(1, inserter.inserted.size)
    }

    @Test
    fun `when the text box is gone the text is copied`() = runTest {
        inserter.outcome = InsertOutcome.COPIED
        val c = controller()
        recorder.audio = speech(1.0)
        c.toggle()
        c.toggle()
        assertEquals(UserMessages.COPIED, view.hints.last())
    }

    // --- fakes ---

    class FakeRecorder : AudioRecorder {
        var audio: ShortArray = speech(1.0)
        var startError: RecorderException? = null
        var elapsed = 0L
        override var isRecording = false
        override val elapsedMillis: Long get() = elapsed
        override val level: Float = 0.5f
        override var sampleSink: ((ShortArray) -> Unit)? = null

        override suspend fun start() {
            startError?.let { throw it }
            isRecording = true
            sampleSink?.invoke(audio.copyOf(minOf(1600, audio.size)))
        }

        override suspend fun stop(): RecordedAudio {
            isRecording = false
            return RecordedAudio(audio, 16000)
        }

        override suspend fun cancel() {
            isRecording = false
        }
    }

    class FakeTranscriber : Transcriber {
        var result = "Text."
        var error: Exception? = null
        var gate: CompletableDeferred<Unit>? = null
        var calls = 0
        var warmUps = 0

        override suspend fun transcribe(request: TranscriptionRequest, settings: PlainspokenSettings, status: ((String) -> Unit)?): TranscriptionOutcome {
            calls++
            gate?.await()
            error?.let { throw it }
            return TranscriptionOutcome(result, EngineKind.TRANSCRIBE)
        }

        override fun warmUp(settings: PlainspokenSettings) {
            warmUps++
        }
    }

    class FakeInserter : TextInserter {
        val inserted = ArrayList<String>()
        val copied = ArrayList<String>()
        var outcome = InsertOutcome.INSERTED

        override suspend fun insert(text: String): InsertOutcome {
            if (outcome == InsertOutcome.INSERTED) inserted.add(text) else copied.add(text)
            return outcome
        }

        override suspend fun copy(text: String): Boolean {
            copied.add(text)
            return true
        }
    }

    class FakeView : DictationView {
        val events = ArrayList<String>()
        val hints = ArrayList<String>()
        val warnings = ArrayList<String>()
        val errors = ArrayList<UserMessage>()

        override fun onStateChanged(state: DictationState) {
            events.add("state:$state")
        }

        override fun showRecording() {
            events.add("recording")
        }

        override fun showTranscribing(text: String) {
            events.add("transcribing:$text")
        }

        override fun showWarning(text: String) {
            warnings.add(text)
        }

        override fun showHint(text: String) {
            hints.add(text)
        }

        override fun showError(message: UserMessage) {
            errors.add(message)
        }

        override fun showIdle() {
            events.add("idle")
        }
    }

    class FakeSounds : SoundPlayer {
        var starts = 0
        var stops = 0

        override fun playStart() {
            starts++
        }

        override fun playStop() {
            stops++
        }
    }

    class FakeLiveTranscriber : LiveTranscriber {
        var result = "Live text."
        var error: Exception? = null
        var available = true
        val sessions = ArrayList<FakeSession>()

        override fun canStart(settings: PlainspokenSettings) = available

        override fun start(settings: PlainspokenSettings): LiveSession = FakeSession(this).also { sessions.add(it) }
    }

    class FakeSession(private val owner: FakeLiveTranscriber) : LiveSession {
        var pushed = 0
        var closed = false

        override fun push(samples: ShortArray) {
            pushed += samples.size
        }

        override suspend fun finish(): String {
            owner.error?.let { throw it }
            return owner.result
        }

        override fun abort() {
            closed = true
        }
    }
}
