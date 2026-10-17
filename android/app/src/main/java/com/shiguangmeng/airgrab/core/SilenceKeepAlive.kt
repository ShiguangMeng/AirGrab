package com.shiguangmeng.airgrab.core

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack

/**
 * 静音音频保活。
 *
 * Android 的前台服务已经把进程维持在前台优先级，理论上不需要音频 ——
 * 但部分厂商 ROM（尤其国产系统）仍会在息屏后冻结前台服务里的摄像头采集。
 * 持续输出一段零幅度 PCM 会让系统把本进程判定为"正在播放媒体"，
 * 从而避开这类额外的省电策略。
 *
 * 音量恒为 0 的缓冲区循环播放，用户听不到任何声音，也不占用音频焦点
 * （USAGE_MEDIA + MODE_STATIC，不申请独占）。
 */
class SilenceKeepAlive {
    private var track: AudioTrack? = null

    val isRunning: Boolean get() = track != null

    fun start() {
        if (track != null) return

        runCatching {
            val sampleRate = 44_100
            val minBytes = AudioTrack.getMinBufferSize(
                sampleRate,
                AudioFormat.CHANNEL_OUT_MONO,
                AudioFormat.ENCODING_PCM_16BIT
            )
            val frames = if (minBytes > 0) minBytes / 2 else 4096
            val silence = ShortArray(frames)   // 全零即完全静音

            val created = AudioTrack.Builder()
                .setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                        .build()
                )
                .setAudioFormat(
                    AudioFormat.Builder()
                        .setSampleRate(sampleRate)
                        .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .build()
                )
                .setBufferSizeInBytes(frames * 2)
                .setTransferMode(AudioTrack.MODE_STATIC)
                .build()

            created.write(silence, 0, silence.size)
            // -1 表示无限循环
            created.setLoopPoints(0, silence.size, -1)
            created.setVolume(0f)
            created.play()

            track = created
        }
    }

    fun stop() {
        val current = track ?: return
        track = null

        runCatching { current.stop() }
        runCatching { current.release() }
    }
}
