package app.plainspoken.platform

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.SystemClock
import app.plainspoken.core.audio.ClipAnalyzer
import app.plainspoken.core.dictation.AudioRecorder
import app.plainspoken.core.dictation.RecordedAudio
import app.plainspoken.core.dictation.RecorderErrorKind
import app.plainspoken.core.dictation.RecorderException
import app.plainspoken.core.logging.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlin.math.log10

/**
 * Captures 16 kHz mono PCM16 straight from the microphone (no resampling needed on Android) on its own
 * thread, keeps the whole recording in memory and hands each 100 ms block to [sampleSink] for live streaming.
 */
class AndroidRecorder(private val context: Context, private val log: Log) : AudioRecorder {
    @Volatile
    private var record: AudioRecord? = null
    private var thread: Thread? = null

    @Volatile
    private var running = false
    private val samples = Samples()

    @Volatile
    private var startedAt = 0L

    @Volatile
    override var level: Float = 0f
        private set

    @Volatile
    override var sampleSink: ((ShortArray) -> Unit)? = null

    override val isRecording: Boolean get() = running

    override val elapsedMillis: Long get() = if (running) SystemClock.elapsedRealtime() - startedAt else 0L

    @SuppressLint("MissingPermission") // checked just below
    override suspend fun start() {
        if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            throw RecorderException(RecorderErrorKind.ACCESS_DENIED, "RECORD_AUDIO not granted")
        }

        val minBuffer = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        if (minBuffer <= 0) throw RecorderException(RecorderErrorKind.NO_DEVICE, "getMinBufferSize=$minBuffer")
        val bufferBytes = maxOf(minBuffer * 2, CHUNK * 2 * 4)

        var r: AudioRecord? = null
        for (source in listOf(MediaRecorder.AudioSource.VOICE_RECOGNITION, MediaRecorder.AudioSource.MIC)) {
            val candidate = try {
                AudioRecord(source, RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, bufferBytes)
            } catch (e: SecurityException) {
                throw RecorderException(RecorderErrorKind.ACCESS_DENIED, "AudioRecord refused", e)
            } catch (e: IllegalArgumentException) {
                log.warn("mic: source $source rejected (${e.javaClass.simpleName})")
                continue
            }

            if (candidate.state == AudioRecord.STATE_INITIALIZED) {
                r = candidate
                break
            }

            candidate.release()
        }

        val rec = r ?: throw RecorderException(RecorderErrorKind.NO_DEVICE, "AudioRecord not initialised")
        try {
            rec.startRecording()
        } catch (e: IllegalStateException) {
            rec.release()
            throw RecorderException(RecorderErrorKind.FAILED, "startRecording failed", e)
        }

        if (rec.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
            rec.release()
            throw RecorderException(RecorderErrorKind.DEVICE_BUSY, "microphone in use")
        }

        samples.clear()
        level = 0f
        startedAt = SystemClock.elapsedRealtime()
        record = rec
        running = true
        thread = Thread({ readLoop(rec) }, "plainspoken-mic").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    override suspend fun stop(): RecordedAudio {
        release()
        return RecordedAudio(samples.toArray(), RATE)
    }

    override suspend fun cancel() {
        release()
        samples.clear()
    }

    private suspend fun release() {
        val rec = record ?: return
        record = null
        running = false
        withContext(Dispatchers.IO) {
            try {
                rec.stop()
            } catch (_: IllegalStateException) {
            }

            thread?.join(1500)
            rec.release()
        }

        thread = null
        level = 0f
    }

    private fun readLoop(rec: AudioRecord) {
        val chunk = ShortArray(CHUNK)
        try {
            while (running) {
                val n = rec.read(chunk, 0, chunk.size)
                if (n < 0) {
                    log.warn("mic: read error $n")
                    break
                }

                if (n == 0) continue
                val block = chunk.copyOf(n)
                samples.add(block)
                val rms = ClipAnalyzer.rms(block)
                level = if (rms <= 0.0) 0f else ((20 * log10(rms) + 60) / 50).toFloat().coerceIn(0f, 1f)
                try {
                    sampleSink?.invoke(block)
                } catch (e: Exception) {
                    log.error("mic: sample sink failed", e)
                }
            }
        } catch (e: Exception) {
            // An exception escaping this thread would crash the app.
            log.error("mic: capture thread failed", e)
        }
    }

    /** Growing sample buffer shared between the capture thread and the caller. */
    private class Samples {
        private var data = ShortArray(RATE * 10)
        private var size = 0

        @Synchronized
        fun add(block: ShortArray) {
            if (size + block.size > data.size) data = data.copyOf(maxOf(data.size * 2, size + block.size))
            block.copyInto(data, size)
            size += block.size
        }

        @Synchronized
        fun toArray(): ShortArray = data.copyOf(size)

        @Synchronized
        fun clear() {
            data = ShortArray(RATE * 10)
            size = 0
        }
    }

    private companion object {
        const val RATE = 16_000
        const val CHUNK = 1600 // 100 ms
    }
}
