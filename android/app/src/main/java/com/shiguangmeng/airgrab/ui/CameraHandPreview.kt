package com.shiguangmeng.airgrab.ui

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.SystemClock
import android.util.Size
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.Box
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.shiguangmeng.airgrab.core.HandPoseDetector
import java.util.concurrent.Executors

/**
 * 摄像头采集与预览。
 *
 * MediaPipe 的 `HandLandmarker` 只负责"给定一张图，算出 21 关键点" ——
 * 它自己不会去拿摄像头，帧必须由外部持续喂进来。这里就是那个帧源：
 *
 * CameraX 前置摄像头 → `ImageAnalysis`（RGBA_8888、只保留最新帧）
 *   → `ImageProxy.toBitmap()` → [HandPoseDetector.detect]
 *
 * 推理按 100 ms 节流（约 10 fps）。手势判定依赖 500 ms 级时间窗，
 * 10 fps 的采样密度足够，而全速推理会让 CPU 长时间满载。
 *
 * 采集绑在 lifecycle 上：Composable 进入时绑定，离开时解绑并关闭线程池，
 * 不会在后台偷偷占着摄像头。
 */
@Composable
fun CameraHandPreview(
    detector: HandPoseDetector,
    modifier: Modifier = Modifier
) {
    val context = LocalContext.current

    // CameraX 绑定需要 CAMERA 运行时权限。没有权限时 bindToLifecycle 会被系统
    // 静默拒绝 —— 预览就成了一块黑屏，且不一定抛出可捕获的异常。
    var cameraGranted by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
                PackageManager.PERMISSION_GRANTED
        )
    }

    val cameraLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        cameraGranted = granted
        if (!granted) {
            detector.reportStatus("未授予摄像头权限 · 请在系统设置中开启")
        }
    }

    val notificationLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) {
        // 通知权限只决定前台服务通知是否可见，不影响采集本身。
    }

    LaunchedEffect(Unit) {
        if (!cameraGranted) {
            cameraLauncher.launch(Manifest.permission.CAMERA)
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            notificationLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    if (!cameraGranted) {
        Box(modifier = modifier, contentAlignment = Alignment.Center) {
            Text(
                text = "需要摄像头权限才能识别手势",
                style = MaterialTheme.typography.bodyMedium,
                textAlign = TextAlign.Center
            )
        }
        return
    }

    val lifecycleOwner = LocalLifecycleOwner.current

    val previewView = remember {
        PreviewView(context).apply {
            scaleType = PreviewView.ScaleType.FILL_CENTER
            // COMPATIBLE 走 TextureView，兼容性最好；
            // PERFORMANCE 走 SurfaceView，部分机型上叠加 Compose 会闪黑。
            implementationMode = PreviewView.ImplementationMode.COMPATIBLE
        }
    }

    DisposableEffect(lifecycleOwner) {
        val executor = Executors.newSingleThreadExecutor()
        val providerFuture = ProcessCameraProvider.getInstance(context)

        providerFuture.addListener({
            val provider = providerFuture.get()

            val preview = Preview.Builder()
                .build()
                .also { it.surfaceProvider = previewView.surfaceProvider }

            @Suppress("DEPRECATION")
            val analysis = ImageAnalysis.Builder()
                // 消费不过来时丢旧帧，不排队 —— 手势要的是"现在"，不是补帧。
                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                // 直接输出 RGBA_8888，toBitmap() 才能拿到 MediaPipe 可用的位图。
                .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
                .setTargetResolution(Size(640, 480))
                .build()

            var lastInferenceMs = 0L

            analysis.setAnalyzer(executor) { proxy ->
                val now = SystemClock.elapsedRealtime()

                if (now - lastInferenceMs >= 100) {
                    lastInferenceMs = now
                    try {
                        detector.detect(proxy.toBitmap(), now)
                    } catch (_: Throwable) {
                        // 单帧失败不应中断采集：下一帧继续。
                    }
                }

                proxy.close()
            }

            try {
                provider.unbindAll()
                provider.bindToLifecycle(
                    lifecycleOwner,
                    CameraSelector.DEFAULT_FRONT_CAMERA,
                    preview,
                    analysis
                )
            } catch (_: Throwable) {
                detector.reportStatus("摄像头绑定失败 · 可能被其他应用占用")
            }
        }, ContextCompat.getMainExecutor(context))

        onDispose {
            // future 可能尚未完成 —— 直接 get() 会阻塞 UI 线程。
            if (providerFuture.isDone) {
                runCatching { providerFuture.get().unbindAll() }
            }
            executor.shutdown()
        }
    }

    AndroidView(
        factory = { previewView },
        modifier = modifier
    )
}
