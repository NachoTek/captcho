// MonitorPickerTargetResolver.cs — Pure, headless-testable Selected Monitor
// picker contract.
//
// The interactive monitor picker overlay is a Win32 layered window that cannot
// run in a headless test. Its externally observable contract — which monitor
// is hovered, how gaps are rejected, how bounds and labels survive mixed-DPI
// and negative-coordinate layouts — is factored into this pure resolver so it
// can be covered by focused contract tests. The production overlay delegates
// hover resolution to this type; the runtime CaptureWorkflowSession consumes
// the resulting MonitorTarget.
//
// Hit-testing matches the established Win32 convention used by MonitorResolver
// for the Current Monitor route: inclusive left/top edge, exclusive
// right/bottom edge. Monitors are ordered left-to-right then top-to-bottom so
// the on-screen highlight order, the 1-based label index, and the capture
// bounds stay stable across renders.

using System;
using System.Collections.Generic;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Resolves the hovered <see cref="MonitorTarget"/> for a cursor position over
/// a Virtual Desktop monitor layout, or null when the cursor is in a
/// monitor-layout gap. Pure and side-effect-free so the picker's hover/gap
/// contract is testable without an interactive desktop.
/// </summary>
public static class MonitorPickerTargetResolver
{
    /// <summary>
    /// Returns the monitor under the cursor as a <see cref="MonitorTarget"/>
    /// carrying its Virtual Desktop bounds and a pointer-adjacent label, or
    /// null when the cursor is outside every monitor rectangle (a layout gap).
    /// An empty monitor enumeration also yields null. The signed X/Y origin and
    /// exact pixel dimensions (including non-round dimensions from mixed-DPI
    /// scaling) are preserved so the bounds reach the workflow intact.
    /// </summary>
    public static MonitorTarget? Resolve(IReadOnlyList<MonitorRect> monitors, int cursorX, int cursorY)
    {
        if (monitors is null || monitors.Count == 0)
        {
            return null;
        }

        var sorted = MonitorLayout.SortByPosition(monitors);

        for (int i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            if (MonitorLayout.Contains(r, cursorX, cursorY))
            {
                return new MonitorTarget
                {
                    Label = FormatLabel(i + 1, r),
                    X = r.Left,
                    Y = r.Top,
                    Width = (uint)(r.Right - r.Left),
                    Height = (uint)(r.Bottom - r.Top),
                };
            }
        }

        // Cursor is in a monitor-layout gap. The overlay keeps the picker open
        // on clicks here (gap rejection); only Escape cancels.
        return null;
    }

    private static string FormatLabel(int oneBasedIndex, MonitorRect r) =>
        $"Monitor {oneBasedIndex} ({r.Right - r.Left}×{r.Bottom - r.Top})";
}
