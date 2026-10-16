package com.shiguangmeng.airgrab.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.shiguangmeng.airgrab.core.FileTransfer
import com.shiguangmeng.airgrab.core.GestureState
import com.shiguangmeng.airgrab.core.GestureStateMachine
import com.shiguangmeng.airgrab.core.HandPoseDetector
import com.shiguangmeng.airgrab.core.Payload
import com.shiguangmeng.airgrab.core.PayloadKind
import com.shiguangmeng.airgrab.core.Peer
import com.shiguangmeng.airgrab.core.PeerDiscovery
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * 应用状态中枢：把手势、发现、传输三条链路汇聚成界面可直接消费的单一状态源。
 */
class AppViewModel(app: Application) : AndroidViewModel(app) {

    enum class Channel(val title: String, val detail: String) {
        SIMULATED("模拟输入", "触摸事件驱动状态机"),
        CAMERA("摄像头识别", "MediaPipe 手部 21 关键点")
    }

    val discovery = PeerDiscovery()
    val transfer = FileTransfer()
    val detector = HandPoseDetector(app)

    private val machine = GestureStateMachine()

    private val _peers = MutableStateFlow<List<Peer>>(emptyList())
    val peers: StateFlow<List<Peer>> = _peers.asStateFlow()

    private val _outbox = MutableStateFlow(seedOutbox())
    val outbox: StateFlow<List<Payload>> = _outbox.asStateFlow()

    private val _inbox = MutableStateFlow<List<Payload>>(emptyList())
    val inbox: StateFlow<List<Payload>> = _inbox.asStateFlow()

    private val _state = MutableStateFlow(GestureState.IDLE)
    val state: StateFlow<GestureState> = _state.asStateFlow()

    private val _carried = MutableStateFlow("—")
    val carried: StateFlow<String> = _carried.asStateFlow()

    private val _status = MutableStateFlow("就绪")
    val status: StateFlow<String> = _status.asStateFlow()

    private val _channel = MutableStateFlow(Channel.SIMULATED)
    val channel: StateFlow<Channel> = _channel.asStateFlow()

    private val _toast = MutableStateFlow<String?>(null)
    val toast: StateFlow<String?> = _toast.asStateFlow()

    var dragging: Payload? = null
        private set

    init {
        viewModelScope.launch {
            discovery.peers.collect { _peers.value = it }
        }
        viewModelScope.launch {
            transfer.log.collect { /* 传输日志单独暴露在设置页 */ }
        }
        viewModelScope.launch {
            detector.pose.collect { pose ->
                if (_channel.value == Channel.CAMERA) machine.ingest(pose)
            }
        }
        viewModelScope.launch {
            detector.status.collect { if (_channel.value == Channel.CAMERA) _status.value = it }
        }

        machine.onStateChanged = { _state.value = it }
        machine.onGrabConfirmed = { _status.value = "抓取已确认，移向目标设备" }
        machine.onDropConfirmed = { _status.value = "松开手掌，投递中…" }
        machine.onCancelled = { _status.value = "本次抓取已取消" }

        transfer.onPayloadReceived = { payload ->
            _inbox.value = listOf(payload) + _inbox.value
            showToast("已收到 ${payload.name}")
        }
        transfer.onPayloadSent = { payload, peerName ->
            _outbox.value = listOf(payload) + _outbox.value
            machine.finish()
            showToast("已由本机投送至 $peerName")
        }
    }

    fun start() {
        discovery.start()
        transfer.startServer()
        if (_channel.value == Channel.CAMERA) detector.start()
        _status.value = "正在局域网内发现设备…"
    }

    fun stop() {
        discovery.stop()
        transfer.stopServer()
        detector.stop()
    }

    fun setChannel(target: Channel) {
        if (target == _channel.value) return
        _channel.value = target
        if (target == Channel.CAMERA) {
            detector.start()
            _status.value = detector.status.value
        } else {
            detector.stop()
            _status.value = "触摸事件驱动状态机"
        }
        showToast(
            if (target == Channel.CAMERA) "已切换至摄像头手势通道" else "已切换至模拟输入通道"
        )
    }

    // ---------------------------------------------------------------- 抓取投送

    /** 把一个拖拽会话映射成与摄像头通道完全相同的状态机输入。 */
    fun beginDrag(payload: Payload) {
        dragging = payload
        machine.beginCarrying(payload.name)
        _carried.value = payload.name
        _status.value = "搬运 ${payload.name}，移向目标设备"
    }

    fun endDrag(payload: Payload, target: Peer?) {
        machine.confirmDrop()
        machine.finish()
        dragging = null
        _carried.value = "—"

        if (target == null) {
            _status.value = "附近暂无设备，已取消投送"
            return
        }
        viewModelScope.launch {
            transfer.send(payload, target)
        }
    }

    fun cancelDrag() {
        machine.cancel()
        machine.finish()
        dragging = null
        _carried.value = "—"
        _status.value = "已取消本次抓取"
    }

    fun sendToFirstPeer(payload: Payload) {
        val target = _peers.value.firstOrNull()
        if (target == null) {
            showToast("没有可接收的设备")
            return
        }
        viewModelScope.launch { transfer.send(payload, target) }
    }

    fun dismissToast() {
        _toast.value = null
    }

    private fun showToast(message: String) {
        _toast.value = message
    }

    override fun onCleared() {
        stop()
        super.onCleared()
    }

    companion object {
        private fun seedOutbox(): List<Payload> = listOf(
            Payload.of(PayloadKind.IMAGE, "IMG_2481.HEIC", 3_100_000),
            Payload.fromLink("https://example.com/inspiration"),
            Payload.of(PayloadKind.FILE, "设计稿.fig", 12_400_000),
            Payload.fromText("会议纪要：隔空手势抓取投送评审要点")
        )
    }
}
