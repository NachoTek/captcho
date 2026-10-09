// QrStatusFormatter.cs — Pure-logic status formatter for QR scan results.
//
// Converts QrScanResult outcomes into user-facing status strings. No WinUI
// dependencies — testable in headless xUnit. Mirrors OcrStatusFormatter:
// distinct found/no-code/failure wording, no jargon.

using System;

namespace captcho.UI;

/// <summary>
/// Formats QR scan results into user-facing status strings. Pure logic — no
/// UI dependencies.
/// </summary>
public static class QrStatusFormatter
{
    /// <summary>
    /// Formats a Found outcome: a compact count summary (the full values are
    /// presented separately in the dialog).
    /// </summary>
    public static string FormatFound(QrScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        int count = result.Values.Count;
        return count == 1
            ? "Found 1 QR code."
            : $"Found {count} QR codes.";
    }

    /// <summary>
    /// Formats a no-code outcome. Distinct from every failure: the scan ran,
    /// the Frame just contains no QR code.
    /// </summary>
    public static string FormatNotFound() => "No QR codes found.";

    /// <summary>
    /// Returns the status text for a no-Frame scan attempt.
    /// </summary>
    public static string FormatNoFrame() => "No captured Frame to scan. Take a capture first.";
}
