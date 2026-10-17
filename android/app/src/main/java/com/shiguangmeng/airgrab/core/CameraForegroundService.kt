package com.shiguangmeng.airgrab.core

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.shiguangmeng.airgrab.MainActivity
import com.shiguangmeng.airgrab.R

/**
 * 后台摄像头保活服务。
 *
 * Android 10 起对后台应用访问摄像头有硬性限制：进程一旦降到后台优先级，
 * CameraX / MediaPipe 的采集会被系统直接切断。声明
 * `foregroundServiceType="camera"` 的前台服务把进程维持在前台优先级，
 * 采集才能在切到其他应用后继续。
 *
 * 服务本身不持有摄像头 —— 采集管线仍在 [HandPoseDetector] 中，
 * 二者通过进程内共享状态协作。服务只负责保活与常驻通知，
 * 这样采集代码不需要改造成 Service 绑定模型。
 */
class CameraForegroundService : Service() {

    private val silence = SilenceKeepAlive()

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        ensureChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val notification = buildNotification()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }

        // 双保险：前台服务维持进程优先级，静音音频再挡住部分厂商 ROM 的额外冻结策略。
        silence.start()

        // 被系统回收后自动重建，避免用户切回应用时才发现采集已停。
        return START_STICKY
    }

    override fun onDestroy() {
        silence.stop()
        super.onDestroy()
    }

    private fun ensureChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return

        val manager = getSystemService(NotificationManager::class.java) ?: return
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return

        val channel = NotificationChannel(
            CHANNEL_ID,
            "隔空手势采集",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "保持摄像头在后台持续识别手势"
            setShowBadge(false)
        }
        manager.createNotificationChannel(channel)
    }

    private fun buildNotification(): Notification {
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("AirGrab 正在隔空识别手势")
            .setContentText("摄像头在前台服务中持续采集，切换应用不会中断")
            .setSmallIcon(R.drawable.ic_launcher)
            .setContentIntent(open)
            .setOngoing(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    companion object {
        private const val CHANNEL_ID = "airgrab.camera"
        private const val NOTIFICATION_ID = 1001

        fun start(context: Context) {
            val intent = Intent(context, CameraForegroundService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, CameraForegroundService::class.java))
        }
    }
}
