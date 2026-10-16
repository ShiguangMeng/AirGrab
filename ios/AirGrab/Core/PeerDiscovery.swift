import Foundation
import Network
import Combine

struct Peer: Identifiable, Hashable {
    let id: String
    var name: String
    var platform: String
    var address: String
    var lastSeen: Date

    var platformSymbol: String {
        switch platform.lowercased() {
        case "windows": return "pc"
        case "android": return "phone"
        case "ipados":  return "ipad"
        case "ios":     return "iphone"
        default:        return "rectangle.on.rectangle"
        }
    }
}

/// 设备发现：UDP 广播信标 + 超时淘汰。
///
/// 信标为单行文本 `AGG-DISCOVER|<id>|<name>|<platform>|<tcpPort>`，
/// 每 `beaconInterval` 秒向受限广播地址发一次；超过 `ttl` 未再收到即视为离线。
/// 不依赖任何厂商账号体系，局域网内平权发现。
final class PeerDiscovery: ObservableObject {
    static let udpPort: UInt16 = 25585
    static let beaconPrefix = "AGG-DISCOVER"

    @Published private(set) var peers: [Peer] = []

    var beaconInterval: TimeInterval = 2.0
    /// 淘汰阈值。取 14 秒而非 8 秒：进入后台时定时器会被系统挂起数秒，
    /// 过短的窗口会把对方误判为离线，表现为设备列表频繁清空。
    var ttl: TimeInterval = 14.0
    var displayName: String = "iPhone 17 Pro"
    var platform: String = "iOS"

    private let queue = DispatchQueue(label: "airgrab.discovery")
    private var listener: NWListener?
    private var sender: NWConnection?
    private var beaconTimer: DispatchSourceTimer?
    private var sweepTimer: DispatchSourceTimer?

    private let selfID = String(UUID().uuidString.prefix(8)).lowercased()

    func start() {
        queue.async { [weak self] in
            guard let self else { return }
            self.startListener()
            self.startBeacon()
            self.startSweep()
        }
    }

    func stop() {
        queue.async { [weak self] in
            guard let self else { return }
            self.beaconTimer?.cancel(); self.beaconTimer = nil
            self.sweepTimer?.cancel(); self.sweepTimer = nil
            self.listener?.cancel(); self.listener = nil
            self.sender?.cancel(); self.sender = nil
        }
    }

    // MARK: - 接收

    private func startListener() {
        let params = NWParameters.udp
        params.allowLocalEndpointReuse = true

        guard let port = NWEndpoint.Port(rawValue: Self.udpPort),
              let l = try? NWListener(using: params, on: port) else { return }

        l.newConnectionHandler = { [weak self] conn in
            conn.start(queue: self?.queue ?? .main)
            self?.receive(on: conn)
        }
        l.stateUpdateHandler = { _ in }
        l.start(queue: queue)
        listener = l
    }

    private func receive(on conn: NWConnection) {
        conn.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            if let data, let text = String(data: data, encoding: .utf8) {
                self.handleBeacon(text, from: conn.endpoint)
            }
            if error == nil { self.receive(on: conn) }
        }
    }

    private func handleBeacon(_ text: String, from endpoint: NWEndpoint) {
        let fields = text.trimmingCharacters(in: .whitespacesAndNewlines)
            .split(separator: "|", omittingEmptySubsequences: false)
            .map(String.init)
        guard fields.count >= 4, fields[0] == Self.beaconPrefix else { return }

        let id = fields[1]
        guard id != selfID else { return }   // 自己的广播丢弃

        let peer = Peer(
            id: id,
            name: fields[2],
            platform: fields[3],
            address: Self.hostDescription(of: endpoint),
            lastSeen: Date()
        )

        DispatchQueue.main.async {
            if let idx = self.peers.firstIndex(where: { $0.id == id }) {
                self.peers[idx] = peer
            } else {
                self.peers.append(peer)
            }
        }
    }

    private static func hostDescription(of endpoint: NWEndpoint) -> String {
        if case let .hostPort(host, _) = endpoint { return "\(host)" }
        return "\(endpoint)"
    }

    // MARK: - 广播

    private func startBeacon() {
        guard let port = NWEndpoint.Port(rawValue: Self.udpPort) else { return }
        let params = NWParameters.udp
        params.allowLocalEndpointReuse = true

        // 受限广播地址；iOS 的本地网络权限会在首次发送时向用户申请。
        let conn = NWConnection(host: NWEndpoint.Host("255.255.255.255"), port: port, using: params)
        conn.start(queue: queue)
        sender = conn

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: beaconInterval)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            let line = "\(Self.beaconPrefix)|\(self.selfID)|\(self.displayName)|\(self.platform)|\(FileTransfer.tcpPort)\n"
            conn.send(content: Data(line.utf8), completion: .idempotent)
        }
        timer.resume()
        beaconTimer = timer
    }

    /// 淘汰超过 ttl 未续期的设备。
    private func startSweep() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + ttl, repeating: ttl / 2)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            let deadline = Date().addingTimeInterval(-self.ttl)
            DispatchQueue.main.async {
                self.peers.removeAll { $0.lastSeen < deadline }
            }
        }
        timer.resume()
        sweepTimer = timer
    }
}
