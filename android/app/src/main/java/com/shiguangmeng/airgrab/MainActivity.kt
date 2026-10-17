package com.shiguangmeng.airgrab

import android.content.Context
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.ui.Modifier
import com.shiguangmeng.airgrab.core.ScreenCaptureManager
import com.shiguangmeng.airgrab.ui.AppViewModel
import com.shiguangmeng.airgrab.ui.RootScreen
import com.shiguangmeng.airgrab.ui.theme.AirGrabTheme

class MainActivity : ComponentActivity() {

    private val viewModel: AppViewModel by viewModels()

    private val projectionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        val data = result.data
        if (result.resultCode == RESULT_OK && data != null) {
            ScreenCaptureManager.start(applicationContext, result.resultCode, data)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 边到边：内容延伸到状态栏与导航栏下方，符合 M3 的沉浸式层级
        enableEdgeToEdge()

        // 屏幕抓取授权：握拳确认后要立即截屏投送，必须先把授权拿到手。
        // 投影建立后保持常驻 —— Android 14 起同一个授权 Intent 只能用于一次
        // getMediaProjection，每次抓帧都重建会抛 SecurityException。
        if (!ScreenCaptureManager.isReady) {
            requestScreenCapture()
        }

        setContent {
            AirGrabTheme {
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.surface
                ) {
                    RootScreen(viewModel)
                }
            }
        }
    }

    private fun requestScreenCapture() {
        val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as? MediaProjectionManager
            ?: return

        runCatching { projectionLauncher.launch(manager.createScreenCaptureIntent()) }
    }

    override fun onResume() {
        super.onResume()
        viewModel.start()
    }

    override fun onPause() {
        super.onPause()
        viewModel.stop()
    }
}
