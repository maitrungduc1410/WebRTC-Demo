package com.example.myapplication.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.MotionScheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

private val LightColors = lightColorScheme(
    primary = Color(0xFF5B4FD6),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFE4DFFF),
    onPrimaryContainer = Color(0xFF170065),
    secondary = Color(0xFF5E5C71),
    secondaryContainer = Color(0xFFE4DFF9),
    tertiary = Color(0xFF7D5260),
    tertiaryContainer = Color(0xFFFFD9E3),
    surface = Color(0xFFFCF8FF),
    surfaceContainer = Color(0xFFF1ECF6),
    surfaceContainerHigh = Color(0xFFEBE6F0),
    surfaceContainerHighest = Color(0xFFE5E1EB),
)

private val DarkColors = darkColorScheme(
    primary = Color(0xFFC6BFFF),
    onPrimary = Color(0xFF2B1A9F),
    primaryContainer = Color(0xFF4335BC),
    onPrimaryContainer = Color(0xFFE4DFFF),
    secondary = Color(0xFFC8C3DC),
    secondaryContainer = Color(0xFF474459),
    tertiary = Color(0xFFEFB8C8),
    tertiaryContainer = Color(0xFF633B48),
    surface = Color(0xFF131218),
    surfaceContainer = Color(0xFF1F1E25),
    surfaceContainerHigh = Color(0xFF2A2830),
    surfaceContainerHighest = Color(0xFF35333B),
)

@Composable
private fun appColorScheme(dark: Boolean): ColorScheme {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        val context = LocalContext.current
        return if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
    }
    return if (dark) DarkColors else LightColors
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun AppTheme(darkTheme: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    MaterialExpressiveTheme(
        colorScheme = appColorScheme(darkTheme),
        motionScheme = MotionScheme.expressive(),
        content = content
    )
}
