// CaptureWorkflowSession.cs — WinUI-free runtime workflow session.
//
// The agreed high-level behavioral seam for capture and post-capture behavior.
// Owns Capture Mode routing for the Full Desktop, Active Window, Selection,
// Selected Monitor, and Selected Window Triggers; operation state (concurrency
// guard); the captured Frame; and the preview transition. For the interactive
// modes, the overlay is supplied as a narrow adapter that returns confirmed
// target geometry/outcome or cancellation, after which the workflow owns
// Capture, the Frame, and the preview transition — the overlay itself never
// performs Capture or owns post-capture state. Exposes operation-in-progress,
// cancellation, and failure outcomes consistently so the WinUI layer can
// present a retryable, user-visible state. Platform-specific work — native
// pixel acquisition, the interactive overlays, and presenting the Frame to a
// XAML Image — is supplied through narrow adapters, mirroring the
// CliCaptureService shape at the GUI's higher Capture-through-completion
// boundary.
//
// The session is generic over the preview's display token (TImage) so it stays
// free of WinUI types while production still gets a strongly-typed
// WriteableBitmap back. Adapters must not throw for expected failures — they
// surface them through PreviewPresentResult.Fail so the Frame stays retryable.
//
// Threading contract: capture runs on the thread pool (the adapter is allowed
// to block on native pixel acquisition); the preview adapter is invoked on the
// caller's thread, which in production is the UI thread — required for
// WriteableBitmap construction. The session itself does not marshal calls.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Outcome of a workflow attempt. The session never throws for expected
/// failures; it reports them through this status instead.
/// </summary>
public enum WorkflowStatus
{
    /// <summary>
    /// The Frame was captured and presented to the preview adapter
    /// successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The Capture adapter reported a failure. No Frame is available.
    /// The user can retry the same Trigger.
    /// </summary>
    CaptureFailed,

    /// <summary>
    /// The Capture succeeded but the preview adapter could not present the
    /// Frame. The Frame is preserved on the result so the user can retry
    /// without another Capture.
    /// </summary>
    PreviewFailed,

    /// <summary>
    /// Another workflow operation was already in progress when this Trigger
    /// arrived. No work was performed.
    /// </summary>
    OperationInProgress,

    /// <summary>
    /// The user cancelled an interactive Target Selection overlay (e.g.,
    /// pressed Escape in the Selection overlay). No Frame was produced and no
    /// delivery side effects occurred. Distinct from failure: cancellation is
    /// a user-initiated end to the operation, not a retryable error.
    /// </summary>
    Cancelled,
}

/// <summary>
/// Immutable Selection Target Selection geometry in Virtual Desktop
/// coordinates. The X/Y origin is signed so multi-monitor layouts with
/// negative Virtual Desktop origins (e.g., a monitor to the left of and
/// above the primary) survive intact; Width/Height are unsigned because
/// the Capture engine requires positive dimensions. This is the contract
/// type that crosses the WinUI-free workflow seam — it deliberately
/// mirrors the native parameter shape of
/// <c>SafeCaptureResult.CaptureRegion</c> without dragging in
/// <c>Windows.Foundation.Rect</c> or any UI type.
/// </summary>
public sealed record SelectionGeometry
{
    /// <summary>Left edge of the Selection in Virtual Desktop pixels.</summary>
    public int X { get; init; }

    /// <summary>Top edge of the Selection in Virtual Desktop pixels.</summary>
    public int Y { get; init; }

    /// <summary>Selection width in pixels. Always positive.</summary>
    public uint Width { get; init; }

    /// <summary>Selection height in pixels. Always positive.</summary>
    public uint Height { get; init; }
}

/// <summary>
/// WinUI-free result of a runtime workflow attempt. The session's Frame and
/// adapter-produced display token are surfaced here so the WinUI code-behind
/// can bind preview/status/timing from a single object.
/// </summary>
public sealed class WorkflowResult<TImage>
{
    /// <summary>Workflow outcome.</summary>
    public WorkflowStatus Status { get; init; }

    /// <summary>
    /// Domain Capture Mode label (e.g., "Full Desktop"). Always set, even on
    /// failure, so the WinUI status bar can describe which Trigger failed.
    /// </summary>
    public string Mode { get; init; } = "";

    /// <summary>
    /// Frame dimensions string (e.g., "1920×1080") from the Capture adapter.
    /// Empty until a Capture succeeds.
    /// </summary>
    public string Dimensions { get; init; } = "";

    /// <summary>
    /// The captured Frame. Set on Succeeded and on PreviewFailed so failed
    /// preview transitions remain retryable. Null on CaptureFailed and
    /// OperationInProgress.
    /// </summary>
    public ContiguousBitmap? Frame { get; init; }

    /// <summary>
    /// Display token produced by the preview adapter (e.g., a WriteableBitmap
    /// in production). Null unless the preview adapter succeeded.
    /// </summary>
    public TImage? PreviewImage { get; init; }

    /// <summary>
    /// User-visible error message. Null on Succeeded; non-null on every other
    /// status.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Capture phase duration in milliseconds.</summary>
    public double CaptureMs { get; init; }

    /// <summary>Preview transition duration in milliseconds.</summary>
    public double DisplayMs { get; init; }

    /// <summary>Total wall-clock duration in milliseconds.</summary>
    public double TotalMs { get; init; }

    /// <summary>True only when the workflow reached the Succeeded status.</summary>
    public bool IsSuccess => Status == WorkflowStatus.Succeeded;
}

/// <summary>
/// Result of a Capture adapter call. Carries the Frame in domain terms — the
/// session owns the resulting Frame on success.
/// </summary>
public sealed class CaptureFrameResult
{
    /// <summary>Whether the Capture succeeded.</summary>
    public bool Success { get; }

    /// <summary>The captured Frame on success; null on failure.</summary>
    public ContiguousBitmap? Frame { get; }

    /// <summary>Frame dimensions string on success; empty on failure.</summary>
    public string Dimensions { get; }

    /// <summary>User-visible error on failure; null on success.</summary>
    public string? Error { get; }

    /// <summary>Capture phase duration in milliseconds.</summary>
    public double ElapsedMs { get; }

    private CaptureFrameResult(bool success, ContiguousBitmap? frame, string dimensions, string? error, double elapsedMs)
    {
        Success = success;
        Frame = frame;
        Dimensions = dimensions;
        Error = error;
        ElapsedMs = elapsedMs;
    }

    /// <summary>Builds a successful Capture result carrying the captured Frame.</summary>
    public static CaptureFrameResult Ok(ContiguousBitmap frame, string dimensions, double elapsedMs) =>
        new(true, frame, dimensions, null, elapsedMs);

    /// <summary>Builds a failed Capture result with a user-visible error.</summary>
    public static CaptureFrameResult Fail(string error) =>
        new(false, null, "", error ?? "Capture failed.", 0);
}

/// <summary>
/// Result of a preview adapter call. The display token is typed by the
/// adapter's TImage type parameter.
/// </summary>
public sealed class PreviewPresentResult<TImage>
{
    /// <summary>Whether the preview transition succeeded.</summary>
    public bool Success { get; }

    /// <summary>
    /// Display token on success (e.g., a WriteableBitmap). Null on failure.
    /// </summary>
    public TImage? Image { get; }

    /// <summary>User-visible error on failure; null on success.</summary>
    public string? Error { get; }

    /// <summary>Preview transition duration in milliseconds.</summary>
    public double ElapsedMs { get; }

    private PreviewPresentResult(bool success, TImage? image, string? error, double elapsedMs)
    {
        Success = success;
        Image = image;
        Error = error;
        ElapsedMs = elapsedMs;
    }

    /// <summary>Builds a successful preview result with a display token.</summary>
    public static PreviewPresentResult<TImage> Ok(TImage image, double elapsedMs) =>
        new(true, image, null, elapsedMs);

    /// <summary>Builds a failed preview result with a user-visible error.</summary>
    public static PreviewPresentResult<TImage> Fail(string error) =>
        new(false, default, error ?? "Preview transition failed.", 0);
}

/// <summary>
/// Narrow adapter over native pixel acquisition. Production implementation
/// invokes the Rust Capture engine through SafeCaptureResult; tests supply
/// fakes that return synthetic Frames. Named IWorkflowCaptureAdapter to match
/// the ICliCaptureAdapter prefix convention and to leave room for non-workflow
/// capture adapters in the future.
/// </summary>
public interface IWorkflowCaptureAdapter
{
    /// <summary>
    /// Captures the complete Virtual Desktop into a Frame.
    /// Implementations must not throw for expected failures — surface them
    /// through CaptureFrameResult.Fail instead.
    /// </summary>
    CaptureFrameResult CaptureFullDesktop();

    /// <summary>
    /// Captures the current eligible active (foreground) window into a Frame.
    /// Implementations must not throw for expected failures — surface them
    /// through CaptureFrameResult.Fail instead (including the "no eligible
    /// active window" case, so the workflow can report it as CaptureFailed
    /// without retaining stale Frame or operation state).
    /// </summary>
    CaptureFrameResult CaptureActiveWindow();

    /// <summary>
    /// Captures a rectangular Selection of the Virtual Desktop into a Frame.
    /// The geometry is the confirmed output of Selection Target Selection;
    /// its signed X/Y preserves negative Virtual Desktop origins so
    /// cross-monitor and mixed-coordinate layouts stay aligned. Width/Height
    /// are always positive. Implementations must not throw for expected
    /// failures — surface them through CaptureFrameResult.Fail instead.
    /// </summary>
    CaptureFrameResult CaptureSelection(SelectionGeometry geometry);

    /// <summary>
    /// Captures a single Selected Monitor into a Frame. The target carries the
    /// monitor's Virtual Desktop bounds (signed X/Y so negative-coordinate and
    /// mixed-DPI layouts stay aligned) and a display label; capture is performed
    /// over those bounds, which uniquely identify one monitor's pixel region.
    /// Implementations must not throw for expected failures — surface them
    /// through CaptureFrameResult.Fail instead.
    /// </summary>
    CaptureFrameResult CaptureMonitor(MonitorTarget target);

    /// <summary>
    /// Captures a single Selected Window into a Frame. The target carries the
    /// window's stable HWND (the identity the picker confirmed), its title, and
    /// its full Virtual Desktop bounds (which may cross monitor boundaries and
    /// sit at negative coordinates). Capture is performed by handle so the
    /// frozen Frame matches the window the user clicked even if later window
    /// movement would have moved its bounds. Implementations must not throw for
    /// expected failures — surface them through CaptureFrameResult.Fail instead
    /// (including the "window vanished before capture" case, so the workflow
    /// can report it as CaptureFailed without retaining stale state).
    /// </summary>
    CaptureFrameResult CaptureWindow(WindowTarget target);
}

/// <summary>
/// Narrow adapter over the interactive Selection overlay. Production shows a
/// transparent Win32 layered window over the live desktop; tests supply fakes
/// that return canned geometry or cancellation. The overlay adapter's only
/// job is to return confirmed geometry (or cancellation) — it must NOT
/// perform Capture or own any post-capture state, because the runtime
/// workflow owns Capture, the Frame, and the preview transition. Invoked on
/// the workflow caller's thread (the UI thread in production), since the
/// overlay runs a modal Win32 message loop.
/// </summary>
public interface ISelectionOverlayAdapter
{
    /// <summary>
    /// Shows the Selection overlay and returns the confirmed geometry, or
    /// null if the user cancelled (Escape or overlay dismissal). Returning
    /// null must not produce a Frame or any delivery side effect.
    /// Implementations must not throw — surface unexpected failures as a
    /// null result so the workflow reports cancellation rather than crashing.
    /// </summary>
    Task<SelectionGeometry?> ShowAsync();
}

/// <summary>
/// Immutable Selected Monitor target in Virtual Desktop coordinates — the
/// contract type that crosses the WinUI-free workflow seam from the monitor
/// picker overlay. The signed X/Y origin preserves negative Virtual Desktop
/// origins (e.g., a secondary monitor to the left of and/or above the
/// primary); Width/Height are unsigned exact pixel dimensions (including the
/// non-round dimensions that arise from mixed-DPI scaling) because the Capture
/// engine requires positive dimensions. The label is the pointer-adjacent text
/// the picker showed for the hovered monitor, surfaced for diagnostics and
/// tests.
/// </summary>
public sealed record MonitorTarget
{
    /// <summary>
    /// Pointer-adjacent label the picker displayed for this monitor (e.g.,
    /// "Monitor 1 (1920×1080)"). Carried for diagnostics and contract tests;
    /// the workflow does not interpret it.
    /// </summary>
    public string Label { get; init; } = "";

    /// <summary>Left edge of the monitor in Virtual Desktop pixels.</summary>
    public int X { get; init; }

    /// <summary>Top edge of the monitor in Virtual Desktop pixels.</summary>
    public int Y { get; init; }

    /// <summary>Monitor width in pixels. Always positive.</summary>
    public uint Width { get; init; }

    /// <summary>Monitor height in pixels. Always positive.</summary>
    public uint Height { get; init; }
}

/// <summary>
/// Narrow adapter over the interactive Selected Monitor picker overlay.
/// Production shows a scrimmed Win32 layered window that highlights the
/// hovered monitor and its label; tests supply fakes that return a canned
/// monitor target or cancellation. The overlay adapter's only job is to
/// return the confirmed monitor target (or cancellation) — it must NOT
/// perform Capture or own any post-capture state, because the runtime
/// workflow owns Capture, the Frame, and the preview transition. Clicks in
/// monitor-layout gaps must keep the overlay open (the adapter handles gap
/// rejection internally); Escape dismisses it with a null result. Invoked on
/// the workflow caller's thread (the UI thread in production), since the
/// overlay runs a modal Win32 message loop.
/// </summary>
public interface IMonitorPickerOverlayAdapter
{
    /// <summary>
    /// Shows the Selected Monitor picker overlay and returns the confirmed
    /// monitor target, or null if the user cancelled (Escape or overlay
    /// dismissal). Returning null must not produce a Frame or any delivery
    /// side effect. Implementations must not throw — surface unexpected
    /// failures as a null result so the workflow reports cancellation
    /// rather than crashing.
    /// </summary>
    Task<MonitorTarget?> ShowAsync();
}

/// <summary>
/// Immutable Selected Window target in Virtual Desktop coordinates — the
/// contract type that crosses the WinUI-free workflow seam from the window
/// picker overlay. <see cref="Handle"/> is the stable HWND identity the picker
/// confirmed (and re-validates against a fresh snapshot at click time so a
/// window that disappeared can never be captured as a stale target); the
/// capture engine receives it directly. The signed X/Y origin and unsigned
/// Width/Height carry the window's full bounds, which may cross monitor
/// boundaries and sit at negative Virtual Desktop coordinates. The title is the
/// pointer-adjacent text the picker showed for the hovered window, surfaced for
/// diagnostics and tests.
/// </summary>
public sealed record WindowTarget
{
    /// <summary>
    /// The stable HWND the picker confirmed. Captured by handle so the Frame
    /// matches the window the user clicked.
    /// </summary>
    public IntPtr Handle { get; init; }

    /// <summary>
    /// Pointer-adjacent title the picker displayed for this window. Carried for
    /// diagnostics and contract tests; the workflow does not interpret it.
    /// </summary>
    public string Title { get; init; } = "";

    /// <summary>Left edge of the window in Virtual Desktop pixels.</summary>
    public int X { get; init; }

    /// <summary>Top edge of the window in Virtual Desktop pixels.</summary>
    public int Y { get; init; }

    /// <summary>Window width in pixels. Always positive.</summary>
    public uint Width { get; init; }

    /// <summary>Window height in pixels. Always positive.</summary>
    public uint Height { get; init; }
}

/// <summary>
/// Outcome of a Selected Window picker interaction that the overlay adapter
/// returns to the workflow. <see cref="WindowConfirmed"/> carries a confirmed
/// <see cref="WindowTarget"/>; <see cref="EmptyDesktopFallback"/> is the
/// click-on-empty-desktop case that the workflow routes to a Full Desktop
/// capture (including the taskbar). Cancellation (Escape) is signaled by a null
/// result from the adapter rather than an outcome, matching the Selection and
/// Selected Monitor overlay adapters.
/// </summary>
public enum WindowPickerOutcome
{
    /// <summary>
    /// The user clicked an eligible window. The target's HWND is captured by
    /// handle.
    /// </summary>
    WindowConfirmed,

    /// <summary>
    /// The user clicked empty desktop (no eligible window). The workflow
    /// captures Full Desktop including the taskbar.
    /// </summary>
    EmptyDesktopFallback,
}

/// <summary>
/// Result of a Selected Window picker interaction. Set by the overlay adapter
/// for both confirming outcomes; null (from the adapter) signals cancellation.
/// <see cref="Target"/> is set only when <see cref="Outcome"/> is
/// <see cref="WindowPickerOutcome.WindowConfirmed"/>.
/// </summary>
public sealed record WindowPickerResult
{
    /// <summary>
    /// Which confirming outcome the picker produced. Cancellation is signalled
    /// by a null <see cref="WindowPickerResult"/> from the adapter, not by an
    /// outcome value.
    /// </summary>
    public WindowPickerOutcome Outcome { get; init; }

    /// <summary>
    /// The confirmed window target. Set only for
    /// <see cref="WindowPickerOutcome.WindowConfirmed"/>; null for
    /// <see cref="WindowPickerOutcome.EmptyDesktopFallback"/>.
    /// </summary>
    public WindowTarget? Target { get; init; }
}

/// <summary>
/// Narrow adapter over the interactive Selected Window picker overlay.
/// Production shows a scrimmed Win32 layered window that highlights the hovered
/// window's full bounds and title; tests supply fakes that return a canned
/// window confirmation, an empty-desktop fallback, or cancellation. The overlay
/// adapter's only job is to return the confirming outcome (or cancellation) —
/// it must NOT perform Capture or own any post-capture state, because the
/// runtime workflow owns Capture, the Frame, and the preview transition. A
/// click on an eligible window yields
/// <see cref="WindowPickerOutcome.WindowConfirmed"/>; a click on empty desktop
/// yields <see cref="WindowPickerOutcome.EmptyDesktopFallback"/> (the workflow
/// routes that to a Full Desktop capture); Escape dismisses with a null result.
/// Invoked on the workflow caller's thread (the UI thread in production), since
/// the overlay runs a modal Win32 message loop.
/// </summary>
public interface IWindowPickerOverlayAdapter
{
    /// <summary>
    /// Shows the Selected Window picker overlay and returns the confirming
    /// outcome, or null if the user cancelled (Escape or overlay dismissal).
    /// Returning null must not produce a Frame or any delivery side effect.
    /// Implementations must not throw — surface unexpected failures as a null
    /// result so the workflow reports cancellation rather than crashing.
    /// </summary>
    Task<WindowPickerResult?> ShowAsync();
}

/// <summary>
/// Narrow adapter over presenting a captured Frame to the live preview. The
/// production implementation converts the Frame into a WinUI WriteableBitmap;
/// the session itself stays WinUI-free by talking to this interface.
/// </summary>
/// <typeparam name="TImage">
/// The display token the adapter produces — WriteableBitmap in production, an
/// arbitrary stand-in for tests.
/// </typeparam>
public interface IPreviewAdapter<TImage>
{
    /// <summary>
    /// Presents the supplied Frame to the preview path. Implementations must
    /// not throw for expected failures — surface them through
    /// PreviewPresentResult.Fail so the Frame stays retryable.
    /// </summary>
    PreviewPresentResult<TImage> Present(ContiguousBitmap frame);
}

/// <summary>
/// WinUI-free runtime workflow session. Owns Capture Mode routing, operation
/// state, the captured Frame, and the preview transition for the production
/// GUI. WinUI windows and code-behind are thin event/rendering adapters over
/// this session and must not duplicate workflow rules.
/// </summary>
/// <typeparam name="TImage">
/// The preview adapter's display token. Production instantiates
/// <c>CaptureWorkflowSession&lt;WriteableBitmap&gt;</c>; tests instantiate with
/// a stand-in type.
/// </typeparam>
public sealed class CaptureWorkflowSession<TImage>
{
    /// <summary>Domain Capture Mode label for the Full Desktop route.</summary>
    public const string FullDesktopMode = "Full Desktop";

    /// <summary>Domain Capture Mode label for the Active Window route.</summary>
    public const string ActiveWindowMode = "Active Window";

    /// <summary>Domain Capture Mode label for the Selection route.</summary>
    public const string SelectionMode = "Selection";

    /// <summary>Domain Capture Mode label for the Selected Monitor route.</summary>
    public const string SelectedMonitorMode = "Selected Monitor";

    /// <summary>Domain Capture Mode label for the Selected Window route.</summary>
    public const string SelectedWindowMode = "Selected Window";

    private readonly IWorkflowCaptureAdapter _capture;
    private readonly IPreviewAdapter<TImage> _preview;
    private readonly ISelectionOverlayAdapter _selectionOverlay;
    private readonly IMonitorPickerOverlayAdapter _monitorPickerOverlay;
    private readonly IWindowPickerOverlayAdapter _windowPickerOverlay;

    // Operation-state guard. 0 = idle, non-zero = an operation is in flight.
    // Manipulated only through Interlocked so concurrent Triggers are rejected
    // without locks. The WinUI layer maintains its own fast-path guard
    // (_isOperationRunning in MainWindow) so UI triggers don't queue work that
    // the session would immediately reject; the session's guard is the
    // authoritative concurrency contract for the workflow itself.
    private int _operationInProgress;

    // The Frame owned by the session after the most recent successful Capture.
    // Stays null until the first successful Capture; replaced on each success.
    // Production export still reads from CapturePreviewService's cache (a
    // transitional bridge); the export migration will move readers here so the
    // session becomes the canonical Frame owner.
    private ContiguousBitmap? _lastFrame;

    /// <summary>
    /// The Frame owned by the session after the most recent successful Capture.
    /// Null until a Capture succeeds. Preview failures do not clear it.
    /// </summary>
    public ContiguousBitmap? LastFrame => _lastFrame;

    /// <summary>
    /// Creates a workflow session bound to the supplied platform adapters.
    /// The Selection, Selected Monitor, and Selected Window overlay adapters
    /// are required even for routes that do not use them (Full Desktop, Active
    /// Window), so production wires every platform adapter exactly once at
    /// construction and tests inject fakes uniformly.
    /// </summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _selectionOverlay = selectionOverlay ?? throw new ArgumentNullException(nameof(selectionOverlay));
        _monitorPickerOverlay = monitorPickerOverlay ?? throw new ArgumentNullException(nameof(monitorPickerOverlay));
        _windowPickerOverlay = windowPickerOverlay ?? throw new ArgumentNullException(nameof(windowPickerOverlay));
    }

    /// <summary>
    /// Routes a Full Desktop Trigger through the production workflow: performs
    /// the real Capture, owns the resulting Frame, drives the preview
    /// transition, and exposes operation-in-progress and failure outcomes
    /// consistently. Never throws for expected failures.
    /// </summary>
    public Task<WorkflowResult<TImage>> CaptureFullDesktopAsync() =>
        RouteCaptureAsync(FullDesktopMode, _capture.CaptureFullDesktop);

    /// <summary>
    /// Routes an Active Window Trigger through the same production workflow as
    /// Full Desktop: captures the current eligible active window, owns the
    /// resulting Frame, drives the preview transition, and exposes
    /// operation-in-progress and failure outcomes consistently. An unavailable
    /// target (no eligible active window) is surfaced by the capture adapter as
    /// a CaptureFailed outcome, so no stale Frame or operation state is
    /// retained. Never throws for expected failures.
    /// </summary>
    public Task<WorkflowResult<TImage>> CaptureActiveWindowAsync() =>
        RouteCaptureAsync(ActiveWindowMode, _capture.CaptureActiveWindow);

    /// <summary>
    /// Routes a Selection Trigger through the production workflow. The
    /// interactive Selection overlay is shown first; on confirmation the
    /// workflow owns Capture of the returned geometry, the resulting Frame,
    /// and the preview transition. On cancellation (Escape or overlay
    /// dismissal) the workflow ends with <see cref="WorkflowStatus.Cancelled"/>
    /// — no Capture is performed, no Frame is produced, and no delivery side
    /// effects occur. Cross-monitor and negative-coordinate selections are
    /// preserved: the geometry's signed X/Y is forwarded to the capture
    /// adapter untouched. Never throws for expected failures.
    /// </summary>
    public async Task<WorkflowResult<TImage>> CaptureSelectionAsync()
    {
        if (!TryBeginOperation())
        {
            return WorkflowResultFor(SelectionMode, WorkflowStatus.OperationInProgress,
                error: "A capture is already in progress.");
        }

        var totalSw = Stopwatch.StartNew();
        try
        {
            // The overlay runs on the caller's thread (the UI thread in
            // production), where the modal Win32 message loop must live. The
            // adapter returns confirmed geometry or null for cancellation.
            var geometry = await _selectionOverlay.ShowAsync();

            if (geometry is null)
            {
                return WorkflowResultFor(SelectionMode, WorkflowStatus.Cancelled,
                    error: "Selection cancelled.",
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            // With confirmed geometry in hand, Capture runs off-thread just
            // like the immediate-capture routes — the adapter may block on
            // native pixel acquisition.
            var captureResult = await RunOffThread(() => _capture.CaptureSelection(geometry));
            if (!captureResult.Success)
            {
                return WorkflowResultFor(SelectionMode, WorkflowStatus.CaptureFailed,
                    error: captureResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            var frame = captureResult.Frame!;
            _lastFrame = frame;

            var previewResult = _preview.Present(frame);
            if (!previewResult.Success)
            {
                return WorkflowResultFor(SelectionMode, WorkflowStatus.PreviewFailed,
                    frame: frame,
                    dimensions: captureResult.Dimensions,
                    error: previewResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    displayMs: previewResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            return WorkflowResultFor(SelectionMode, WorkflowStatus.Succeeded,
                frame: frame,
                dimensions: captureResult.Dimensions,
                previewImage: previewResult.Image,
                captureMs: captureResult.ElapsedMs,
                displayMs: previewResult.ElapsedMs,
                totalMs: totalSw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Routes a Selected Monitor Trigger through the production workflow. The
    /// interactive monitor picker overlay is shown first; on confirmation the
    /// workflow owns Capture of the returned monitor bounds, the resulting
    /// Frame, and the preview transition. On cancellation (Escape or overlay
    /// dismissal) the workflow ends with <see cref="WorkflowStatus.Cancelled"/>
    /// — no Capture is performed, no Frame is produced, and no delivery side
    /// effects occur. Mixed-DPI and negative-coordinate monitor layouts are
    /// preserved: the target's signed X/Y is forwarded to the capture adapter
    /// untouched. Never throws for expected failures.
    /// </summary>
    public async Task<WorkflowResult<TImage>> CaptureSelectedMonitorAsync()
    {
        if (!TryBeginOperation())
        {
            return WorkflowResultFor(SelectedMonitorMode, WorkflowStatus.OperationInProgress,
                error: "A capture is already in progress.");
        }

        var totalSw = Stopwatch.StartNew();
        try
        {
            // The picker runs on the caller's thread (the UI thread in
            // production), where the modal Win32 message loop must live. The
            // adapter returns the confirmed monitor target or null for
            // cancellation (gap clicks keep the overlay open inside the adapter).
            var target = await _monitorPickerOverlay.ShowAsync();

            if (target is null)
            {
                return WorkflowResultFor(SelectedMonitorMode, WorkflowStatus.Cancelled,
                    error: "Selected Monitor cancelled.",
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            // With a confirmed monitor target in hand, Capture runs off-thread
            // just like the other routes — the adapter may block on native
            // pixel acquisition.
            var captureResult = await RunOffThread(() => _capture.CaptureMonitor(target));
            if (!captureResult.Success)
            {
                return WorkflowResultFor(SelectedMonitorMode, WorkflowStatus.CaptureFailed,
                    error: captureResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            var frame = captureResult.Frame!;
            _lastFrame = frame;

            var previewResult = _preview.Present(frame);
            if (!previewResult.Success)
            {
                return WorkflowResultFor(SelectedMonitorMode, WorkflowStatus.PreviewFailed,
                    frame: frame,
                    dimensions: captureResult.Dimensions,
                    error: previewResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    displayMs: previewResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            return WorkflowResultFor(SelectedMonitorMode, WorkflowStatus.Succeeded,
                frame: frame,
                dimensions: captureResult.Dimensions,
                previewImage: previewResult.Image,
                captureMs: captureResult.ElapsedMs,
                displayMs: previewResult.ElapsedMs,
                totalMs: totalSw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Routes a Selected Window Trigger through the production workflow. The
    /// interactive window picker overlay is shown first; on confirmation the
    /// workflow owns Capture of the returned target, the resulting Frame, and
    /// the preview transition. The picker's three outcomes map as follows:
    /// cancellation (Escape or overlay dismissal) ends the workflow with
    /// <see cref="WorkflowStatus.Cancelled"/>; a confirmed window is captured by
    /// handle; an empty-desktop click is routed to a Full Desktop capture
    /// (including the taskbar). Mixed-DPI, cross-monitor, and negative-
    /// coordinate window bounds are preserved. Never throws for expected
    /// failures.
    /// </summary>
    public async Task<WorkflowResult<TImage>> CaptureSelectedWindowAsync()
    {
        if (!TryBeginOperation())
        {
            return WorkflowResultFor(SelectedWindowMode, WorkflowStatus.OperationInProgress,
                error: "A capture is already in progress.");
        }

        var totalSw = Stopwatch.StartNew();
        try
        {
            // The picker runs on the caller's thread (the UI thread in
            // production), where the modal Win32 message loop must live. The
            // adapter returns a confirming outcome (window or empty-desktop
            // fallback) or null for cancellation (Escape).
            var result = await _windowPickerOverlay.ShowAsync();

            if (result is null)
            {
                return WorkflowResultFor(SelectedWindowMode, WorkflowStatus.Cancelled,
                    error: "Selected Window cancelled.",
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            // A confirmed window is captured by handle; an empty-desktop click
            // routes to Full Desktop (including the taskbar). The route label
            // stays "Selected Window" in both cases — the user invoked this
            // route, and the fallback is an internal capture-path detail.
            var captureResult = result.Outcome == WindowPickerOutcome.EmptyDesktopFallback
                ? await RunOffThread(() => _capture.CaptureFullDesktop())
                : await RunOffThread(() => _capture.CaptureWindow(result.Target!));

            if (!captureResult.Success)
            {
                return WorkflowResultFor(SelectedWindowMode, WorkflowStatus.CaptureFailed,
                    error: captureResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            var frame = captureResult.Frame!;
            _lastFrame = frame;

            var previewResult = _preview.Present(frame);
            if (!previewResult.Success)
            {
                return WorkflowResultFor(SelectedWindowMode, WorkflowStatus.PreviewFailed,
                    frame: frame,
                    dimensions: captureResult.Dimensions,
                    error: previewResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    displayMs: previewResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            return WorkflowResultFor(SelectedWindowMode, WorkflowStatus.Succeeded,
                frame: frame,
                dimensions: captureResult.Dimensions,
                previewImage: previewResult.Image,
                captureMs: captureResult.ElapsedMs,
                displayMs: previewResult.ElapsedMs,
                totalMs: totalSw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Shared Capture Mode routing for an immediate-capture Trigger (no Target
    /// Selection). Owns the operation guard, Capture, Frame ownership, and the
    /// preview transition. Both Full Desktop and Active Window route through
    /// this so their behavior — and their test coverage — stays symmetric at
    /// the seam.
    /// </summary>
    private async Task<WorkflowResult<TImage>> RouteCaptureAsync(
        string mode, Func<CaptureFrameResult> captureFunc)
    {
        if (!TryBeginOperation())
        {
            return WorkflowResultFor(mode, WorkflowStatus.OperationInProgress,
                error: "A capture is already in progress.");
        }

        var totalSw = Stopwatch.StartNew();
        try
        {
            // Capture runs on the thread pool — the adapter may block on native
            // pixel acquisition. The session's caller (the UI thread, in
            // production) is freed for the duration.
            var captureResult = await RunOffThread(captureFunc);
            if (!captureResult.Success)
            {
                return WorkflowResultFor(mode, WorkflowStatus.CaptureFailed,
                    error: captureResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            var frame = captureResult.Frame!;
            _lastFrame = frame;

            // Preview runs on the caller's thread. In production this is the UI
            // thread, which is required for WriteableBitmap construction. The
            // adapter's TImage-aware result keeps the session WinUI-free.
            var previewResult = _preview.Present(frame);
            if (!previewResult.Success)
            {
                // Frame is preserved for retry — see WorkflowResult.Frame doc.
                return WorkflowResultFor(mode, WorkflowStatus.PreviewFailed,
                    frame: frame,
                    dimensions: captureResult.Dimensions,
                    error: previewResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    displayMs: previewResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            return WorkflowResultFor(mode, WorkflowStatus.Succeeded,
                frame: frame,
                dimensions: captureResult.Dimensions,
                previewImage: previewResult.Image,
                captureMs: captureResult.ElapsedMs,
                displayMs: previewResult.ElapsedMs,
                totalMs: totalSw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Attempts to enter an operation. Returns false if one is already in
    /// flight, so concurrent Triggers are rejected without locks.
    /// </summary>
    private bool TryBeginOperation() =>
        Interlocked.CompareExchange(ref _operationInProgress, 1, 0) == 0;

    /// <summary>
    /// Releases the operation guard. Safe to call from finally even when the
    /// guard was never acquired (no-op in that case).
    /// </summary>
    private void EndOperation() =>
        Interlocked.Exchange(ref _operationInProgress, 0);

    /// <summary>
    /// Runs an adapter call on the thread pool. Capture adapter implementations
    /// are permitted to block on native pixel acquisition; offloading keeps
    /// the WinUI thread free.
    /// </summary>
    private static Task<T> RunOffThread<T>(Func<T> work) => Task.Run(work);

    private static WorkflowResult<TImage> WorkflowResultFor(
        string mode,
        WorkflowStatus status,
        ContiguousBitmap? frame = null,
        string dimensions = "",
        TImage? previewImage = default,
        string? error = null,
        double captureMs = 0,
        double displayMs = 0,
        double totalMs = 0) =>
        new()
        {
            Status = status,
            Mode = mode,
            Dimensions = dimensions,
            Frame = frame,
            PreviewImage = previewImage,
            Error = error,
            CaptureMs = captureMs,
            DisplayMs = displayMs,
            TotalMs = totalMs,
        };
}
