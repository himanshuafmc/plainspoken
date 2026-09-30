package app.plainspoken.platform

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Handler
import android.os.Looper
import app.plainspoken.core.audio.ToneGenerator
import app.plainspoken.core.dictation.SoundPlayer
import app.plainspoken.core.logging.Log

/**
 * Start: a short vibration only (a sound would be picked up by the microphone that just opened).
 * Stop: the same soft falling chirp as the Windows app, plus a vibration.
 */
class AndroidSounds(private val log: Log, private val haptic: () -> Unit) : SoundPlayer {
    private val handler = Handler(Looper.getMainLooper())
    private val stopSound by lazy { ToneGenerator.stopSound() }

    override fun playStart() = haptic()

    override fun playStop() {
        haptic()
        play(stopSound)
    }

    private fun play(samples: ShortArray) {
        try {
            val track = AudioTrack.Builder()
                .setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_ASSISTANCE_SONIFICATION)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                        .build(),
                )
                .setAudioFormat(
                    AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(ToneGenerator.RATE)
                        .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                        .build(),
                )
                .setBufferSizeInBytes(samples.size * 2)
                .setTransferMode(AudioTrack.MODE_STATIC)
                .build()
            track.write(samples, 0, samples.size)
            track.play()
            handler.postDelayed({ track.release() }, 1000)
        } catch (e: Exception) {
            log.warn("sound failed: ${e.javaClass.simpleName}")
        }
    }
}
