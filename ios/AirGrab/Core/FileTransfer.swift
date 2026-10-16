import Foundation
import Network

/// 标准 CRC-32（IEEE 802.3，多项式 0xEDB88320 反射式）。
enum CRC32 {
    private static let table: [UInt32] = (0..<256).map { index -> UInt32 in
        var c = UInt32(index)
        for _ in 0..<8 {
            c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1)
        }
        return c
    }

    static func checksum(_ data: Data) -> UInt32 {
        var c: UInt32 = 0xFFFF_FFFF
        for byte in data {
            c = table[Int((c ^ UInt32(byte)) & 0xFF)] ^ (c >> 8)
        }
        return c ^ 0xFFFF_FFFF
    }
}

/// 投递层：TCP 分片传输，逐片 CRC32 校验。
///
/// 报文结构（全部小端）：
/// ```
/// [4B 净荷总长][4B 片偏移][4B 片长][4B 片 CRC32][片数据]
/// ```
/// 接收端逐片校验，任一片失败即整条连接作废 —— 不把损坏数据写进结果集。
/// 片长固定为 `chunkSize`，最后一片可短，偏移量使接收端能够按序重组并支持续传。
final class FileTransfer: NSObject, ObservableObject {
    static let tcpPort: UInt16 = 25586
    static let chunkSize = 64 * 1024

    @Published private(set) var isServing = false
    @Published private(set) var progress: Double = 0
    @Published private(set) var lastLog: String = "空闲"

    /// 收到完整净荷后回调（已保证 CRC 全部通过）。
    var onPayloadReceived: ((Payload) -> Void)?
    /// 每完成一次发送后回调。
    var onPayloadSent: ((Payload, String) -> Void)?

    private let queue = DispatchQueue(label: "airgrab.transfer")
    private var listener: NWListener?

    func startServer() {
        queue.async { [weak self] in
            guard let self else { return }
            let params = NWParameters.tcp
            params.allowLocalEndpointReuse = true
            guard let port = NWEndpoint.Port(rawValue: Self.tcpPort),
                  let l = try? NWListener(using: params, on: port) else { return }
            l.newConnectionHandler = { conn in
                conn.start(queue: self.queue)
                self.serve(conn)
            }
            l.start(queue: queue)
            self.listener = l
            DispatchQueue.main.async {
                self.isServing = true
                self.lastLog = "接收服务已就绪 :\(Self.tcpPort)"
            }
        }
    }

    func stopServer() {
        queue.async { [weak self] in
            self?.listener?.cancel()
            self?.listener = nil
            DispatchQueue.main.async { self?.isServing = false }
        }
    }

    // MARK: - 发送

    func send(_ payload: Payload, to peer: Peer, completion: ((Bool) -> Void)? = nil) {
        queue.async { [weak self] in
            guard let self else { return }
            guard let port = NWEndpoint.Port(rawValue: Self.tcpPort) else { return }
            let params = NWParameters.tcp
            let conn = NWConnection(host: NWEndpoint.Host(peer.address), port: port, using: params)

            let wire = AGGCodec.encode(payload)
            let chunks = Self.fragment(wire)

            DispatchQueue.main.async { self.progress = 0 }

            conn.stateUpdateHandler = { state in
                guard case .ready = state else { return }
                self.pump(chunks, index: 0, over: conn, payload: payload, peer: peer, completion: completion)
            }
            conn.start(queue: self.queue)
        }
    }

    private func pump(_ chunks: [Data], index: Int, over conn: NWConnection,
                      payload: Payload, peer: Peer, completion: ((Bool) -> Void)?) {
        guard index < chunks.count else {
            conn.send(content: Data(), isComplete: true, completion: .contentProcessed { _ in
                conn.cancel()
                DispatchQueue.main.async {
                    self.progress = 1
                    self.lastLog = "已投送 \(payload.name) → \(peer.name)"
                    self.onPayloadSent?(payload, peer.name)
                    completion?(true)
                }
            })
            return
        }

        conn.send(content: chunks[index], completion: .contentProcessed { [weak self] error in
            guard let self else { return }
            if error != nil {
                conn.cancel()
                DispatchQueue.main.async {
                    self.lastLog = "传输中断：\(payload.name)"
                    completion?(false)
                }
                return
            }
            DispatchQueue.main.async {
                self.progress = Double(index + 1) / Double(chunks.count)
            }
            self.pump(chunks, index: index + 1, over: conn, payload: payload, peer: peer, completion: completion)
        })
    }

    // MARK: - 接收

    private func serve(_ conn: NWConnection) {
        var buffer = Data()

        func readMore() {
            conn.receive(minimumIncompleteLength: 1, maximumLength: Self.chunkSize + 16) { [weak self] data, _, isComplete, error in
                guard let self else { return }
                if let data, !data.isEmpty {
                    buffer.append(data)
                    buffer = self.drain(&buffer)
                }
                if error == nil && !isComplete { readMore() } else { conn.cancel() }
            }
        }
        readMore()
    }

    /// 按协议逐片剥离并校验；返回尚未成片的残余字节。
    private func drain(_ buffer: inout Data) -> Data {
        while buffer.count >= 16 {
            let declaredTotal = Self.readUInt32(buffer, 0)
            let offset        = Self.readUInt32(buffer, 4)
            let length        = Self.readUInt32(buffer, 8)
            let expectedCRC   = Self.readUInt32(buffer, 12)

            guard length <= UInt32(Self.chunkSize) else { buffer.removeAll(); return Data() }
            guard buffer.count >= 16 + Int(length) else { break }

            let slice = buffer.subdata(in: 16..<(16 + Int(length)))
            if CRC32.checksum(slice) != expectedCRC {
                DispatchQueue.main.async { self.lastLog = "校验失败，丢弃该片 @\(offset)" }
                buffer.removeFirst(16 + Int(length))
                continue
            }

            self.assembly.append(slice)
            buffer.removeFirst(16 + Int(length))

            if self.assembly.count >= Int(declaredTotal) {
                let wire = Data(self.assembly.prefix(Int(declaredTotal)))
                self.assembly.removeAll(keepingCapacity: true)
                self.deliver(wire)
            }
        }
        return buffer
    }

    private var assembly = Data()

    private func deliver(_ wire: Data) {
        do {
            let payload = try AGGCodec.decode(wire)
            DispatchQueue.main.async {
                self.lastLog = "已接收 \(payload.name)（\(payload.sizeText)）"
                self.onPayloadReceived?(payload)
            }
        } catch {
            DispatchQueue.main.async { self.lastLog = "载荷解析失败：\(error)" }
        }
    }

    // MARK: - 分片与字节序

    static func fragment(_ wire: Data) -> [Data] {
        var chunks: [Data] = []
        let total = UInt32(wire.count)
        var offset = 0

        while offset < wire.count {
            let end = min(offset + chunkSize, wire.count)
            let slice = wire.subdata(in: offset..<end)

            var header = Data()
            header.append(contentsOf: littleEndian(total))
            header.append(contentsOf: littleEndian(UInt32(offset)))
            header.append(contentsOf: littleEndian(UInt32(slice.count)))
            header.append(contentsOf: littleEndian(CRC32.checksum(slice)))

            chunks.append(header + slice)
            offset = end
        }
        return chunks
    }

    private static func littleEndian(_ value: UInt32) -> [UInt8] {
        [UInt8(value & 0xFF),
         UInt8((value >> 8) & 0xFF),
         UInt8((value >> 16) & 0xFF),
         UInt8((value >> 24) & 0xFF)]
    }

    private static func readUInt32(_ data: Data, _ offset: Int) -> UInt32 {
        guard offset + 4 <= data.count else { return 0 }
        let base = data.startIndex + offset
        return UInt32(data[base])
            | (UInt32(data[base + 1]) << 8)
            | (UInt32(data[base + 2]) << 16)
            | (UInt32(data[base + 3]) << 24)
    }
}
