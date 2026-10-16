package com.shiguangmeng.airgrab.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket

/**
 * 投递层：TCP 分片传输，逐片 CRC32 校验。
 *
 * 报文结构（全部小端）：
 * ```
 * [4B 净荷总长][4B 片偏移][4B 片长][4B 片 CRC32][片数据]
 * ```
 * 接收端逐片校验，任一片失败即丢弃该片 —— 不把损坏数据写进结果集。
 * 偏移量使接收端能够按序重组，并为断点续传预留。
 */
class FileTransfer {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _isServing = MutableStateFlow(false)
    val isServing: StateFlow<Boolean> = _isServing.asStateFlow()

    private val _log = MutableStateFlow("空闲")
    val log: StateFlow<String> = _log.asStateFlow()

    private val _progress = MutableStateFlow(0f)
    val progress: StateFlow<Float> = _progress.asStateFlow()

    var onPayloadReceived: ((Payload) -> Unit)? = null
    var onPayloadSent: ((Payload, String) -> Unit)? = null

    private var server: ServerSocket? = null

    fun startServer() {
        if (server != null) return
        runCatching {
            ServerSocket().apply {
                reuseAddress = true
                bind(InetSocketAddress(TCP_PORT))
            }
        }.onSuccess { sock ->
            server = sock
            _isServing.value = true
            _log.value = "接收服务已就绪 :$TCP_PORT"
            scope.launch { acceptLoop(sock) }
        }.onFailure {
            _log.value = "接收服务启动失败：${it.message}"
        }
    }

    fun stopServer() {
        runCatching { server?.close() }
        server = null
        _isServing.value = false
    }

    private suspend fun acceptLoop(sock: ServerSocket) {
        while (scope.isActive && !sock.isClosed) {
            val client = runCatching { sock.accept() }.getOrNull() ?: continue
            scope.launch { receive(client) }
        }
    }

    // ---------------------------------------------------------------- 接收

    private fun receive(client: Socket) {
        client.use { socket ->
            val input: InputStream = socket.getInputStream()
            val header = ByteArray(HEADER_SIZE)
            val assembly = ByteArrayOutputStream()
            var declaredTotal = -1

            while (true) {
                if (!readFully(input, header, HEADER_SIZE)) break

                val total = readInt(header, 0)
                val offset = readInt(header, 4)
                val length = readInt(header, 8)
                val expected = readInt(header, 12)

                if (length < 0 || length > CHUNK_SIZE) break

                val body = ByteArray(length)
                if (!readFully(input, body, length)) break

                if (Crc32.compute(body) != expected) {
                    _log.value = "校验失败，丢弃该片 @$offset"
                    continue
                }

                if (declaredTotal < 0) declaredTotal = total
                assembly.write(body)

                if (declaredTotal in 1..assembly.size()) {
                    deliver(assembly.toByteArray(), declaredTotal)
                    assembly.reset()
                    declaredTotal = -1
                }
            }
        }
    }

    private fun deliver(frame: ByteArray, length: Int) {
        runCatching { AggCodec.decode(frame, length) }
            .onSuccess { payload ->
                _log.value = "已接收 ${payload.name}（${payload.sizeText}）"
                onPayloadReceived?.invoke(payload)
            }
            .onFailure { _log.value = "载荷解析失败：${it.message}" }
    }

    private fun readFully(input: InputStream, buffer: ByteArray, length: Int): Boolean {
        var read = 0
        while (read < length) {
            val n = runCatching { input.read(buffer, read, length - read) }.getOrDefault(-1)
            if (n <= 0) return false
            read += n
        }
        return true
    }

    // ---------------------------------------------------------------- 发送

    suspend fun send(payload: Payload, peer: Peer): Boolean {
        val wire = AggCodec.encode(payload)
        val chunks = fragment(wire)

        return runCatching {
            Socket().use { socket ->
                socket.connect(InetSocketAddress(peer.address, TCP_PORT), CONNECT_TIMEOUT_MS)
                val output: OutputStream = socket.getOutputStream()

                chunks.forEachIndexed { index, chunk ->
                    output.write(chunk)
                    output.flush()
                    _progress.value = (index + 1).toFloat() / chunks.size
                }
            }
            _log.value = "已投送 ${payload.name} → ${peer.name}"
            onPayloadSent?.invoke(payload, peer.name)
            true
        }.getOrElse {
            _log.value = "传输中断：${it.message}"
            false
        }
    }

    companion object {
        const val TCP_PORT = 25586
        const val CHUNK_SIZE = 64 * 1024
        private const val HEADER_SIZE = 16
        private const val CONNECT_TIMEOUT_MS = 4_000

        fun fragment(wire: ByteArray): List<ByteArray> {
            val chunks = ArrayList<ByteArray>()
            var offset = 0
            while (offset < wire.size) {
                val length = minOf(CHUNK_SIZE, wire.size - offset)
                val chunk = ByteArray(HEADER_SIZE + length)

                writeInt(chunk, 0, wire.size)
                writeInt(chunk, 4, offset)
                writeInt(chunk, 8, length)
                writeInt(chunk, 12, Crc32.compute(wire, offset, length))
                System.arraycopy(wire, offset, chunk, HEADER_SIZE, length)

                chunks.add(chunk)
                offset += length
            }
            return chunks
        }

        private fun writeInt(target: ByteArray, offset: Int, value: Int) {
            target[offset] = (value and 0xFF).toByte()
            target[offset + 1] = ((value ushr 8) and 0xFF).toByte()
            target[offset + 2] = ((value ushr 16) and 0xFF).toByte()
            target[offset + 3] = ((value ushr 24) and 0xFF).toByte()
        }

        private fun readInt(source: ByteArray, offset: Int): Int =
            (source[offset].toInt() and 0xFF) or
                ((source[offset + 1].toInt() and 0xFF) shl 8) or
                ((source[offset + 2].toInt() and 0xFF) shl 16) or
                ((source[offset + 3].toInt() and 0xFF) shl 24)
    }
}
