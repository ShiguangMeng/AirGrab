import Foundation
import SwiftUI
import Combine
import UIKit

/// 应用状态中枢：把手势、发现、传输三条链路汇聚成界面可消费的单一状态源。
@MainActor
final class AppModel: ObservableObject {

    enum Channel: String, CaseIterable, Identifiable {
        case simulated
        case camera
        var id: String { rawValue }
        var title: String { self == .simulated ? "模拟输入" : "摄像头识别" }
    }

    @Published private(set) var peers: [Peer] = []
    @Published private(set) var inbox: [Payload] = []
    @Published private(set) var outbox: [Payload] = []
    @Published private(set) var state: GestureState = .idle
    @Published private(set) var carriedName: String = "—"
    @Published private(set) var statusLine: String = "就绪"
    @Published private(set) var transferProgress: Double = 0
    @Published var channel: Channel = .simulated
    @Published var toast: String?
    @Published var toastDetail: String?

    let discovery = PeerDiscovery()
    let transfer = FileTransfer()
    let detector = HandPoseDetector()

    private let machine = GestureStateMachine()
    private var bag = Set<AnyCancellable>()
    private var toastTask: Task<Void, Never>?

    init() {
        discovery.displayName = Self.currentDeviceName()
        discovery.platform = Self.currentPlatformName()

        seedSampleContent()
        bind()
    }

    func start() {
        discovery.start()
        transfer.startServer()
        if channel == .camera { detector.start() }
        statusLine = "正在局域网内发现设备…"
    }

    func stop() {
        discovery.stop()
        transfer.stopServer()
        detector.stop()
    }

    // MARK: - 通道切换

    func setChannel(_ value: Channel) {
        guard value != channel else { return }
        channel = value
        if value == .camera {
            detector.start()
            statusLine = detector.statusText
        } else {
            detector.stop()
            statusLine = "指针事件驱动状态机"
        }
        showToast(value == .camera ? "已切换至摄像头手势通道" : "已切换至模拟输入通道",
                  detail: value == .camera ? "手部 21 关键点 · Vision 端侧推理" : "用于无摄像头环境演示")
    }

    // MARK: - 发送

    func send(_ payload: Payload, to peer: Peer) {
        transfer.send(payload, to: peer)
    }

    func sendToFirstPeer(_ payload: Payload) {
        guard let peer = peers.first else {
            showToast("没有可接收的设备", detail: "请确认对方已打开 AirGrab 且在同一局域网")
            return
        }
        send(payload, to: peer)
    }

    // MARK: - 通道绑定

    private func bind() {
        discovery.$peers
            .receive(on: RunLoop.main)
            .assign(to: &$peers)

        machine.$state
            .receive(on: RunLoop.main)
            .assign(to: &$state)

        machine.$payloadName
            .receive(on: RunLoop.main)
            .assign(to: &$carriedName)

        detector.$pose
            .receive(on: RunLoop.main)
            .sink { [weak self] pose in
                guard let self, self.channel == .camera else { return }
                self.machine.ingest(pose)
            }
            .store(in: &bag)

        detector.$statusText
            .receive(on: RunLoop.main)
            .sink { [weak self] text in
                guard let self, self.channel == .camera else { return }
                self.statusLine = text
            }
            .store(in: &bag)

        transfer.$progress
            .receive(on: RunLoop.main)
            .assign(to: &$transferProgress)

        transfer.onPayloadReceived = { [weak self] payload in
            guard let self else { return }
            Task { @MainActor in
                self.inbox.insert(payload, at: 0)
                self.showToast("已收到 \(payload.name)", detail: "来自局域网设备 · \(payload.sizeText)")
            }
        }

        transfer.onPayloadSent = { [weak self] payload, peerName in
            guard let self else { return }
            Task { @MainActor in
                self.outbox.insert(payload, at: 0)
                self.showToast(payload.name, detail: "已由 \(Self.currentDeviceName()) 投送至 \(peerName)")
                self.machine.finish()
            }
        }

        machine.onGrabConfirmed = { [weak self] _ in
            Task { @MainActor in self?.statusLine = "抓取已确认，移向目标设备" }
        }
        machine.onDropConfirmed = { [weak self] _ in
            Task { @MainActor in self?.statusLine = "松开手掌，投递中…" }
        }
        machine.onCancelled = { [weak self] in
            Task { @MainActor in self?.statusLine = "本次抓取已取消" }
        }
    }

    // MARK: - 模拟通道（指针事件）

    /// 模拟通道把一个拖拽会话映射成与摄像头完全相同的状态机输入。
    /// 载荷名由 `beginCarrying` 写入状态机，界面从状态机读回。
    func simulateGrab(_ payload: Payload) {
        guard channel == .simulated else { return }

        machine.beginCarrying(name: payload.name)
        statusLine = "搬运 \(payload.name)，移向目标设备"
    }

    func simulateDrop(on peer: Peer?) {
        guard channel == .simulated else { return }
        machine.confirmDrop()
        machine.finish()
        if let peer, let payload = outboxCandidate {
            send(payload, to: peer)
        }
        outboxCandidate = nil
    }

    func simulateCancel() {
        machine.cancel()
        machine.finish()
        outboxCandidate = nil
        statusLine = "已取消本次抓取"
    }

    private var outboxCandidate: Payload?

    func beginSimulatedDrag(_ payload: Payload) {
        outboxCandidate = payload
        simulateGrab(payload)
    }

    // MARK: - 提示

    func showToast(_ title: String, detail: String? = nil) {
        toast = title
        toastDetail = detail
        toastTask?.cancel()
        toastTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 2_600_000_000)
            guard !Task.isCancelled else { return }
            await MainActor.run {
                self?.toast = nil
                self?.toastDetail = nil
            }
        }
    }

    // MARK: - 采样内容

    private func seedSampleContent() {
        outbox = [
            Payload(kind: .image, name: "相册精选.jpg", byteCount: 4_800_000, body: Data("stub".utf8)),
            Payload(kind: .file,  name: "AirGrab 文档.pdf", byteCount: 204_000, body: Data("stub".utf8)),
            Payload(kind: .link,  name: "灵感链接", byteCount: 96, body: Data("https://example.com".utf8)),
            Payload(kind: .image, name: "截图 2026-10-16.png", byteCount: 1_100_000, body: Data("stub".utf8))
        ]
    }

    private static func currentDeviceName() -> String {
        let name = UIDevice.current.name
        return name.isEmpty ? "iPhone" : name
    }

    private static func currentPlatformName() -> String {
        UIDevice.current.userInterfaceIdiom == .pad ? "iPadOS" : "iOS"
    }
}
