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
using System.Collections.Generic;
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
    /// on failure. The effective CaptureOptions are composed by the workflow
    /// session; only the mouse-pointer flag is applicable to a Full Desktop
    /// Frame and is forwarded into the managed/native contract (spec #34).
    /// Never throws for expected failures.
    /// </summary>
    public CaptureFrameResult CaptureFullDesktop(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Capture("Full Desktop", () => SafeCaptureResult.CaptureAllMonitors(options));
    }

    /// <summary>
    /// Captures the current eligible active (foreground) window and returns a
    /// CaptureFrameResult carrying the Frame and dimensions on success or a
    /// user-visible error on failure — including the "no eligible active
    /// window" case, which the native engine surfaces as a failed status. The
    /// effective CaptureOptions are composed by the workflow session; pointer,
    /// decorations, and shadow are each applicable and forwarded into the
    /// managed/native contract (spec #34). Never throws for expected failures.
    /// </summary>
    public CaptureFrameResult CaptureActiveWindow(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Capture("Active Window", () => SafeCaptureResult.CaptureActiveWindow(options));
    }

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
    /// carrying the Frame and dimensions on success or a user-visible error
    /// on failure. A monitor's Virtual Desktop bounds uniquely identify its pixel
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
    /// Captures a single Selected Window with the effective CaptureOptions for
    /// the Selected Window mode and returns a CaptureFrameResult carrying the
    /// Frame and dimensions on success or a user-visible error on failure —
    /// including the "window vanished before capture" case, which the native
    /// engine surfaces as a failed status. Pointer, decorations, and shadow are
    /// each applicable and forwarded into the managed/native contract. Capture
    /// is performed by handle via the native window-by-handle export, so the
    /// frozen Frame matches the window the user clicked even if later movement
    /// would have shifted its bounds. Never throws for expected failures.
    /// </summary>
    public CaptureFrameResult CaptureWindow(WindowTarget target, CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        return Capture("Selected Window",
            () => SafeCaptureResult.CaptureWindowByHandle(target.Handle, options));
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

/// <summary>Production adapter for post-capture Annotation.</summary>
public sealed class AnnotationOverlayAdapter : IAnnotationOverlayStateAdapter
{
    public async Task<AnnotationPresentResult> ShowAsync(ContiguousBitmap sourceFrame)
        => await ShowAsync(sourceFrame, AnnotationToolState.WithDefaults());

    public async Task<AnnotationPresentResult> ShowAsync(
        ContiguousBitmap sourceFrame,
        AnnotationToolState initialToolState)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        ArgumentNullException.ThrowIfNull(initialToolState);

        try
        {
            using var overlay = new AnnotationOverlayWindow(sourceFrame, initialToolState);
            return await overlay.ShowAndWaitAsync();
        }
        catch (Exception ex)
        {
            return AnnotationPresentResult.Fail($"Annotation failed: {ex.Message}");
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
    public async Task<TargetSelectionResult<SelectionGeometry>?> ShowAsync(SelectionGeometry? initialGeometry)
    {
        try
        {
            using var overlay = new RegionOverlayWindow(initialGeometry);
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

/// <summary>Reads the current Virtual Desktop topology from Win32 monitor enumeration.</summary>
public sealed class NativeVirtualDesktopTopologyProvider : IVirtualDesktopTopologyProvider
{
    private readonly MonitorInterop _interop = MonitorInterop.CreateNative();

    public IReadOnlyList<MonitorRect> GetCurrent() => _interop.EnumerateMonitors();
}

/// <summary>Atomically writes Always remembered Selection geometry to Configuration.</summary>
public sealed class ConfigurationRememberedSelectionPersistence : IRememberedSelectionPersistence
{
    private readonly AppSettings _settings;
    private readonly ConfigurationService _configuration;

    public ConfigurationRememberedSelectionPersistence(
        AppSettings settings,
        ConfigurationService configuration)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public bool Save(RememberedSelectionGeometry? geometry)
    {
        var candidate = _settings.Normalized();
        candidate.RememberedSelection = geometry?.Normalized();
        if (!_configuration.Save(candidate).Success)
            return false;

        _settings.RememberedSelection = candidate.RememberedSelection?.Normalized();
        return true;
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
    public async Task<TargetSelectionResult<MonitorTarget>?> ShowAsync()
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

/// <summary>
/// Production <see cref="IWorkflowExportAdapter"/>. Owns no workflow state:
/// image encoding and file writing delegate to <see cref="PngExportService"/>
/// and <see cref="JpegExportService"/>, clipboard placement to the injected
/// <see cref="IClipboardAdapter"/>. The configured destination, Filename
/// Template, and export format (issue #45) are read from the live runtime
/// <see cref="AppSettings"/> the adapter was constructed from, so committed
/// Configuration changes are observable without reconstructing it. Save
/// encodes the configured format (PNG preserves alpha; JPEG flattens
/// transparent pixels onto white at the configured quality); Save As encodes
/// what the chosen file's extension says so the written bytes always match
/// the name. Never throws for expected failures — every method returns the
/// structured result the session maps into a retryable
/// <see cref="WorkflowExportResult"/>.
/// </summary>
public sealed class WorkflowExportAdapter : IWorkflowExportAdapter
{
    private readonly AppSettings _settings;
    private readonly IClipboardAdapter _clipboard;

    /// <summary>
    /// Creates the production Export adapter over the live runtime settings
    /// and clipboard boundary.
    /// </summary>
    /// <param name="settings">
    /// Live runtime settings supplying the committed Save Location,
    /// Filename Template, and export format/quality. Retained by reference —
    /// never written back to.
    /// </param>
    /// <param name="clipboard">Platform clipboard boundary.</param>
    public WorkflowExportAdapter(AppSettings settings, IClipboardAdapter clipboard)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    /// <summary>
    /// Writes the Frame to the configured Save Location using the configured
    /// Filename Template and export format — PNG or JPEG (with the configured
    /// quality) — deriving the output extension from the format, resolving
    /// filename collisions so an existing file is never silently overwritten.
    /// </summary>
    public ExportResult SaveDefault(ContiguousBitmap frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        string path = ExportFilenameTemplate.GetExportPath(_settings, DateTime.Now);
        path = ExportFilenameTemplate.ResolveCollision(path);
        return SaveInConfiguredFormat(frame, path, cancellationToken);
    }

    /// <summary>
    /// Writes the Frame to the explicitly supplied full path — the confirmed
    /// Save As choice or the already-recorded default saved-file identity on
    /// a repeated Save. The encoded format follows the destination's
    /// extension (.jpg encodes JPEG with the configured quality, anything
    /// else encodes PNG) so the written bytes always match the chosen name.
    /// Creates the destination directory when missing; performs no collision
    /// resolution because the caller chose the path.
    /// </summary>
    public ExportResult SaveTo(ContiguousBitmap frame, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrWhiteSpace(destinationPath))
            return ExportResult.Fail(ExportPhase.Validation, "No destination path provided", TimeSpan.Zero);
        return SaveByPathExtension(frame, destinationPath, cancellationToken);
    }

    /// <summary>
    /// Encodes the Frame according to the configured format and writes it to
    /// <paramref name="path"/>. PNG preserves the Frame's alpha; JPEG uses
    /// the configured quality and flattens transparent pixels onto white.
    /// </summary>
    private ExportResult SaveInConfiguredFormat(ContiguousBitmap frame, string path, CancellationToken cancellationToken)
    {
        var export = _settings.EffectiveExportSettings;
        return export.Format == ExportImageFormat.Jpeg
            ? JpegExportService.SaveAsJpeg(frame, path, export.JpegQuality, cancellationToken)
            : PngExportService.SaveAsPng(frame, path, cancellationToken);
    }

    /// <summary>
    /// Encodes the Frame by the destination path's extension so a Save As
    /// choice (or a recorded identity) never carries bytes that disagree
    /// with its name. A .jpg/.jpeg path encodes JPEG with the configured
    /// quality; every other extension encodes PNG.
    /// </summary>
    private ExportResult SaveByPathExtension(ContiguousBitmap frame, string path, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
            ? JpegExportService.SaveAsJpeg(frame, path, _settings.EffectiveExportSettings.JpegQuality, cancellationToken)
            : PngExportService.SaveAsPng(frame, path, cancellationToken);
    }

    /// <summary>
    /// Encodes the Frame as PNG image content and places it on the clipboard.
    /// Creates no file.
    /// </summary>
    public ClipboardExportResult CopyImage(ContiguousBitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Width <= 0 || frame.Height <= 0)
            return ClipboardExportResult.Fail(
                $"Invalid capture dimensions: {frame.Width}x{frame.Height}", TimeSpan.Zero);

        byte[]? pngBytes = PngExportService.EncodeToPngBytes(frame);
        if (pngBytes is null || pngBytes.Length == 0)
            return ClipboardExportResult.Fail("Failed to encode image for clipboard", TimeSpan.Zero);

        if (!_clipboard.SetPngImage(pngBytes))
            return ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.Zero);

        return ClipboardExportResult.Ok(frame.Width, frame.Height, pngBytes.Length, TimeSpan.Zero);
    }

    /// <summary>
    /// Places the supplied text (a saved file's path) on the clipboard.
    /// </summary>
    public ClipboardExportResult CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return ClipboardExportResult.Fail("No text to copy", TimeSpan.Zero);

        if (!_clipboard.SetText(text))
            return ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.Zero);

        return ClipboardExportResult.TextOk(text.Length, TimeSpan.Zero);
    }
}

/// <summary>
/// Production <see cref="ISaveAsDialogAdapter"/>. Shows a WinUI
/// FileSavePicker initialized from the configured Filename Template and the
/// Pictures library, offering PNG and JPEG with the configured format
/// preselected (issue #45). Must be shown on the UI thread; the parent HWND
/// is resolved from the supplied provider at ShowAsync time. Translates
/// picker failures into a null (cancelled) result so the workflow reports
/// cancellation rather than crashing.
/// </summary>
public sealed class FileSavePickerDialogAdapter : ISaveAsDialogAdapter
{
    private readonly Func<IntPtr> _hwndProvider;
    private readonly Func<AppSettings> _settingsProvider;

    /// <summary>
    /// Creates the dialog adapter over the main window's HWND and the live
    /// runtime settings. Both are deferred so the handle, the committed
    /// Filename Template, and the committed export format are resolved at
    /// ShowAsync time.
    /// </summary>
    public FileSavePickerDialogAdapter(Func<IntPtr> hwndProvider, Func<AppSettings> settingsProvider)
    {
        _hwndProvider = hwndProvider ?? throw new ArgumentNullException(nameof(hwndProvider));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    /// <summary>
    /// Shows the Save As picker and returns the chosen full path, or null if
    /// the user cancelled (or the picker could not be shown).
    /// </summary>
    public async Task<string?> ShowAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();

            var hwnd = _hwndProvider();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var export = _settingsProvider().EffectiveExportSettings;

            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add("PNG Image", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG Image", new List<string> { ".jpg" });
            picker.DefaultFileExtension = "." + export.Format.Extension();
            picker.SuggestedFileName = ExportFilenameTemplate.Expand(
                _settingsProvider().EffectiveFilenameTemplate, DateTime.Now);

            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch
        {
            // Surface unexpected picker failures as cancellation so the
            // workflow reports a user-visible "Save cancelled." outcome
            // instead of crashing the application.
            return null;
        }
    }
}

/// <summary>
/// Production <see cref="IWindowPickerOverlayAdapter"/>. Wraps the Win32 layered
/// <see cref="WindowPickerOverlayWindow"/>: shows the scrimmed picker over the
/// live desktop, highlights the hovered window's full bounds and title, and
/// returns a confirming outcome — <see cref="WindowPickerOutcome.WindowConfirmed"/>
/// for a click on an eligible window or
/// <see cref="WindowPickerOutcome.EmptyDesktopFallback"/> for a click on empty
/// desktop — or null for cancellation (Escape). Per spec #29, the adapter never
/// performs Capture and never owns post-capture state — the runtime
/// CaptureWorkflowSession owns Capture (by handle for a window, Full Desktop for
/// the fallback), the resulting Frame, and the preview transition. Translates
/// overlay exceptions into a null result so the workflow reports cancellation
/// rather than crashing.
/// </summary>
public sealed class WindowPickerOverlayAdapter : IWindowPickerOverlayAdapter
{
    /// <summary>
    /// Shows the Selected Window picker on the caller's thread (the UI thread
    /// in production, where the modal Win32 message loop must live) and returns
    /// the confirming outcome. Returns null if the user cancelled or if the
    /// overlay could not be shown.
    /// </summary>
    public async Task<TargetSelectionResult<WindowPickerResult>?> ShowAsync()
    {
        try
        {
            using var overlay = new WindowPickerOverlayWindow();
            return await overlay.ShowAndWaitAsync();
        }
        catch
        {
            // Surface unexpected overlay failures as cancellation so the
            // workflow reports a user-visible "Selected Window cancelled."
            // outcome instead of crashing the application.
            return null;
        }
    }
}
