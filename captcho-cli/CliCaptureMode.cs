// CliCaptureMode.cs — Capture mode enum for CLI arguments.

namespace captcho.Cli;

/// <summary>
/// The capture mode selected by the user via CLI flags.
/// Exactly one mode must be specified per invocation.
/// </summary>
public enum CliCaptureMode
{
    /// <summary>Capture all monitors (full virtual desktop).</summary>
    Full,

    /// <summary>Capture a specific monitor by index.</summary>
    Monitor,

    /// <summary>Capture the currently active (foreground) window.</summary>
    WindowActive,

    /// <summary>Capture the top-level window under the cursor.</summary>
    WindowCursor,

    /// <summary>Capture a rectangular region of the virtual desktop.</summary>
    Region,
}
