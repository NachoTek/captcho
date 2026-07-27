// CaptureWorkflowSession.cs — WinUI-free runtime workflow session.
//
// The agreed high-level behavioral seam for capture and post-capture behavior.
// Owns Capture Mode routing for the Full Desktop Trigger, operation state
// (concurrency guard), the captured Frame, and the preview transition. Exposes
// operation-in-progress and failure outcomes consistently so the WinUI layer
// can present a retryable, user-visible state. Platform-specific work — native
// pixel acquisition and presenting the Frame to a XAML Image — is supplied
// through narrow adapters, mirroring the CliCaptureService shape at the GUI's
// higher Capture-through-completion boundary.
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

    private readonly IWorkflowCaptureAdapter _capture;
    private readonly IPreviewAdapter<TImage> _preview;

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
    /// </summary>
    public CaptureWorkflowSession(IWorkflowCaptureAdapter capture, IPreviewAdapter<TImage> preview)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
    }

    /// <summary>
    /// Routes a Full Desktop Trigger through the production workflow: performs
    /// the real Capture, owns the resulting Frame, drives the preview
    /// transition, and exposes operation-in-progress and failure outcomes
    /// consistently. Never throws for expected failures.
    /// </summary>
    public async Task<WorkflowResult<TImage>> CaptureFullDesktopAsync()
    {
        if (!TryBeginOperation())
        {
            return WorkflowResultFor(FullDesktopMode, WorkflowStatus.OperationInProgress,
                error: "A capture is already in progress.");
        }

        var totalSw = Stopwatch.StartNew();
        try
        {
            // Capture runs on the thread pool — the adapter may block on native
            // pixel acquisition. The session's caller (the UI thread, in
            // production) is freed for the duration.
            var captureResult = await RunOffThread(() => _capture.CaptureFullDesktop());
            if (!captureResult.Success)
            {
                return WorkflowResultFor(FullDesktopMode, WorkflowStatus.CaptureFailed,
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
                return WorkflowResultFor(FullDesktopMode, WorkflowStatus.PreviewFailed,
                    frame: frame,
                    dimensions: captureResult.Dimensions,
                    error: previewResult.Error,
                    captureMs: captureResult.ElapsedMs,
                    displayMs: previewResult.ElapsedMs,
                    totalMs: totalSw.Elapsed.TotalMilliseconds);
            }

            return WorkflowResultFor(FullDesktopMode, WorkflowStatus.Succeeded,
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
