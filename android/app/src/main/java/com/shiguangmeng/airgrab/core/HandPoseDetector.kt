package com.shiguangmeng.airgrab.core

import android.content.Context
import android.graphics.Bitmap
import com.google.mediapipe.framework.image.BitmapImageBuilder
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.core.Delegate
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.handlandmarker.HandLandmarker
import com.google.mediapipe.tasks.vision.handlandmarker.HandLandmarkerResult
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlin.math.hypot

/**
 * 摄像头通道：CameraX 采集 + MediaPipe Hand Landmarker 手部 21 关键点。
 *
 * 张开度 openness 的推导方式：取四指指尖到掌心点的平均距离，
 * 用掌根到中指掌指关节的距离（掌宽）归一化。
 * 五指张开时指尖远离掌心，握拳时指尖回落到掌心附近：
 * 归一化距离 > 1.35 视为全张开，< 0.45 视为握拳，中间线性映射到 0...1。
 */
class HandPoseDetector(private val context: Context) {

    private val _pose = MutableStateFlow(HandPose.NONE)
    val pose: StateFlow<HandPose> = _pose.asStateFlow()

    private val _status = MutableStateFlow("未启动")
    val status: StateFlow<String> = _status.asStateFlow()

    private var landmarker: HandLandmarker? = null

    val isReady: Boolean get() = landmarker != null

    fun start() {
        if (landmarker != null) return

        runCatching {
            val baseOptions = BaseOptions.builder()
                .setModelAssetPath(MODEL_ASSET)
                .setDelegate(Delegate.CPU)
                .build()

            val options = HandLandmarker.HandLandmarkerOptions.builder()
                .setBaseOptions(baseOptions)
                .setRunningMode(RunningMode.LIVE_STREAM)
                .setNumHands(2)
                .setMinHandDetectionConfidence(0.5f)
                .setMinTrackingConfidence(0.5f)
                .setResultListener { result, _ -> _pose.value = analyze(result) }
                .setErrorListener { error -> _status.value = "推理错误：${error.message}" }
                .build()

            landmarker = HandLandmarker.createFromOptions(context, options)
            _status.value = "手部 21 关键点 · 端侧推理"
        }.onFailure {
            _status.value = "模型不可用，已回退模拟输入（${it.message}）"
        }
    }

    fun stop() {
        runCatching { landmarker?.close() }
        landmarker = null
        _status.value = "已停止"
    }

    fun detect(bitmap: Bitmap, timestampMs: Long) {
        val detector = landmarker ?: return
        runCatching {
            detector.detectAsync(BitmapImageBuilder(bitmap).build(), timestampMs)
        }
    }

    companion object {
        /** 模型来自 MediaPipe 官方模型库，由 CI 在构建前下载到 assets。 */
        const val MODEL_ASSET = "hand_landmarker.task"

        private const val WRIST = 0
        private const val MIDDLE_MCP = 9
        private val TIP_INDICES = intArrayOf(8, 12, 16, 20)

        fun analyze(result: HandLandmarkerResult): HandPose {
            val hands = result.landmarks()
            if (hands.isEmpty()) return HandPose.NONE

            val primary = hands.first()
            if (primary.size < 21) return HandPose.NONE

            val wrist = primary[WRIST]
            val middleMcp = primary[MIDDLE_MCP]

            val palmScale = maxOf(
                hypot((middleMcp.x() - wrist.x()).toDouble(), (middleMcp.y() - wrist.y()).toDouble()),
                0.001
            )

            val palmX = (wrist.x() + middleMcp.x()) / 2f
            val palmY = (wrist.y() + middleMcp.y()) / 2f

            var sum = 0.0
            var count = 0
            for (index in TIP_INDICES) {
                val tip = primary[index]
                sum += hypot((tip.x() - palmX).toDouble(), (tip.y() - palmY).toDouble()) / palmScale
                count++
            }
            if (count == 0) return HandPose.NONE

            val average = sum / count
            val openness = ((average - 0.45) / (1.35 - 0.45)).coerceIn(0.0, 1.0).toFloat()

            return HandPose(
                openness = openness,
                x = palmX.coerceIn(0f, 1f),
                y = palmY.coerceIn(0f, 1f),
                confidence = 0.9f,
                handCount = hands.size
            )
        }
    }
}
