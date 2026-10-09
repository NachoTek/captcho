// CaptureMode.cs — Domain identity for the five Capture Modes from spec #24.
//
// Lives in the capture layer (alongside Configuration and AppSettings) so the
// Settings/Configuration seam and the runtime workflow session share one
// vocabulary that does not depend on WinUI or Win32. Matches the five modes
// accepted by the KDE Spectacle parity spec (issue #24): Full Desktop and
// Active Window skip Target Selection; Selected Window, Selected Monitor, and
// Selection use interactive overlays before Capture.

namespace captcho.Capture;

/// <summary>
/// One of the five Capture Modes the user can Trigger. This is the stable
/// domain identity that the runtime workflow session keys its option
/// applicability, override visibility, and Target Selection routing by — its
/// meaning is defined here, free of WinUI and Win32 dependencies.
/// </summary>
public enum CaptureMode
{
    /// <summary>
    /// Captures the entire Virtual Desktop immediately, without Target Selection.
    /// Corresponds to <see cref="GlobalHotkeyRoute.FullDesktop"/>.
    /// </summary>
    FullDesktop,

    /// <summary>
    /// Captures the foreground window immediately, without Target Selection.
    /// Corresponds to <see cref="GlobalHotkeyRoute.ActiveWindow"/>.
    /// </summary>
    ActiveWindow,

    /// <summary>
    /// The user clicks on a target window to capture. Opens a window picker overlay.
    /// Corresponds to <see cref="GlobalHotkeyRoute.ActiveWindow"/> when triggered
    /// through a Global Hotkey, but is its own interactive Capture Mode in the
    /// workflow (issue #29).
    /// </summary>
    SelectedWindow,

    /// <summary>
    /// The user clicks on a target monitor to capture. Opens a monitor picker overlay
    /// (issue #28).
    /// </summary>
    SelectedMonitor,

    /// <summary>
    /// The user draws a rectangular region of the Virtual Desktop. Opens the Selection
    /// overlay (issue #27). Corresponds to <see cref="GlobalHotkeyRoute.RectangularRegion"/>.
    /// </summary>
    Selection,
}
