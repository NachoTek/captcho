// MonitorResolver.cs — Shared monitor resolution logic for CLI and UI consumers.
//
// Provides a testable abstraction for determining which monitor contains a
// given point, using sorted virtual-desktop coordinates. The Win32 P/Invoke
// calls (GetCursorPos, EnumDisplayMonitors) are injected via delegates so
// headless tests can supply fake monitor geometries and cursor positions
// without requiring an interactive desktop session.
//
// Public contract returns 0-based indexes matching SafeCaptureResult.CaptureMonitorByIndex(uint)
// and R009 (monitor indexing requirement).

using System;
using System.Collections.Generic;

namespace Respectacle.Capture;

/// <summary>
/// Describes a monitor rectangle in virtual-desktop coordinates.
/// Left/Top may be negative for secondary monitors positioned left-of or above the primary.
/// </summary>
public readonly record struct MonitorRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Result of resolving the current monitor from a cursor position.
/// Distinguishes between a successful match, a fallback-to-primary, and
/// a total resolution failure (e.g., Win32 API returned false).
/// </summary>
public readonly record struct MonitorResolveResult(
    uint Index,
    bool IsFallback,
    string? FailureReason)
{
    /// <summary>
    /// Creates a successful result with an exact monitor match.
    /// </summary>
    public static MonitorResolveResult Success(uint index) =>
        new(index, IsFallback: false, FailureReason: null);

    /// <summary>
    /// Creates a fallback result (no monitor matched, defaulting to primary).
    /// </summary>
    public static MonitorResolveResult Fallback(string reason) =>
        new(0, IsFallback: true, FailureReason: reason);

    /// <summary>
    /// Creates a failure result where Win32 resolution could not proceed.
    /// Returns index 0 so callers can still attempt capture if desired.
    /// </summary>
    public static MonitorResolveResult Failure(string reason) =>
        new(0, IsFallback: true, FailureReason: reason);
}

/// <summary>
/// Injectable Win32 interop boundary for monitor enumeration.
/// Production code supplies real P/Invoke delegates; tests supply fake data.
/// </summary>
public sealed class MonitorInterop
{
    /// <summary>
    /// Gets the current cursor position. Returns (x, y) or null if the call fails.
    /// </summary>
    public required Func<(int x, int y)?> GetCursorPosition { get; init; }

    /// <summary>
    /// Enumerates all monitor rectangles in virtual-desktop coordinates.
    /// Returns an empty list if enumeration fails.
    /// </summary>
    public required Func<IReadOnlyList<MonitorRect>> EnumerateMonitors { get; init; }

    /// <summary>
    /// Creates a MonitorInterop backed by real Win32 P/Invoke calls.
    /// </summary>
    public static MonitorInterop CreateNative()
    {
        return new MonitorInterop
        {
            GetCursorPosition = NativeGetCursorPosition,
            EnumerateMonitors = NativeEnumerateMonitors,
        };
    }

    #region Native Win32 Implementation

    private static (int x, int y)? NativeGetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
            return null;
        return (pt.x, pt.y);
    }

    private static IReadOnlyList<MonitorRect> NativeEnumerateMonitors()
    {
        var monitors = new List<MonitorRect>();
        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData) =>
        {
            var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.RECT>(lprcMonitor);
            monitors.Add(new MonitorRect(rect.left, rect.top, rect.right, rect.bottom));
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return monitors;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT
        {
            public int left, top, right, bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT
        {
            public int x, y;
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    }

    #endregion
}

/// <summary>
/// Resolves the 0-based monitor index containing a point, using sorted
/// virtual-desktop monitor rectangles. Sorting is left-to-right, then
/// top-to-bottom, matching the established UI behavior.
/// </summary>
public static class MonitorResolver
{
    /// <summary>
    /// Resolves the monitor index under the cursor using the provided interop boundary.
    /// </summary>
    /// <param name="interop">Win32 interop (real or fake for testing).</param>
    /// <returns>A result with 0-based index, fallback flag, and optional failure reason.</returns>
    public static MonitorResolveResult ResolveCurrentMonitor(MonitorInterop interop)
    {
        try
        {
            var cursorPos = interop.GetCursorPosition();
            if (cursorPos is null)
            {
                return MonitorResolveResult.Failure("GetCursorPos returned false");
            }

            var (cx, cy) = cursorPos.Value;
            return ResolveMonitorForPoint(interop.EnumerateMonitors(), cx, cy);
        }
        catch (Exception ex)
        {
            return MonitorResolveResult.Failure($"Monitor resolution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure function: given a list of monitor rectangles and a cursor position,
    /// returns the 0-based index of the monitor containing the point.
    /// Monitors are sorted left-to-right, top-to-bottom for stable ordering.
    /// Falls back to index 0 with IsFallback=true if no monitor contains the point.
    /// </summary>
    public static MonitorResolveResult ResolveMonitorForPoint(
        IReadOnlyList<MonitorRect> monitors, int cursorX, int cursorY)
    {
        if (monitors.Count == 0)
        {
            return MonitorResolveResult.Fallback("No monitors enumerated");
        }

        // Sort monitors left-to-right, then top-to-bottom for stable ordering
        var sorted = new List<MonitorRect>(monitors);
        sorted.Sort((a, b) =>
        {
            int cmp = a.Left.CompareTo(b.Left);
            return cmp != 0 ? cmp : a.Top.CompareTo(b.Top);
        });

        for (int i = 0; i < sorted.Count; i++)
        {
            var r = sorted[i];
            // Inclusive left/top, exclusive right/bottom (matches Win32 convention)
            if (cursorX >= r.Left && cursorX < r.Right &&
                cursorY >= r.Top && cursorY < r.Bottom)
            {
                return MonitorResolveResult.Success((uint)i);
            }
        }

        return MonitorResolveResult.Fallback("Cursor outside all monitor bounds");
    }
}
