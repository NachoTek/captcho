// CapturePreviewService.cs — Orchestrates capture→convert→display pipeline.
//
// Runs the native capture off the UI thread, converts to a WriteableBitmap,
// and returns structured results with timing for all phases.
// Surfaces errors as user-readable strings rather than exceptions.
// Caches the last successful ContiguousBitmap so the legacy capture routes
// can hand their Frame to the runtime Workflow Session, which owns the
// Export actions (Save, Save As, Copy Frame, Copy Path) since issue #44.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using captcho.Capture;

namespace captcho.UI;

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
/// Caches the last successful ContiguousBitmap; the runtime Workflow Session
/// adopts it (AdoptFrame) so the session remains the single Frame owner for
/// the Export actions.
/// </summary>
public sealed class CapturePreviewService
{
    private volatile ContiguousBitmap? _lastCapturedBitmap;

    /// <summary>
    /// The most recent successfully captured bitmap, or null if no capture has succeeded.
    /// Thread-safe read via volatile. Written by the legacy capture routes
    /// (under CaptureRaw/BuildDisplayResultAsync on this class); MainWindow
    /// hands it to <see cref="CaptureWorkflowSession{TImage}.AdoptFrame"/> so
    /// the workflow session owns the Frame for Export.
    /// </summary>
    public ContiguousBitmap? LastCapturedBitmap => _lastCapturedBitmap;

    /// <summary>
    /// Clears the cached last capture. Useful for testing or explicit reset.
    /// </summary>
    public void ClearLastCapture()
    {
        _lastCapturedBitmap = null;
    }

    /// <summary>
    /// Captures the monitor the cursor is on and converts to displayable form.
    /// Falls back to primary monitor if cursor resolution fails.
    /// Native capture runs on a background thread; WriteableBitmap is created on the calling (UI) thread.
    /// </summary>
    public async Task<CapturePreviewResult> CaptureCurrentMonitorAsync()
    {
        uint monitorIndex = CurrentMonitorResolver.GetCurrentMonitorIndex();
        string mode = $"Current Monitor [{monitorIndex}]";
        var raw = await Task.Run(() => CaptureRaw(mode, () => SafeCaptureResult.CaptureMonitorByIndex(monitorIndex)));
        return await BuildDisplayResultAsync(raw);
    }

    /// <summary>
    /// Captures the top-level window under the mouse cursor and converts to displayable form.
    /// Uses WindowResolver for a descriptive mode label with title and handle.
    /// Native capture runs on a background thread; WriteableBitmap is created on the calling (UI) thread.
    /// </summary>
    public async Task<CapturePreviewResult> CaptureWindowUnderCursorAsync()
    {
        string mode = WindowResolver.BuildWindowUnderCursorLabel();
        var raw = await Task.Run(() => CaptureRaw(mode, SafeCaptureResult.CaptureWindowUnderCursor));
        return await BuildDisplayResultAsync(raw);
    }

    /// <summary>
    /// Intermediate result from the native capture phase (runs on background thread).
    /// Contains either raw pixel data ready for display conversion, or an error.
    /// </summary>
    private sealed class RawCaptureResult
    {
        public string Mode { get; init; } = "";
        public string Dimensions { get; init; } = "";
        public double CaptureMs { get; init; }
        public ContiguousBitmap? Bitmap { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// Phase 1+2: Runs native capture and stride stripping on a background thread.
    /// Returns a RawCaptureResult with either pixel data or an error.
    /// Never creates UI objects — safe to call from any thread.
    /// </summary>
    private static RawCaptureResult CaptureRaw(
        string mode,
        Func<SafeCaptureResult> captureFunc)
    {
        var captureSw = Stopwatch.StartNew();
        string? error = null;
        string dimensions = "";
        ContiguousBitmap? contiguousResult = null;

        try
        {
            using var result = captureFunc();
            captureSw.Stop();

            if (!result.IsSuccess)
            {
                error = string.IsNullOrEmpty(result.ErrorMessage)
                    ? $"Capture failed: {result.Status}"
                    : result.ErrorMessage;
                return new RawCaptureResult
                {
                    Mode = mode,
                    CaptureMs = Math.Round(captureSw.Elapsed.TotalMilliseconds, 1),
                    Error = error
                };
            }

            dimensions = $"{result.Width}×{result.Height}";
            contiguousResult = BitmapBufferConverter.StripPadding(
                result.Pixels!,
                (int)result.Width,
                (int)result.Height,
                (int)result.Stride);
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

        return new RawCaptureResult
        {
            Mode = mode,
            Dimensions = dimensions,
            CaptureMs = captureSw.Elapsed.TotalMilliseconds > 0
                ? Math.Round(captureSw.Elapsed.TotalMilliseconds, 1) : 0,
            Bitmap = contiguousResult,
            Error = error
        };
    }

    /// <summary>
    /// Phase 3: Converts raw capture data to a displayable WriteableBitmap.
    /// Must run on the UI thread because WriteableBitmap is a WinRT UI object.
    /// Caches the bitmap on success for export workflows.
    /// </summary>
    private async Task<CapturePreviewResult> BuildDisplayResultAsync(RawCaptureResult raw)
    {
        var totalSw = Stopwatch.StartNew();
        // Account for capture time already elapsed
        double captureMs = raw.CaptureMs;
        double displayMs = 0;
        WriteableBitmap? imageSource = null;
        string? error = raw.Error;

        if (error == null && raw.Bitmap != null)
        {
            try
            {
                var displaySw = Stopwatch.StartNew();
                imageSource = await SoftwareBitmapConverter.ToWriteableBitmapAsync(raw.Bitmap);
                displaySw.Stop();
                displayMs = displaySw.Elapsed.TotalMilliseconds;

                // Cache only on successful capture
                _lastCapturedBitmap = raw.Bitmap;
            }
            catch (ArgumentException ex)
            {
                error = $"Display conversion error: {ex.Message}";
            }
            catch (Exception ex)
            {
                error = $"Unexpected display error: {ex.Message}";
            }
        }

        totalSw.Stop();
        return new CapturePreviewResult
        {
            Mode = raw.Mode,
            Dimensions = raw.Dimensions,
            CaptureMs = captureMs,
            DisplayMs = Math.Round(displayMs, 1),
            TotalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 1),
            Error = error,
            ImageSource = imageSource
        };
    }
}
