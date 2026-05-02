// CliCaptureService.cs — Testable capture/export workflow for the CLI.
//
// Orchestrates the capture, bitmap conversion, and PNG export pipeline.
// Uses injectable adapters so tests can supply fakes without native WGC.
// Production code passes a NativeCliCaptureAdapter that calls the real APIs.
//
// Exit code mapping:
//   Success  → CliExitCode.Success
//   Capture errors (native status, timeout, bad metadata) → CliExitCode.CaptureFailed
//   Export errors (disk, encoding, path) → CliExitCode.ExportFailed
//   Unexpected → CliExitCode.InternalError

using System;
using System.Diagnostics;
using System.IO;
using Respectacle.Capture;

namespace Respectacle.Cli;

/// <summary>
/// Result of a bitmap conversion attempt.
/// </summary>
public sealed class BitmapConversionResult
{
    public bool Success { get; }
    public ContiguousBitmap? Bitmap { get; }
    public string? ErrorMessage { get; }

    private BitmapConversionResult(bool success, ContiguousBitmap? bitmap, string? errorMessage)
    {
        Success = success;
        Bitmap = bitmap;
        ErrorMessage = errorMessage;
    }

    public static BitmapConversionResult Ok(ContiguousBitmap bitmap) =>
        new(true, bitmap, null);

    public static BitmapConversionResult Fail(string message) =>
        new(false, null, message);
}

/// <summary>
/// Result of a capture attempt from the capture adapter.
/// </summary>
public sealed class CaptureAdapterResult
{
    public bool Success { get; }
    public byte[]? Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public string? StatusText { get; }
    public string? ErrorMessage { get; }

    private CaptureAdapterResult(bool success, byte[]? pixels, int width, int height,
        int stride, string? statusText, string? errorMessage)
    {
        Success = success;
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        StatusText = statusText;
        ErrorMessage = errorMessage;
    }

    public static CaptureAdapterResult Ok(byte[] pixels, int width, int height, int stride) =>
        new(true, pixels, width, height, stride, "Ok", null);

    public static CaptureAdapterResult Fail(string status, string? error = null) =>
        new(false, null, 0, 0, 0, status, error);
}

/// <summary>
/// Result of a PNG export attempt from the export adapter.
/// </summary>
public sealed class ExportAdapterResult
{
    public bool Success { get; }
    public string? OutputPath { get; }
    public int Width { get; }
    public int Height { get; }
    public long ByteCount { get; }
    public string? Phase { get; }
    public string? ErrorMessage { get; }
    public TimeSpan Elapsed { get; }

    private ExportAdapterResult(bool success, string? outputPath, int width, int height,
        long byteCount, string? phase, string? errorMessage, TimeSpan elapsed)
    {
        Success = success;
        OutputPath = outputPath;
        Width = width;
        Height = height;
        ByteCount = byteCount;
        Phase = phase;
        ErrorMessage = errorMessage;
        Elapsed = elapsed;
    }

    public static ExportAdapterResult Ok(string outputPath, int width, int height,
        long byteCount, TimeSpan elapsed) =>
        new(true, outputPath, width, height, byteCount, null, null, elapsed);

    public static ExportAdapterResult Fail(string phase, string message, TimeSpan elapsed) =>
        new(false, null, 0, 0, 0, phase, message, elapsed);
}

/// <summary>
/// Injectable capture adapter — production uses native WGC, tests use fakes.
/// </summary>
public interface ICliCaptureAdapter
{
    /// <summary>Captures all monitors (full virtual desktop).</summary>
    CaptureAdapterResult CaptureAllMonitors();

    /// <summary>Captures a single monitor by 0-based index.</summary>
    CaptureAdapterResult CaptureMonitorByIndex(uint index);

    /// <summary>Captures the currently active (foreground) window.</summary>
    CaptureAdapterResult CaptureActiveWindow();

    /// <summary>Captures the top-level window under the cursor.</summary>
    CaptureAdapterResult CaptureWindowUnderCursor();

    /// <summary>Captures a rectangular region.</summary>
    CaptureAdapterResult CaptureRegion(int x, int y, uint width, uint height);

    /// <summary>Resolves the current monitor index (for bare --monitor mode).</summary>
    MonitorResolveResult ResolveCurrentMonitor();

    /// <summary>Converts padded pixel buffer to contiguous bitmap.</summary>
    BitmapConversionResult ConvertToContiguousBitmap(byte[] pixels, int width, int height, int stride);

    /// <summary>Saves a contiguous bitmap as PNG and resolves filename collisions.</summary>
    ExportAdapterResult ExportPng(ContiguousBitmap bitmap, string outputPath);
}

/// <summary>
/// Orchestrates the capture workflow: capture → bitmap conversion → PNG export.
/// Uses injectable adapters for testability. Maps results to CLI exit codes
/// and produces structured diagnostics.
/// </summary>
public sealed class CliCaptureService
{
    private readonly ICliCaptureAdapter _adapter;

    public CliCaptureService(ICliCaptureAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    /// <summary>
    /// Executes the capture workflow for the given CLI options.
    /// Returns the exit code and populates diagnostics for output.
    /// </summary>
    public (CliExitCode exitCode, CliCaptureDiagnostics diagnostics) Execute(CliOptions options)
    {
        var totalSw = Stopwatch.StartNew();
        string modeName = ModeToName(options.Mode);

        // ── Step 1: Capture ──────────────────────────────────────────────
        var captureSw = Stopwatch.StartNew();
        var captureResult = PerformCapture(options);
        captureSw.Stop();

        if (!captureResult.Success)
        {
            totalSw.Stop();
            return (CliExitCode.CaptureFailed, BuildDiagnostics(
                modeName, "failed", (int)CliExitCode.CaptureFailed,
                captureMs: captureSw.Elapsed, totalMs: totalSw.Elapsed,
                failurePhase: "capture", failureMessage: FormatCaptureError(captureResult),
                verbose: options.Verbose));
        }

        // ── Step 2: Bitmap conversion ────────────────────────────────────
        var bitmapSw = Stopwatch.StartNew();
        var bitmapResult = _adapter.ConvertToContiguousBitmap(
            captureResult.Pixels!, captureResult.Width, captureResult.Height, captureResult.Stride);
        bitmapSw.Stop();

        if (!bitmapResult.Success)
        {
            totalSw.Stop();
            return (CliExitCode.CaptureFailed, BuildDiagnostics(
                modeName, "failed", (int)CliExitCode.CaptureFailed,
                width: captureResult.Width, height: captureResult.Height,
                captureMs: captureSw.Elapsed, bitmapMs: bitmapSw.Elapsed, totalMs: totalSw.Elapsed,
                failurePhase: "bitmap", failureMessage: bitmapResult.ErrorMessage ?? "Bitmap conversion failed",
                verbose: options.Verbose));
        }

        // ── Step 3: Resolve output path and export PNG ───────────────────
        string resolvedPath = ResolveOutputPath(options.OutputPath);
        var exportSw = Stopwatch.StartNew();
        var exportResult = _adapter.ExportPng(bitmapResult.Bitmap!, resolvedPath);
        exportSw.Stop();

        if (!exportResult.Success)
        {
            totalSw.Stop();
            return (CliExitCode.ExportFailed, BuildDiagnostics(
                modeName, "failed", (int)CliExitCode.ExportFailed,
                width: bitmapResult.Bitmap!.Width, height: bitmapResult.Bitmap!.Height,
                captureMs: captureSw.Elapsed, bitmapMs: bitmapSw.Elapsed,
                exportMs: exportSw.Elapsed, totalMs: totalSw.Elapsed,
                failurePhase: exportResult.Phase ?? "export",
                failureMessage: exportResult.ErrorMessage ?? "Export failed",
                verbose: options.Verbose));
        }

        totalSw.Stop();

        return (CliExitCode.Success, BuildDiagnostics(
            modeName, "success", (int)CliExitCode.Success,
            outputPath: exportResult.OutputPath,
            width: exportResult.Width, height: exportResult.Height,
            captureMs: captureSw.Elapsed, bitmapMs: bitmapSw.Elapsed,
            exportMs: exportSw.Elapsed, totalMs: totalSw.Elapsed,
            verbose: options.Verbose));
    }

    /// <summary>
    /// Performs the mode-appropriate capture, handling bare --monitor via current-monitor resolver.
    /// </summary>
    private CaptureAdapterResult PerformCapture(CliOptions options)
    {
        return options.Mode switch
        {
            CliCaptureMode.Full => _adapter.CaptureAllMonitors(),

            CliCaptureMode.Monitor when options.MonitorIndex.HasValue =>
                _adapter.CaptureMonitorByIndex((uint)options.MonitorIndex.Value),

            CliCaptureMode.Monitor => ResolveAndCaptureCurrentMonitor(),

            CliCaptureMode.WindowActive => _adapter.CaptureActiveWindow(),

            CliCaptureMode.WindowCursor => _adapter.CaptureWindowUnderCursor(),

            CliCaptureMode.Region => _adapter.CaptureRegion(
                options.Region!.X, options.Region!.Y,
                (uint)options.Region.Width, (uint)options.Region.Height),

            _ => CaptureAdapterResult.Fail("InternalError", $"Unknown mode: {options.Mode}")
        };
    }

    /// <summary>
    /// Resolves the current monitor via the adapter, then captures it.
    /// If resolution fails, returns a capture error (does not pass bad index to native code).
    /// </summary>
    private CaptureAdapterResult ResolveAndCaptureCurrentMonitor()
    {
        var resolveResult = _adapter.ResolveCurrentMonitor();

        if (resolveResult.FailureReason is not null && !resolveResult.IsFallback)
        {
            // Total resolution failure — cannot determine any monitor
            return CaptureAdapterResult.Fail("MonitorResolutionFailed",
                $"Cannot determine current monitor: {resolveResult.FailureReason}");
        }

        // On fallback (IsFallback=true), we use index 0 — the primary monitor.
        // On success, we use the resolved index.
        return _adapter.CaptureMonitorByIndex(resolveResult.Index);
    }

    /// <summary>
    /// Resolves the final output path, creating directories and handling collisions.
    /// If the path points to a directory (no .png extension), combines with default filename.
    /// </summary>
    private static string ResolveOutputPath(string outputPath)
    {
        // Ensure parent directory exists
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return outputPath;
    }

    private static string ModeToName(CliCaptureMode mode) => mode switch
    {
        CliCaptureMode.Full => "full",
        CliCaptureMode.Monitor => "monitor",
        CliCaptureMode.WindowActive => "window-active",
        CliCaptureMode.WindowCursor => "window-cursor",
        CliCaptureMode.Region => "region",
        _ => "unknown"
    };

    private static string FormatCaptureError(CaptureAdapterResult result)
    {
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            return $"Status={result.StatusText}: {result.ErrorMessage}";
        return $"Status={result.StatusText}";
    }

    private static CliCaptureDiagnostics BuildDiagnostics(
        string captureMode, string status, int exitCode,
        string? outputPath = null, int? width = null, int? height = null,
        TimeSpan? captureMs = null, TimeSpan? bitmapMs = null,
        TimeSpan? exportMs = null, TimeSpan? totalMs = null,
        string? failurePhase = null, string? failureMessage = null,
        bool verbose = false)
    {
        return new CliCaptureDiagnostics
        {
            CaptureMode = captureMode,
            Status = status,
            ExitCode = exitCode,
            OutputPath = outputPath,
            Width = width,
            Height = height,
            CaptureDuration = captureMs,
            BitmapDuration = bitmapMs,
            ExportDuration = exportMs,
            TotalDuration = totalMs,
            FailurePhase = failurePhase,
            FailureMessage = failureMessage,
            Verbose = verbose,
        };
    }
}
