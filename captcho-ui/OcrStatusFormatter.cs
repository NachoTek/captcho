// OcrStatusFormatter.cs — Pure-logic status formatter for OCR results.
//
// Converts OcrResult outcomes into user-facing status strings. No WinUI
// dependencies — testable in headless xUnit. Mirrors ExportStatusFormatter:
// sanitized messages, distinct Recognized-text/no-text/failure wording.

using System;

namespace captcho.UI;

/// <summary>
/// Formats OCR results into user-facing status strings. Pure logic — no UI
/// dependencies.
/// </summary>
public static class OcrStatusFormatter
{
    /// <summary>
    /// Formats a Recognized-text outcome: a compact summary with language and
    /// line count (the full text is presented separately in the dialog).
    /// </summary>
    public static string FormatRecognized(OcrResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        int lines = CountLines(result.Text);
        string language = string.IsNullOrEmpty(result.LanguageTag) ? "" : $" ({result.LanguageTag})";
        return $"Recognized {lines} line{(lines == 1 ? "" : "s")}{language}.";
    }

    /// <summary>
    /// Formats a no-text outcome. Distinct from every failure: recognition
    /// ran, the Frame just contains no readable text.
    /// </summary>
    public static string FormatNoText(OcrResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string language = string.IsNullOrEmpty(result.LanguageTag) ? "" : $" ({result.LanguageTag})";
        return $"No text found{language}.";
    }

    /// <summary>
    /// Returns the status text for a no-Frame recognition attempt.
    /// </summary>
    public static string FormatNoFrame() => "No captured Frame to recognize. Take a capture first.";

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int lines = 1;
        foreach (char c in text)
        {
            if (c == '\n')
                lines++;
        }

        return lines;
    }
}
