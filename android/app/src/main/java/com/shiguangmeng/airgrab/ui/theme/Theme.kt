package com.shiguangmeng.airgrab.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

/**
 * Material 3 Expressive 的落点：
 * 1. 动态取色 —— Android 12+ 直接取壁纸种子色，整机主题随之变化；
 * 2. 高饱和撞色 —— 低版本兜底方案用紫色主色 + 玫红第三色，保持 Expressive 的色彩张力；
 * 3. 大圆角与弹性动效在组件层实现（见 Components.kt）。
 */
private val SeedPrimary = Color(0xFF5B46C8)
private val SeedPrimaryContainer = Color(0xFFE6DDFF)
private val SeedOnPrimaryContainer = Color(0xFF190055)
private val SeedTertiary = Color(0xFFB04E7A)
private val SeedTertiaryContainer = Color(0xFFFFD9E2)
private val SeedSurface = Color(0xFFFDF7FF)
private val SeedSurfaceContainer = Color(0xFFF3ECFA)
private val SeedOnSurface = Color(0xFF1C1A22)
private val SeedOnSurfaceVariant = Color(0xFF4A4459)

private val LightScheme = lightColorScheme(
    primary = SeedPrimary,
    onPrimary = Color.White,
    primaryContainer = SeedPrimaryContainer,
    onPrimaryContainer = SeedOnPrimaryContainer,
    tertiary = SeedTertiary,
    tertiaryContainer = SeedTertiaryContainer,
    surface = SeedSurface,
    surfaceVariant = SeedSurfaceContainer,
    onSurface = SeedOnSurface,
    onSurfaceVariant = SeedOnSurfaceVariant
)

private val DarkScheme = darkColorScheme(
    primary = Color(0xFFCBBEFF),
    onPrimary = Color(0xFF2A1A6B),
    primaryContainer = Color(0xFF43309F),
    onPrimaryContainer = Color(0xFFE6DDFF),
    tertiary = Color(0xFFFFB0CB),
    tertiaryContainer = Color(0xFF74334F),
    surface = Color(0xFF141218),
    surfaceVariant = Color(0xFF2B2930),
    onSurface = Color(0xFFE6E1E9),
    onSurfaceVariant = Color(0xFFCAC4D0)
)

@Composable
fun AirGrabTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    dynamicColor: Boolean = true,
    content: @Composable () -> Unit
) {
    val context = LocalContext.current

    val colorScheme = when {
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S ->
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)

        darkTheme -> DarkScheme
        else -> LightScheme
    }

    MaterialTheme(
        colorScheme = colorScheme,
        typography = Typography(),
        content = content
    )
}
