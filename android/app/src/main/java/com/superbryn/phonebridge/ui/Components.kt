package com.superbryn.phonebridge.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import com.superbryn.phonebridge.state.LinkStatus

/** Card with a section title. The one container everything else sits in. */
@Composable
fun Section(
    title: String,
    modifier: Modifier = Modifier,
    trailing: @Composable (() -> Unit)? = null,
    content: @Composable ColumnScope.() -> Unit
) {
    Column(
        modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(16.dp))
            .background(Palette.Surface)
            .border(1.dp, Palette.Outline, RoundedCornerShape(16.dp))
            .padding(16.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                title.uppercase(),
                style = MaterialTheme.typography.labelSmall,
                color = Palette.TextMuted,
                modifier = Modifier.weight(1f)
            )
            trailing?.invoke()
        }
        Spacer(Modifier.height(12.dp))
        content()
    }
}

/** Label + control on one line. The repeating unit of the control panel. */
@Composable
fun ControlRow(
    label: String,
    subtitle: String? = null,
    enabled: Boolean = true,
    trailing: @Composable () -> Unit
) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text(
                label,
                style = MaterialTheme.typography.bodyMedium,
                color = if (enabled) Palette.TextPrimary else Palette.TextMuted
            )
            if (subtitle != null) {
                Text(subtitle, style = MaterialTheme.typography.bodySmall, color = Palette.TextMuted)
            }
        }
        Spacer(Modifier.width(12.dp))
        trailing()
    }
}

@Composable
fun BridgeSwitch(checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Switch(
        checked = checked,
        onCheckedChange = onChange,
        enabled = enabled,
        colors = SwitchDefaults.colors(
            checkedThumbColor = Palette.OnPrimary,
            checkedTrackColor = Palette.Primary,
            uncheckedThumbColor = Palette.TextMuted,
            uncheckedTrackColor = Palette.SurfaceRaised,
            uncheckedBorderColor = Palette.Outline,
            disabledCheckedTrackColor = Palette.Outline,
            disabledUncheckedTrackColor = Palette.Surface
        )
    )
}

/** Segmented selector. Used for camera facing and mic source. */
@Composable
fun SegmentedChoice(
    options: List<Pair<String, String>>,   // value to label
    selected: String,
    enabled: Boolean = true,
    onSelect: (String) -> Unit
) {
    Row(
        Modifier
            .clip(RoundedCornerShape(10.dp))
            .background(Palette.SurfaceRaised)
            .padding(3.dp)
    ) {
        for ((value, label) in options) {
            val active = value == selected
            val bg by animateColorAsState(
                if (active) Palette.Primary else Color.Transparent,
                label = "segbg"
            )
            Box(
                Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .background(bg)
                    .clickable(enabled = enabled && !active) { onSelect(value) }
                    .padding(horizontal = 14.dp, vertical = 7.dp)
            ) {
                Text(
                    label,
                    style = MaterialTheme.typography.bodySmall,
                    color = when {
                        active -> Palette.OnPrimary
                        enabled -> Palette.TextSecondary
                        else -> Palette.TextMuted
                    }
                )
            }
        }
    }
}

@Composable
fun StatusDot(status: LinkStatus, modifier: Modifier = Modifier) {
    val color = when (status) {
        LinkStatus.CONNECTED -> Palette.Success
        LinkStatus.CONNECTING -> Palette.Warning
        LinkStatus.INCOMPATIBLE -> Palette.Danger
        LinkStatus.DISCONNECTED -> Palette.TextMuted
    }
    val animated by animateColorAsState(color, label = "dot")
    Box(modifier.size(8.dp).clip(CircleShape).background(animated))
}

/** Small monospace key/value line, for signal strength, resolution and the like. */
@Composable
fun MetricLine(key: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 2.dp)) {
        Text(key, style = MaterialTheme.typography.bodySmall, color = Palette.TextMuted, modifier = Modifier.width(104.dp))
        Text(
            value,
            style = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
            color = Palette.TextSecondary
        )
    }
}

/** Explains a capability the app genuinely cannot have, so it does not read as a bug. */
@Composable
fun InfoNote(text: String) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(Palette.SurfaceRaised)
            .padding(12.dp)
    ) {
        Text("i", style = MaterialTheme.typography.labelSmall, color = Palette.Primary, modifier = Modifier.width(16.dp))
        Text(text, style = MaterialTheme.typography.bodySmall, color = Palette.TextSecondary)
    }
}

@Composable
fun ErrorNote(text: String) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(Palette.Danger.copy(alpha = 0.12f))
            .border(1.dp, Palette.Danger.copy(alpha = 0.35f), RoundedCornerShape(10.dp))
            .padding(12.dp)
    ) {
        Text(text, style = MaterialTheme.typography.bodySmall, color = Palette.Danger)
    }
}
