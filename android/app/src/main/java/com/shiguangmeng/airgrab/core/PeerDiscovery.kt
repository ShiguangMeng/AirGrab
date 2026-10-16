package com.shiguangmeng.airgrab.core

import android.os.Build
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress

data class Peer(
    val id: String,
    val name: String,
    val platform: String,
    val address: String,
    val lastSeen: Long = System.currentTimeMillis()
) {
    /** 信号强度档位 1..4，由最近一次续期间隔换算。 */
    val signalBars: Int
        get() {
            val ageSeconds = (System.currentTimeMillis() - lastSeen) / 1000.0
            return when {
                ageSeconds < 2.5 -> 4
                ageSeconds < 4.5 -> 3
                ageSeconds < 6.5 -> 2
                else -> 1
            }
        }
}

/**
 * 设备发现：UDP 广播信标 + 超时淘汰。
 *
 * 信标为单行文本 `AGG-DISCOVER|<id>|<name>|<platform>|<tcpPort>`，
 * 每 [beaconIntervalMs] 向受限广播地址发一次；超过 [ttlMs] 未再收到即视为离线。
 * 不依赖任何账号体系，局域网内平权发现。
 */
class PeerDiscovery(
    var displayName: String = Build.MODEL ?: "Android",
    var platform: String = "Android",
    var beaconIntervalMs: Long = 2_000L,
    var ttlMs: Long = 8_000L
) {
    private val selfId: String = java.util.UUID.randomUUID().toString().take(8).lowercase()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _peers = MutableStateFlow<List<Peer>>(emptyList())
    val peers: StateFlow<List<Peer>> = _peers.asStateFlow()

    private val _status = MutableStateFlow("未启动")
    val status: StateFlow<String> = _status.asStateFlow()

    private var socket: DatagramSocket? = null
    private var receiverJob: Job? = null
    private var beaconJob: Job? = null

    fun start() {
        if (socket != null) return
        runCatching {
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                soTimeout = 0
                bind(InetSocketAddress(UDP_PORT))
            }
        }.onSuccess { sock ->
            socket = sock
            receiverJob = scope.launch { receiveLoop(sock) }
            beaconJob = scope.launch { beaconLoop(sock) }
            sweepJob = scope.launch { sweepLoop() }
            _status.value = "广播发现中 :$UDP_PORT"
        }.onFailure {
            _status.value = "发现服务启动失败：${it.message}"
        }
    }

    private var sweepJob: Job? = null

    fun stop() {
        receiverJob?.cancel()
        beaconJob?.cancel()
        sweepJob?.cancel()
        socket?.close()
        socket = null
        _status.value = "已停止"
    }

    private suspend fun receiveLoop(sock: DatagramSocket) {
        val buffer = ByteArray(1024)
        while (scope.isActive) {
            try {
                val packet = DatagramPacket(buffer, buffer.size)
                sock.receive(packet)
                val text = String(packet.data, 0, packet.length, Charsets.UTF_8).trim()
                handleBeacon(text, packet.address.hostAddress ?: continue)
            } catch (_: Exception) {
                if (sock.isClosed) return
            }
        }
    }

    private fun handleBeacon(text: String, address: String) {
        val fields = text.split('|')
        if (fields.size < 4 || fields[0] != BEACON_PREFIX) return
        if (fields[1].equals(selfId, ignoreCase = true)) return   // 自己的广播丢弃

        val peer = Peer(
            id = fields[1],
            name = fields[2],
            platform = fields[3],
            address = address,
            lastSeen = System.currentTimeMillis()
        )

        _peers.value = _peers.value
            .filterNot { it.id == peer.id }
            .plus(peer)
            .sortedWith(compareByDescending<Peer> { it.signalBars }.thenBy { it.name })
    }

    private suspend fun beaconLoop(sock: DatagramSocket) {
        val target = InetAddress.getByName("255.255.255.255")
        val line = "$BEACON_PREFIX|$selfId|$displayName|$platform|${FileTransfer.TCP_PORT}\n"
        val bytes = line.toByteArray(Charsets.UTF_8)

        while (scope.isActive) {
            runCatching {
                sock.send(DatagramPacket(bytes, bytes.size, target, UDP_PORT))
            }
            delay(beaconIntervalMs)
        }
    }

    /** 淘汰超过 ttl 未续期的设备。 */
    private suspend fun sweepLoop() {
        while (scope.isActive) {
            delay(ttlMs / 2)
            val deadline = System.currentTimeMillis() - ttlMs
            val filtered = _peers.value.filter { it.lastSeen >= deadline }
            if (filtered.size != _peers.value.size) {
                _peers.value = filtered
            }
        }
    }

    companion object {
        const val UDP_PORT = 25585
        const val BEACON_PREFIX = "AGG-DISCOVER"
    }
}
