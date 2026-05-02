// RegionSelectionStatusFormatter.cs — Formats region selection results into user-readable status.
//
// Pure static formatting with no UI dependencies so xUnit can exercise it headless.
// Follows MEM037 convention: numeric coordinates/dimensions only, no raw pixel data,
// no raw exception dumps, no screen content.

using System;
using Windows.Foundation;

namespace Respectacle.UI;

/// <summary>
/// Formats region selection outcomes into sanitized, user-readable status strings
/// for display in the MainWindow status bar.
/// </summary>
public static class RegionSelectionStatusFormatter
{
    /// <summary>
    /// Formats a confirmed region selection into a compact status string
    /// showing dimensions and position in virtual-desktop coordinates.
    /// Handles negative coordinates (multi-monitor setups) gracefully.
    /// </summary>
    /// <param name="region">The confirmed selection rectangle in virtual-desktop coordinates.</param>
    /// <returns>Sanitized status string like "Region selected: 800×600 at (1920, 0)".</returns>
    public static string FormatConfirmed(Rect region)
    {
        return $"Region selected: {(int)region.Width}×{(int)region.Height} at ({(int)region.X}, {(int)region.Y})";
    }

    /// <summary>
    /// Formats a cancellation result — user pressed Escape or closed the overlay.
    /// </summary>
    public static string FormatCancelled()
    {
        return "Region selection cancelled.";
    }

    /// <summary>
    /// Formats an invalid/null region result — overlay returned null but not from cancellation.
    /// This covers cases where the user clicked without dragging or the region was too small.
    /// </summary>
    public static string FormatInvalid()
    {
        return "No valid region selected.";
    }

    /// <summary>
    /// Formats an exception from the overlay into a sanitized user-readable error.
    /// Strips raw exception details per MEM037 — no stack traces, no type names,
    /// no file paths in the status bar.
    /// </summary>
    /// <param name="ex">The exception that occurred during region selection.</param>
    /// <returns>Sanitized error status like "Region selector error: could not show overlay".</returns>
    public static string FormatError(Exception ex)
    {
        if (ex == null)
            return "Region selector error: unknown failure.";

        // Sanitize: use the message but truncate long messages and strip stack-trace-like content
        string message = ex.Message ?? "unknown failure";

        // Truncate at first newline (stack traces start after message)
        int newlineIndex = message.IndexOf('\n');
        if (newlineIndex >= 0)
            message = message.Substring(0, newlineIndex).TrimEnd();

        // Truncate long messages
        const int maxLen = 120;
        if (message.Length > maxLen)
            message = message.Substring(0, maxLen - 3) + "...";

        return $"Region selector error: {message}";
    }

    /// <summary>
    /// Formats a capture-service error into a sanitized user-readable status.
    /// Distinct from overlay errors — used when region selection succeeded
    /// but the native capture pipeline failed.
    /// </summary>
    /// <param name="mode">The capture mode label (e.g., "Rectangular Region (X=100, Y=200, 800×600)").</param>
    /// <param name="error">The error message from CapturePreviewResult.</param>
    /// <returns>Sanitized error status like "Rectangular Region (X=100, Y=200, 800×600) — capture failed: ...".</returns>
    public static string FormatCaptureError(string mode, string? error)
    {
        if (string.IsNullOrEmpty(error))
            return $"{mode} — capture failed.";

        // Truncate at first newline (stack traces start after message)
        string message = error;
        int newlineIndex = message.IndexOf('\n');
        if (newlineIndex >= 0)
            message = message.Substring(0, newlineIndex).TrimEnd();

        const int maxLen = 200;
        if (message.Length > maxLen)
            message = message.Substring(0, maxLen - 3) + "...";

        return $"{mode} — {message}";
    }

    /// <summary>
    /// Formats the overall result from ShowAndWaitAsync into a status string.
    /// Maps null → cancelled, valid rect → confirmed.
    /// </summary>
    /// <param name="result">The result from RegionOverlayWindow.ShowAndWaitAsync().</param>
    /// <returns>Appropriate status string for the main window.</returns>
    public static string FormatResult(Rect? result)
    {
        if (result.HasValue)
        {
            return FormatConfirmed(result.Value);
        }

        return FormatCancelled();
    }
}
