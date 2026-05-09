// CurrentMonitorResolver.cs — Determines which monitor the mouse cursor is on,
// returning a 0-based index suitable for the Rust FFI (0 = primary).
//
// Delegates to the shared MonitorResolver in captcho-capture for the actual
// geometry matching logic. This file provides backward compatibility with the
// existing UI public surface (GetCurrentMonitorIndex) while keeping monitor
// resolution consistent across CLI and UI consumers.

using System;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Resolves the 0-based monitor index (0 = primary) for the monitor
/// containing the mouse cursor, using the shared MonitorResolver.
/// </summary>
public static class CurrentMonitorResolver
{
    private static readonly MonitorInterop _nativeInterop = MonitorInterop.CreateNative();

    /// <summary>
    /// Returns the 0-based monitor index where the cursor currently resides.
    /// Falls back to 0 (primary) if resolution fails.
    /// </summary>
    public static uint GetCurrentMonitorIndex()
    {
        var result = MonitorResolver.ResolveCurrentMonitor(_nativeInterop);
        return result.Index;
    }
}
