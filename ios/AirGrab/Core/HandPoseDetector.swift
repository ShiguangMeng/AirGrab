import Foundation
import AVFoundation
import Vision
import CoreGraphics
import Combine
import UserNotifications

/// 摄像头通道：AVFoundation 采集前置摄像头 + Vision 手部 21 关键点。
///
/// **置信度与稳定性是本类的主要职责。** Vision 的手部检测会把头部轮廓、
/// 手臂边缘等误判为手，因此原始观测不能直接喂给状态机，必须先过四道闸门：
///
/// 1. 整体手部置信度 —— 低于 `minObservationConfidence` 的观测整帧丢弃；
/// 2. 关键点质量 —— 高置信关键点不足 `minReliableJoints` 个则丢弃，
///    且手腕与中指掌指关节必须可靠（张开度依赖这两点做尺度）；
/// 3. 手掌尺度合理性 —— 手掌在画面中的占比必须落在 `palmScaleRange` 内，
///    脸或背景物体被误检时，关键点尺度通常明显偏小；
/// 4. 时间一致性 —— 姿态类别需连续 `stableFramesRequired` 帧一致才切换，
///    单帧噪声不进入状态机。
///
/// 张开度 openness 的推导：以**掌根**为参考点，取四指指尖到掌根的平均距离，
/// 用掌长（掌根 → 中指掌指关节）归一化。五指张开时指尖远离掌根，握拳时回落到掌中部，
/// 归一化值约 1.05（握拳）～ 1.65（五指张开）。
final class HandPoseDetector: NSObject, ObservableObject {
    @Published private(set) var pose: HandPose = .none
    @Published private(set) var isRunning = false
    @Published private(set) var statusText: String = "未启动"
    /// 诊断行：供界面显示原始张开度比值，便于按实际手型标定。
    @Published private(set) var diagnostics: String = "—"

    /// 模拟器与无摄像头设备上不启动采集，仅回报状态。
    static var isCameraAvailable: Bool {
        #if targetEnvironment(simulator)
        return false
        #else
        return AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .front) != nil
        #endif
    }

    // MARK: - 质量闸门参数

    /// 整体手部置信度下限。
    private let minObservationConfidence: Float = 0.6
    /// 单点置信度下限。
    private let minJointConfidence: Float = 0.5
    /// 达标关键点的最少数量（共 21 点）。
    private let minReliableJoints = 12
    /// 掌长占画面短边的合理区间；越界视为误检。
    private let palmScaleRange: ClosedRange<CGFloat> = 0.04...0.70
    /// 姿态类别需要连续一致的帧数。
    private let stableFramesRequired = 3
    /// openness 滑动窗口长度，取中位数以抑制椒盐噪声。
    private let smoothWindow = 5

    /// 归一化张开度区间：掌根到指尖的平均距离 / 掌长。
    private let opennessFist: CGFloat = 1.05
    private let opennessSpread: CGFloat = 1.65

    /// 姿态类别，用于时间一致性判定。
    private enum PoseCategory { case fist, open, neutral }

    private let session = AVCaptureSession()
    private let request = VNDetectHumanHandPoseRequest()
    private let queue = DispatchQueue(label: "airgrab.vision")
    private var isConfigured = false

    /// 后台保活播放器。iOS 会在进程进入后台时挂起 → AVCaptureSession 停止出帧，
    /// 声明 audio 后台模式并循环播放一段零幅度缓冲，可把进程维持在前台等价态，
    /// 采集与推理得以继续。仅在 `keepsRunningInBackground` 为真时启用。
    private let keepAliveEngine = AVAudioEngine()
    private let keepAliveNode = AVAudioPlayerNode()
    private var keepAliveRunning = false

    /// 是否在进入后台后继续采集。置为 false 可在不需要时避免耗电。
    var keepsRunningInBackground = true

    private var recentOpenness: [CGFloat] = []
    private var recentCategories: [PoseCategory] = []
    private var lastCategory: PoseCategory = .neutral
    private var stableCount = 0
    private var lastPublishedOpenness: CGFloat = 0

    private static let noticeIdentifier = "airgrab.keepalive"

    func start() {
        guard Self.isCameraAvailable else {
            statusText = "无可用摄像头 · 已回退模拟输入"
            return
        }
        startKeepAlive()
        queue.async { [weak self] in
            guard let self else { return }
            self.configureIfNeeded()
            if !self.session.isRunning {
                self.session.startRunning()
            }
            DispatchQueue.main.async {
                self.isRunning = true
                self.statusText = self.keepsRunningInBackground
                    ? "手部 21 关键点 · 端侧推理 · 后台保活中"
                    : "手部 21 关键点 · 端侧推理"
            }
        }
    }

    func stop() {
        stopKeepAlive()
        queue.async { [weak self] in
            guard let self else { return }
            if self.session.isRunning { self.session.stopRunning() }
            DispatchQueue.main.async {
                self.isRunning = false
                self.statusText = "已停止"
            }
        }
    }

    // MARK: - 后台保活

    /// 以静音音频把进程维持在前台等价态，使采集在切到后台后继续出帧。
    private func startKeepAlive() {
        guard keepsRunningInBackground, !keepAliveRunning else { return }
        keepAliveRunning = true

        postBackgroundNotice()

        DispatchQueue.main.async { [weak self] in
            guard let self else { return }

            do {
                let audioSession = AVAudioSession.sharedInstance()
                try audioSession.setCategory(.playback, mode: .default, options: [.mixWithOthers])
                try audioSession.setActive(true)
            } catch {
                return
            }

            guard let format = AVAudioFormat(standardFormatWithSampleRate: 44_100, channels: 1),
                  let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 44_100) else { return }

            // 缓冲默认零填充 —— 循环播放一段完全静音的 1 秒 PCM。
            buffer.frameLength = 44_100

            self.keepAliveEngine.attach(self.keepAliveNode)
            self.keepAliveEngine.connect(
                self.keepAliveNode,
                to: self.keepAliveEngine.mainMixerNode,
                format: format
            )
            self.keepAliveEngine.mainMixerNode.outputVolume = 0

            do {
                try self.keepAliveEngine.start()
            } catch {
                return
            }

            self.keepAliveNode.scheduleBuffer(buffer, at: nil, options: .loops)
            self.keepAliveNode.play()
        }
    }

    private func stopKeepAlive() {
        guard keepAliveRunning else { return }
        keepAliveRunning = false

        removeBackgroundNotice()

        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.keepAliveNode.stop()
            self.keepAliveEngine.stop()
            try? AVAudioSession.sharedInstance().setActive(false, options: [.notifyOthersOnDeactivation])
        }
    }

    // MARK: - 后台提示

    /// 发出「正在后台识别」的提示。
    ///
    /// iOS 的通知不支持真正的常驻（用户可随手清除），这里的作用是让用户知道
    /// 摄像头仍在工作、可以直接切到其他应用做手势投送。
    private func postBackgroundNotice() {
        let center = UNUserNotificationCenter.current()

        center.requestAuthorization(options: [.alert]) { granted, _ in
            guard granted else { return }

            let content = UNMutableNotificationContent()
            content.title = "AirGrab 正在识别手势"
            content.body = "摄像头在后台持续采集，切到其他应用不会中断"
            content.sound = nil

            let request = UNNotificationRequest(
                identifier: Self.noticeIdentifier,
                content: content,
                trigger: nil   // nil 表示立即投递
            )
            center.add(request, withCompletionHandler: nil)
        }
    }

    private func removeBackgroundNotice() {
        let center = UNUserNotificationCenter.current()
        center.removeDeliveredNotifications(withIdentifiers: [Self.noticeIdentifier])
        center.removePendingNotificationRequests(withIdentifiers: [Self.noticeIdentifier])
    }

    private func configureIfNeeded() {
        guard !isConfigured else { return }
        isConfigured = true

        session.beginConfiguration()
        session.sessionPreset = .vga640x480

        if let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .front),
           let input = try? AVCaptureDeviceInput(device: device),
           session.canAddInput(input) {
            session.addInput(input)
        }

        let output = AVCaptureVideoDataOutput()
        output.alwaysDiscardsLateVideoFrames = true
        output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
        output.setSampleBufferDelegate(self, queue: queue)
        if session.canAddOutput(output) {
            session.addOutput(output)
        }

        request.maximumHandCount = 2
        session.commitConfiguration()
    }
}

extension HandPoseDetector: AVCaptureVideoDataOutputSampleBufferDelegate {
    func captureOutput(_ output: AVCaptureOutput,
                       didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        let handler = VNImageRequestHandler(cvPixelBuffer: pixelBuffer, orientation: .leftMirrored)
        do {
            try handler.perform([request])
        } catch {
            return
        }

        // 只有达到整体置信度门槛的观测才算「画面里真的有手」。
        let observations = (request.results ?? []).filter {
            $0.confidence >= minObservationConfidence
        }

        guard !observations.isEmpty else {
            // 无有效手部：清空平滑窗口，避免旧值残留。
            recentOpenness.removeAll()
            recentCategories.removeAll()
            stableCount = 0
            lastCategory = .neutral
            lastPublishedOpenness = 0
            publish(.none)
            return
        }

        guard let raw = Self.analyze(observations) else {
            recentOpenness.removeAll()
            recentCategories.removeAll()
            stableCount = 0
            lastCategory = .neutral
            lastPublishedOpenness = 0
            publish(.none)
            return
        }

        let category = Self.category(of: raw.openness)

        // 时间一致性：类别连续 stableFramesRequired 帧一致才切换；
        // 未达成时沿用上一帧已发布的 openness，只更新掌心位置。
        if category == lastCategory {
            stableCount += 1
        } else {
            lastCategory = category
            stableCount = 1
        }

        let smoothed: CGFloat
        if stableCount >= stableFramesRequired {
            recentOpenness.append(raw.openness)
            if recentOpenness.count > smoothWindow { recentOpenness.removeFirst() }
            smoothed = Self.median(recentOpenness)
            lastPublishedOpenness = smoothed
        } else {
            smoothed = lastPublishedOpenness
        }

        let value = HandPose(
            openness: smoothed,
            center: raw.center,
            confidence: raw.confidence,
            handCount: observations.count
        )

        DispatchQueue.main.async {
            self.pose = value
            self.diagnostics = String(
                format: "比值 %.2f → 张开度 %.2f · %@ · 置信 %.2f · %d 手",
                raw.ratio, smoothed, Self.categoryLabel(category),
                raw.confidence, observations.count
            )
        }
    }

    private func publish(_ value: HandPose) {
        DispatchQueue.main.async {
            self.pose = value
            self.diagnostics = value.confidence > 0
                ? self.diagnostics
                : "未检测到手部（置信度不足或画面内无手）"
        }
    }

    // MARK: - 单帧分析

    /// 单帧分析结果。声明为模块内可见 —— `analyze` 是 internal 方法，
    /// 返回类型不能比它更窄（private 会直接编译不过）。
    struct RawPose {
        let openness: CGFloat   // 已映射到 0...1
        let ratio: CGFloat      // 原始归一化比值，供标定
        let center: CGPoint
        let confidence: Float
    }

    /// 把 Vision 的观测结果折算成 openness / 掌心位置。
    /// 返回 nil 表示该帧质量不达标，调用方应按「无有效手部」处理。
    static func analyze(_ observations: [VNHumanHandPoseObservation]) -> RawPose? {
        guard let primary = observations.max(by: { $0.confidence < $1.confidence }) else { return nil }
        guard primary.confidence >= 0.6 else { return nil }
        guard let points = try? primary.recognizedPoints(.all) else { return nil }

        // 关键点质量：高低置信点的总数必须足够，否则视为不可靠观测。
        let reliableCount = points.values.filter { $0.confidence > 0.5 }.count
        guard reliableCount >= 12 else { return nil }

        func point(_ name: VNHumanHandPoseObservation.JointName) -> CGPoint? {
            guard let p = points[name], p.confidence > 0.5 else { return nil }
            return CGPoint(x: p.location.x, y: p.location.y)
        }

        guard let wrist = point(.wrist),
              let middleMCP = point(.middleMCP) else { return nil }

        // 掌长：掌根到中指掌指关节，作为尺度基准。
        let palmLength = hypot(middleMCP.x - wrist.x, middleMCP.y - wrist.y)
        guard palmLength >= 0.04, palmLength <= 0.70 else { return nil }

        // 参考点取掌根：指尖到掌根的距离在张开 / 握拳之间差异最大。
        let reference = wrist
        let tips: [VNHumanHandPoseObservation.JointName] = [.indexTip, .middleTip, .ringTip, .littleTip]
        var distances: [CGFloat] = []
        for tip in tips {
            guard let t = point(tip) else { continue }
            distances.append(hypot(t.x - reference.x, t.y - reference.y) / palmLength)
        }
        // 有效指尖不足三个时放弃 —— 单指数据无法判断整体拳 / 掌。
        guard distances.count >= 3 else { return nil }

        let ratio = distances.reduce(0, +) / CGFloat(distances.count)
        let openness = min(max((ratio - 1.05) / (1.65 - 1.05), 0), 1)

        let palmCenter = CGPoint(x: (wrist.x + middleMCP.x) / 2, y: (wrist.y + middleMCP.y) / 2)

        return RawPose(
            openness: openness,
            ratio: ratio,
            center: palmCenter,
            confidence: primary.confidence
        )
    }

    private static func category(of openness: CGFloat) -> PoseCategory {
        if openness <= 0.35 { return .fist }
        if openness >= 0.62 { return .open }
        return .neutral
    }

    private static func categoryLabel(_ category: PoseCategory) -> String {
        switch category {
        case .fist:    return "握拳"
        case .open:    return "张开"
        case .neutral: return "过渡"
        }
    }

    private static func median(_ values: [CGFloat]) -> CGFloat {
        guard !values.isEmpty else { return 0 }
        let sorted = values.sorted()
        let mid = sorted.count / 2
        return sorted.count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2
            : sorted[mid]
    }
}
