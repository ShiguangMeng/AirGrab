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
    val isFist: Boolean get() = openness <= 0.38f
    val isOpenPalm: Boolean get() = openness >= 0.55f

    companion object {
        val NONE = HandPose()
    }
}

enum class MultiHandPolicy { REJECT, USE_NEAREST }

/**
 * 手势状态机：把连续的手部姿态流收敛成离散的抓取 / 投送事件。
 *
 * 四道防误触闸门，缺一不可：
 * 1. 握拳必须**连续**持续 [holdDurationMs] 才被确认为有意识的抓取；
 * 2. 画面中同时出现两只手时整帧作废，宁可要求重试也不猜测意图；
 * 3. 张开手掌必须连续保持 [openPalmHoldMs] 才被记为「见过张开」，
 *    单帧噪声不会打开抓取的前置条件；
 * 4. 「见过张开」只在 [openPalmValidityMs] 时间窗内有效 —— 张开的动作必须
 *    紧邻随后的握拳，隔了很久的旧状态不能为凭空握拳背书。
 *
 * 第 3、4 条是误触发的直接原因：检测器偶发噪声若把某一帧判为张开，
 * 没有时效约束时它会永久为后续任何握拳放行，表现为「手没动也抓取了内容」。
 */
class GestureStateMachine(
    var holdDurationMs: Long = 500L,
    var multiHandPolicy: MultiHandPolicy = MultiHandPolicy.REJECT
) {
    /** 张开手掌需连续保持的时长，用于确认「见过张开」这一前置条件。 */
    var openPalmHoldMs: Long = 180L

    /** 「见过张开」的有效期；超出则前置条件失效。 */
    var openPalmValidityMs: Long = 2500L

    /** 搬运阶段张开手掌需连续保持的时长，用于确认投送意图。 */
    var dropHoldMs: Long = 120L

    var state: GestureState = GestureState.IDLE
        private set

    var payloadName: String = "—"
        private set

    var onGrabConfirmed: ((HandPose) -> Unit)? = null
    var onDropConfirmed: ((HandPose) -> Unit)? = null
    var onCancelled: (() -> Unit)? = null
    var onStateChanged: ((GestureState) -> Unit)? = null

    /** 连续握拳的起点；0 表示当前不在握拳。 */
    private var fistSince: Long = 0L

    /** 当前这段连续张开的起点；0 表示当前没有张开。 */
    private var openSince: Long = 0L

    /** 最近一次被确认的张开时刻，受 [openPalmValidityMs] 约束；0 表示从未确认过。 */
    private var lastOpenAt: Long = 0L

    /** 搬运阶段连续张开的起点；0 表示当前没有张开。 */
    private var dropSince: Long = 0L

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
            // 搬运阶段：保持拳，掌心位置持续输出；张开并保持即投送。
            if (pose.isOpenPalm) {
                if (dropSince != 0L) {
                    if (now - dropSince >= dropHoldMs) {
                        isCarrying = false
                        dropSince = 0L
                        setState(GestureState.DROP)
                        onDropConfirmed?.invoke(pose)
                    }
                } else {
                    dropSince = now
                }
            } else {
                dropSince = 0L
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
            } else if (now - fistSince >= holdDurationMs &&
                lastOpenAt != 0L &&
                now - lastOpenAt <= openPalmValidityMs
            ) {
                // 抓取成立的前提是「近期」出现过张开手掌，防止凭空握拳触发。
                hasGrabbed = true
                setState(GestureState.GRABBING)
                onGrabConfirmed?.invoke(pose)
            }
            return
        }

        fistSince = 0L

        if (pose.isOpenPalm) {
            // 张开需连续保持 openPalmHoldMs 才确认，单帧噪声不打开抓取前置条件。
            val start = if (openSince == 0L) now else openSince
            openSince = start
            if (now - start >= openPalmHoldMs) {
                lastOpenAt = now
                if (state == GestureState.IDLE || state == GestureState.HOVER) {
                    setState(GestureState.HOVER)
                }
            }
        } else {
            openSince = 0L
            if (state == GestureState.GRABBING) {
                setState(GestureState.HOVER)
            }
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
        dropSince = 0L
        setState(GestureState.DROP)
        onDropConfirmed?.invoke(lastPose)
    }

    fun cancel() {
        val wasActive = isCarrying || hasGrabbed
        hasGrabbed = false
        isCarrying = false
        fistSince = 0L
        openSince = 0L
        dropSince = 0L
        setState(GestureState.IDLE)
        if (wasActive) onCancelled?.invoke()
    }

    /** 一次完整投送结束后复位，等待下一轮手势。 */
    fun finish() {
        hasGrabbed = false
        isCarrying = false
        fistSince = 0L
        openSince = 0L
        lastOpenAt = 0L
        dropSince = 0L
        payloadName = "—"
        setState(GestureState.IDLE)
    }

    private fun resetToIdleIfIdle() {
        if (isCarrying || hasGrabbed) return
        fistSince = 0L
        openSince = 0L
        dropSince = 0L
        setState(GestureState.IDLE)
    }

    private fun setState(next: GestureState) {
        if (state == next) return
        state = next
        onStateChanged?.invoke(next)
    }
}
