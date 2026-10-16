import Foundation

/// 投送载荷的类型。协议首字段以 ASCII 标识区分，接收端据此决定落地方式：
/// 文本进剪贴板、链接交浏览器、图片入相册、其余落接收目录。
enum PayloadKind: String, Codable, CaseIterable, Sendable {
    case text
    case link
    case image
    case file

    var title: String {
        switch self {
        case .text:  return "文本"
        case .link:  return "链接"
        case .image: return "图片"
        case .file:  return "文件"
        }
    }

    var symbol: String {
        switch self {
        case .text:  return "text.alignleft"
        case .link:  return "link"
        case .image: return "photo"
        case .file:  return "doc"
        }
    }
}

struct Payload: Identifiable, Hashable, Sendable {
    let id: UUID
    var kind: PayloadKind
    var name: String
    var byteCount: Int
    var body: Data

    init(id: UUID = UUID(), kind: PayloadKind, name: String, byteCount: Int? = nil, body: Data) {
        self.id = id
        self.kind = kind
        self.name = name
        self.byteCount = byteCount ?? body.count
        self.body = body
    }

    var sizeText: String {
        ByteCountFormatter.string(fromByteCount: Int64(byteCount), countStyle: .file)
    }

    static func == (lhs: Payload, rhs: Payload) -> Bool { lhs.id == rhs.id }
    func hash(into hasher: inout Hasher) { hasher.combine(id) }
}

extension Payload {
    static func text(_ value: String) -> Payload {
        Payload(kind: .text, name: String(value.prefix(24)), body: Data(value.utf8))
    }

    static func link(_ url: URL) -> Payload {
        Payload(kind: .link, name: url.host ?? url.absoluteString, body: Data(url.absoluteString.utf8))
    }
}

enum AGGCodecError: Error {
    case badHeader
    case truncated
    case badSize
}

/// AirGrab 交换协议编解码。
///
/// 明文格式：`AGG|<type>|<size>|<name>|<payload>`
/// 净荷允许包含分隔符，因此解码按前四个分隔符切分，第四段之后的全部内容都归净荷。
enum AGGCodec {
    static let magic = "AGG"
    static let separator: Character = "|"
    static let maxHeaderLength = 512

    static func encode(_ payload: Payload) -> Data {
        var head = "\(magic)\(separator)"
        head += "\(payload.kind.rawValue)\(separator)"
        head += "\(payload.body.count)\(separator)"
        head += "\(payload.name)\(separator)"

        var data = Data(head.utf8)
        data.append(payload.body)
        return data
    }

    static func decode(_ data: Data) throws -> Payload {
        // 头部为 UTF-8 文本，净荷可能任意；只在前 maxHeaderLength 字节内寻找第四个分隔符。
        let probe = data.prefix(maxHeaderLength)
        guard let probeText = String(data: probe, encoding: .utf8) else { throw AGGCodecError.badHeader }

        var cursor = probeText.startIndex
        var fields: [String] = []
        for _ in 0..<4 {
            guard let idx = probeText[cursor...].firstIndex(of: separator) else { throw AGGCodecError.badHeader }
            fields.append(String(probeText[cursor..<idx]))
            cursor = probeText.index(after: idx)
        }

        guard fields[0] == magic else { throw AGGCodecError.badHeader }
        guard let kind = PayloadKind(rawValue: fields[1]) else { throw AGGCodecError.badHeader }
        guard let declared = Int(fields[2]), declared >= 0 else { throw AGGCodecError.badSize }

        let headerByteCount = String(probeText[..<cursor]).utf8.count
        guard data.count >= headerByteCount else { throw AGGCodecError.truncated }
        let body = data.subdata(in: headerByteCount..<data.count)

        guard body.count == declared else { throw AGGCodecError.badSize }
        return Payload(kind: kind, name: fields[3], byteCount: declared, body: body)
    }
}
