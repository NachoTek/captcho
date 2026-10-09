// MonitorLayout.cs — Shared monitor-layout helpers.
//
// Stable left-to-right, top-to-bottom ordering and Win32-convention
// point-in-rect hit-testing for monitor rectangles. Extracted so the Current
// Monitor resolver (MonitorResolver) and the Selected Monitor picker
// (MonitorPickerTargetResolver) share one source of truth for monitor ordering
// and edge semantics — a future change to either rule cannot silently desync
// the two.

using System.Collections.Generic;

namespace captcho.Capture;

/// <summary>
/// Shared ordering and hit-test helpers over <see cref="MonitorRect"/>.
/// </summary>
public static class MonitorLayout
{
    /// <summary>
    /// Orders monitors left-to-right, then top-to-bottom, returning a new list.
    /// The stable ordering backs both monitor-index assignment and the on-screen
    /// highlight/label order in the picker.
    /// </summary>
    public static List<MonitorRect> SortByPosition(IReadOnlyList<MonitorRect> monitors)
    {
        var sorted = new List<MonitorRect>(monitors);
        sorted.Sort((a, b) =>
        {
            int cmp = a.Left.CompareTo(b.Left);
            return cmp != 0 ? cmp : a.Top.CompareTo(b.Top);
        });
        return sorted;
    }

    /// <summary>
    /// Win32-convention hit test: inclusive left/top edge, exclusive
    /// right/bottom edge, so a point on a shared edge belongs to exactly one
    /// monitor (or a gap), never two.
    /// </summary>
    public static bool Contains(MonitorRect rect, int x, int y) =>
        x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
}
