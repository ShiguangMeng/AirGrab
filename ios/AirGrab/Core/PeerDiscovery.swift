import Foundation
import Darwin
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

/// 设备发现：UDP 信标 + 超时淘汰。
///
/// **为什么不直接靠广播**：iOS 真机向广播地址发送需要 Apple 特批的
/// `com.apple.developer.networking.multicast` 授权，未签名侧载包不可能具备，
/// 因此本机发出的广播会被系统直接丢弃 —— 表现为「Windows / Android 永远看不到 iOS」。
///
/// 解法是**反相单播**：iOS 发不出广播，但完全允许普通 UDP 单播。
/// 一旦收到某台设备的广播，就向它的 IP 单播一份自己的信标，对方由此发现本机。
/// 单播不依赖任何授权，双向可达。
///
/// **接收走 BSD socket 而非 Network.framework**：`NWListener` 对无连接 UDP
/// 广播的分发在真机上并不可靠，且不便拿到发送方 IP（反相需要它）。
/// 直接 `recvfrom` 循环最稳。
final class PeerDiscovery: ObservableObject {
    static let udpPort: UInt16 = 25585
    static let beaconPrefix = "AGG-DISCOVER"

    @Published private(set) var peers: [Peer] = []

    var beaconInterval: TimeInterval = 2.0
    /// 淘汰阈值取 14 秒：进入后台时定时器会被系统挂起数秒，
    /// 过短的窗口会把对方误判为离线。
    var ttl: TimeInterval = 14.0
    var displayName: String = "iPhone"
    var platform: String = "iOS"

    private let queue = DispatchQueue(label: "airgrab.discovery")
    private var socketFD: Int32 = -1
    private var receiveSource: DispatchSourceRead?
    private var beaconTimer: DispatchSourceTimer?
    private var sweepTimer: DispatchSourceTimer?

    private let selfID = String(UUID().uuidString.prefix(8)).lowercased()

    /// 每个来源最近一次反相的时间，用于限流，避免与对方形成信标乒乓。
    private var lastReplyAt: [String: Date] = [:]

    private var beaconLine: String {
        "\(Self.beaconPrefix)|\(selfID)|\(displayName)|\(platform)|\(FileTransfer.tcpPort)\n"
    }

    func start() {
        queue.async { [weak self] in
            guard let self else { return }
            self.openSocket()
            self.startBeacon()
            self.startSweep()
        }
    }

    func stop() {
        queue.async { [weak self] in
            guard let self else { return }

            self.beaconTimer?.cancel(); self.beaconTimer = nil
            self.sweepTimer?.cancel(); self.sweepTimer = nil
            self.receiveSource?.cancel(); self.receiveSource = nil

            if self.socketFD >= 0 {
                close(self.socketFD)
                self.socketFD = -1
            }

            self.lastReplyAt.removeAll()
        }
    }

    // MARK: - 接收

    private func openSocket() {
        guard socketFD < 0 else { return }

        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { return }

        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &yes, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = Self.udpPort.bigEndian
        addr.sin_addr.s_addr = INADDR_ANY

        let bound = withUnsafePointer(to: &addr) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }

        guard bound == 0 else {
            close(fd)
            return
        }

        socketFD = fd

        let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: queue)
        source.setEventHandler { [weak self] in
            self?.drainSocket()
        }
        source.resume()
        receiveSource = source
    }

    /// 把内核缓冲区里的数据报全部取完。
    private func drainSocket() {
        guard socketFD >= 0 else { return }

        var buffer = [UInt8](repeating: 0, count: 2048)

        while true {
            var senderStorage = sockaddr_storage()
            var senderLength = socklen_t(MemoryLayout<sockaddr_storage>.size)

            let received = withUnsafeMutablePointer(to: &senderStorage) { storagePointer in
                storagePointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                    recvfrom(socketFD, &buffer, buffer.count, 0, sockaddrPointer, &senderLength)
                }
            }

            // 0 或负值表示当前已无更多数据。
            if received <= 0 { break }

            guard let text = String(bytes: buffer[0..<received], encoding: .utf8) else { continue }

            handleBeacon(text, from: Self.ipString(from: senderStorage))
        }
    }

    private static func ipString(from storage: sockaddr_storage) -> String {
        var copy = storage

        let family = withUnsafePointer(to: &copy) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { $0.pointee.sa_family }
        }

        guard family == sa_family_t(AF_INET) else { return "" }

        var sin = withUnsafePointer(to: &copy) {
            $0.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
        }

        var host = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        guard inet_ntop(AF_INET, &sin.sin_addr, &host, socklen_t(INET_ADDRSTRLEN)) != nil else {
            return ""
        }

        return String(cString: host)
    }

    private func handleBeacon(_ text: String, from ip: String) {
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
            address: ip.isEmpty ? fields[1] : ip,
            lastSeen: Date()
        )

        DispatchQueue.main.async {
            if let index = self.peers.firstIndex(where: { $0.id == id }) {
                self.peers[index] = peer
            } else {
                self.peers.append(peer)
            }
        }

        // 反相单播：让对方也能发现本机。
        // 限流 4 秒，必须短于对端淘汰阈值（Windows 10s / Android 8s），
        // 否则对方刚记下本机就又把它判为离线。
        guard !ip.isEmpty else { return }

        let now = Date()
        if now.timeIntervalSince(lastReplyAt[ip] ?? .distantPast) >= 4.0 {
            lastReplyAt[ip] = now
            sendBeacon(to: ip)
        }
    }

    // MARK: - 发送

    private func startBeacon() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: beaconInterval)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            // iOS 上这条多半会被系统丢弃（缺 multicast 授权），
            // 但作为对其他平台设备的补充通道保留，成本极低。
            self.sendBeacon(to: "255.255.255.255")
        }
        timer.resume()
        beaconTimer = timer
    }

    private func sendBeacon(to ip: String) {
        guard socketFD >= 0 else { return }

        var destination = sockaddr_in()
        destination.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        destination.sin_family = sa_family_t(AF_INET)
        destination.sin_port = Self.udpPort.bigEndian

        guard inet_pton(AF_INET, ip, &destination.sin_addr) == 1 else { return }

        let line = beaconLine

        _ = withUnsafePointer(to: &destination) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
                line.withCString { cString in
                    sendto(
                        socketFD,
                        cString,
                        strlen(cString),
                        0,
                        sockaddrPointer,
                        socklen_t(MemoryLayout<sockaddr_in>.size)
                    )
                }
            }
        }
    }

    // MARK: - 淘汰

    private func startSweep() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + ttl, repeating: ttl / 2)
        timer.setEventHandler { [weak self] in
            guard let self else { return }

            let deadline = Date().addingTimeInterval(-self.ttl)

            DispatchQueue.main.async {
                self.peers.removeAll { $0.lastSeen < deadline }
            }

            // 顺带清理过期的反相记录，避免字典无限增长。
            let replyDeadline = Date().addingTimeInterval(-60)
            self.lastReplyAt = self.lastReplyAt.filter { $0.value > replyDeadline }
        }
        timer.resume()
        sweepTimer = timer
    }
}
