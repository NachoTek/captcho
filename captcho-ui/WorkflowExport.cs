// WorkflowExport.cs — WinUI-free Export workflow seam for the runtime
// Workflow Session (issue #44).
//
// Save, Save As, Copy Frame, and Copy Path are Export actions — post-capture
// processing of a session-owned Frame — not Capture concerns. The session
// owns the one default saved-file identity per Capture so repeating Save
// (including after an automatic save routed through the same action) refers
// to the same file instead of creating accidental duplicates, while Save As
// intentionally produces another file without replacing that identity.
// Open With and Share (issue #46) extend the same action surface over the
// delivery seam in WorkflowDelivery.cs.
// Platform work — PNG encoding/writing, the Windows clipboard, and the Save As
// file dialog — is supplied through narrow adapters so the workflow rules
// stay headless-testable, mirroring the capture/overlay adapter seams.

using System;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Which Export workflow action produced a <see cref="WorkflowExportResult"/>.
/// </summary>
public enum WorkflowExportAction
{
    /// <summary>
    /// Save: writes a PNG to the configured destination using the configured
    /// Filename Template and records that file as the Capture's default
    /// saved-file identity. Repeats write the same file.
    /// </summary>
    Save,

    /// <summary>
    /// Save As: explicitly chooses another location/name. Can create a second
    /// file without replacing the default saved-file identity.
    /// </summary>
    SaveAs,

    /// <summary>
    /// Copy Frame: places image content on the Windows clipboard without
    /// creating a file.
    /// </summary>
    CopyFrame,

    /// <summary>
    /// Copy Path: places the saved file's path on the clipboard. Unavailable
    /// until a valid saved file exists for the current Capture.
    /// </summary>
    CopyPath,

    /// <summary>
    /// Open With: hands the saved file to Windows application association —
    /// discovery of the applications registered for its type plus launch.
    /// Unavailable until a valid saved file exists for the current Capture.
    /// </summary>
    OpenWith,

    /// <summary>
    /// Share: invokes the Windows share interface with the delivered Capture
    /// file. Unavailable until a valid saved file exists for the current
    /// Capture.
    /// </summary>
    Share,
}

/// <summary>
/// Outcome of an Export workflow action. The session never throws for
/// expected failures; it reports them through this status instead so the
/// WinUI layer can present retryable, user-visible state.
/// </summary>
public enum WorkflowExportStatus
{
    /// <summary>The Export action completed successfully.</summary>
    Succeeded,

    /// <summary>
    /// The session owns no Frame — no Capture has succeeded (and no legacy
    /// Frame was adopted) since startup or the last identity reset.
    /// </summary>
    NoFrame,

    /// <summary>
    /// The user cancelled the action (e.g., dismissed the Save As dialog), or
    /// the export adapter reported cancellation. No file identity changed.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The action failed. Retryable: neither the Frame nor the default
    /// saved-file identity is consumed by a failure.
    /// </summary>
    Failed,

    /// <summary>
    /// Another workflow operation was already in flight. No work was
    /// performed.
    /// </summary>
    OperationInProgress,
}

/// <summary>
/// WinUI-free result of one Export workflow action. Carries the produced
/// file identity (Save/Save As), clipboard content dimensions (Copy Frame),
/// a user-visible error for non-success statuses, and elapsed time.
/// </summary>
public sealed class WorkflowExportResult
{
    /// <summary>Which Export action produced this result.</summary>
    public WorkflowExportAction Action { get; init; }

    /// <summary>Outcome of the action.</summary>
    public WorkflowExportStatus Status { get; init; }

    /// <summary>
    /// Full path of the file written on success (Save/Save As); the path
    /// placed on the clipboard for Copy Path. Null otherwise.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>Exported image width in pixels, when available.</summary>
    public int? Width { get; init; }

    /// <summary>Exported image height in pixels, when available.</summary>
    public int? Height { get; init; }

    /// <summary>Size of the produced content in bytes, when available.</summary>
    public long? ByteCount { get; init; }

    /// <summary>
    /// User-visible error. Null on Succeeded; set on every other status
    /// except Cancelled, where it may carry a short reason.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Action duration in milliseconds.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>True only when the action reached the Succeeded status.</summary>
    public bool IsSuccess => Status == WorkflowExportStatus.Succeeded;
}

/// <summary>
/// Narrow adapter over platform Export work: PNG encoding and file writing,
/// clipboard placement, and nothing else. The workflow session owns the
/// action rules (one default saved-file identity per Capture, Copy Path
/// availability, operation guarding); implementations must not throw for
/// expected failures — surface them through the structured results instead.
/// </summary>
public interface IWorkflowExportAdapter
{
    /// <summary>
    /// Writes the Frame as a PNG to the configured destination (committed
    /// Save Location) using the configured Filename Template, resolving
    /// filename collisions. Returns an <see cref="ExportResult"/> carrying
    /// the destination path on success.
    /// </summary>
    ExportResult SaveDefault(ContiguousBitmap frame, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the Frame as a PNG to the explicitly supplied full path — the
    /// confirmed Save As choice, or the already-recorded default saved-file
    /// identity on a repeated Save. No collision resolution: the caller chose
    /// the path.
    /// </summary>
    ExportResult SaveTo(ContiguousBitmap frame, string destinationPath, CancellationToken cancellationToken);

    /// <summary>
    /// Encodes the Frame as PNG image content and places it on the clipboard.
    /// Creates no file.
    /// </summary>
    ClipboardExportResult CopyImage(ContiguousBitmap frame);

    /// <summary>
    /// Places the supplied text (a saved file's path) on the clipboard.
    /// </summary>
    ClipboardExportResult CopyText(string text);
}

/// <summary>
/// Narrow adapter over the interactive Save As file dialog. Production shows
/// a FileSavePicker initialized from the configured Filename Template; tests
/// supply fakes that return a chosen path or null for cancellation. Invoked
/// on the workflow caller's thread (the UI thread in production) because the
/// picker is modal.
/// </summary>
public interface ISaveAsDialogAdapter
{
    /// <summary>
    /// Shows the Save As dialog and returns the chosen full path, or null if
    /// the user cancelled. Returning null must not produce a file or change
    /// any saved-file identity.
    /// </summary>
    Task<string?> ShowAsync();
}

/// <summary>
/// Null-object Export adapter for sessions constructed without Export
/// wiring. Every action fails cleanly instead of throwing, so legacy
/// constructions keep compiling and headless capture-only tests are
/// unaffected.
/// </summary>
internal sealed class UnconfiguredWorkflowExportAdapter : IWorkflowExportAdapter
{
    public static UnconfiguredWorkflowExportAdapter Instance { get; } = new();

    private const string Unavailable = "Export is not configured for this session.";

    public ExportResult SaveDefault(ContiguousBitmap frame, CancellationToken cancellationToken) =>
        ExportResult.Fail(ExportPhase.Validation, Unavailable, TimeSpan.Zero);

    public ExportResult SaveTo(ContiguousBitmap frame, string destinationPath, CancellationToken cancellationToken) =>
        ExportResult.Fail(ExportPhase.Validation, Unavailable, TimeSpan.Zero);

    public ClipboardExportResult CopyImage(ContiguousBitmap frame) =>
        ClipboardExportResult.Fail(Unavailable, TimeSpan.Zero);

    public ClipboardExportResult CopyText(string text) =>
        ClipboardExportResult.Fail(Unavailable, TimeSpan.Zero);
}

/// <summary>
/// Null-object Save As dialog that always reports cancellation, for sessions
/// constructed without Save As wiring.
/// </summary>
internal sealed class UnavailableSaveAsDialogAdapter : ISaveAsDialogAdapter
{
    public static UnavailableSaveAsDialogAdapter Instance { get; } = new();

    public Task<string?> ShowAsync() => Task.FromResult<string?>(null);
}
