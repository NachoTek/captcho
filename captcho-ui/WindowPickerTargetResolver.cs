// WindowPickerTargetResolver.cs — Pure, headless-testable Selected Window
// picker contract.
//
// The interactive window picker overlay is a Win32 layered window that cannot
// run in a headless test. Its externally observable contract — which eligible
// window is hovered, how empty-desktop points yield no target (the Full Desktop
// fallback), how z-order picks the topmost window, and how a window that has
// disappeared between hover and click can never be confirmed — is factored into
// this pure resolver so it can be covered by focused contract tests. The
// production overlay delegates hover/click resolution to this type; the runtime
// CaptureWorkflowSession consumes the resulting WindowPickerResult.
//
// Hit-testing matches the established Win32 convention used elsewhere in
// Captcho (MonitorLayout): inclusive left/top edge, exclusive right/bottom
// edge. Windows are expected in top-to-bottom z-order — the overlay enumerates
// them that way via EnumWindows — so the first containing window is the
// topmost, which is what the user sees under the cursor. The resolver only
// ever returns a handle drawn from the supplied snapshot, so a window that has
// disappeared (absent from a fresh snapshot) can never be confirmed as a stale
// target.

using System;
using System.Collections.Generic;

namespace captcho.UI;

/// <summary>
/// An eligible top-level window in the picker's input snapshot — the pure
/// resolver's input shape. Carries the stable HWND identity, the full Virtual
/// Desktop bounds (which may cross monitor boundaries and sit at negative
/// coordinates), and the title the overlay shows near the pointer. A record
/// struct so snapshot lists compare by value in tests.
/// </summary>
public readonly record struct PickableWindow(
    IntPtr Handle,
    int Left,
    int Top,
    int Right,
    int Bottom,
    string Title);

/// <summary>
/// Resolves the hovered <see cref="WindowTarget"/> for a cursor position over a
/// snapshot of eligible top-level windows, or null when the cursor is over empty
/// desktop (no eligible window). Pure and side-effect-free so the picker's
/// hover, empty-desktop, z-order, and disappearance contracts are testable
/// without an interactive desktop.
/// </summary>
public static class WindowPickerTargetResolver
{
    /// <summary>
    /// Returns the topmost eligible window under the cursor as a
    /// <see cref="WindowTarget"/> carrying its HWND, title, and full Virtual
    /// Desktop bounds, or null when the cursor is over empty desktop (no
    /// eligible window contains the point). An empty window enumeration also
    /// yields null. <paramref name="windows"/> must be supplied in top-to-bottom
    /// z-order — the order the overlay enumerates via EnumWindows — so the first
    /// containing window is the topmost the user sees. The signed X/Y origin and
    /// exact pixel dimensions are preserved so cross-monitor and
    /// negative-coordinate window bounds reach the workflow intact.
    /// </summary>
    public static WindowTarget? Resolve(IReadOnlyList<PickableWindow> windows, int cursorX, int cursorY)
    {
        if (windows is null || windows.Count == 0)
        {
            return null;
        }

        foreach (var w in windows)
        {
            if (Contains(w, cursorX, cursorY))
            {
                return new WindowTarget
                {
                    Handle = w.Handle,
                    Title = w.Title,
                    X = w.Left,
                    Y = w.Top,
                    Width = (uint)(w.Right - w.Left),
                    Height = (uint)(w.Bottom - w.Top),
                };
            }
        }

        // Cursor is over empty desktop — no eligible window contains it. The
        // overlay treats a click here as the Full Desktop fallback (including
        // the taskbar); only Escape cancels.
        return null;
    }

    /// <summary>
    /// Win32-convention hit test: inclusive left/top edge, exclusive
    /// right/bottom edge, matching <see cref="Capture.MonitorLayout"/> so a
    /// point on a shared window edge belongs to exactly one window.
    /// </summary>
    private static bool Contains(in PickableWindow w, int x, int y) =>
        x >= w.Left && x < w.Right && y >= w.Top && y < w.Bottom;
}
