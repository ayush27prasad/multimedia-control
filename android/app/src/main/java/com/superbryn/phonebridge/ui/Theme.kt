package com.superbryn.phonebridge.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/**
 * Dark-first palette (FR13). There is no light scheme: the desktop counterpart is dark
 * only, and a mirrored control surface that changes appearance on one end undermines the
 * "same panel, two screens" idea.
 */
object Palette {
    val Background = Color(0xFF0B0E14)
    val Surface = Color(0xFF141822)
    val SurfaceRaised = Color(0xFF1C2130)
    val Outline = Color(0xFF2A3145)

    val Primary = Color(0xFF7AA2F7)
    val OnPrimary = Color(0xFF0B0E14)

    val Success = Color(0xFF9ECE6A)
    val Warning = Color(0xFFE0AF68)
    val Danger = Color(0xFFF7768E)

    val TextPrimary = Color(0xFFC8D3F5)
    val TextSecondary = Color(0xFF8892B0)
    val TextMuted = Color(0xFF5A6684)
}

private val Scheme = darkColorScheme(
    primary = Palette.Primary,
    onPrimary = Palette.OnPrimary,
    secondary = Palette.Success,
    background = Palette.Background,
    onBackground = Palette.TextPrimary,
    surface = Palette.Surface,
    onSurface = Palette.TextPrimary,
    surfaceVariant = Palette.SurfaceRaised,
    onSurfaceVariant = Palette.TextSecondary,
    outline = Palette.Outline,
    error = Palette.Danger
)

private val AppTypography = Typography(
    headlineSmall = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.2).sp),
    titleMedium = TextStyle(fontSize = 15.sp, fontWeight = FontWeight.Medium),
    bodyMedium = TextStyle(fontSize = 14.sp, fontWeight = FontWeight.Normal),
    bodySmall = TextStyle(fontSize = 12.sp, fontWeight = FontWeight.Normal),
    // Monospace for identifiers - SSIDs, resolutions, bitrates - so they align and scan.
    labelSmall = TextStyle(fontSize = 11.sp, fontWeight = FontWeight.Medium, fontFamily = FontFamily.Monospace, letterSpacing = 0.4.sp)
)

@Composable
fun PhoneBridgeTheme(
    @Suppress("UNUSED_PARAMETER") darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit
) {
    MaterialTheme(colorScheme = Scheme, typography = AppTypography, content = content)
}
