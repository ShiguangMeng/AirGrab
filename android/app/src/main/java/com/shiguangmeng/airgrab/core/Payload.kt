package com.shiguangmeng.airgrab.core

import java.util.UUID

/**
 * 投送载荷类型。协议首字段以 ASCII 标识区分，接收端据此决定落地方式：
 * 文本进剪贴板、链接交浏览器、图片入相册、其余落接收目录。
 */
enum class PayloadKind(val wire: String, val title: String) {
    TEXT("text", "文本"),
    LINK("link", "链接"),
    IMAGE("image", "图片"),
    FILE("file", "文件");

    companion object {
        fun fromWire(value: String): PayloadKind? = entries.firstOrNull { it.wire == value }
    }
}

class Payload(
    val id: String = UUID.randomUUID().toString(),
    val kind: PayloadKind,
    val name: String,
    val body: ByteArray
) {
    val byteCount: Int get() = body.size

    val sizeText: String get() = formatSize(byteCount)

    /** 卡片缩略区的大字标签：取扩展名，无扩展名时退回类型名。 */
    val extensionTag: String
        get() {
            val dot = name.lastIndexOf('.')
            return if (dot > 0 && dot < name.length - 1) {
                name.substring(dot + 1).uppercase()
            } else {
                kind.title
            }
        }

    override fun equals(other: Any?): Boolean = other is Payload && other.id == id
    override fun hashCode(): Int = id.hashCode()

    companion object {
        fun formatSize(bytes: Int): String {
            if (bytes < 1024) return "$bytes B"
            val units = arrayOf("KB", "MB", "GB")
            var value = bytes.toDouble() / 1024
            var unit = 0
            while (value >= 1024 && unit < units.size - 1) {
                value /= 1024
                unit++
            }
            val rounded = Math.round(value * 10) / 10.0
            return if (rounded == rounded.toLong().toDouble()) {
                "${rounded.toLong()} ${units[unit]}"
            } else {
                "$rounded ${units[unit]}"
            }
        }

        fun fromText(text: String) = Payload(
            kind = PayloadKind.TEXT,
            name = if (text.length <= 24) text else text.substring(0, 24),
            body = text.toByteArray(Charsets.UTF_8)
        )

        fun fromLink(url: String) = Payload(
            kind = PayloadKind.LINK,
            name = runCatching { java.net.URI(url).host }.getOrNull() ?: url,
            body = url.toByteArray(Charsets.UTF_8)
        )

        fun of(kind: PayloadKind, name: String, size: Int) =
            Payload(kind = kind, name = name, body = ByteArray(size))
    }
}

class AggFormatException(message: String) : Exception(message)

/**
 * AirGrab 交换协议编解码。
 *
 * 明文格式：`AGG|<type>|<size>|<name>|<payload>`。
 * 净荷允许包含分隔符，因此解码按前四个分隔符切分，第三段之后的全部字节归净荷。
 * 头部长度上限 [MAX_HEADER_LENGTH]，避免畸形报文触发超大分配。
 */
object AggCodec {
    const val MAGIC = "AGG"
    const val SEPARATOR = '|'
    const val MAX_HEADER_LENGTH = 512

    fun encode(payload: Payload): ByteArray {
        val head = buildString {
            append(MAGIC).append(SEPARATOR)
            append(payload.kind.wire).append(SEPARATOR)
            append(payload.body.size).append(SEPARATOR)
            append(payload.name).append(SEPARATOR)
        }.toByteArray(Charsets.UTF_8)

        return head + payload.body
    }

    @Throws(AggFormatException::class)
    fun decode(data: ByteArray, length: Int = data.size): Payload {
        val probeLength = minOf(length, MAX_HEADER_LENGTH)
        val probe = String(data, 0, probeLength, Charsets.UTF_8)

        val fields = arrayOfNulls<String>(4)
        var cursor = 0
        for (i in 0 until 4) {
            val index = probe.indexOf(SEPARATOR, cursor)
            if (index < 0) throw AggFormatException("头部缺少分隔符")
            fields[i] = probe.substring(cursor, index)
            cursor = index + 1
        }

        if (fields[0] != MAGIC) throw AggFormatException("magic 不匹配")
        val kind = fields[1]?.let { PayloadKind.fromWire(it) }
            ?: throw AggFormatException("未知载荷类型")
        val declared = fields[2]?.toIntOrNull()
            ?: throw AggFormatException("长度字段非法")
        if (declared < 0) throw AggFormatException("长度字段非法")

        val headerBytes = probe.substring(0, cursor).toByteArray(Charsets.UTF_8).size
        if (length - headerBytes != declared) throw AggFormatException("净荷长度与声明不符")

        val body = data.copyOfRange(headerBytes, headerBytes + declared)
        return Payload(kind = kind, name = fields[3].orEmpty(), body = body)
    }
}

/** 标准 CRC-32（IEEE 802.3，多项式 0xEDB88320 反射式）。 */
object Crc32 {
    private val TABLE = IntArray(256) { index ->
        var c = index
        repeat(8) {
            c = if (c and 1 != 0) (0xEDB88320.toInt() xor (c ushr 1)) else (c ushr 1)
        }
        c
    }

    fun compute(data: ByteArray, offset: Int = 0, length: Int = data.size - offset): Int {
        var c = 0xFFFFFFFF.toInt()
        for (i in offset until offset + length) {
            c = TABLE[(c xor data[i].toInt()) and 0xFF] xor (c ushr 8)
        }
        return c xor 0xFFFFFFFF.toInt()
    }
}
