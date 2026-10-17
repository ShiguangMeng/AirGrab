package com.shiguangmeng.airgrab.ui

import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.PanTool
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.LargeTopAppBar
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.shiguangmeng.airgrab.core.GestureState
import com.shiguangmeng.airgrab.core.HandPoseDetector
import com.shiguangmeng.airgrab.core.Payload
import kotlin.math.roundToInt

private val TAB_TITLES = listOf("投送", "设备", "记录", "设置")
private val TAB_ICONS = listOf(
    Icons.Filled.PanTool,
    Icons.Filled.Devices,
    Icons.Filled.History,
    Icons.Filled.Settings
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RootScreen(viewModel: AppViewModel) {
    val peers by viewModel.peers.collectAsStateWithLifecycle()
    val outbox by viewModel.outbox.collectAsStateWithLifecycle()
    val inbox by viewModel.inbox.collectAsStateWithLifecycle()
    val state by viewModel.state.collectAsStateWithLifecycle()
    val carried by viewModel.carried.collectAsStateWithLifecycle()
    val status by viewModel.status.collectAsStateWithLifecycle()
    val channel by viewModel.channel.collectAsStateWithLifecycle()
    val toast by viewModel.toast.collectAsStateWithLifecycle()

    var tab by rememberSaveable { mutableIntStateOf(0) }
    var dragging by remember { mutableStateOf<Payload?>(null) }
    var dragOffset by remember { mutableStateOf(Offset.Zero) }

    val snackbarHostState = remember { SnackbarHostState() }

    LaunchedEffect(toast) {
        toast?.let {
            snackbarHostState.showSnackbar(it)
            viewModel.dismissToast()
        }
    }

    Scaffold(
        modifier = Modifier.fillMaxSize(),
        containerColor = MaterialTheme.colorScheme.surface,
        snackbarHost = { SnackbarHost(snackbarHostState) },
        topBar = {
            LargeTopAppBar(
                title = {
                    Column {
                        Text(
                            text = if (tab == 0) "AirGrab" else TAB_TITLES[tab],
                            fontWeight = FontWeight.SemiBold,
                            letterSpacing = (-0.5).sp
                        )
                        Text(
                            text = "${peers.size} 台设备在附近 · ${channel.title}",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                },
                colors = TopAppBarDefaults.largeTopAppBarColors(
                    containerColor = Color.Transparent
                )
            )
        },
        floatingActionButton = {
            if (tab == 0) {
                FloatingActionButton(
                    onClick = {
                        outbox.firstOrNull()?.let { viewModel.sendToFirstPeer(it) }
                    },
                    shape = MaterialTheme.shapes.large,
                    containerColor = MaterialTheme.colorScheme.primary
                ) {
                    Icon(Icons.Filled.Add, contentDescription = "投送首项")
                }
            }
        },
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surfaceVariant) {
                TAB_TITLES.forEachIndexed { index, title ->
                    NavigationBarItem(
                        selected = tab == index,
                        onClick = { tab = index },
                        icon = { Icon(TAB_ICONS[index], contentDescription = title) },
                        label = { Text(title, fontSize = 11.sp) }
                    )
                }
            }
        }
    ) { padding ->
        Box(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
        ) {
            when (tab) {
                0 -> SendPage(
                    outbox = outbox,
                    state = state,
                    carried = carried,
                    peers = peers,
                    channel = channel,
                    detector = viewModel.detector,
                    onDragStart = { payload ->
                        dragging = payload
                        viewModel.beginDrag(payload)
                    },
                    onDrag = { offset -> dragOffset += offset },
                    onDragEnd = { payload ->
                        viewModel.endDrag(payload, peers.firstOrNull())
                        dragging = null
                        dragOffset = Offset.Zero
                    },
                    onDragCancel = {
                        viewModel.cancelDrag()
                        dragging = null
                        dragOffset = Offset.Zero
                    }
                )

                1 -> DevicesPage(peers = peers, onSend = { viewModel.sendToFirstPeer(it) }, outbox = outbox)
                2 -> HistoryPage(inbox = inbox, outbox = outbox)
                else -> SettingsPage(
                    channel = channel,
                    status = status,
                    onChannel = { viewModel.setChannel(it) }
                )
            }

            // 抓取中的悬浮卡片
            dragging?.let { payload ->
                Box(modifier = Modifier.fillMaxSize()) {
                    DragGhost(
                        payload = payload,
                        modifier = Modifier
                            .align(Alignment.BottomStart)
                            .offset { IntOffset(80 + dragOffset.x.roundToInt(), -420 + dragOffset.y.roundToInt()) }
                    )
                }
            }
        }
    }
}

@Composable
private fun SendPage(
    outbox: List<Payload>,
    state: GestureState,
    carried: String,
    peers: List<com.shiguangmeng.airgrab.core.Peer>,
    channel: AppViewModel.Channel,
    detector: HandPoseDetector,
    onDragStart: (Payload) -> Unit,
    onDrag: (Offset) -> Unit,
    onDragEnd: (Payload) -> Unit,
    onDragCancel: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp)
    ) {
        GesturePanel(state = state, carried = carried, hint = state.hint)

        // 摄像头通道：预览与采集是同一份 CameraX 会话，
        // 预览出现即代表帧正在喂给推理器。
        if (channel == AppViewModel.Channel.CAMERA) {
            Spacer(Modifier.height(14.dp))
            CameraHandPreview(
                detector = detector,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(220.dp)
                    .clip(RoundedCornerShape(22.dp))
            )
        }

        Spacer(Modifier.height(18.dp))

        if (peers.isNotEmpty()) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                peers.take(3).forEach { peer ->
                    FilterChip(
                        selected = peer == peers.first(),
                        onClick = { },
                        label = { Text(peer.name, fontSize = 12.sp) },
                        colors = FilterChipDefaults.filterChipColors(
                            selectedContainerColor = MaterialTheme.colorScheme.primaryContainer
                        )
                    )
                }
            }
            Spacer(Modifier.height(14.dp))
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "可抓取内容",
                fontWeight = FontWeight.SemiBold,
                fontSize = 14.sp,
                modifier = Modifier.weight(1f)
            )
            Text(
                text = "${outbox.size} 项",
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        Spacer(Modifier.height(10.dp))

        LazyVerticalGrid(
            columns = GridCells.Fixed(2),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
            modifier = Modifier
                .fillMaxWidth()
                .height(((outbox.size + 1) / 2 * 168).dp)
        ) {
            items(outbox, key = { it.id }) { payload ->
                PayloadTile(
                    payload = payload,
                    modifier = Modifier.pointerInput(payload.id) {
                        detectDragGestures(
                            onDragStart = { onDragStart(payload) },
                            onDragCancel = onDragCancel,
                            onDragEnd = { onDragEnd(payload) },
                            onDrag = { change, offset ->
                                change.consume()
                                onDrag(offset)
                            }
                        )
                    }
                )
            }
        }

        Spacer(Modifier.height(16.dp))

        Text(
            text = "按住卡片拖动 = 握拳抓取；松手 = 张开手掌投送",
            fontSize = 11.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.fillMaxWidth()
        )

        Spacer(Modifier.height(96.dp))
    }
}

@Composable
private fun DevicesPage(
    peers: List<com.shiguangmeng.airgrab.core.Peer>,
    onSend: (Payload) -> Unit,
    outbox: List<Payload>
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp)
    ) {
        if (peers.isEmpty()) {
            EmptyHint(
                title = "未发现附近设备",
                detail = "请确认对方设备已打开 AirGrab，并与本机处于同一局域网。"
            )
        } else {
            peers.forEach { peer ->
                DeviceCard(peer = peer, onSend = { outbox.firstOrNull()?.let(onSend) })
                Spacer(Modifier.height(10.dp))
            }
        }

        Spacer(Modifier.height(12.dp))
        ProtocolNote(
            title = "发现机制",
            detail = "UDP 广播信标每 2 秒一次，超过 8 秒未续期即判定离线。不依赖账号体系。"
        )
        Spacer(Modifier.height(96.dp))
    }
}

@Composable
private fun HistoryPage(inbox: List<Payload>, outbox: List<Payload>) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp)
    ) {
        SectionList(title = "已接收", items = inbox, empty = "暂无接收记录")
        Spacer(Modifier.height(18.dp))
        SectionList(title = "已投送", items = outbox, empty = "暂无投送记录")
        Spacer(Modifier.height(18.dp))
        ProtocolNote(
            title = "传输层",
            detail = "TCP 分片，片长 64KB，逐片 CRC32 校验；校验失败的片整条丢弃。"
        )
        Spacer(Modifier.height(96.dp))
    }
}

@Composable
private fun SettingsPage(
    channel: AppViewModel.Channel,
    status: String,
    onChannel: (AppViewModel.Channel) -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp)
    ) {
        Text("手势通道", fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
        Spacer(Modifier.height(10.dp))

        AppViewModel.Channel.entries.forEach { item ->
            val selected = item == channel
            androidx.compose.material3.Card(
                onClick = { onChannel(item) },
                colors = androidx.compose.material3.CardDefaults.cardColors(
                    containerColor = if (selected) {
                        MaterialTheme.colorScheme.primaryContainer
                    } else {
                        MaterialTheme.colorScheme.surfaceVariant
                    }
                ),
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(Modifier.padding(14.dp)) {
                    Text(item.title, fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
                    Spacer(Modifier.height(3.dp))
                    Text(
                        item.detail,
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
            Spacer(Modifier.height(10.dp))
        }

        Spacer(Modifier.height(8.dp))
        ProtocolNote(title = "运行状态", detail = status)
        ProtocolNote(
            title = "协议",
            detail = "AGG|<type>|<size>|<name>|<payload>\nUDP :25585 发现 · TCP :25586 传输"
        )
        Spacer(Modifier.height(96.dp))
    }
}
