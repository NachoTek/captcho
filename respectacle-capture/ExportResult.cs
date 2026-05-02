// ExportResult.cs — Result and diagnostic types for PNG export operations.

using System;
using System.Diagnostics;

namespace Respectacle.Capture;

/// <summary>
/// Describes which phase of export failed or succeeded, for diagnostic display.
/// </summary>
public enum ExportPhase
{
    None,
    Validation,
    Encode,
    Write,
    Cancelled
}

/// <summary>
/// Result of a PNG export attempt. Always returns (never throws) so callers
/// can display sanitized status without try/catch.
/// </summary>
public sealed class ExportResult
{
    /// <summary>Whether the export completed successfully.</summary>
    public bool Success { get; }

    /// <summary>Phase at which failure occurred (or None on success).</summary>
    public ExportPhase Phase { get; }

    /// <summary>Sanitized human-readable message (no raw pixels, no full paths unless user-selected).</summary>
    public string Message { get; }

    /// <summary>Destination file path on success; null on failure.</summary>
    public string? DestinationPath { get; }

    /// <summary>Width of exported image in pixels, when available.</summary>
    public int? Width { get; }

    /// <summary>Height of exported image in pixels, when available.</summary>
    public int? Height { get; }

    /// <summary>Size of the PNG file in bytes, when available.</summary>
    public long? ByteCount { get; }

    /// <summary>Wall-clock duration of the export operation.</summary>
    public TimeSpan Elapsed { get; }

    private ExportResult(bool success, ExportPhase phase, string message,
        string? destinationPath, int? width, int? height, long? byteCount, TimeSpan elapsed)
    {
        Success = success;
        Phase = phase;
        Message = message;
        DestinationPath = destinationPath;
        Width = width;
        Height = height;
        ByteCount = byteCount;
        Elapsed = elapsed;
    }

    /// <summary>
    /// Creates a successful export result.
    /// </summary>
    public static ExportResult Ok(string destinationPath, int width, int height,
        long byteCount, TimeSpan elapsed)
    {
        return new ExportResult(
            success: true,
            phase: ExportPhase.None,
            message: $"Saved {System.IO.Path.GetFileName(destinationPath)}",
            destinationPath: destinationPath,
            width: width,
            height: height,
            byteCount: byteCount,
            elapsed: elapsed);
    }

    /// <summary>
    /// Creates a failed export result with sanitized message.
    /// </summary>
    public static ExportResult Fail(ExportPhase phase, string sanitizedMessage, TimeSpan elapsed)
    {
        return new ExportResult(
            success: false,
            phase: phase,
            message: sanitizedMessage,
            destinationPath: null,
            width: null,
            height: null,
            byteCount: null,
            elapsed: elapsed);
    }

    /// <summary>
    /// Creates a cancellation result.
    /// </summary>
    public static ExportResult Cancelled(TimeSpan elapsed)
    {
        return new ExportResult(
            success: false,
            phase: ExportPhase.Cancelled,
            message: "Export cancelled",
            destinationPath: null,
            width: null,
            height: null,
            byteCount: null,
            elapsed: elapsed);
    }
}
