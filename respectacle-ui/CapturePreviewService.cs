// CapturePreviewService.cs — Orchestrates capture→convert→display pipeline.
//
// Runs the native capture off the UI thread, converts to a WriteableBitmap,
// and returns structured results with timing for all phases.
// Surfaces errors as user-readable strings rather than exceptions.
// Caches the last successful ContiguousBitmap for export workflows.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Respectacle.Capture;

namespace Respectacle.UI;

/// <summary>
/// Result of a capture+display operation including timing and any error.
/// </summary>
public sealed class CapturePreviewResult
{
    /// <summary>Mode label (e.g., "Full Desktop", "Current Monitor").</summary>
    public string Mode { get; init; } = "";
    /// <summary>Image dimensions string (e.g., "1920×1080") or empty on error.</summary>
    public string Dimensions { get; init; } = "";
    /// <summary>Capture phase duration in milliseconds.</summary>
    public double CaptureMs { get; init; }
    /// <summary>Display conversion phase duration in milliseconds.</summary>
    public double DisplayMs { get; init; }
    /// <summary>Total wall-clock duration in milliseconds.</summary>
    public double TotalMs { get; init; }
    /// <summary>User-readable error message, or null/empty on success.</summary>
    public string? Error { get; init; }
    /// <summary>The bitmap for display, or null on error.</summary>
    public WriteableBitmap? ImageSource { get; init; }

    public bool IsSuccess => string.IsNullOrEmpty(Error);
}

/// <summary>
/// Orchestrates the capture→convert→display pipeline asynchronously.
/// Does not touch UI elements directly — returns a result for the caller to apply.
/// Caches the last successful ContiguousBitmap for save/copy export workflows.
/// </summary>
public sealed class CapturePreviewService
{
    private volatile ContiguousBitmap? _lastCapturedBitmap;

    /// <summary>
    /// The most recent successfully captured bitmap, or null if no capture has succeeded.
    /// Thread-safe read via volatile; written only under lock in CaptureCoreAsync.
    /// </summary>
    public ContiguousBitmap? LastCapturedBitmap => _lastCapturedBitmap;

    /// <summary>
    /// Saves the last captured bitmap to a specific file path as PNG.
    /// Returns a structured ExportResult — never throws for expected failures.
    /// </summary>
    /// <param name="destinationPath">Full file path for the output PNG.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>ExportResult with success/failure, diagnostics, and timing.</returns>
    public ExportResult SaveLastCaptureToFileAsync(string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var bitmap = _lastCapturedBitmap;
        if (bitmap == null)
        {
            return ExportResult.Fail(ExportPhase.Validation,
                "No capture to export",
                TimeSpan.Zero);
        }

        return PngExportService.SaveAsPng(bitmap, destinationPath, cancellationToken);
    }

    /// <summary>
    /// Saves the last captured bitmap using the provided settings for directory and template.
    /// Resolves filename collisions automatically.
    /// Returns a structured ExportResult — never throws for expected failures.
    /// </summary>
    /// <param name="settings">Application settings providing save location and filename template.</param>
    /// <param name="title">Optional title for the filename template.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>ExportResult with success/failure, diagnostics, and timing.</returns>
    public ExportResult SaveLastCaptureWithSettingsAsync(
        AppSettings settings,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        var bitmap = _lastCapturedBitmap;
        if (bitmap == null)
        {
            return ExportResult.Fail(ExportPhase.Validation,
                "No capture to export",
                TimeSpan.Zero);
        }

        string path = ExportFilenameTemplate.GetExportPath(settings, DateTime.Now, title);
        path = ExportFilenameTemplate.ResolveCollision(path);

        return PngExportService.SaveAsPng(bitmap, path, cancellationToken);
    }

    /// <summary>
    /// Saves the last captured bitmap to the default location using the standard template.
    /// Resolves filename collisions automatically.
    /// Returns a structured ExportResult — never throws for expected failures.
    /// </summary>
    /// <param name="title">Optional title for the filename template.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>ExportResult with success/failure, diagnostics, and timing.</returns>
    public ExportResult SaveLastCaptureToDefaultLocationAsync(
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        return SaveLastCaptureWithSettingsAsync(AppSettings.WithDefaults(), title, cancellationToken);
    }

    /// <summary>
    /// Creates a ClipboardExportService wired to this instance's last capture cache.
    /// </summary>
    /// <param name="clipboardAdapter">Platform clipboard adapter.</param>
    /// <returns>A ClipboardExportService that reads from this service's cache.</returns>
    public ClipboardExportService CreateClipboardExporter(IClipboardAdapter clipboardAdapter)
    {
        ArgumentNullException.ThrowIfNull(clipboardAdapter);
        return new ClipboardExportService(clipboardAdapter, () => _lastCapturedBitmap);
    }

    /// <summary>
    /// Clears the cached last capture. Useful for testing or explicit reset.
    /// </summary>
    public void ClearLastCapture()
    {
        _lastCapturedBitmap = null;
    }

    /// <summary>
    /// Captures the full virtual desktop (all monitors stitched) and converts to displayable form.
    /// </summary>
    public Task<CapturePreviewResult> CaptureFullDesktopAsync()
    {
        return Task.Run(() => CaptureCoreAsync("Full Desktop", SafeCaptureResult.CaptureAllMonitors));
    }

    /// <summary>
    /// Captures the monitor the cursor is on and converts to displayable form.
    /// Falls back to primary monitor if cursor resolution fails.
    /// </summary>
    public Task<CapturePreviewResult> CaptureCurrentMonitorAsync()
    {
        return Task.Run(async () =>
        {
            uint monitorIndex = CurrentMonitorResolver.GetCurrentMonitorIndex();
            return await CaptureCoreAsync(
                $"Current Monitor [{monitorIndex}]",
                () => SafeCaptureResult.CaptureMonitorByIndex(monitorIndex));
        });
    }

    /// <summary>
    /// Captures the currently active (foreground) window and converts to displayable form.
    /// Uses WindowResolver for a descriptive mode label with title and handle.
    /// </summary>
    public Task<CapturePreviewResult> CaptureActiveWindowAsync()
    {
        return Task.Run(() =>
        {
            string mode = WindowResolver.BuildActiveWindowLabel();
            return CaptureCoreAsync(mode, SafeCaptureResult.CaptureActiveWindow);
        });
    }

    /// <summary>
    /// Captures the top-level window under the mouse cursor and converts to displayable form.
    /// Uses WindowResolver for a descriptive mode label with title and handle.
    /// </summary>
    public Task<CapturePreviewResult> CaptureWindowUnderCursorAsync()
    {
        return Task.Run(() =>
        {
            string mode = WindowResolver.BuildWindowUnderCursorLabel();
            return CaptureCoreAsync(mode, SafeCaptureResult.CaptureWindowUnderCursor);
        });
    }

    /// <summary>
    /// Captures a rectangular region of the virtual desktop and converts to displayable form.
    /// Rounds fractional coordinates intentionally; rejects zero-dimension regions.
    /// The mode label includes signed coordinates and dimensions for diagnostics.
    /// </summary>
    /// <param name="region">The region rectangle in virtual-desktop coordinates (WinUI Rect).</param>
    /// <returns>A capture result with "Rectangular Region (...)" mode label, timings, and error info.</returns>
    public Task<CapturePreviewResult> CaptureRegionAsync(Windows.Foundation.Rect region)
    {
        // Round/cast coordinates intentionally: X/Y are signed (virtual-desktop can have negative origin),
        // Width/Height are unsigned (must be positive). Clamp near-zero dimensions to zero.
        int x = (int)Math.Round(region.X);
        int y = (int)Math.Round(region.Y);
        uint width = (uint)Math.Max(0, (int)Math.Round(region.Width));
        uint height = (uint)Math.Max(0, (int)Math.Round(region.Height));

        string mode = $"Rectangular Region (X={x}, Y={y}, {width}×{height})";

        return Task.Run(async () =>
        {
            if (width == 0 || height == 0)
            {
                return new CapturePreviewResult
                {
                    Mode = mode,
                    Error = $"Region has zero dimensions after rounding: {width}×{height}.",
                };
            }

            return await CaptureCoreAsync(mode, () => SafeCaptureResult.CaptureRegion(x, y, width, height));
        });
    }

    private async Task<CapturePreviewResult> CaptureCoreAsync(
        string mode,
        Func<SafeCaptureResult> captureFunc)
    {
        var totalSw = Stopwatch.StartNew();
        string? error = null;
        string dimensions = "";
        double captureMs = 0;
        double displayMs = 0;
        WriteableBitmap? imageSource = null;
        ContiguousBitmap? contiguousResult = null;

        try
        {
            // Phase 1: Native capture
            var captureSw = Stopwatch.StartNew();
            using var result = captureFunc();
            captureSw.Stop();
            captureMs = captureSw.Elapsed.TotalMilliseconds;

            if (!result.IsSuccess)
            {
                // Failed capture does NOT overwrite the last successful cache.
                error = string.IsNullOrEmpty(result.ErrorMessage)
                    ? $"Capture failed: {result.Status}"
                    : result.ErrorMessage;
                totalSw.Stop();
                return new CapturePreviewResult
                {
                    Mode = mode,
                    CaptureMs = Math.Round(captureMs, 1),
                    TotalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 1),
                    Error = error
                };
            }

            dimensions = $"{result.Width}×{result.Height}";

            // Phase 2: Strip stride padding → contiguous BGRA
            contiguousResult = BitmapBufferConverter.StripPadding(
                result.Pixels!,
                (int)result.Width,
                (int)result.Height,
                (int)result.Stride);

            // Phase 3: Convert to WriteableBitmap
            var displaySw = Stopwatch.StartNew();
            imageSource = await SoftwareBitmapConverter.ToWriteableBitmapAsync(contiguousResult);
            displaySw.Stop();
            displayMs = displaySw.Elapsed.TotalMilliseconds;
        }
        catch (DllNotFoundException ex)
        {
            error = $"Native DLL not found: {ex.Message}";
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"Native export not found: {ex.Message}";
        }
        catch (ArgumentException ex)
        {
            error = $"Buffer conversion error: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            error = $"Capture error: {ex.Message}";
        }
        catch (Exception ex)
        {
            error = $"Unexpected error: {ex.Message}";
        }

        // Cache only on successful capture — failures leave prior cache intact.
        if (error == null && contiguousResult != null)
        {
            _lastCapturedBitmap = contiguousResult;
        }

        totalSw.Stop();
        return new CapturePreviewResult
        {
            Mode = mode,
            Dimensions = dimensions,
            CaptureMs = Math.Round(captureMs, 1),
            DisplayMs = Math.Round(displayMs, 1),
            TotalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 1),
            Error = error,
            ImageSource = imageSource
        };
    }
}
