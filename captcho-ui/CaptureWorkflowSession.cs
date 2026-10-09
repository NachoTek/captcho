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
    /// Capture succeeded but Annotation could not be presented.
    /// The source Frame is preserved so presentation can be retried without
    /// another Capture.
    /// </summary>
    AnnotationFailed,

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

    /// <summary>
    /// Report of the configured automatic delivery actions that ran after
    /// Annotation confirmation (issue #47). Null when no automatic action was
    /// enabled, when Annotation (or Target Selection) was cancelled, or when
    /// the workflow failed before the delivery point — cancellation and
    /// failure create no delivery side effects.
    /// </summary>
    public AutomaticExportReport? AutomaticExport { get; init; }

    /// <summary>
    /// The Capture Mode this Trigger actually captured, set when the capture
    /// succeeded (after any Target Selection mode switch). Drives the
    /// last-Capture-Mode recording for the launch behavior (issue #53).
    /// </summary>
    public CaptureMode? TriggeredCaptureMode { get; init; }

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
    /// Captures the complete Virtual Desktop into a Frame. The effective
    /// CaptureOptions for the Full Desktop mode are composed by the workflow
    /// session from committed defaults and session overrides, then forwarded;
    /// only the mouse-pointer flag is applicable to a Full Desktop Frame (spec
    /// #34). Implementations must not throw for expected failures — surface
    /// them through CaptureFrameResult.Fail instead.
    /// </summary>
    CaptureFrameResult CaptureFullDesktop(CaptureOptions options);

    /// <summary>
    /// Captures the current eligible active (foreground) window into a Frame.
    /// The effective CaptureOptions for the Active Window mode are composed by
    /// the workflow session from committed defaults and session overrides, then
    /// forwarded; pointer, decorations, and shadow are each applicable to a
    /// window Capture (spec #34). Implementations must not throw for expected
    /// failures — surface them through CaptureFrameResult.Fail instead
    /// (including the "no eligible active window" case, so the workflow can
    /// report it as CaptureFailed without retaining stale Frame or operation
    /// state).
    /// </summary>
    CaptureFrameResult CaptureActiveWindow(CaptureOptions options);

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
    /// Captures a single Selected Window into a Frame with the effective
    /// CaptureOptions for the Selected Window mode. The target carries the
    /// window's stable HWND (the identity the picker confirmed), its title, and
    /// its full Virtual Desktop bounds (which may cross monitor boundaries and
    /// sit at negative coordinates). Pointer, decorations, and shadow are each
    /// applicable to a window Capture; the workflow session composes the
    /// options from committed defaults and session overrides and forwards them
    /// here. Capture is performed by handle so the frozen Frame matches the
    /// window the user clicked even if later window movement would have moved
    /// its bounds. Implementations must not throw for expected failures —
    /// surface them through CaptureFrameResult.Fail instead (including the
    /// "window vanished before capture" case, so the workflow can report it as
    /// CaptureFailed without retaining stale state).
    /// </summary>
    CaptureFrameResult CaptureWindow(WindowTarget target, CaptureOptions options);
}

/// <summary>
/// Result returned by an interactive Target Selection overlay. A result either
/// confirms a target or requests another Capture Mode; null still represents
/// cancellation so Escape remains uniform across all overlays.
/// </summary>
public sealed class TargetSelectionResult<TTarget>
{
    private TargetSelectionResult(TTarget? target, CaptureMode? requestedMode)
    {
        Target = target;
        RequestedMode = requestedMode;
    }

    public TTarget? Target { get; }
    public CaptureMode? RequestedMode { get; }

    public static TargetSelectionResult<TTarget> Confirmed(TTarget target) =>
        new(target ?? throw new ArgumentNullException(nameof(target)), null);

    public static TargetSelectionResult<TTarget> RouteTo(CaptureMode mode) =>
        new(default, mode);
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
    Task<TargetSelectionResult<SelectionGeometry>?> ShowAsync(SelectionGeometry? initialGeometry);
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
    Task<TargetSelectionResult<MonitorTarget>?> ShowAsync();
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
    Task<TargetSelectionResult<WindowPickerResult>?> ShowAsync();
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

/// <summary>Terminal outcome of one post-capture Annotation interaction.</summary>
public enum AnnotationOutcome
{
    Confirmed,
    Cancelled,
    Failed,
}

/// <summary>
/// Why the Workflow Session requested an application exit (issue #54). The
/// exit decision is workflow-owned state; the WinUI layer only turns the
/// request into shutdown.
/// </summary>
public enum WorkflowExitReason
{
    /// <summary>
    /// A confirmed Capture's delivery completed — the Annotation overlay was
    /// dismissed and every configured delivery action (manual or automatic)
    /// finished successfully.
    /// </summary>
    DeliveryCompleted,
}

/// <summary>
/// WinUI-free result returned by Annotation. Confirmation carries
/// the frozen composed Frame; cancellation carries no Frame; failure preserves
/// an error for user-visible reporting.
/// </summary>
public sealed class AnnotationPresentResult
{
    public AnnotationOutcome Outcome { get; }
    public ContiguousBitmap? Frame { get; }
    public string? Error { get; }
    public double ElapsedMs { get; }

    private AnnotationPresentResult(
        AnnotationOutcome outcome,
        ContiguousBitmap? frame,
        string? error,
        double elapsedMs)
    {
        Outcome = outcome;
        Frame = frame;
        Error = error;
        ElapsedMs = elapsedMs;
    }

    public static AnnotationPresentResult Confirmed(ContiguousBitmap frame, double elapsedMs = 0) =>
        new(AnnotationOutcome.Confirmed, frame ?? throw new ArgumentNullException(nameof(frame)), null, elapsedMs);

    public static AnnotationPresentResult Cancelled(double elapsedMs = 0) =>
        new(AnnotationOutcome.Cancelled, null, null, elapsedMs);

    public static AnnotationPresentResult Fail(string error, double elapsedMs = 0) =>
        new(AnnotationOutcome.Failed, null, error ?? "Annotation presentation failed.", elapsedMs);
}

/// <summary>
/// Presents the source Frame in full-screen Annotation and returns
/// either its composed Frame, cancellation, or a presentation failure.
/// </summary>
public interface IAnnotationOverlayAdapter
{
    Task<AnnotationPresentResult> ShowAsync(ContiguousBitmap sourceFrame);
}

/// <summary>
/// Optional Annotation adapter capability for overlays that consume the committed
/// toolbar defaults when a session opens.
/// </summary>
public interface IAnnotationOverlayStateAdapter : IAnnotationOverlayAdapter
{
    Task<AnnotationPresentResult> ShowAsync(
        ContiguousBitmap sourceFrame,
        AnnotationToolState initialToolState);
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
    private readonly IAnnotationOverlayAdapter _annotationOverlay;
    private readonly Func<bool> _annotationEnabled;
    private readonly Func<AnnotationToolState> _annotationToolState;
    private readonly IOcrEngine _ocrEngine;
    private readonly Func<string?> _ocrLanguageTag;
    private readonly IQrScanner _qrScanner;
    private readonly IWorkflowExportAdapter _export;
    private readonly ISaveAsDialogAdapter _saveAsDialog;
    private readonly IWorkflowDeliveryAdapter _delivery;
    private readonly Func<AutomaticExportSettings> _automaticExport;

    // Exit-after-delivery (issue #54). The setting is read per-evaluation so
    // committed Settings changes apply without rebuilding the session; the
    // recorder is the WinUI shutdown seam. Both default to inert values so
    // every pre-#54 construction keeps its behavior.
    private readonly Func<bool> _exitAfterDelivery;
    private readonly Action<WorkflowExitReason>? _exitRecorder;

    // Exit-decision state for the current Capture: which configured
    // automatic actions are still pending since the Annotation overlay was
    // dismissed, and whether the exit condition was already satisfied (the
    // per-Capture exactly-once guard). Cleared when a new Frame is owned.
    private bool _exitPending;
    private bool _exitSatisfied;

    // The configured automatic actions that failed at the last automatic
    // run of the current Capture — the outstanding obligation a later manual
    // success must complete before the exit can fire. Cleared when a new
    // Frame is owned.
    private readonly HashSet<WorkflowExportAction> _pendingFailedAutomatic = new();

    // Last-Capture-Mode recording for the launch behavior (issue #53). When
    // wired, every successful Capture records the mode that actually ran
    // (after any Target Selection mode switch); the state itself decides —
    // per capture — whether the configured launch behavior needs the value.
    private readonly ILaunchBehaviorRecorder? _launchRecorder;

    // Workflow-side Capture-options override holder. Composed committed defaults
    // with per-Capture-Mode session overrides to produce the effective options
    // each route forwards to the capture adapter (spec #34/#35). Held as a
    // shared instance so the interactive overlays can apply per-mode overrides
    // through the same object the workflow reads.
    private readonly SessionCaptureOptions _captureOptions;
    private readonly RememberedSelectionState _rememberedSelection;

    // Operation-state guard. 0 = idle, non-zero = an operation is in flight.
    // Manipulated only through Interlocked so concurrent Triggers are rejected
    // without locks. The WinUI layer maintains its own fast-path guard
    // (_isOperationRunning in MainWindow) so UI triggers don't queue work that
    // the session would immediately reject; the session's guard is the
    // authoritative concurrency contract for the workflow itself.
    private int _operationInProgress;

    // The Frame owned by the session after the most recent successful Capture.
    // Stays null until the first successful Capture; replaced on each success.
    // Export actions read from this field — the session is the canonical Frame
    // owner for Save, Save As, Copy Frame, and Copy Path (issue #44).
    private ContiguousBitmap? _lastFrame;

    // The one default saved-file identity for the current Capture. Computed
    // by the first successful Save (or the export adapter's automatic-save
    // path) and reused by every later Save so repeats refer to the same file
    // instead of creating accidental duplicates. Reset when a new Frame is
    // captured or adopted.
    private string? _defaultSavedFilePath;

    // The most recently saved file of any kind (default or Save As). Copy
    // Path reads this once a valid saved file exists.
    private string? _lastSavedFilePath;

    /// <summary>
    /// The Frame owned by the session after the most recent successful Capture.
    /// Null until a Capture succeeds. Preview failures do not clear it.
    /// </summary>
    public ContiguousBitmap? LastFrame => _lastFrame;

    /// <summary>
    /// The workflow's Capture-options override holder. Committed defaults are
    /// read from the AppSettings the holder was constructed from; per-Capture-Mode
    /// overrides applied through this object take precedence for the session.
    /// Exposed so the interactive overlays (Target Selection) can apply session
    /// overrides through the same instance the workflow reads (spec #35).
    /// </summary>
    public SessionCaptureOptions SessionOptions => _captureOptions;

    /// <summary>
    /// The one default saved-file identity for the current Capture — the path
    /// the first successful Save wrote. Null until Save succeeds, after a new
    /// Capture, or after <see cref="AdoptFrame"/>. Repeating Save refers to
    /// this file; Save As never replaces it.
    /// </summary>
    public string? DefaultSavedFilePath => _defaultSavedFilePath;

    /// <summary>
    /// The most recently saved file for the current Capture — the default
    /// identity or the latest Save As choice. Null until some save succeeds
    /// for this Capture. Copy Path copies this once it is valid.
    /// </summary>
    public string? LastSavedFilePath => _lastSavedFilePath;

    /// <summary>
    /// Whether a saved file exists as workflow state for the current Capture.
    /// Copy Path is available only while this is true.
    /// </summary>
    public bool HasSavedFile => !string.IsNullOrEmpty(_lastSavedFilePath);

    /// <summary>
    /// Creates a workflow session bound to the supplied platform adapters and a
    /// fresh Capture-options holder over <see cref="AppSettings.WithDefaults"/>.
    /// Equivalent to the six-argument constructor with
    /// <c>new SessionCaptureOptions(AppSettings.WithDefaults())</c> — kept for
    /// callers and tests that do not supply a committed AppSettings. Production
    /// wires the real committed AppSettings through the six-argument constructor.
    /// </summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay)
        : this(capture, preview, selectionOverlay, monitorPickerOverlay, windowPickerOverlay,
               new SessionCaptureOptions(AppSettings.WithDefaults()),
               RememberedSelectionState.Disabled)
    {
    }

    /// <summary>
    /// Creates a workflow session bound to the supplied platform adapters and
    /// Capture-options holder. The Selection, Selected Monitor, and Selected
    /// Window overlay adapters are required even for routes that do not use
    /// them (Full Desktop, Active Window), so production wires every platform
    /// adapter exactly once at construction and tests inject fakes uniformly.
    /// <paramref name="captureOptions"/> is retained as the shared override
    /// holder so the interactive overlays can apply per-mode overrides through
    /// the same instance.
    /// </summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay,
        SessionCaptureOptions captureOptions)
        : this(capture, preview, selectionOverlay, monitorPickerOverlay, windowPickerOverlay,
               captureOptions, RememberedSelectionState.Disabled)
    {
    }

    /// <summary>Creates a workflow with configured remembered-Selection behavior.</summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay,
        SessionCaptureOptions captureOptions,
        RememberedSelectionState rememberedSelection)
        : this(capture, preview, selectionOverlay, monitorPickerOverlay, windowPickerOverlay,
               captureOptions, rememberedSelection, DisabledAnnotationOverlayAdapter.Instance,
               static () => false)
    {
    }

    /// <summary>Creates a workflow with the post-capture Annotation gate.</summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay,
        SessionCaptureOptions captureOptions,
        RememberedSelectionState rememberedSelection,
        IAnnotationOverlayAdapter annotationOverlay,
        Func<bool> annotationEnabled)
        : this(capture, preview, selectionOverlay, monitorPickerOverlay, windowPickerOverlay,
               captureOptions, rememberedSelection, annotationOverlay, annotationEnabled,
               static () => AnnotationToolState.WithDefaults())
    {
    }

    /// <summary>
    /// Creates a workflow with Annotation and committed toolbar defaults.
    /// OCR defaults to the engine-less null engine and the Export actions
    /// default to unconfigured adapters; production wires both through the
    /// thirteen-argument constructor.
    /// </summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay,
        SessionCaptureOptions captureOptions,
        RememberedSelectionState rememberedSelection,
        IAnnotationOverlayAdapter annotationOverlay,
        Func<bool> annotationEnabled,
        Func<AnnotationToolState> annotationToolState)
        : this(capture, preview, selectionOverlay, monitorPickerOverlay, windowPickerOverlay,
               captureOptions, rememberedSelection, annotationOverlay, annotationEnabled,
               annotationToolState, NullOcrEngine.Instance, static () => null,
               UnconfiguredWorkflowExportAdapter.Instance,
               UnavailableSaveAsDialogAdapter.Instance,
               static () => AutomaticExportSettings.WithDefaults())
    {
    }

    /// <summary>
    /// Creates a workflow with the OCR engine and the persisted OCR language
    /// selection the Recognize Text Trigger resolves against the installed
    /// language packs. The delegates are read per-recognition so committed
    /// Settings changes apply without rebuilding the session. Export actions
    /// default to unconfigured adapters.
    /// </summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay,
        SessionCaptureOptions captureOptions,
        RememberedSelectionState rememberedSelection,
        IAnnotationOverlayAdapter annotationOverlay,
        Func<bool> annotationEnabled,
        Func<AnnotationToolState> annotationToolState,
        IOcrEngine ocrEngine,
        Func<string?> ocrLanguageTag)
        : this(capture, preview, selectionOverlay, monitorPickerOverlay, windowPickerOverlay,
               captureOptions, rememberedSelection, annotationOverlay, annotationEnabled,
               annotationToolState, ocrEngine, ocrLanguageTag,
               UnconfiguredWorkflowExportAdapter.Instance,
               UnavailableSaveAsDialogAdapter.Instance,
               static () => AutomaticExportSettings.WithDefaults())
    {
    }

    /// <summary>
    /// Creates a workflow with the Export and delivery actions wired
    /// (issues #44/#46). The session owns the default saved-file identity,
    /// Copy Path/Open With/Share availability, and the operation guard
    /// across all Export actions; the adapters supply only PNG writing,
    /// clipboard placement, the Save As dialog, and Windows delivery. OCR
    /// defaults to the engine-less null engine, QR to the scanner-less null
    /// scanner, and the automatic Export source to defaults.
    /// </summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay,
        SessionCaptureOptions captureOptions,
        RememberedSelectionState rememberedSelection,
        IAnnotationOverlayAdapter annotationOverlay,
        Func<bool> annotationEnabled,
        Func<AnnotationToolState> annotationToolState,
        IWorkflowExportAdapter export,
        ISaveAsDialogAdapter saveAsDialog,
        IWorkflowDeliveryAdapter delivery)
        : this(capture, preview, selectionOverlay, monitorPickerOverlay, windowPickerOverlay,
               captureOptions, rememberedSelection, annotationOverlay, annotationEnabled,
               annotationToolState, NullOcrEngine.Instance, static () => null, NullQrScanner.Instance,
               export, saveAsDialog,
               static () => AutomaticExportSettings.WithDefaults(),
               launchRecorder: null,
               delivery)
    {
    }

    /// <summary>
    /// Creates a workflow with the Export actions wired (issue #44). The
    /// session owns the default saved-file identity, Copy Path availability,
    /// and the operation guard across Save, Save As, Copy Frame, and Copy
    /// Path; the adapters supply only PNG writing, clipboard placement, and
    /// the Save As dialog. OCR defaults to the engine-less null engine.
    /// Delivery actions (issue #46) stay unwired — they fail cleanly through
    /// the null-object delivery adapter.
    /// </summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay,
        SessionCaptureOptions captureOptions,
        RememberedSelectionState rememberedSelection,
        IAnnotationOverlayAdapter annotationOverlay,
        Func<bool> annotationEnabled,
        Func<AnnotationToolState> annotationToolState,
        IWorkflowExportAdapter export,
        ISaveAsDialogAdapter saveAsDialog)
        : this(capture, preview, selectionOverlay, monitorPickerOverlay, windowPickerOverlay,
               captureOptions, rememberedSelection, annotationOverlay, annotationEnabled,
               annotationToolState, NullOcrEngine.Instance, static () => null,
               export, saveAsDialog,
               static () => AutomaticExportSettings.WithDefaults())
    {
    }

    /// <summary>
    /// Creates a workflow with the OCR engine, the persisted OCR language
    /// selection, the Export actions, and the configured automatic Export
    /// source all wired — QR scanning defaults to the scanner-less null
    /// scanner. The automatic Export settings are read through
    /// <paramref name="automaticExport"/> when each Capture completes, so
    /// committed Settings changes take effect on the next Capture without
    /// reconstructing the session. The optional
    /// <paramref name="launchRecorder"/> wires last-Capture-Mode recording
    /// for the launch behavior (issue #53); when omitted, captures are not
    /// recorded. The optional <paramref name="delivery"/> wires the Open
    /// With/Share delivery actions (issue #46); when omitted, they fail
    /// cleanly through the null-object delivery adapter. The optional
    /// <paramref name="exitRecorder"/> and <paramref name="exitAfterDelivery"/>
    /// wire the exit-after-delivery Behavior Setting (issue #54); when
    /// omitted, the session never requests an exit.
    /// </summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay,
        SessionCaptureOptions captureOptions,
        RememberedSelectionState rememberedSelection,
        IAnnotationOverlayAdapter annotationOverlay,
        Func<bool> annotationEnabled,
        Func<AnnotationToolState> annotationToolState,
        IOcrEngine ocrEngine,
        Func<string?> ocrLanguageTag,
        IWorkflowExportAdapter export,
        ISaveAsDialogAdapter saveAsDialog,
        Func<AutomaticExportSettings> automaticExport,
        ILaunchBehaviorRecorder? launchRecorder = null,
        IWorkflowDeliveryAdapter? delivery = null,
        Action<WorkflowExitReason>? exitRecorder = null,
        Func<bool>? exitAfterDelivery = null)
        : this(capture, preview, selectionOverlay, monitorPickerOverlay, windowPickerOverlay,
               captureOptions, rememberedSelection, annotationOverlay, annotationEnabled,
               annotationToolState, ocrEngine, ocrLanguageTag, NullQrScanner.Instance,
               export, saveAsDialog, automaticExport, launchRecorder, delivery,
               exitRecorder, exitAfterDelivery)
    {
    }

    /// <summary>
    /// Creates a workflow with the OCR engine, the persisted OCR language
    /// selection, the QR scanner, the Export actions, and the configured
    /// automatic Export source all wired — the constructor production uses.
    /// The automatic Export settings are read through
    /// <paramref name="automaticExport"/> when each Capture completes, so
    /// committed Settings changes take effect on the next Capture without
    /// reconstructing the session. The optional
    /// <paramref name="launchRecorder"/> wires last-Capture-Mode recording
    /// for the launch behavior (issue #53); when omitted, captures are not
    /// recorded. The optional <paramref name="delivery"/> wires the Open
    /// With/Share delivery actions (issue #46); when omitted, they fail
    /// cleanly through the null-object delivery adapter. The optional
    /// <paramref name="exitRecorder"/> and <paramref name="exitAfterDelivery"/>
    /// wire the exit-after-delivery Behavior Setting (issue #54): when the
    /// setting is enabled, a confirmed Capture's completed delivery raises
    /// exactly one exit request; when omitted, the session never requests an
    /// exit.
    /// </summary>
    public CaptureWorkflowSession(
        IWorkflowCaptureAdapter capture,
        IPreviewAdapter<TImage> preview,
        ISelectionOverlayAdapter selectionOverlay,
        IMonitorPickerOverlayAdapter monitorPickerOverlay,
        IWindowPickerOverlayAdapter windowPickerOverlay,
        SessionCaptureOptions captureOptions,
        RememberedSelectionState rememberedSelection,
        IAnnotationOverlayAdapter annotationOverlay,
        Func<bool> annotationEnabled,
        Func<AnnotationToolState> annotationToolState,
        IOcrEngine ocrEngine,
        Func<string?> ocrLanguageTag,
        IQrScanner qrScanner,
        IWorkflowExportAdapter export,
        ISaveAsDialogAdapter saveAsDialog,
        Func<AutomaticExportSettings> automaticExport,
        ILaunchBehaviorRecorder? launchRecorder = null,
        IWorkflowDeliveryAdapter? delivery = null,
        Action<WorkflowExitReason>? exitRecorder = null,
        Func<bool>? exitAfterDelivery = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _selectionOverlay = selectionOverlay ?? throw new ArgumentNullException(nameof(selectionOverlay));
        _monitorPickerOverlay = monitorPickerOverlay ?? throw new ArgumentNullException(nameof(monitorPickerOverlay));
        _windowPickerOverlay = windowPickerOverlay ?? throw new ArgumentNullException(nameof(windowPickerOverlay));
        _captureOptions = captureOptions ?? throw new ArgumentNullException(nameof(captureOptions));
        _rememberedSelection = rememberedSelection ?? throw new ArgumentNullException(nameof(rememberedSelection));
        _annotationOverlay = annotationOverlay ?? throw new ArgumentNullException(nameof(annotationOverlay));
        _annotationEnabled = annotationEnabled ?? throw new ArgumentNullException(nameof(annotationEnabled));
        _annotationToolState = annotationToolState ?? throw new ArgumentNullException(nameof(annotationToolState));
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _ocrLanguageTag = ocrLanguageTag ?? throw new ArgumentNullException(nameof(ocrLanguageTag));
        _qrScanner = qrScanner ?? throw new ArgumentNullException(nameof(qrScanner));
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _saveAsDialog = saveAsDialog ?? throw new ArgumentNullException(nameof(saveAsDialog));
        _automaticExport = automaticExport ?? throw new ArgumentNullException(nameof(automaticExport));
        _launchRecorder = launchRecorder;
        _delivery = delivery ?? UnconfiguredWorkflowDeliveryAdapter.Instance;
        _exitRecorder = exitRecorder;
        _exitAfterDelivery = exitAfterDelivery ?? (static () => false);
    }

    /// <summary>
    /// Routes a Full Desktop Trigger through the production workflow: composes
    /// the effective CaptureOptions (committed defaults overlaid with any
    /// session override for Full Desktop), performs the real Capture, owns the
    /// resulting Frame, drives the preview transition, and exposes
    /// operation-in-progress and failure outcomes consistently. Only the
    /// mouse-pointer flag is applicable to a Full Desktop Frame (spec #34).
    /// Never throws for expected failures.
    /// </summary>
    public Task<WorkflowResult<TImage>> CaptureFullDesktopAsync() =>
        CaptureModeAsync(CaptureMode.FullDesktop);

    /// <summary>
    /// Routes an Active Window Trigger through the same production workflow as
    /// Full Desktop: composes the effective CaptureOptions (committed defaults
    /// overlaid with any session override for Active Window — pointer,
    /// decorations, and shadow are each applicable), captures the current
    /// eligible active window, owns the resulting Frame, drives the preview
    /// transition, and exposes operation-in-progress and failure outcomes
    /// consistently. An unavailable target (no eligible active window) is
    /// surfaced by the capture adapter as a CaptureFailed outcome, so no stale
    /// Frame or operation state is retained. Never throws for expected failures.
    /// </summary>
    public Task<WorkflowResult<TImage>> CaptureActiveWindowAsync() =>
        CaptureModeAsync(CaptureMode.ActiveWindow);

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
    public Task<WorkflowResult<TImage>> CaptureSelectionAsync() =>
        CaptureModeAsync(CaptureMode.Selection);

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
    public Task<WorkflowResult<TImage>> CaptureSelectedMonitorAsync() =>
        CaptureModeAsync(CaptureMode.SelectedMonitor);

    /// <summary>
    /// Routes a Selected Window Trigger through the production workflow. The
    /// interactive window picker overlay is shown first; on confirmation the
    /// workflow owns Capture of the returned target, the resulting Frame,
    /// and the preview transition. The picker's three outcomes map as follows:
    /// cancellation (Escape or overlay dismissal) ends the workflow with
    /// <see cref="WorkflowStatus.Cancelled"/>; a confirmed window is captured by
    /// handle; an empty-desktop click is routed to a Full Desktop capture
    /// (including the taskbar). Mixed-DPI, cross-monitor, and negative-
    /// coordinate window bounds are preserved. Never throws for expected failures.
    /// </summary>
    public Task<WorkflowResult<TImage>> CaptureSelectedWindowAsync() =>
        CaptureModeAsync(CaptureMode.SelectedWindow);

    /// <summary>
    /// Routes a Recognize Text Trigger through the production workflow. OCR
    /// is a post-capture workflow feature (not an Export): it operates on the
    /// in-memory Frame the session already owns — no file is written — and
    /// surfaces distinct Recognized-text, no-text, no-Frame,
    /// unsupported-language, engine-failure, and operation-in-progress
    /// outcomes through <see cref="OcrResult"/> (spec #49). The persisted
    /// language selection is resolved against the installed OCR language
    /// packs before the engine runs, so a removed pack produces a retryable
    /// unsupported-language error instead of a silent fallback. Every
    /// retryable outcome preserves the Frame: a retry re-recognizes the same
    /// pixels without another Capture. Runs under the same operation guard as
    /// Capture so a Trigger that arrives mid-recognition is rejected rather
    /// than queued. Never throws for expected failures.
    /// </summary>
    public async Task<OcrResult> RecognizeTextAsync()
    {
        if (!TryBeginOperation())
            return OcrResult.Busy();

        try
        {
            var frame = _lastFrame;
            if (frame is null)
                return OcrResult.NoFrame();

            var installed = _ocrEngine.GetAvailableLanguages();
            var selectedTag = _ocrLanguageTag();
            var effectiveTag = OcrLanguageResolver.EffectiveTag(selectedTag, installed);
            if (!string.IsNullOrWhiteSpace(selectedTag) && effectiveTag is null)
                return OcrResult.Unsupported(selectedTag);

            return await _ocrEngine.RecognizeAsync(frame, effectiveTag);
        }
        catch (Exception ex)
        {
            // The engine contract says never-throw, but a defensive catch
            // keeps an unexpected adapter failure retryable instead of
            // crashing the workflow caller.
            return OcrResult.Fail($"Text recognition failed: {ex.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Routes a Scan QR Trigger through the production workflow. QR scanning
    /// is a post-capture workflow feature (not an Export): it operates on the
    /// in-memory Frame the session already owns — no file is created or
    /// required — and surfaces distinct found, no-code, no-Frame,
    /// scanner-failure, and operation-in-progress outcomes through
    /// <see cref="QrScanResult"/> (spec #48). Found carries every decoded
    /// value so one or many codes are returned without silently dropping
    /// valid results. Every retryable outcome preserves the Frame: a retry
    /// re-scans the same pixels without another Capture, and the Frame stays
    /// available for delivery. Runs under the same operation guard as Capture
    /// so a Trigger that arrives mid-scan is rejected rather than queued.
    /// Never throws for expected failures.
    /// </summary>
    public async Task<QrScanResult> ScanQrAsync()
    {
        if (!TryBeginOperation())
            return QrScanResult.Busy();

        try
        {
            var frame = _lastFrame;
            if (frame is null)
                return QrScanResult.NoFrame();

            // The scanner offloads its synchronous CPU-bound decode
            // internally (mirroring the capture adapters' thread-pool
            // contract), so the session awaits it directly.
            return await _qrScanner.ScanAsync(frame);
        }
        catch (Exception ex)
        {
            // The scanner contract says never-throw, but a defensive catch
            // keeps an unexpected adapter failure retryable instead of
            // crashing the workflow caller.
            return QrScanResult.Fail($"QR scanning failed: {ex.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Shared Capture Mode routing for an immediate-capture Trigger (no Target
    /// Selection). Owns the operation guard, effective-CaptureOptions
    /// composition, Capture, Frame ownership, and the preview transition. Both
    /// Full Desktop and Active Window route through this so their behavior —
    /// and their test coverage — stays symmetric at the seam.
    /// </summary>
    private async Task<WorkflowResult<TImage>> CaptureModeAsync(CaptureMode initialMode)
    {
        if (!TryBeginOperation())
        {
            return WorkflowResultFor(ModeLabel(initialMode), WorkflowStatus.OperationInProgress,
                error: "A capture is already in progress.");
        }

        var totalSw = Stopwatch.StartNew();
        var mode = initialMode;
        bool switched = false;
        try
        {
            while (true)
            {
                CaptureFrameResult captureResult;
                switch (mode)
                {
                    case CaptureMode.FullDesktop:
                    {
                        var options = _captureOptions.EffectiveFor(mode);
                        captureResult = await RunOffThread(() => _capture.CaptureFullDesktop(options));
                        break;
                    }
                    case CaptureMode.ActiveWindow:
                    {
                        var options = _captureOptions.EffectiveFor(mode);
                        captureResult = await RunOffThread(() => _capture.CaptureActiveWindow(options));
                        break;
                    }
                    case CaptureMode.Selection:
                    {
                        var result = await _selectionOverlay.ShowAsync(
                            switched ? null : _rememberedSelection.GetInitialGeometry());
                        if (result is null)
                            return Cancelled(mode, totalSw);
                        if (result.RequestedMode is CaptureMode requestedMode)
                        {
                            mode = requestedMode;
                            switched = true;
                            continue;
                        }

                        var geometry = result.Target!;
                        _rememberedSelection.Remember(geometry);
                        captureResult = await RunOffThread(() => _capture.CaptureSelection(geometry));
                        break;
                    }
                    case CaptureMode.SelectedMonitor:
                    {
                        var result = await _monitorPickerOverlay.ShowAsync();
                        if (result is null)
                            return Cancelled(mode, totalSw);
                        if (result.RequestedMode is CaptureMode requestedMode)
                        {
                            mode = requestedMode;
                            switched = true;
                            continue;
                        }

                        captureResult = await RunOffThread(() => _capture.CaptureMonitor(result.Target!));
                        break;
                    }
                    case CaptureMode.SelectedWindow:
                    {
                        var selection = await _windowPickerOverlay.ShowAsync();
                        if (selection is null)
                            return Cancelled(mode, totalSw);
                        if (selection.RequestedMode is CaptureMode requestedMode)
                        {
                            mode = requestedMode;
                            switched = true;
                            continue;
                        }

                        var result = selection.Target!;
                        if (result.Outcome == WindowPickerOutcome.EmptyDesktopFallback)
                        {
                            var options = _captureOptions.EffectiveFor(CaptureMode.FullDesktop);
                            captureResult = await RunOffThread(() => _capture.CaptureFullDesktop(options));
                        }
                        else
                        {
                            var windowOptions = _captureOptions.EffectiveFor(CaptureMode.SelectedWindow);
                            captureResult = await RunOffThread(() => _capture.CaptureWindow(result.Target!, windowOptions));
                        }
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode));
                }

                return await CompleteCaptureAsync(mode, captureResult, totalSw);
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<WorkflowResult<TImage>> CompleteCaptureAsync(
        CaptureMode mode,
        CaptureFrameResult captureResult,
        Stopwatch totalSw)
    {
        string label = ModeLabel(mode);
        if (!captureResult.Success)
        {
            return WorkflowResultFor(label, WorkflowStatus.CaptureFailed,
                error: captureResult.Error,
                captureMs: captureResult.ElapsedMs,
                totalMs: totalSw.Elapsed.TotalMilliseconds);
        }

        var frame = captureResult.Frame!;
        if (_annotationEnabled())
        {
            var annotationState = _annotationToolState();
            var annotationResult = _annotationOverlay is IAnnotationOverlayStateAdapter statefulOverlay
                ? await statefulOverlay.ShowAsync(frame, annotationState)
                : await _annotationOverlay.ShowAsync(frame);
            if (annotationResult.Outcome == AnnotationOutcome.Cancelled)
            {
                return WorkflowResultFor(label, WorkflowStatus.Cancelled,
                    error: "Annotation cancelled.",
                    captureMs: captureResult.ElapsedMs,
                    displayMs: annotationResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            if (annotationResult.Outcome == AnnotationOutcome.Failed)
            {
                return WorkflowResultFor(label, WorkflowStatus.AnnotationFailed,
                    frame: frame,
                    dimensions: captureResult.Dimensions,
                    error: annotationResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    displayMs: annotationResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            var composedFrame = annotationResult.Frame!;
            OwnFrame(composedFrame);
            var automaticReport = await RunAutomaticExportAsync();
            BeginPendingExit(automaticReport);
            RecordTriggeredMode(mode);
            return WorkflowResultFor(label, WorkflowStatus.Succeeded,
                frame: composedFrame,
                dimensions: $"{composedFrame.Width}×{composedFrame.Height}",
                captureMs: captureResult.ElapsedMs,
                displayMs: annotationResult.ElapsedMs,
                totalMs: totalSw.Elapsed.TotalMilliseconds,
                automaticExport: automaticReport,
                triggeredCaptureMode: mode);
        }

        OwnFrame(frame);
        var previewResult = _preview.Present(frame);
        if (!previewResult.Success)
        {
            return WorkflowResultFor(label, WorkflowStatus.PreviewFailed,
                frame: frame,
                dimensions: captureResult.Dimensions,
                error: previewResult.Error,
                captureMs: captureResult.ElapsedMs,
                displayMs: previewResult.ElapsedMs,
                totalMs: totalSw.Elapsed.TotalMilliseconds);
        }

        var previewAutomaticReport = await RunAutomaticExportAsync();
        BeginPendingExit(previewAutomaticReport);
        RecordTriggeredMode(mode);
        return WorkflowResultFor(label, WorkflowStatus.Succeeded,
            frame: frame,
            dimensions: captureResult.Dimensions,
            previewImage: previewResult.Image,
            captureMs: captureResult.ElapsedMs,
            displayMs: previewResult.ElapsedMs,
            totalMs: totalSw.Elapsed.TotalMilliseconds,
            automaticExport: previewAutomaticReport,
            triggeredCaptureMode: mode);
    }

    /// <summary>
    /// Records the Capture Mode a successful Trigger actually ran (after any
    /// Target Selection mode switch) as the last Capture Mode, when the
    /// launch behavior recording is wired and needs it (issue #53). Failures
    /// never propagate: a recording that cannot be written must not fail the
    /// capture that produced it.
    /// </summary>
    private void RecordTriggeredMode(CaptureMode mode)
    {
        if (_launchRecorder is null)
            return;

        try
        {
            _launchRecorder.Record(mode);
        }
        catch
        {
            // Recording is best-effort state; never fail a successful capture.
        }
    }

    // ── Configured automatic delivery (issue #47) ────────────────────

    /// <summary>
    /// Runs the configured automatic delivery actions for the just-confirmed
    /// Frame, in dependency order: save, Copy Frame, then Copy Path. Save runs
    /// first so it can establish the saved-file identity Copy Path consumes
    /// later in the same sequence; Copy Path is skipped — not failed — when no
    /// valid saved file exists by the time it would run. Each action composes
    /// the same primitives the manual Export actions use, so state (identity,
    /// clipboard) stays shared and retryable. Runs inside the already-held
    /// operation guard of the completing Capture. Failures never throw and
    /// never consume the Frame or a valid saved-file identity — they surface
    /// on the returned report for manual retry. Returns null when no automatic
    /// action is enabled.
    /// </summary>
    private async Task<AutomaticExportReport?> RunAutomaticExportAsync()
    {
        var settings = _automaticExport();
        if (!settings.AnyEnabled)
            return null;

        WorkflowExportResult? save = null;
        WorkflowExportResult? copyFrame = null;
        WorkflowExportResult? copyPath = null;

        if (settings.AutoSave)
        {
            save = await RunSaveCoreAsync(CancellationToken.None);
        }

        if (settings.AutoCopyFrame)
        {
            copyFrame = await RunCopyFrameCoreAsync();
        }

        if (settings.AutoCopyPath && HasSavedFile)
        {
            copyPath = await RunCopyPathCoreAsync();
        }

        return new AutomaticExportReport
        {
            Save = save,
            CopyFrame = copyFrame,
            CopyPath = copyPath,
        };
    }

    // ── Exit after confirmed delivery (issue #54) ────────────────────

    /// <summary>
    /// Marks the delivery evaluation of the current Capture as pending exit
    /// gating. Called once the Annotation overlay is dismissed and the
    /// configured automatic delivery has run: the enabled actions become
    /// the obligation the exit waits on, satisfied immediately when they all
    /// succeeded (a skipped action is not a failure), unsatisfied while any
    /// enabled action failed — until a later manual success of each failed
    /// action completes the configured delivery. With no automatic action
    /// enabled (a null report) the obligation is empty but unsatisfied:
    /// only a later manual delivery can complete it.
    /// </summary>
    private void BeginPendingExit(AutomaticExportReport? report)
    {
        _exitPending = true;
        _pendingFailedAutomatic.Clear();
        if (report is null)
            return;

        foreach (var failure in report.Failures)
            _pendingFailedAutomatic.Add(failure.Action);

        if (_pendingFailedAutomatic.Count == 0)
            RequestExit();
    }

    /// <summary>
    /// Evaluates the exit condition after a manual Export action completed.
    /// A successful manual file or clipboard delivery satisfies the exit
    /// condition when nothing outstanding remains: either no configured
    /// automatic delivery failed, or this success just completed the last
    /// failed configured action. Cancellations, failures, and busy
    /// rejections never satisfy anything — an unfinished action never exits.
    /// </summary>
    private void EvaluateManualExit(WorkflowExportResult result)
    {
        if (!_exitPending || _exitSatisfied || !result.IsSuccess)
            return;

        // The configured actions that failed at the automatic run are the
        // outstanding obligation. This manual success satisfies the exit
        // only when it completes that obligation: with an empty outstanding
        // set the manual delivery stands on its own; otherwise the manual
        // success must be one of the outstanding actions.
        if (_pendingFailedAutomatic.Contains(result.Action))
            _pendingFailedAutomatic.Remove(result.Action);

        if (_pendingFailedAutomatic.Count > 0)
            return;

        RequestExit();
    }

    /// <summary>
    /// Raises the exit request exactly once per Capture, and only while the
    /// Behavior Setting enables it. The recorder is the WinUI shutdown
    /// seam; in-session nothing aborts — the request observes the finished
    /// delivery state.
    /// </summary>
    private void RequestExit()
    {
        if (!_exitAfterDelivery() || _exitSatisfied)
            return;

        _exitSatisfied = true;
        _exitRecorder?.Invoke(WorkflowExitReason.DeliveryCompleted);
    }

    // ── Export workflow actions (issue #44) ──────────────────────────

    /// <summary>
    /// Takes ownership of a Frame as the session's current exportable Frame,
    /// resetting the saved-file identity — one default saved-file identity
    /// per Capture. Called on Capture success (preview and Annotation routes
    /// both land here) and by <see cref="AdoptFrame(ContiguousBitmap)"/> for
    /// legacy routes that still capture outside the session.
    /// </summary>
    private void OwnFrame(ContiguousBitmap frame)
    {
        _lastFrame = frame;
        _defaultSavedFilePath = null;
        _lastSavedFilePath = null;
        // The exit decision is per-Capture: a new Frame clears both the
        // pending automatic-delivery obligation and any satisfied exit, so
        // the next Capture must be delivered again before another exit.
        _exitPending = false;
        _exitSatisfied = false;
        _pendingFailedAutomatic.Clear();
    }

    /// <summary>
    /// Adopts a Frame captured outside the session (the remaining legacy
    /// capture routes) as the session's current exportable Frame, resetting
    /// the saved-file identity. Export actions act on this Frame until the
    /// next Capture or adoption replaces it. The adopted Capture arms the
    /// same exit evaluation with no automatic obligation — a legacy route
    /// runs no configured automatic delivery — so a successful manual
    /// delivery satisfies the exit exactly like a session Capture with no
    /// automatic actions enabled.
    /// </summary>
    public void AdoptFrame(ContiguousBitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        OwnFrame(frame);
        BeginPendingExit(report: null);
    }

    /// <summary>
    /// Save: writes the session-owned Frame as a PNG using the configured
    /// destination and Filename Template, and records that file as the
    /// Capture's default saved-file identity. A repeated Save — including
    /// after an automatic save routed through this action — writes the same
    /// recorded file instead of expanding the template again, so one Capture
    /// never accumulates accidental duplicate files. Failures and
    /// cancellations are retryable: a failed attempt records no identity.
    /// Never throws for expected failures.
    /// </summary>
    public async Task<WorkflowExportResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginOperation())
        {
            return ExportInProgress(WorkflowExportAction.Save);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RunSaveCoreAsync(cancellationToken);
            var mapped = WithResultOverride(result, elapsedMs: sw.Elapsed.TotalMilliseconds);
            EvaluateManualExit(mapped);
            return mapped;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Save core, shared by the manual action and the configured automatic
    /// delivery: writes the session-owned Frame as a PNG using the configured
    /// destination and Filename Template, records the Capture's default
    /// saved-file identity on the first success, and refers repeats to that
    /// same file. Callers own the operation guard. Elapsed time on the
    /// returned result is the adapter's; manual callers overwrite it with the
    /// action-level stopwatch.
    /// </summary>
    private async Task<WorkflowExportResult> RunSaveCoreAsync(CancellationToken cancellationToken)
    {
        var frame = _lastFrame;
        if (frame is null)
        {
            return ExportResultFor(WorkflowExportAction.Save, WorkflowExportStatus.NoFrame,
                error: "No capture to export.");
        }

        // First Save for this Capture: compute the default identity.
        // Repeats (manual or after an automatic save recorded it): write
        // the same recorded file.
        if (_defaultSavedFilePath is null)
        {
            var first = await RunOffThread(() => _export.SaveDefault(frame, cancellationToken));
            if (!first.Success)
            {
                return MapFileExportResult(WorkflowExportAction.Save, first);
            }

            _defaultSavedFilePath = first.DestinationPath;
            _lastSavedFilePath = first.DestinationPath;
            return FileExportSucceeded(WorkflowExportAction.Save, first, sw: null);
        }

        var repeat = await RunOffThread(() =>
            _export.SaveTo(frame, _defaultSavedFilePath, cancellationToken));
        if (!repeat.Success)
        {
            return MapFileExportResult(WorkflowExportAction.Save, repeat);
        }

        _lastSavedFilePath = _defaultSavedFilePath;
        return FileExportSucceeded(WorkflowExportAction.Save, repeat, sw: null);
    }

    /// <summary>
    /// Save As: shows the Save As dialog, then writes the session-owned Frame
    /// to the explicitly chosen path. The created file becomes the last
    /// saved file (so Copy Path refers to it) but does not replace the
    /// default saved-file identity — a later Save still refers to (or
    /// creates) the Capture's default file. Cancelling the dialog ends the
    /// action with <see cref="WorkflowExportStatus.Cancelled"/> and no side
    /// effects. Never throws for expected failures.
    /// </summary>
    public async Task<WorkflowExportResult> SaveAsAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginOperation())
        {
            return ExportInProgress(WorkflowExportAction.SaveAs);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            if (_lastFrame is null)
            {
                return ExportResultFor(WorkflowExportAction.SaveAs, WorkflowExportStatus.NoFrame,
                    error: "No capture to export.");
            }

            // The dialog is modal UI: invoked on the caller's thread, matching
            // the interactive overlay adapters' contract.
            var chosenPath = await _saveAsDialog.ShowAsync();
            if (string.IsNullOrEmpty(chosenPath))
            {
                return ExportResultFor(WorkflowExportAction.SaveAs, WorkflowExportStatus.Cancelled,
                    error: "Save cancelled.");
            }

            var frame = _lastFrame;
            var save = await RunOffThread(() => _export.SaveTo(frame, chosenPath!, cancellationToken));
            if (!save.Success)
            {
                return MapFileExportResult(WorkflowExportAction.SaveAs, save);
            }

            _lastSavedFilePath = save.DestinationPath;
            var savedAs = FileExportSucceeded(WorkflowExportAction.SaveAs, save, sw);
            EvaluateManualExit(savedAs);
            return savedAs;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Copy Frame: encodes the session-owned Frame as PNG image content and
    /// places it on the Windows clipboard without creating a file — the
    /// clipboard-only workflow requires no saved file. Never throws for
    /// expected failures.
    /// </summary>
    public async Task<WorkflowExportResult> CopyFrameAsync()
    {
        if (!TryBeginOperation())
        {
            return ExportInProgress(WorkflowExportAction.CopyFrame);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RunCopyFrameCoreAsync();
            var mapped = WithResultOverride(result, elapsedMs: sw.Elapsed.TotalMilliseconds);
            EvaluateManualExit(mapped);
            return mapped;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Copy Frame core, shared by the manual action and the configured
    /// automatic delivery: encodes the session-owned Frame as PNG image
    /// content and places it on the clipboard without creating a file.
    /// Callers own the operation guard.
    /// </summary>
    private async Task<WorkflowExportResult> RunCopyFrameCoreAsync()
    {
        var frame = _lastFrame;
        if (frame is null)
        {
            return ExportResultFor(WorkflowExportAction.CopyFrame, WorkflowExportStatus.NoFrame,
                error: "No capture to copy.");
        }

        var copy = await RunOffThread(() => _export.CopyImage(frame));
        return MapClipboardExportResult(WorkflowExportAction.CopyFrame, copy, sw: null);
    }

    /// <summary>
    /// Copy Path: places the current saved file's path on the clipboard.
    /// Unavailable — <see cref="WorkflowExportStatus.Failed"/> with a
    /// user-visible error — until a valid saved file exists as workflow
    /// state for the current Capture (a successful Save or Save As). Never
    /// throws for expected failures.
    /// </summary>
    public async Task<WorkflowExportResult> CopyPathAsync()
    {
        if (!TryBeginOperation())
        {
            return ExportInProgress(WorkflowExportAction.CopyPath);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RunCopyPathCoreAsync();
            var mapped = WithResultOverride(result, elapsedMs: sw.Elapsed.TotalMilliseconds);
            EvaluateManualExit(mapped);
            return mapped;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Open With: hands the saved Capture file to Windows application
    /// association — discovery of the applications Windows registers for the
    /// file's type, then launch (Windows shows its application picker when no
    /// single default is registered). Unavailable — <see cref="WorkflowExportStatus.Failed"/>
    /// with a user-visible error — until a valid saved file exists as workflow
    /// state for the current Capture, mirroring Copy Path. Cancellation and
    /// platform failures preserve the composed Frame, Annotation state, and
    /// saved-file identity for retry. Never throws for expected failures.
    /// </summary>
    public async Task<WorkflowExportResult> OpenWithAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginOperation())
        {
            return ExportInProgress(WorkflowExportAction.OpenWith);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var path = _lastSavedFilePath;
            if (string.IsNullOrEmpty(path))
            {
                return ExportResultFor(WorkflowExportAction.OpenWith, WorkflowExportStatus.Failed,
                    error: "No saved file to open.");
            }

            var delivery = await _delivery.OpenWithAsync(path!, cancellationToken);
            var opened = MapDeliveryResult(WorkflowExportAction.OpenWith, path!, delivery, sw);
            EvaluateManualExit(opened);
            return opened;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Share: invokes the Windows share interface with the delivered Capture
    /// file in a supported state. Unavailable — <see cref="WorkflowExportStatus.Failed"/>
    /// with a user-visible error — until a valid saved file exists as workflow
    /// state for the current Capture, mirroring Copy Path. Cancellation and
    /// platform failures preserve the composed Frame, Annotation state, and
    /// saved-file identity for retry. Never throws for expected failures.
    /// </summary>
    public async Task<WorkflowExportResult> ShareAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginOperation())
        {
            return ExportInProgress(WorkflowExportAction.Share);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var path = _lastSavedFilePath;
            if (string.IsNullOrEmpty(path))
            {
                return ExportResultFor(WorkflowExportAction.Share, WorkflowExportStatus.Failed,
                    error: "No saved file to share.");
            }

            var delivery = await _delivery.ShareAsync(path!, cancellationToken);
            var shared = MapDeliveryResult(WorkflowExportAction.Share, path!, delivery, sw);
            EvaluateManualExit(shared);
            return shared;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Maps a platform delivery result onto the Export action surface,
    /// carrying the delivered path so status text can name the file.
    /// </summary>
    private static WorkflowExportResult MapDeliveryResult(
        WorkflowExportAction action,
        string path,
        DeliveryResult delivery,
        Stopwatch sw)
    {
        if (delivery.Status == DeliveryStatus.Succeeded)
        {
            return new WorkflowExportResult
            {
                Action = action,
                Status = WorkflowExportStatus.Succeeded,
                FilePath = path,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }

        if (delivery.Status == DeliveryStatus.Cancelled)
        {
            return ExportResultFor(action, WorkflowExportStatus.Cancelled,
                error: delivery.Error ?? $"{action} cancelled.");
        }

        return ExportResultFor(action, WorkflowExportStatus.Failed,
            error: delivery.Error ?? $"{action} failed.");
    }

    /// <summary>
    /// Copy Path core, shared by the manual action and the configured
    /// automatic delivery: places the current saved file's path on the
    /// clipboard. Unavailable until a valid saved file exists as workflow
    /// state (a successful Save or Save As); the automatic delivery sequence
    /// checks availability before calling. Callers own the operation guard.
    /// </summary>
    private async Task<WorkflowExportResult> RunCopyPathCoreAsync()
    {
        var path = _lastSavedFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return ExportResultFor(WorkflowExportAction.CopyPath, WorkflowExportStatus.Failed,
                error: "No saved file to copy.");
        }

        var copy = await RunOffThread(() => _export.CopyText(path!));
        var mapped = MapClipboardExportResult(WorkflowExportAction.CopyPath, copy, sw: null);
        // Carry the copied path so report consumers (status text, automatic
        // delivery reporting) can show what was placed on the clipboard.
        return mapped.IsSuccess
            ? WithResultOverride(mapped, filePath: path!)
            : mapped;
    }

    private static WorkflowExportResult ExportInProgress(WorkflowExportAction action) =>
        ExportResultFor(action, WorkflowExportStatus.OperationInProgress,
            error: "An operation is already in progress.");

    /// <summary>
    /// Copies a core-action result with per-field overrides. The manual
    /// actions override the elapsed time with their action-level stopwatch;
    /// the Copy Path core overrides the file path so report consumers (status
    /// text, automatic delivery reporting) can show what was placed on the
    /// clipboard. Unspecified fields are carried over unchanged.
    /// </summary>
    private static WorkflowExportResult WithResultOverride(
        WorkflowExportResult result,
        double? elapsedMs = null,
        string? filePath = null) =>
        new()
        {
            Action = result.Action,
            Status = result.Status,
            FilePath = filePath ?? result.FilePath,
            Width = result.Width,
            Height = result.Height,
            ByteCount = result.ByteCount,
            Error = result.Error,
            ElapsedMs = elapsedMs ?? result.ElapsedMs,
        };

    private static WorkflowExportResult FileExportSucceeded(
        WorkflowExportAction action,
        ExportResult save,
        Stopwatch? sw) =>
        new()
        {
            Action = action,
            Status = WorkflowExportStatus.Succeeded,
            FilePath = save.DestinationPath,
            Width = save.Width,
            Height = save.Height,
            ByteCount = save.ByteCount,
            ElapsedMs = sw?.Elapsed.TotalMilliseconds ?? save.Elapsed.TotalMilliseconds,
        };

    private static WorkflowExportResult MapFileExportResult(
        WorkflowExportAction action,
        ExportResult save)
    {
        if (save.Phase == ExportPhase.Cancelled)
        {
            return ExportResultFor(action, WorkflowExportStatus.Cancelled, error: "Export cancelled.");
        }

        return ExportResultFor(action, WorkflowExportStatus.Failed, error: save.Message);
    }

    private static WorkflowExportResult MapClipboardExportResult(
        WorkflowExportAction action,
        ClipboardExportResult copy,
        Stopwatch? sw)
    {
        if (copy.Success)
        {
            return new()
            {
                Action = action,
                Status = WorkflowExportStatus.Succeeded,
                Width = copy.Width,
                Height = copy.Height,
                ByteCount = copy.ByteCount,
                ElapsedMs = sw?.Elapsed.TotalMilliseconds ?? copy.Elapsed.TotalMilliseconds,
            };
        }

        return ExportResultFor(action, WorkflowExportStatus.Failed, error: copy.Message);
    }

    private static WorkflowExportResult ExportResultFor(
        WorkflowExportAction action,
        WorkflowExportStatus status,
        string? error = null) =>
        new()
        {
            Action = action,
            Status = status,
            Error = error,
        };

    private static WorkflowResult<TImage> Cancelled(CaptureMode mode, Stopwatch totalSw) =>
        WorkflowResultFor(ModeLabel(mode), WorkflowStatus.Cancelled,
            error: $"{ModeLabel(mode)} cancelled.",
            totalMs: totalSw.Elapsed.TotalMilliseconds);

    private static string ModeLabel(CaptureMode mode) => mode switch
    {
        CaptureMode.FullDesktop => FullDesktopMode,
        CaptureMode.ActiveWindow => ActiveWindowMode,
        CaptureMode.SelectedWindow => SelectedWindowMode,
        CaptureMode.SelectedMonitor => SelectedMonitorMode,
        CaptureMode.Selection => SelectionMode,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

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

    private sealed class DisabledAnnotationOverlayAdapter : IAnnotationOverlayAdapter
    {
        public static DisabledAnnotationOverlayAdapter Instance { get; } = new();

        public Task<AnnotationPresentResult> ShowAsync(ContiguousBitmap sourceFrame) =>
            Task.FromResult(AnnotationPresentResult.Fail("Annotation is disabled."));
    }

    /// <summary>
    /// Stand-in engine for sessions constructed without OCR wiring. Recognize
    /// Text is a post-capture workflow feature delivered by the OCR adapter —
    /// without one there is nothing to recognize with, which is a retryable
    /// configuration gap, not a crash.
    /// </summary>
    private sealed class NullOcrEngine : IOcrEngine
    {
        public static NullOcrEngine Instance { get; } = new();

        public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => Array.Empty<OcrLanguage>();

        public Task<OcrResult> RecognizeAsync(ContiguousBitmap frame, string? languageTag) =>
            Task.FromResult(OcrResult.Fail("Text recognition is not available."));
    }

    /// <summary>
    /// Stand-in scanner for sessions constructed without QR wiring. Scan QR
    /// is a post-capture workflow feature delivered by the scanner adapter —
    /// without one there is nothing to scan with, which is a retryable
    /// configuration gap, not a crash.
    /// </summary>
    private sealed class NullQrScanner : IQrScanner
    {
        public static NullQrScanner Instance { get; } = new();

        public Task<QrScanResult> ScanAsync(ContiguousBitmap frame) =>
            Task.FromResult(QrScanResult.Fail("QR scanning is not available."));
    }

    private static WorkflowResult<TImage> WorkflowResultFor(
        string mode,
        WorkflowStatus status,
        ContiguousBitmap? frame = null,
        string dimensions = "",
        TImage? previewImage = default,
        string? error = null,
        double captureMs = 0,
        double displayMs = 0,
        double totalMs = 0,
        AutomaticExportReport? automaticExport = null,
        CaptureMode? triggeredCaptureMode = null) =>
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
            AutomaticExport = automaticExport,
            TriggeredCaptureMode = triggeredCaptureMode,
        };
}
