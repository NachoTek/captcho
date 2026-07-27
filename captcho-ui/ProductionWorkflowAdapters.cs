// ProductionWorkflowAdapters.cs — Production implementations of the
// CaptureWorkflowSession platform adapters.
//
// WindowsCaptureAdapter drives the Rust Capture engine through
// SafeCaptureResult.CaptureAllMonitors and converts the result into a
// CaptureFrameResult. WriteableBitmapPreviewAdapter converts the captured
// Frame into a WinUI WriteableBitmap via SoftwareBitmapConverter so the
// WinUI code-behind can bind it to a XAML Image. Both adapters translate
// expected failures into result objects rather than throwing, so the
// workflow session can surface them as retryable WorkflowResult outcomes.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Production IWorkflowCaptureAdapter. Drives the Rust Capture engine through
/// SafeCaptureResult and converts the outcome into a CaptureFrameResult.
/// Translates DllNotFoundException, EntryPointNotFoundException, and native
/// capture status failures into user-visible CaptureFrameResult.Fail messages.
/// </summary>
public sealed class WindowsCaptureAdapter : IWorkflowCaptureAdapter
{
    /// <summary>
    /// Captures the complete Virtual Desktop and returns a CaptureFrameResult
    /// carrying the Frame and dimensions on success or a user-visible error
    /// on failure. Never throws for expected failures.
    /// </summary>
    public CaptureFrameResult CaptureFullDesktop() =>
        Capture("Full Desktop", SafeCaptureResult.CaptureAllMonitors);

    /// <summary>
    /// Captures the current eligible active (foreground) window and returns a
    /// CaptureFrameResult carrying the Frame and dimensions on success or a
    /// user-visible error on failure — including the "no eligible active
    /// window" case, which the native engine surfaces as a failed status.
    /// Never throws for expected failures.
    /// </summary>
    public CaptureFrameResult CaptureActiveWindow() =>
        Capture("Active Window", SafeCaptureResult.CaptureActiveWindow);

    /// <summary>
    /// Captures a rectangular Selection of the Virtual Desktop and returns a
    /// CaptureFrameResult carrying the Frame and dimensions on success or a
    /// user-visible error on failure. The geometry's signed X/Y preserves
    /// negative Virtual Desktop origins, so cross-monitor and mixed-coordinate
    /// layouts stay aligned. Never throws for expected failures.
    /// </summary>
    public CaptureFrameResult CaptureSelection(SelectionGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return Capture("Selection",
            () => SafeCaptureResult.CaptureRegion(geometry.X, geometry.Y, geometry.Width, geometry.Height));
    }

    /// <summary>
    /// Captures a single Selected Monitor and returns a CaptureFrameResult
    /// carrying the Frame and dimensions on success or a user-visible error on
    /// failure. A monitor's Virtual Desktop bounds uniquely identify its pixel
    /// region, so capture is performed over those bounds via the region path —
    /// this keeps negative-coordinate and mixed-DPI layouts aligned without
    /// depending on monitor-index alignment between the picker and the native
    /// engine. Never throws for expected failures.
    /// </summary>
    public CaptureFrameResult CaptureMonitor(MonitorTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Capture("Selected Monitor",
            () => SafeCaptureResult.CaptureRegion(target.X, target.Y, target.Width, target.Height));
    }

    /// <summary>
    /// Shared native-capture-to-Frame conversion for both immediate-capture
    /// routes. Runs the supplied SafeCaptureResult factory, strips stride
    /// padding, and translates expected failures into CaptureFrameResult.Fail.
    /// The <paramref name="mode"/> label scopes diagnostics for error messages.
    /// </summary>
    private static CaptureFrameResult Capture(string mode, Func<SafeCaptureResult> capture)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var result = capture();
            sw.Stop();

            if (!result.IsSuccess)
            {
                return CaptureFrameResult.Fail(
                    string.IsNullOrEmpty(result.ErrorMessage)
                        ? $"{mode} capture failed: {result.Status}"
                        : result.ErrorMessage);
            }

            var frame = BitmapBufferConverter.StripPadding(
                result.Pixels!,
                (int)result.Width,
                (int)result.Height,
                (int)result.Stride);

            return CaptureFrameResult.Ok(
                frame,
                $"{result.Width}×{result.Height}",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (DllNotFoundException ex)
        {
            return CaptureFrameResult.Fail($"Native DLL not found: {ex.Message}");
        }
        catch (EntryPointNotFoundException ex)
        {
            return CaptureFrameResult.Fail($"Native export not found: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return CaptureFrameResult.Fail($"Buffer conversion error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return CaptureFrameResult.Fail($"Capture error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CaptureFrameResult.Fail($"Unexpected error: {ex.Message}");
        }
    }
}

/// <summary>
/// Production IPreviewAdapter&lt;WriteableBitmap&gt;. Converts a captured Frame
/// into a WinUI WriteableBitmap via SoftwareBitmapConverter so the WinUI
/// code-behind can bind it to a XAML Image. Translates conversion failures
/// into a PreviewPresentResult.Fail so the Frame stays retryable.
/// </summary>
public sealed class WriteableBitmapPreviewAdapter : IPreviewAdapter<WriteableBitmap>
{
    /// <summary>
    /// Presents the supplied Frame by converting it into a WriteableBitmap.
    /// Called on the workflow caller's thread (the UI thread in production),
    /// which is required for WriteableBitmap construction. The returned
    /// PreviewPresentResult.Image is the bitmap to assign to Image.Source.
    /// </summary>
    public PreviewPresentResult<WriteableBitmap> Present(ContiguousBitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var sw = Stopwatch.StartNew();
        try
        {
            // SoftwareBitmapConverter.ToWriteableBitmapAsync is async because it
            // writes to the bitmap's pixel buffer via a stream. Block on it
            // here — the session invokes Present on the UI thread, and the
            // underlying stream write is synchronous to memory.
            var bitmap = SoftwareBitmapConverter.ToWriteableBitmapAsync(frame).GetAwaiter().GetResult();
            sw.Stop();
            return PreviewPresentResult<WriteableBitmap>.Ok(bitmap, sw.Elapsed.TotalMilliseconds);
        }
        catch (ArgumentException ex)
        {
            return PreviewPresentResult<WriteableBitmap>.Fail($"Display conversion error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return PreviewPresentResult<WriteableBitmap>.Fail($"Unexpected display error: {ex.Message}");
        }
    }
}

/// <summary>
/// Production <see cref="ISelectionOverlayAdapter"/>. Wraps the Win32 layered
/// <see cref="RegionOverlayWindow"/>: shows the transparent overlay over the
/// live desktop, waits for the user to confirm a Selection or cancel, and
/// returns the confirmed geometry (or null for cancellation). Per spec #27,
/// the adapter never performs Capture and never owns post-capture state —
/// the runtime CaptureWorkflowSession owns Capture of the returned geometry,
/// the resulting Frame, and the preview transition. Translates overlay
/// exceptions into a null result so the workflow reports cancellation
/// rather than crashing.
/// </summary>
public sealed class RegionSelectionOverlayAdapter : ISelectionOverlayAdapter
{
    /// <summary>
    /// Shows the Selection overlay on the caller's thread (the UI thread in
    /// production, where the modal Win32 message loop must live) and returns
    /// the confirmed geometry. Returns null if the user cancelled or if the
    /// overlay could not be shown.
    /// </summary>
    public async Task<SelectionGeometry?> ShowAsync()
    {
        try
        {
            using var overlay = new RegionOverlayWindow();
            return await overlay.ShowAndWaitAsync();
        }
        catch
        {
            // Surface unexpected overlay failures as cancellation so the
            // workflow reports a user-visible "Selection cancelled." outcome
            // instead of crashing the application.
            return null;
        }
    }
}

/// <summary>
/// Production <see cref="IMonitorPickerOverlayAdapter"/>. Wraps the Win32
/// layered <see cref="MonitorPickerOverlayWindow"/>: shows the scrimmed picker
/// over the live desktop, highlights the hovered monitor and its label, and
/// returns the confirmed monitor target (or null for cancellation). Per spec
/// #28, the adapter never performs Capture and never owns post-capture state —
/// the runtime CaptureWorkflowSession owns Capture of the returned bounds, the
/// resulting Frame, and the preview transition. Translates overlay exceptions
/// into a null result so the workflow reports cancellation rather than
/// crashing.
/// </summary>
public sealed class MonitorPickerOverlayAdapter : IMonitorPickerOverlayAdapter
{
    /// <summary>
    /// Shows the Selected Monitor picker on the caller's thread (the UI thread
    /// in production, where the modal Win32 message loop must live) and returns
    /// the confirmed monitor target. Returns null if the user cancelled or if
    /// the overlay could not be shown.
    /// </summary>
    public async Task<MonitorTarget?> ShowAsync()
    {
        try
        {
            using var overlay = new MonitorPickerOverlayWindow();
            return await overlay.ShowAndWaitAsync();
        }
        catch
        {
            // Surface unexpected overlay failures as cancellation so the
            // workflow reports a user-visible "Selected Monitor cancelled."
            // outcome instead of crashing the application.
            return null;
        }
    }
}
