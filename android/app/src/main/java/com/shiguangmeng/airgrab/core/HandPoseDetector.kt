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

    /** 供界面层回报采集层的问题（例如摄像头绑定失败）。 */
    fun reportStatus(message: String) {
        _status.value = message
    }

    private var landmarker: HandLandmarker? = null

    val isReady: Boolean get() = landmarker != null

    fun start() {
        if (landmarker != null) return

        // 前台服务保活：Android 10+ 进程降到后台优先级后摄像头采集会被切断，
        // 声明 camera 类型的前台服务把进程维持在前台优先级。
        CameraForegroundService.start(context.applicationContext)

        runCatching {
            val baseOptions = BaseOptions.builder()
                .setModelAssetPath(MODEL_ASSET)
                .setDelegate(Delegate.CPU)
                .build()

            val options = HandLandmarker.HandLandmarkerOptions.builder()
                .setBaseOptions(baseOptions)
                .setRunningMode(RunningMode.LIVE_STREAM)
                .setNumHands(2)
                // 检测门槛取 0.65：低于此值的结果多为把脸部 / 手臂轮廓误判为手，
                // 误判会直接推进手势状态机，代价远高于偶尔漏检。
                .setMinHandDetectionConfidence(0.65f)
                .setMinTrackingConfidence(0.6f)
                .setResultListener { result, _ -> _pose.value = analyze(result) }
                .setErrorListener { error -> _status.value = "推理错误：${error.message}" }
                .build()

            landmarker = HandLandmarker.createFromOptions(context, options)
            _status.value = "手部 21 关键点 · 端侧推理 · 前台服务保活"
        }.onFailure {
            _status.value = "模型不可用，已回退模拟输入（${it.message}）"
        }
    }

    fun stop() {
        runCatching { landmarker?.close() }
        landmarker = null
        CameraForegroundService.stop(context.applicationContext)
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

        /** 掌长占画面短边的合理区间；越界视为误检。 */
        private const val MIN_PALM_LENGTH = 0.04
        private const val MAX_PALM_LENGTH = 0.70

        /** 归一化张开度区间：掌根到指尖的平均距离 / 掌长。 */
        private const val OPENNESS_FIST = 1.05
        private const val OPENNESS_SPREAD = 1.65

        /**
         * 由 MediaPipe 的 21 关键点折算 openness / 掌心位置。
         *
         * 参考点取**掌根**而非掌心：指尖到掌根的距离在张开与握拳之间差异最大，
         * 用掌心中点做参考会压缩这个差异，导致张开手掌难以越过张开阈值。
         *
         * **置信度不作为判据**：MediaPipe 已用 `minHandDetectionConfidence` 过滤过检测，
         * 而 handedness 分类分数在部分配置下可能为空 —— 拿它当硬门槛会让所有正常结果
         * 一起被挡掉，表现为「摄像头开着但抓取毫无反应」。这里只把该分数当诊断值上报，
         * 真正的误检过滤交给下面的**掌长几何校验**。
         */
        fun analyze(result: HandLandmarkerResult): HandPose {
            val hands = result.landmarks()
            if (hands.isEmpty()) return HandPose.NONE

            val primary = hands.first()
            if (primary.size < 21) return HandPose.NONE

            // 仅用于上屏诊断；取不到时给一个乐观值，不参与判断。
            val score = result.handednesses()
                .getOrNull(0)
                ?.firstOrNull()
                ?.score()
                ?: 0f

            val wrist = primary[WRIST]
            val middleMcp = primary[MIDDLE_MCP]

            val palmLength = hypot(
                (middleMcp.x() - wrist.x()).toDouble(),
                (middleMcp.y() - wrist.y()).toDouble()
            )

            // 几何合理性：脸或背景被误判为手时，关键点的尺度或朝向通常明显异常。
            if (palmLength < MIN_PALM_LENGTH || palmLength > MAX_PALM_LENGTH) return HandPose.NONE

            var sum = 0.0
            for (index in TIP_INDICES) {
                val tip = primary[index]
                sum += hypot((tip.x() - wrist.x()).toDouble(), (tip.y() - wrist.y()).toDouble()) / palmLength
            }
            val ratio = sum / TIP_INDICES.size
            val openness = ((ratio - OPENNESS_FIST) / (OPENNESS_SPREAD - OPENNESS_FIST))
                .coerceIn(0.0, 1.0)
                .toFloat()

            val palmX = (wrist.x() + middleMcp.x()) / 2f
            val palmY = (wrist.y() + middleMcp.y()) / 2f

            return HandPose(
                openness = openness,
                x = palmX.coerceIn(0f, 1f),
                y = palmY.coerceIn(0f, 1f),
                confidence = if (score > 0.01f) score else 0.9f,
                handCount = hands.size
            )
        }
    }
}
