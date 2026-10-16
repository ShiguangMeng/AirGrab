import Foundation
import AVFoundation
import Vision
import CoreGraphics
import Combine

/// 摄像头通道：AVFoundation 采集前置摄像头 + Vision 手部 21 关键点。
///
/// 张开度 openness 的推导方式：取四指指尖到掌心点的平均距离，
/// 用掌根到中指掌指关节的距离（掌宽）归一化。
/// 五指张开时指尖远离掌心，握拳时指尖回落到掌心附近：
/// 归一化距离 > 0.9 视为张开，< 0.45 视为握拳。
final class HandPoseDetector: NSObject, ObservableObject {
    @Published private(set) var pose: HandPose = .none
    @Published private(set) var isRunning = false
    @Published private(set) var statusText: String = "未启动"

    /// 模拟器与无摄像头设备上不启动采集，仅回报状态。
    static var isCameraAvailable: Bool {
        #if targetEnvironment(simulator)
        return false
        #else
        return AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .front) != nil
        #endif
    }

    private let session = AVCaptureSession()
    private let request = VNDetectHumanHandPoseRequest()
    private let queue = DispatchQueue(label: "airgrab.vision")
    private var isConfigured = false

    /// 指尖关键点（Vision 的 21 点模型索引）。
    private let tipIndices: [VNHumanHandPoseObservation.JointName] = [.indexTip, .middleTip, .ringTip, .littleTip]

    func start() {
        guard Self.isCameraAvailable else {
            statusText = "无可用摄像头 · 已回退模拟输入"
            return
        }
        queue.async { [weak self] in
            guard let self else { return }
            self.configureIfNeeded()
            if !self.session.isRunning {
                self.session.startRunning()
            }
            DispatchQueue.main.async {
                self.isRunning = true
                self.statusText = "手部 21 关键点 · 端侧推理"
            }
        }
    }

    func stop() {
        queue.async { [weak self] in
            guard let self else { return }
            if self.session.isRunning { self.session.stopRunning() }
            DispatchQueue.main.async {
                self.isRunning = false
                self.statusText = "已停止"
            }
        }
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

        guard let observations = request.results, !observations.isEmpty else {
            publish(.none)
            return
        }

        let pose = Self.analyze(observations)
        publish(pose)
    }

    private func publish(_ value: HandPose) {
        DispatchQueue.main.async { self.pose = value }
    }

    /// 把 Vision 的观测结果折算成 openness / 掌心位置 / 手数量。
    static func analyze(_ observations: [VNHumanHandPoseObservation]) -> HandPose {
        guard let primary = observations.max(by: { $0.confidence < $1.confidence }) else { return .none }
        guard let points = try? primary.recognizedPoints(.all) else { return .none }

        func point(_ name: VNHumanHandPoseObservation.JointName) -> CGPoint? {
            guard let p = points[name], p.confidence > 0.3 else { return nil }
            return CGPoint(x: p.location.x, y: p.location.y)
        }

        guard let wrist = point(.wrist),
              let middleMCP = point(.middleMCP) else { return .none }

        // 掌宽：掌根到中指掌指关节的距离，作为尺度基准。
        let palmScale = max(hypot(middleMCP.x - wrist.x, middleMCP.y - wrist.y), 0.001)
        let palmCenter = CGPoint(x: (wrist.x + middleMCP.x) / 2, y: (wrist.y + middleMCP.y) / 2)

        let tips: [VNHumanHandPoseObservation.JointName] = [.indexTip, .middleTip, .ringTip, .littleTip]
        var distances: [CGFloat] = []
        for tip in tips {
            guard let t = point(tip) else { continue }
            distances.append(hypot(t.x - palmCenter.x, t.y - palmCenter.y) / palmScale)
        }
        guard !distances.isEmpty else { return .none }

        let average = distances.reduce(0, +) / CGFloat(distances.count)

        // 实测区间约 0.45(握拳) ~ 1.35(五指张开)，线性映射到 0...1。
        let openness = min(max((average - 0.45) / (1.35 - 0.45), 0), 1)

        return HandPose(
            openness: openness,
            center: palmCenter,
            confidence: primary.confidence,
            handCount: observations.count
        )
    }
}
