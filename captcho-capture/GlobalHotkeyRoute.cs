// GlobalHotkeyRoute.cs — Capture route identity for per-route enabled state.
//
// The stable, UI-independent identity that a Global Hotkey triggers. Lives in the
// capture layer (alongside Configuration and AppSettings) so AppSettings can key
// per-hotkey enabled state by it without depending on the UI layer's hotkey map
// (Win32 ids, modifier/VK constants, binding names). The UI layer (GlobalHotkeyRouteMap)
// maps its Win32 hotkey ids to these capture routes; it does not define their meaning.

namespace captcho.Capture;

/// <summary>
/// Capture workflow that a Global Hotkey triggers. Maps one-to-one with the capture
/// methods on CapturePreviewService. This is the stable identity Configuration keys the
/// per-Global-Hotkey enabled state by — its meaning is defined here, in the capture layer,
/// free of WinUI and Win32 dependencies.
/// </summary>
public enum GlobalHotkeyRoute
{
    /// <summary>Captures the monitor the cursor is on.</summary>
    CurrentMonitor,
    /// <summary>Captures the active (foreground) window.</summary>
    ActiveWindow,
    /// <summary>Captures the full virtual desktop.</summary>
    FullDesktop,
    /// <summary>Opens the Selection overlay to draw a region and capture it.</summary>
    RectangularRegion,
}
