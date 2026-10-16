import Foundation
import CoreGraphics

/// 四端统一的手势状态。
/// 允许的迁移：idle → hover → grabbing → carrying → drop → idle。
/// 不构成完整「张开 → 握拳 → 移动 → 张开」序列的手部动作一律不响应。
enum GestureState: String, CaseIterable {
    case idle      = "IDLE"
    case hover     = "HOVER"
    case grabbing  = "GRABBING"
    case carrying  = "CARRYING"
    case drop      = "DROP"

    var title: String {
        switch self {
        case .idle:     return "待机"
        case .hover:    return "悬停"
        case .grabbing: return "抓取"
        case .carrying: return "搬运"
        case .drop:     return "投送"
        }
    }

    var hint: String {
        switch self {
        case .idle:     return "等待手势"
        case .hover:    return "手掌已识别"
        case .grabbing: return "握拳确认 0.5s"
        case .carrying: return "移向目标设备"
        case .drop:     return "张开手掌释放"
        }
    }

    /// 进度指示用序号；idle 为 0。
    var step: Int {
        switch self {
        case .idle: return 0
        case .hover: return 1
        case .grabbing: return 2
        case .carrying: return 3
        case .drop: return 4
        }
    }
}

/// 单帧手部姿态的抽象结果。
/// 摄像头通道写入真实关键点推断值；模拟通道由指针事件合成同样结构，
/// 因此状态机对两条输入通道完全无感知。
struct HandPose: Equatable {
    var openness: CGFloat      // 0 = 完全握拳，1 = 五指张开
    var center: CGPoint        // 归一化掌心的屏幕坐标 (0...1)
    var confidence: Float
    var handCount: Int

    static let none = HandPose(openness: 0, center: .init(x: 0.5, y: 0.5), confidence: 0, handCount: 0)

    var isFist: Bool { openness <= 0.35 }
    var isOpenPalm: Bool { openness >= 0.62 }
}

/// 手势状态机：把连续的手部姿态流收敛成离散的抓取 / 投送事件。
///
/// 两个防误触闸门：
/// 1. 握拳必须持续 `holdDuration` 才被确认为有意识的抓取；
/// 2. 画面中同时出现两只手时整帧作废，宁可要求重试也不猜。
final class GestureStateMachine: ObservableObject {
    @Published private(set) var state: GestureState = .idle
    @Published private(set) var payloadName: String = "—"

    var holdDuration: TimeInterval = 0.5
    var multiHandPolicy: MultiHandPolicy = .reject

    enum MultiHandPolicy {
        case reject     // 多手出现时不响应
        case useNearest // 采用置信度最高的一只手
    }

    /// 抓取被确认时回调，携带抓取瞬间的掌心位置。
    var onGrabConfirmed: ((CGPoint) -> Void)?
    /// 投送被确认时回调。
    var onDropConfirmed: ((CGPoint) -> Void)?
    /// 抓取 / 投送被取消时回调。
    var onCancelled: (() -> Void)?

    private var fistSince: Date?
    private var lastOpenSince: Date?
    private var isCarrying = false
    private var hasGrabbed = false
    private var lastCenter: CGPoint = .init(x: 0.5, y: 0.5)

    /// 供状态机判定的单帧输入。
    func ingest(_ pose: HandPose, at now: Date = Date()) {
        guard pose.confidence > 0.25 else {
            resetToIdleIfIdle()
            return
        }
        if pose.handCount > 1 && multiHandPolicy == .reject {
            cancel()
            return
        }

        lastCenter = pose.center

        if isCarrying {
            // 搬运阶段：保持拳，掌心位置持续输出；张开即投送。
            if pose.isOpenPalm {
                state = .drop
                isCarrying = false
                onDropConfirmed?(pose.center)
            }
            return
        }

        if hasGrabbed {
            // 抓取已确认但尚未进入搬运（极短窗口）。
            if pose.isFist {
                isCarrying = true
                state = .carrying
            } else if pose.isOpenPalm {
                cancel()
            }
            return
        }

        if pose.isFist {
            if let since = fistSince {
                if now.timeIntervalSince(since) >= holdDuration {
                    // 抓取成立的前提是此前出现过张开手掌，防止「凭空握拳」触发。
                    if lastOpenSince != nil {
                        hasGrabbed = true
                        state = .grabbing
                        onGrabConfirmed?(pose.center)
                    }
                }
            } else {
                fistSince = now
                state = .grabbing
            }
            return
        }

        fistSince = nil

        if pose.isOpenPalm {
            lastOpenSince = now
            if state == .idle || state == .hover {
                state = .hover
            }
        } else if state == .grabbing {
            state = .hover
        }
    }

    /// 确认抓取后进入搬运，用于模拟通道在按下即抓取的场景。
    func beginCarrying(name: String) {
        hasGrabbed = true
        isCarrying = true
        state = .carrying
        payloadName = name
    }

    func confirmDrop() {
        guard isCarrying else { return }
        isCarrying = false
        state = .drop
        onDropConfirmed?(lastCenter)
    }

    func cancel() {
        let wasActive = isCarrying || hasGrabbed
        hasGrabbed = false
        isCarrying = false
        fistSince = nil
        if state != .idle { state = .idle }
        if wasActive { onCancelled?() }
    }

    /// 一次完整投送结束后复位，等待下一轮手势。
    func finish() {
        hasGrabbed = false
        isCarrying = false
        fistSince = nil
        lastOpenSince = nil
        state = .idle
        payloadName = "—"
    }

    private func resetToIdleIfIdle() {
        guard !isCarrying && !hasGrabbed else { return }
        if state != .idle { state = .idle }
        fistSince = nil
    }
}
