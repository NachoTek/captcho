// CaptureDiagnostics.cs — Structured timing/invariant diagnostics for capture operations.
//
// Produces stable key/value output lines for scripts and automation.
// Never logs raw pixel data — only dimensions, timings, statuses, and sanitized errors.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Respectacle.Capture;

/// <summary>
/// A structured record of a single capture diagnostic measurement.
/// Can be serialized to stable key=value lines for script parsing.
/// </summary>
public sealed class CaptureDiagnosticRecord
{
    /// <summary>Capture mode identifier (e.g., "primary", "full_desktop", "monitor[0]").</summary>
    public string CaptureMode { get; }
    /// <summary>Status string (e.g., "Ok", "CaptureUnavailable", "InteropError").</summary>
    public string Status { get; }
    /// <summary>Frame width in pixels. Zero on error.</summary>
    public int Width { get; }
    /// <summary>Frame height in pixels. Zero on error.</summary>
    public int Height { get; }
    /// <summary>Row stride in bytes. Zero on error.</summary>
    public int Stride { get; }
    /// <summary>Total buffer size in bytes. Zero on error.</summary>
    public long DataLen { get; }
    /// <summary>Total round-trip time in milliseconds.</summary>
    public double RoundTripMs { get; }
    /// <summary>Sanitized error message, or null on success.</summary>
    public string? Error { get; }

    public CaptureDiagnosticRecord(
        string captureMode,
        string status,
        int width,
        int height,
        int stride,
        long dataLen,
        double roundTripMs,
        string? error)
    {
        CaptureMode = captureMode;
        Status = status;
        Width = width;
        Height = height;
        Stride = stride;
        DataLen = dataLen;
        RoundTripMs = Math.Round(roundTripMs, 1);
        Error = error;
    }

    /// <summary>
    /// Serializes the record to stable key=value lines for script parsing.
    /// The "Error" line is omitted when null.
    /// </summary>
    public List<string> ToKeyValueLines()
    {
        var lines = new List<string>
        {
            $"CaptureMode={CaptureMode}",
            $"Status={Status}",
            $"Width={Width}",
            $"Height={Height}",
            $"Stride={Stride}",
            $"DataLen={DataLen}",
            $"RoundTripMs={RoundTripMs}",
        };

        if (Error != null)
        {
            lines.Add($"Error={Error}");
        }

        return lines;
    }
}

/// <summary>
/// Helpers for measuring capture timing and building diagnostic records.
/// </summary>
public static class CaptureDiagnostics
{
    /// <summary>
    /// Measures a capture operation and produces a diagnostic record.
    /// The capture function receives a label identifying the capture mode.
    /// </summary>
    /// <param name="captureMode">Label for the capture mode (e.g., "full_desktop").</param>
    /// <param name="captureFunc">Function that performs the capture and returns a SafeCaptureResult.</param>
    /// <returns>A diagnostic record with timing and result metadata.</returns>
    public static CaptureDiagnosticRecord Measure(string captureMode, Func<SafeCaptureResult> captureFunc)
    {
        var sw = Stopwatch.StartNew();
        SafeCaptureResult result;
        string status;
        int width = 0, height = 0, stride = 0;
        long dataLen = 0;
        string? error = null;

        try
        {
            result = captureFunc();
            sw.Stop();

            if (result.IsSuccess)
            {
                status = CaptureStatus.Ok.ToString();
                width = (int)result.Width;
                height = (int)result.Height;
                stride = (int)result.Stride;
                dataLen = result.DataLen;
            }
            else
            {
                status = result.Status.ToString();
                error = result.ErrorMessage;
            }

            result.Dispose();
        }
        catch (DllNotFoundException ex)
        {
            sw.Stop();
            status = "InteropError";
            error = $"DLL not found: {ex.Message}";
        }
        catch (EntryPointNotFoundException ex)
        {
            sw.Stop();
            status = "InteropError";
            error = $"Export not found: {ex.Message}";
        }

        return new CaptureDiagnosticRecord(
            captureMode: captureMode,
            status: status,
            width: width,
            height: height,
            stride: stride,
            dataLen: dataLen,
            roundTripMs: sw.Elapsed.TotalMilliseconds,
            error: error
        );
    }

    /// <summary>
    /// Measures a warm capture cycle: first capture (cold), then repeated captures (warm),
    /// and returns both diagnostic records.
    /// </summary>
    /// <param name="captureMode">Label for the capture mode.</param>
    /// <param name="warmCount">Number of warm captures to perform.</param>
    /// <param name="captureFunc">Factory function that creates a fresh capture each time.</param>
    /// <returns>A tuple of (coldRecord, warmRecord) diagnostics.</returns>
    public static (CaptureDiagnosticRecord Cold, CaptureDiagnosticRecord Warm) MeasureWarm(
        string captureMode,
        int warmCount,
        Func<SafeCaptureResult> captureFunc)
    {
        // Cold capture
        var cold = Measure($"{captureMode}_cold", captureFunc);

        // Warm captures — measure total time for all warm captures
        var warmSw = Stopwatch.StartNew();
        for (int i = 0; i < warmCount; i++)
        {
            using var result = captureFunc();
            // Perform capture to warm the path
        }
        warmSw.Stop();

        var warm = new CaptureDiagnosticRecord(
            captureMode: $"{captureMode}_warm",
            status: cold.Status,
            width: cold.Width,
            height: cold.Height,
            stride: cold.Stride,
            dataLen: cold.DataLen,
            roundTripMs: warmSw.Elapsed.TotalMilliseconds / warmCount,
            error: cold.Error
        );

        return (cold, warm);
    }
}
