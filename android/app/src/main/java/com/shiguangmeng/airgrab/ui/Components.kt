package com.shiguangmeng.airgrab.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PanTool
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.shiguangmeng.airgrab.core.GestureState
import com.shiguangmeng.airgrab.core.Payload
import com.shiguangmeng.airgrab.core.PayloadKind
import com.shiguangmeng.airgrab.core.Peer

/** 载荷类型 → 渐变色。四端共用同一套映射，保证跨设备观感一致。 */
fun PayloadKind.gradient(): Brush = when (this) {
    PayloadKind.IMAGE -> Brush.linearGradient(listOf(Color(0xFFFF9E6B), Color(0xFFFF5F8F), Color(0xFFA44BFF)))
    PayloadKind.LINK -> Brush.linearGradient(listOf(Color(0xFF4FACFE), Color(0xFF3B6DFF), Color(0xFF5F3BFF)))
    PayloadKind.FILE -> Brush.linearGradient(listOf(Color(0xFF14D6A4), Color(0xFF12B3C8), Color(0xFF3B8DFF)))
    PayloadKind.TEXT -> Brush.linearGradient(listOf(Color(0xFFB18CFF), Color(0xFF7A5CFF), Color(0xFF4A34AD)))
}

/** 玻璃卡片：载荷在四端统一的呈现形态。 */
@Composable
fun PayloadTile(payload: Payload, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(22.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        elevation = CardDefaults.cardElevation(defaultElevation = 2.dp)
    ) {
        Column {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(92.dp)
                    .background(payload.kind.gradient()),
                contentAlignment = Alignment.Center
            ) {
                Text(
                    text = payload.extensionTag,
                    color = Color.White,
                    fontSize = 19.sp,
                    fontWeight = FontWeight.SemiBold
                )
            }

            Column(Modifier.padding(horizontal = 12.dp, vertical = 10.dp)) {
                Text(
                    text = payload.name,
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Spacer(Modifier.height(3.dp))
                Text(
                    text = payload.sizeText,
                    fontSize = 10.5.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}

/** 抓取中的悬浮卡片。 */
@Composable
fun DragGhost(payload: Payload, modifier: Modifier = Modifier) {
    Card(
        modifier = modifier.width(190.dp),
        shape = RoundedCornerShape(20.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        elevation = CardDefaults.cardElevation(defaultElevation = 12.dp)
    ) {
        Row(
            modifier = Modifier.padding(11.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            Box(
                modifier = Modifier
                    .size(32.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(payload.kind.gradient()),
                contentAlignment = Alignment.Center
            ) {
                Text(payload.extensionTag.take(3), color = Color.White, fontSize = 9.sp)
            }
            Column {
                Text(
                    payload.name,
                    fontSize = 12.sp,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    "松开投送至最近设备",
                    fontSize = 10.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}

/** 手势状态面板：五态进度 + 手型 + 当前搬运载荷。 */
@Composable
fun GesturePanel(state: GestureState, carried: String, hint: String) {
    val accent = when (state) {
        GestureState.GRABBING, GestureState.CARRYING -> MaterialTheme.colorScheme.primary
        GestureState.DROP -> MaterialTheme.colorScheme.tertiary
        else -> MaterialTheme.colorScheme.surfaceVariant
    }
    val animatedAccent by animateColorAsState(accent, label = "gestureAccent")
    val scale by animateFloatAsState(
        targetValue = if (state == GestureState.GRABBING) 0.9f else 1f,
        animationSpec = spring(dampingRatio = 0.55f, stiffness = 900f),
        label = "gestureScale"
    )

    Card(
        shape = RoundedCornerShape(26.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(Modifier.padding(14.dp)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Box(
                    modifier = Modifier
                        .size(46.dp)
                        .clip(RoundedCornerShape(15.dp))
                        .background(animatedAccent),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        Icons.Filled.Handyman,
                        contentDescription = state.title,
                        tint = if (state == GestureState.IDLE || state == GestureState.HOVER) {
                            MaterialTheme.colorScheme.onSurfaceVariant
                        } else {
                            Color.White
                        },
                        modifier = Modifier.size((22 * scale).dp)
                    )
                }

                Column(Modifier.weight(1f)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(state.title, fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
                        Spacer(Modifier.width(8.dp))
                        Text(
                            state.code,
                            fontSize = 10.5.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    Spacer(Modifier.height(3.dp))
                    Text(
                        hint,
                        fontSize = 11.5.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }

                Text(
                    carried,
                    fontSize = 10.5.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.width(72.dp)
                )
            }

            Spacer(Modifier.height(12.dp))

            // 四段进度：HOVER → GRABBING → CARRYING → DROP
            Row(horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                val steps = listOf(
                    GestureState.HOVER,
                    GestureState.GRABBING,
                    GestureState.CARRYING,
                    GestureState.DROP
                )
                steps.forEach { step ->
                    val color = when {
                        state.step > step.step -> MaterialTheme.colorScheme.tertiary
                        state.step == step.step -> MaterialTheme.colorScheme.primary
                        else -> MaterialTheme.colorScheme.outlineVariant
                    }
                    Box(
                        modifier = Modifier
                            .weight(1f)
                            .height(4.dp)
                            .clip(CircleShape)
                            .background(color)
                    )
                }
            }
        }
    }
}

@Composable
fun DeviceCard(peer: Peer, onSend: () -> Unit) {
    Card(
        onClick = onSend,
        shape = RoundedCornerShape(22.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(
            modifier = Modifier.padding(13.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Box(
                modifier = Modifier
                    .size(42.dp)
                    .clip(CircleShape)
                    .background(MaterialTheme.colorScheme.primaryContainer),
                contentAlignment = Alignment.Center
            ) {
                Text(
                    peer.platform.take(1),
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.onPrimaryContainer
                )
            }

            Column(Modifier.weight(1f)) {
                Text(peer.name, fontWeight = FontWeight.SemiBold, fontSize = 14.sp, maxLines = 1)
                Spacer(Modifier.height(3.dp))
                Text(
                    "${peer.platform} · ${peer.address}",
                    fontSize = 11.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }

            LiveBadge()
        }
    }
}

@Composable
fun LiveBadge(text: String = "可接收") {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(5.dp),
        modifier = Modifier
            .clip(CircleShape)
            .background(MaterialTheme.colorScheme.tertiaryContainer)
            .padding(horizontal = 9.dp, vertical = 4.dp)
    ) {
        Box(
            Modifier
                .size(6.dp)
                .clip(CircleShape)
                .background(MaterialTheme.colorScheme.tertiary)
        )
        Text(text, fontSize = 10.5.sp, color = MaterialTheme.colorScheme.onTertiaryContainer)
    }
}

@Composable
fun ProtocolNote(title: String, detail: String) {
    Card(
        shape = RoundedCornerShape(20.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        modifier = Modifier
            .fillMaxWidth()
            .padding(bottom = 10.dp)
    ) {
        Column(Modifier.padding(14.dp)) {
            Text(title, fontWeight = FontWeight.SemiBold, fontSize = 12.5.sp)
            Spacer(Modifier.height(5.dp))
            Text(
                detail,
                fontSize = 11.5.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
fun EmptyHint(title: String, detail: String) {
    Card(
        shape = RoundedCornerShape(22.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(Modifier.padding(16.dp)) {
            Text(title, fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
            Spacer(Modifier.height(6.dp))
            Text(
                detail,
                fontSize = 12.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
    }
}

@Composable
fun SectionList(title: String, items: List<Payload>, empty: String) {
    Column(Modifier.fillMaxWidth()) {
        Text(title, fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
        Spacer(Modifier.height(10.dp))

        if (items.isEmpty()) {
            Text(
                empty,
                fontSize = 12.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        } else {
            items.take(6).forEach { payload ->
                Card(
                    shape = RoundedCornerShape(18.dp),
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.surfaceVariant
                    ),
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(bottom = 9.dp)
                ) {
                    Row(
                        modifier = Modifier.padding(11.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(11.dp)
                    ) {
                        Box(
                            modifier = Modifier
                                .size(42.dp)
                                .clip(RoundedCornerShape(13.dp))
                                .background(payload.kind.gradient()),
                            contentAlignment = Alignment.Center
                        ) {
                            Text(
                                payload.extensionTag.take(3),
                                color = Color.White,
                                fontSize = 10.sp,
                                fontWeight = FontWeight.SemiBold
                            )
                        }
                        Column {
                            Text(
                                payload.name,
                                fontSize = 13.sp,
                                fontWeight = FontWeight.SemiBold,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis
                            )
                            Text(
                                payload.sizeText,
                                fontSize = 10.5.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                }
            }
        }
    }
}
