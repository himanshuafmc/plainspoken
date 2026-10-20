package app.plainspoken.platform

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.MediaMuxer
import app.plainspoken.core.audio.AudioCompressor
import app.plainspoken.core.audio.EncodedAudio
import app.plainspoken.core.logging.Log
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Compresses 16 kHz mono PCM to AAC-LC (32 kbit/s) in an MP4 container with Android's built-in encoder, so uploads
 * are about eight times smaller than WAV. Returns null on any problem; the WAV is sent instead.
 */
class AacCompressor(private val cacheDir: File, private val log: Log) : AudioCompressor {
    override fun compress(samples: ShortArray, sampleRate: Int): EncodedAudio? {
        if (samples.isEmpty()) return null
        val out = File.createTempFile("dictation", ".m4a", cacheDir)
        var codec: MediaCodec? = null
        var muxer: MediaMuxer? = null
        try {
            val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, sampleRate, 1).apply {
                setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
                setInteger(MediaFormat.KEY_BIT_RATE, BIT_RATE)
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 16 * 1024)
            }

            codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_AAC)
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            codec.start()
            muxer = MediaMuxer(out.path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)

            val pcm = ByteBuffer.allocate(samples.size * 2).order(ByteOrder.LITTLE_ENDIAN)
            samples.forEach { pcm.putShort(it) }
            pcm.flip()

            val info = MediaCodec.BufferInfo()
            var track = -1
            var inputDone = false
            var presentationUs = 0L
            val bytesPerSecond = sampleRate * 2L
            var idle = 0
            while (true) {
                if (!inputDone) {
                    val inIndex = codec.dequeueInputBuffer(TIMEOUT_US)
                    if (inIndex >= 0) {
                        val buffer = codec.getInputBuffer(inIndex) ?: return null
                        buffer.clear()
                        val count = minOf(buffer.remaining(), pcm.remaining(), CHUNK_BYTES)
                        if (count <= 0) {
                            codec.queueInputBuffer(inIndex, 0, 0, presentationUs, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                            inputDone = true
                        } else {
                            val slice = pcm.slice()
                            slice.limit(count)
                            buffer.put(slice)
                            pcm.position(pcm.position() + count)
                            codec.queueInputBuffer(inIndex, 0, count, presentationUs, 0)
                            presentationUs += count * 1_000_000L / bytesPerSecond
                        }
                    }
                }

                val outIndex = codec.dequeueOutputBuffer(info, TIMEOUT_US)
                when {
                    outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                        track = muxer.addTrack(codec.outputFormat)
                        muxer.start()
                    }
                    outIndex >= 0 -> {
                        idle = 0
                        val buffer = codec.getOutputBuffer(outIndex) ?: return null
                        val isConfig = info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0
                        if (!isConfig && info.size > 0 && track >= 0) {
                            buffer.position(info.offset)
                            buffer.limit(info.offset + info.size)
                            muxer.writeSampleData(track, buffer, info)
                        }

                        codec.releaseOutputBuffer(outIndex, false)
                        if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
                    }
                    else -> if (inputDone && ++idle > MAX_IDLE_POLLS) {
                        log.warn("compress: encoder stopped producing output")
                        return null
                    }
                }
            }

            if (track < 0) return null
            muxer.stop()
            muxer.release()
            muxer = null
            return EncodedAudio(out.readBytes(), "audio/mp4", "audio.m4a")
        } catch (e: Exception) {
            log.warn("compress: ${e.javaClass.simpleName}; sending WAV")
            return null
        } finally {
            runCatching { codec?.stop() }
            runCatching { codec?.release() }
            runCatching { muxer?.release() }
            out.delete()
        }
    }

    private companion object {
        const val BIT_RATE = 32_000
        const val CHUNK_BYTES = 8 * 1024
        const val TIMEOUT_US = 10_000L
        const val MAX_IDLE_POLLS = 300 // about 3 s without output after the end of input
    }
}
