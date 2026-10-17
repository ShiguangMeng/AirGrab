package com.shiguangmeng.airgrab.core

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.DisplayMetrics

/**
 * 屏幕内容抓取。
 *
 * 手势通道在握拳确认后立即截取当前屏幕作为投送内容 ——
 * 用户不需要事先挑内容，「抓起来的就是此刻的屏幕」。
 *
 * 投影与虚拟显示保持**常驻**：Android 14 起同一个授权 Intent 只能用于一次
 * `getMediaProjection`，每次抓帧都重建会抛 SecurityException。
 */
object ScreenCaptureManager {
    private var projection: MediaProjection? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var imageReader: ImageReader? = null

    val isReady: Boolean get() = imageReader != null

    /** 用 Activity 返回的授权结果建立常驻投影。 */
    fun start(context: Context, resultCode: Int, data: Intent): Boolean {
        stop()

        val manager = context.getSystemService(Context.MEDIA_PROJECTION_SERVICE)
            as? MediaProjectionManager ?: return false

        val created = runCatching { manager.getMediaProjection(resultCode, data) }.getOrNull()
            ?: return false

        // Android 14 要求先注册回调，否则 createVirtualDisplay 直接失败。
        created.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                releaseResources()
            }
        }, Handler(Looper.getMainLooper()))

        val metrics: DisplayMetrics = context.resources.displayMetrics
        val width = metrics.widthPixels
        val height = metrics.heightPixels

        val reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2)

        val display = created.createVirtualDisplay(
            "airgrab-screen",
            width,
            height,
            metrics.densityDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
            reader.surface,
            null,
            null
        )

        if (display == null) {
            runCatching { created.stop() }
            runCatching { reader.close() }
            return false
        }

        projection = created
        imageReader = reader
        virtualDisplay = display

        // 等首帧到达，避免第一次抓取拿到 null。
        val deadline = SystemClock.elapsedRealtime() + 1200
        while (SystemClock.elapsedRealtime() < deadline) {
            val image = runCatching { reader.acquireLatestImage() }.getOrNull()
            if (image != null) {
                image.close()
                break
            }
            Thread.sleep(30)
        }

        return true
    }

    /** 抓取当前屏幕。失败返回 null。 */
    fun capture(): Bitmap? {
        val reader = imageReader ?: return null
        val image = runCatching { reader.acquireLatestImage() }.getOrNull() ?: return null

        return try {
            toBitmap(image)
        } catch (_: Throwable) {
            null
        } finally {
            runCatching { image.close() }
        }
    }

    fun stop() {
        releaseResources()
    }

    private fun releaseResources() {
        // 先把字段置空再释放：projection.stop() 会回调 onStop → 再次进入本函数，
        // 不先置空会形成递归。
        val display = virtualDisplay
        val reader = imageReader
        val mediaProjection = projection

        virtualDisplay = null
        imageReader = null
        projection = null

        runCatching { display?.release() }
        runCatching { reader?.close() }
        runCatching { mediaProjection?.stop() }
    }

    private fun toBitmap(image: Image): Bitmap {
        val plane = image.planes[0]
        val buffer = plane.buffer
        val pixelStride = plane.pixelStride
        val rowStride = plane.rowStride
        val rowPadding = rowStride - pixelStride * image.width

        // 行末可能有 padding，先按带 padding 的宽度建位图再裁掉。
        val padded = Bitmap.createBitmap(
            image.width + rowPadding / pixelStride,
            image.height,
            Bitmap.Config.ARGB_8888
        )
        padded.copyPixelsFromBuffer(buffer)

        val cropped = Bitmap.createBitmap(padded, 0, 0, image.width, image.height)
        if (cropped !== padded) padded.recycle()

        return cropped
    }
}
