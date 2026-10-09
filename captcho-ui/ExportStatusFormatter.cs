// ExportStatusFormatter.cs — Pure-logic status formatter for export results.
//
// Converts ExportResult and ClipboardExportResult into user-facing status
// and timing strings. No WinUI dependencies — testable in headless xUnit.
// Sanitized: never leaks raw pixels, hex dumps, or full filesystem paths
// (except user-selected Save As paths, which the user chose).

using System;
using System.Collections.Generic;
using System.IO;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Formats export results into user-facing status and timing strings.
/// Pure logic — no UI dependencies.
/// </summary>
public static class ExportStatusFormatter
{
    /// <summary>
    /// Formats an ExportResult into a status string for the status bar.
    /// Shows filename on success, sanitized error on failure, and cancellation.
    /// </summary>
    public static string FormatStatus(ExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Success)
        {
            string fileName = Path.GetFileName(result.DestinationPath ?? "unknown.png");
            return $"Saved {fileName} ({result.Width}×{result.Height})";
        }

        if (result.Phase == ExportPhase.Cancelled)
            return "Export cancelled.";

        // Failed — return the already-sanitized message from ExportResult
        return $"Export failed: {result.Message}";
    }

    /// <summary>
    /// Formats a ClipboardExportResult into a status string for the status bar.
    /// </summary>
    public static string FormatStatus(ClipboardExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Success)
        {
            return $"Copied to clipboard ({result.Width}×{result.Height})";
        }

        return $"Clipboard failed: {result.Message}";
    }

    /// <summary>
    /// Formats an ExportResult into a timing string for the diagnostics bar.
    /// Returns empty string if no timing information is available.
    /// </summary>
    public static string FormatTiming(ExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Elapsed <= TimeSpan.Zero)
            return "";

        double ms = result.Elapsed.TotalMilliseconds;
        return ms > 0 ? $"export {ms:F1}ms" : "";
    }

    /// <summary>
    /// Formats a ClipboardExportResult into a timing string.
    /// </summary>
    public static string FormatTiming(ClipboardExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Elapsed <= TimeSpan.Zero)
            return "";

        double ms = result.Elapsed.TotalMilliseconds;
        return ms > 0 ? $"clipboard {ms:F1}ms" : "";
    }

    /// <summary>
    /// Formats a WorkflowExportResult (the Export workflow actions on the
    /// runtime session) into a status string for the status bar. Shows the
    /// produced filename and dimensions on Save/Save As success, clipboard
    /// dimensions on Copy Frame success, and the copied path on Copy Path
    /// success; sanitized errors on failure; cancellation otherwise.
    /// </summary>
    public static string FormatStatus(WorkflowExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            return result.Action switch
            {
                WorkflowExportAction.Save or WorkflowExportAction.SaveAs =>
                    $"Saved {Path.GetFileName(result.FilePath ?? "unknown.png")} ({result.Width}×{result.Height})",
                WorkflowExportAction.CopyFrame =>
                    $"Copied to clipboard ({result.Width}×{result.Height})",
                WorkflowExportAction.CopyPath =>
                    $"Copied path: {result.FilePath}",
                _ => "Export succeeded.",
            };
        }

        if (result.Status == WorkflowExportStatus.NoFrame)
            return FormatNoCapture();

        if (result.Status == WorkflowExportStatus.Cancelled)
        {
            // Save As cancellation means the user dismissed the picker; other
            // action cancellations come from the export itself.
            return result.Action == WorkflowExportAction.SaveAs
                ? FormatPickerCancelled()
                : "Export cancelled.";
        }

        if (result.Status == WorkflowExportStatus.OperationInProgress)
            return result.Error ?? "An operation is already in progress.";

        return result.Action switch
        {
            WorkflowExportAction.CopyFrame => $"Clipboard failed: {result.Error}",
            WorkflowExportAction.CopyPath => $"Copy path failed: {result.Error}",
            _ => $"Export failed: {result.Error}",
        };
    }

    /// <summary>
    /// Formats a WorkflowExportResult into a timing string for the
    /// diagnostics bar. Returns empty string if no timing information is
    /// available.
    /// </summary>
    public static string FormatTiming(WorkflowExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.ElapsedMs <= 0)
            return "";

        return result.Action switch
        {
            WorkflowExportAction.CopyFrame => $"clipboard {result.ElapsedMs:F1}ms",
            _ => $"export {result.ElapsedMs:F1}ms",
        };
    }

    /// <summary>
    /// Returns the status text for a cancelled Save As picker.
    /// </summary>
    public static string FormatPickerCancelled() => "Save cancelled.";

    /// <summary>
    /// Returns the status text for a no-capture export attempt.
    /// </summary>
    public static string FormatNoCapture() => "No capture to export.";
}
