package app.plainspoken.core.audio

import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.PI
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Writes and reads canonical 44-byte-header WAV files holding mono 16-bit PCM.
 * This is the only format Plainspoken sends to Gemini (16 kHz mono PCM16, about 1.9 MB a minute).
 */
object WavEncoder {
    const val HEADER_SIZE = 44
    const val TARGET_SAMPLE_RATE = 16_000

    fun encode(samples: ShortArray, sampleRate: Int = TARGET_SAMPLE_RATE): ByteArray {
        require(sampleRate > 0)
        val dataBytes = Math.multiplyExact(samples.size, 2)
        val b = ByteBuffer.allocate(HEADER_SIZE + dataBytes).order(ByteOrder.LITTLE_ENDIAN)
        b.put("RIFF".toByteArray(Charsets.US_ASCII)).putInt(36 + dataBytes).put("WAVE".toByteArray(Charsets.US_ASCII))
        b.put("fmt ".toByteArray(Charsets.US_ASCII)).putInt(16).putShort(1).putShort(1)
            .putInt(sampleRate).putInt(sampleRate * 2).putShort(2).putShort(16)
        b.put("data".toByteArray(Charsets.US_ASCII)).putInt(dataBytes)
        for (s in samples) b.putShort(s)
        return b.array()
    }

    /** Reads a mono PCM16 WAV, walking the chunk list so files with extra chunks (e.g. LIST) also work. */
    fun decode(wav: ByteArray): Pair<ShortArray, Int> {
        fun id(at: Int) = String(wav, at, 4, Charsets.US_ASCII)
        if (wav.size < 12 || id(0) != "RIFF" || id(8) != "WAVE") throw IllegalArgumentException("Not a RIFF/WAVE file.")
        val b = ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN)
        var sampleRate = 0
        var channels = 0
        var bits = 0
        var format = 0
        var pos = 12
        while (pos + 8 <= wav.size) {
            val chunk = id(pos)
            var size = b.getInt(pos + 4)
            val body = pos + 8
            if (size < 0 || body + size > wav.size) size = wav.size - body // tolerate a truncated last chunk
            if (chunk == "fmt " && size >= 16) {
                format = b.getShort(body).toInt()
                channels = b.getShort(body + 2).toInt()
                sampleRate = b.getInt(body + 4)
                bits = b.getShort(body + 14).toInt()
            } else if (chunk == "data") {
                require(format == 1 && channels == 1 && bits == 16) {
                    "Expected mono PCM16, got format=$format channels=$channels bits=$bits."
                }

                val samples = ShortArray(size / 2) { b.getShort(body + it * 2) }
                return samples to sampleRate
            }

            pos = body + size + (size and 1) // chunks are word-aligned
        }

        throw IllegalArgumentException("WAV has no data chunk.")
    }

    fun durationSeconds(sampleCount: Int, sampleRate: Int = TARGET_SAMPLE_RATE): Double =
        if (sampleRate <= 0) 0.0 else sampleCount.toDouble() / sampleRate
}

/** Audio in a compressed format (AAC in an MP4 container on Android), for services that accept it. */
class EncodedAudio(val bytes: ByteArray, val mimeType: String, val fileName: String)

/** Turns 16 kHz mono PCM16 into [EncodedAudio], or null if this device can't. Implemented per platform. */
fun interface AudioCompressor {
    fun compress(samples: ShortArray, sampleRate: Int): EncodedAudio?
}

/** Splits long recordings for services that only accept short requests (Sarvam: 30 s), cutting at quiet moments. */
object AudioChunker {
    private const val WINDOW_SECONDS = 0.1

    /**
     * Pieces of at most [maxSeconds]. Each cut is placed at the quietest 100 ms within the last [searchSeconds]
     * before the limit, so words aren't split when the speaker pauses.
     */
    fun split(samples: ShortArray, sampleRate: Int, maxSeconds: Double, searchSeconds: Double = 5.0): List<ShortArray> {
        require(sampleRate > 0 && maxSeconds > 1)
        val max = (maxSeconds * sampleRate).toInt()
        if (samples.size <= max) return listOf(samples)
        val window = max(1, (WINDOW_SECONDS * sampleRate).toInt())
        val search = min(max - window, (searchSeconds * sampleRate).toInt())
        val pieces = ArrayList<ShortArray>()
        var start = 0
        while (samples.size - start > max) {
            var cut = start + max
            var quietest = Double.MAX_VALUE
            var pos = start + max - search
            while (pos + window <= start + max) {
                val level = ClipAnalyzer.rms(samples, pos, window)
                if (level < quietest) {
                    quietest = level
                    cut = pos + window / 2
                }

                pos += window / 2
            }

            pieces.add(samples.copyOfRange(start, cut))
            start = cut
        }

        pieces.add(samples.copyOfRange(start, samples.size))
        return pieces
    }
}

enum class ClipVerdict { OK, TOO_SHORT, SILENT }

/** Decides whether a recording is worth sending (avoids wasting free-tier requests). */
object ClipAnalyzer {
    const val MIN_SECONDS = 0.6
    const val FRAME_SECONDS = 0.030

    /** About −45 dBFS. Phone-mic speech is typically −30…−15 dBFS; room noise −60…−50. */
    const val RMS_THRESHOLD = 0.0056
    const val MIN_LOUD_FRAMES = 3

    /** Kept before the first and after the last loud frame when silence is trimmed, for soft starts and endings. */
    const val KEEP_BEFORE_SECONDS = 0.2
    const val KEEP_AFTER_SECONDS = 0.3

    fun analyze(samples: ShortArray, sampleRate: Int = WavEncoder.TARGET_SAMPLE_RATE): ClipVerdict {
        if (sampleRate <= 0 || samples.size < MIN_SECONDS * sampleRate) return ClipVerdict.TOO_SHORT
        return if (countLoudFrames(samples, sampleRate) >= MIN_LOUD_FRAMES) ClipVerdict.OK else ClipVerdict.SILENT
    }

    /**
     * The clip without the silence before its first and after its last loud frame (pauses in between stay), or the clip
     * unchanged when nothing would be trimmed or what is left would be too short.
     */
    fun trimSilence(samples: ShortArray, sampleRate: Int = WavEncoder.TARGET_SAMPLE_RATE): ShortArray {
        if (sampleRate <= 0) return samples
        val frame = max(1, (sampleRate * FRAME_SECONDS).toInt())
        var first = -1
        var last = -1
        var start = 0
        while (start + frame <= samples.size) {
            if (rms(samples, start, frame) >= RMS_THRESHOLD) {
                if (first < 0) first = start
                last = start + frame
            }

            start += frame
        }

        if (first < 0) return samples
        val from = max(0, first - (KEEP_BEFORE_SECONDS * sampleRate).toInt())
        val to = min(samples.size, last + (KEEP_AFTER_SECONDS * sampleRate).toInt())
        if ((from == 0 && to == samples.size) || to - from < MIN_SECONDS * sampleRate) return samples
        return samples.copyOfRange(from, to)
    }

    fun countLoudFrames(samples: ShortArray, sampleRate: Int): Int {
        val frame = max(1, (sampleRate * FRAME_SECONDS).toInt())
        var loud = 0
        var start = 0
        while (start + frame <= samples.size) {
            if (rms(samples, start, frame) >= RMS_THRESHOLD) loud++
            start += frame
        }

        return loud
    }

    fun rms(samples: ShortArray, offset: Int = 0, count: Int = samples.size): Double {
        if (count <= 0) return 0.0
        var sum = 0.0
        for (i in offset until offset + count) {
            val v = samples[i] / 32768.0
            sum += v * v
        }

        return sqrt(sum / count)
    }
}

/** Builds the short, soft start/stop sounds as in-memory PCM (no asset files needed). Same notes as Windows. */
object ToneGenerator {
    const val RATE = 22_050

    /** Rising two-note chirp. */
    fun startSound(): ShortArray = notes(660.0 to 0.07, 880.0 to 0.09)

    /** Falling two-note chirp. */
    fun stopSound(): ShortArray = notes(880.0 to 0.07, 587.0 to 0.09)

    fun notes(vararg notes: Pair<Double, Double>): ShortArray {
        val out = ArrayList<Short>()
        for ((hz, seconds) in notes) {
            val n = (seconds * RATE).toInt()
            val fade = max(1, (0.012 * RATE).toInt())
            for (i in 0 until n) {
                val env = min(1.0, min(i, n - 1 - i) / fade.toDouble())
                val v = sin(2 * PI * hz * i / RATE) * env * 0.18 // quiet
                out.add((v * Short.MAX_VALUE).toInt().toShort())
            }
        }

        return out.toShortArray()
    }
}
