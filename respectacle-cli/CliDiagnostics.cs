// CliDiagnostics.cs — Structured key=value diagnostic formatting for CLI output.
//
// Emits deterministic key=value pairs to stdout/stderr for script consumption.
// Fields: CaptureMode, Status, OutputPath, ExitCode, dimensions, timings, and
// failure phase/message where applicable.
// Redaction: no raw pixels, no native pointer values, no secret/env values.

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Respectacle.Cli;

/// <summary>
/// Structured diagnostic data produced by the CLI capture workflow.
/// Used both for normal output and verbose diagnostics.
/// </summary>
public sealed class CliCaptureDiagnostics
{
    /// <summary>Capture mode name (e.g., "full", "monitor", "window-active").</summary>
    public required string CaptureMode { get; init; }

    /// <summary>Status: "success" or "failed".</summary>
    public required string Status { get; init; }

    /// <summary>Exit code returned to the shell.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Output file path on success; null on failure.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Image width in pixels, when available.</summary>
    public int? Width { get; init; }

    /// <summary>Image height in pixels, when available.</summary>
    public int? Height { get; init; }

    /// <summary>Capture phase wall-clock time.</summary>
    public TimeSpan? CaptureDuration { get; init; }

    /// <summary>Bitmap conversion wall-clock time.</summary>
    public TimeSpan? BitmapDuration { get; init; }

    /// <summary>Export phase wall-clock time.</summary>
    public TimeSpan? ExportDuration { get; init; }

    /// <summary>Total wall-clock time.</summary>
    public TimeSpan? TotalDuration { get; init; }

    /// <summary>Failure phase (e.g., "capture", "bitmap", "export").</summary>
    public string? FailurePhase { get; init; }

    /// <summary>Sanitized failure message.</summary>
    public string? FailureMessage { get; init; }

    /// <summary>Whether verbose output was requested.</summary>
    public bool Verbose { get; init; }
}

/// <summary>
/// Formats CliCaptureDiagnostics as key=value output suitable for stdout.
/// Normal mode: minimal success/failure info. Verbose: all fields.
/// </summary>
public static class CliDiagnostics
{
    /// <summary>
    /// Formats diagnostics as key=value lines for stdout output.
    /// Normal mode outputs: CaptureMode, Status, OutputPath (on success), ExitCode.
    /// Verbose mode adds: Width, Height, timings, failure details.
    /// </summary>
    public static string Format(CliCaptureDiagnostics d)
    {
        var sb = new StringBuilder();
        sb.Append($"CaptureMode={d.CaptureMode}");
        sb.Append($" Status={d.Status}");

        if (d.OutputPath is not null)
            sb.Append($" OutputPath={d.OutputPath}");

        if (d.Width.HasValue && d.Height.HasValue && (d.Verbose || d.Status == "success"))
            sb.Append($" Dimensions={d.Width.Value}x{d.Height.Value}");

        if (d.Verbose)
        {
            if (d.CaptureDuration.HasValue)
                sb.Append($" CaptureMs={d.CaptureDuration.Value.TotalMilliseconds:F1}");
            if (d.BitmapDuration.HasValue)
                sb.Append($" BitmapMs={d.BitmapDuration.Value.TotalMilliseconds:F1}");
            if (d.ExportDuration.HasValue)
                sb.Append($" ExportMs={d.ExportDuration.Value.TotalMilliseconds:F1}");
            if (d.TotalDuration.HasValue)
                sb.Append($" TotalMs={d.TotalDuration.Value.TotalMilliseconds:F1}");
        }

        sb.Append($" ExitCode={d.ExitCode}");

        return sb.ToString();
    }

    /// <summary>
    /// Formats a failure diagnostic line for stderr output.
    /// Includes the failure phase and sanitized message.
    /// </summary>
    public static string FormatError(CliCaptureDiagnostics d)
    {
        if (d.FailurePhase is null && d.FailureMessage is null)
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("Error:");
        if (d.FailurePhase is not null)
            sb.Append($" Phase={d.FailurePhase}");

        if (d.FailureMessage is not null)
            sb.Append($" Message={Sanitize(d.FailureMessage)}");

        return sb.ToString();
    }

    /// <summary>
    /// Sanitizes error messages — removes filesystem paths, stack traces,
    /// native pointer values, and environment variable references.
    /// </summary>
    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
            return "Unknown error";

        // Remove full filesystem paths
        var sanitized = Regex.Replace(message, @"[A-Z]:\\[^\s"")\]]*", "<path>");

        // Remove native pointer values (0x followed by hex digits)
        sanitized = Regex.Replace(sanitized, @"0x[0-9A-Fa-f]+", "<ptr>");

        // Remove environment variable patterns
        sanitized = Regex.Replace(sanitized, @"%[A-Za-z_][A-Za-z0-9_]*%", "<env>");

        // Remove stack trace lines
        sanitized = Regex.Replace(sanitized, @"at\s+System\..*", "");
        sanitized = Regex.Replace(sanitized, @"at\s+Respectacle\..*", "");

        // Collapse whitespace
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown error" : sanitized;
    }
}
