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
