package com.shiguangmeng.airgrab.core

/**
 * 四端统一的手势状态。
 * 允许的迁移：IDLE → HOVER → GRABBING → CARRYING → DROP → IDLE。
 * 不构成完整「张开 → 握拳 → 移动 → 张开」序列的手部动作一律不响应。
 */
enum class GestureState(val code: String, val title: String, val hint: String, val step: Int) {
    IDLE("IDLE", "待机", "等待手势", 0),
    HOVER("HOVER", "悬停", "手掌已识别", 1),
    GRABBING("GRABBING", "抓取", "握拳确认 0.5s", 2),
    CARRYING("CARRYING", "搬运", "移向目标设备", 3),
    DROP("DROP", "投送", "张开手掌释放", 4)
}

/**
 * 单帧手部姿态。
 * 摄像头通道写入 MediaPipe 关键点的推断值；模拟通道由触摸事件合成同一结构，
 * 因此状态机对输入来源完全无感知。
 *
 * @param openness 0 = 完全握拳，1 = 五指张开
 * @param x 归一化掌心横坐标 0..1
 * @param y 归一化掌心纵坐标 0..1
 */
data class HandPose(
    val openness: Float = 0f,
    val x: Float = 0.5f,
    val y: Float = 0.5f,
    val confidence: Float = 0f,
    val handCount: Int = 0
) {
    val isFist: Boolean get() = openness <= 0.35f
    val isOpenPalm: Boolean get() = openness >= 0.62f

    companion object {
        val NONE = HandPose()
    }
}

enum class MultiHandPolicy { REJECT, USE_NEAREST }

/**
 * 手势状态机：把连续的手部姿态流收敛成离散的抓取 / 投送事件。
 *
 * 两道防误触闸门：
 * 1. 握拳必须持续 [holdDurationMs] 才被确认为有意识的抓取；
 * 2. 画面中同时出现两只手时整帧作废，宁可要求重试也不猜测意图。
 * 另有一道隐含闸门：抓取成立前必须出现过张开手掌，阻止「凭空握拳」触发。
 */
class GestureStateMachine(
    var holdDurationMs: Long = 500L,
    var multiHandPolicy: MultiHandPolicy = MultiHandPolicy.REJECT
) {
    var state: GestureState = GestureState.IDLE
        private set

    var payloadName: String = "—"
        private set

    var onGrabConfirmed: ((HandPose) -> Unit)? = null
    var onDropConfirmed: ((HandPose) -> Unit)? = null
    var onCancelled: (() -> Unit)? = null
    var onStateChanged: ((GestureState) -> Unit)? = null

    private var fistSince: Long = 0L
    private var seenOpenPalm = false
    private var hasGrabbed = false
    private var isCarrying = false
    private var lastPose = HandPose.NONE

    fun ingest(pose: HandPose, now: Long = System.currentTimeMillis()) {
        if (pose.confidence <= 0.25f) {
            resetToIdleIfIdle()
            return
        }
        if (pose.handCount > 1 && multiHandPolicy == MultiHandPolicy.REJECT) {
            cancel()
            return
        }

        lastPose = pose

        if (isCarrying) {
            if (pose.isOpenPalm) {
                isCarrying = false
                setState(GestureState.DROP)
                onDropConfirmed?.invoke(pose)
            }
            return
        }

        if (hasGrabbed) {
            when {
                pose.isFist -> {
                    isCarrying = true
                    setState(GestureState.CARRYING)
                }
                pose.isOpenPalm -> cancel()
            }
            return
        }

        if (pose.isFist) {
            if (fistSince == 0L) {
                fistSince = now
                setState(GestureState.GRABBING)
            } else if (now - fistSince >= holdDurationMs && seenOpenPalm) {
                hasGrabbed = true
                setState(GestureState.GRABBING)
                onGrabConfirmed?.invoke(pose)
            }
            return
        }

        fistSince = 0L

        if (pose.isOpenPalm) {
            seenOpenPalm = true
            if (state == GestureState.IDLE || state == GestureState.HOVER) {
                setState(GestureState.HOVER)
            }
        } else if (state == GestureState.GRABBING) {
            setState(GestureState.HOVER)
        }
    }

    /** 模拟通道在按下即抓取的场景下直接进入搬运。 */
    fun beginCarrying(name: String) {
        hasGrabbed = true
        isCarrying = true
        payloadName = name
        setState(GestureState.CARRYING)
    }

    fun confirmDrop() {
        if (!isCarrying) return
        isCarrying = false
        setState(GestureState.DROP)
        onDropConfirmed?.invoke(lastPose)
    }

    fun cancel() {
        val wasActive = isCarrying || hasGrabbed
        hasGrabbed = false
        isCarrying = false
        fistSince = 0L
        setState(GestureState.IDLE)
        if (wasActive) onCancelled?.invoke()
    }

    /** 一次完整投送结束后复位，等待下一轮手势。 */
    fun finish() {
        hasGrabbed = false
        isCarrying = false
        fistSince = 0L
        seenOpenPalm = false
        payloadName = "—"
        setState(GestureState.IDLE)
    }

    private fun resetToIdleIfIdle() {
        if (isCarrying || hasGrabbed) return
        fistSince = 0L
        setState(GestureState.IDLE)
    }

    private fun setState(next: GestureState) {
        if (state == next) return
        state = next
        onStateChanged?.invoke(next)
    }
}
