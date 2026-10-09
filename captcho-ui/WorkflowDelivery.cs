// WorkflowDelivery.cs — WinUI-free delivery seam for the runtime
// Workflow Session (issue #46).
//
// Open With and Share are Export actions that hand a saved Capture to
// Windows: Open With uses Windows application-association discovery and
// launch behavior, Share invokes the Windows share interface with the
// delivered Capture file. Both are available only while a valid saved file
// exists as workflow state for the current Capture — the same gating as
// Copy Path — because Windows consumes a file, not the session-owned Frame.
// Platform work — application-association discovery, launch, and the share
// interface — is supplied through one narrow adapter so the workflow rules
// stay headless-testable, mirroring the Export adapter seams.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace captcho.UI;

/// <summary>
/// Result of one platform delivery call (Open With launch or Share
/// invocation). The adapter never throws for expected failures — including
/// missing files, unsupported share contracts, and Windows API errors — so
/// the session can map them into retryable <see cref="WorkflowExportResult"/>
/// outcomes that preserve the Frame and the saved-file identity.
/// </summary>
public sealed class DeliveryResult
{
    /// <summary>Delivery completed successfully.</summary>
    public DeliveryStatus Status { get; init; }

    /// <summary>True only when the delivery reached the Succeeded status.</summary>
    public bool Success => Status == DeliveryStatus.Succeeded;

    /// <summary>
    /// User-visible error. Null on Succeeded; set on Failed and optionally
    /// on Cancelled.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Number of Windows applications the association discovery found for
    /// the delivered file's type, when available. Null when discovery was
    /// not part of the call.
    /// </summary>
    public int? HandlerCount { get; init; }

    /// <summary>Delivery duration in milliseconds.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>Creates a successful delivery result.</summary>
    public static DeliveryResult Ok(int? handlerCount, double elapsedMs) => new()
    {
        Status = DeliveryStatus.Succeeded,
        HandlerCount = handlerCount,
        ElapsedMs = elapsedMs,
    };

    /// <summary>Creates a failed delivery result carrying a user-visible error.</summary>
    public static DeliveryResult Fail(string error, double elapsedMs) => new()
    {
        Status = DeliveryStatus.Failed,
        Error = error,
        ElapsedMs = elapsedMs,
    };

    /// <summary>Creates a cancelled delivery result (platform dismissal or token).</summary>
    public static DeliveryResult Cancelled(double elapsedMs) => new()
    {
        Status = DeliveryStatus.Cancelled,
        ElapsedMs = elapsedMs,
    };
}

/// <summary>Outcome of a platform delivery call.</summary>
public enum DeliveryStatus
{
    /// <summary>The delivery completed successfully.</summary>
    Succeeded,

    /// <summary>
    /// The user dismissed the platform surface (e.g., closed the share
    /// sheet), or the call was cancelled through its token.
    /// </summary>
    Cancelled,

    /// <summary>
    /// The delivery failed — missing file, no registered application, or a
    /// Windows API error. Retryable.
    /// </summary>
    Failed,
}

/// <summary>
/// Narrow adapter over Windows delivery work: application-association
/// discovery and launch for Open With, and the Windows share interface for
/// Share. The workflow session owns the action rules (saved-file gating,
/// operation guarding, retryability); implementations must not throw for
/// expected failures — surface them through <see cref="DeliveryResult"/>
/// instead.
/// </summary>
public interface IWorkflowDeliveryAdapter
{
    /// <summary>
    /// Opens the saved file with Windows application association: discovers
    /// the applications Windows registers for the file's type and launches
    /// the association (showing Windows' application picker when the user
    /// has no single default). Returns a failed <see cref="DeliveryResult"/>
    /// when no application is registered or the launch fails.
    /// </summary>
    Task<DeliveryResult> OpenWithAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Invokes the Windows share interface with the delivered Capture file
    /// in a supported state (storage item plus bitmap stream). Returns a
    /// failed <see cref="DeliveryResult"/> when the share contract is
    /// unavailable on this system.
    /// </summary>
    Task<DeliveryResult> ShareAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>
/// Null-object delivery adapter for sessions constructed without delivery
/// wiring. Every action fails cleanly instead of throwing, so legacy
/// constructions keep compiling and headless capture-only tests are
/// unaffected.
/// </summary>
internal sealed class UnconfiguredWorkflowDeliveryAdapter : IWorkflowDeliveryAdapter
{
    public static UnconfiguredWorkflowDeliveryAdapter Instance { get; } = new();

    private const string Unavailable = "Windows delivery is not configured for this session.";

    public Task<DeliveryResult> OpenWithAsync(string filePath, CancellationToken cancellationToken) =>
        Task.FromResult(DeliveryResult.Fail(Unavailable, TimeSpan.Zero.TotalMilliseconds));

    public Task<DeliveryResult> ShareAsync(string filePath, CancellationToken cancellationToken) =>
        Task.FromResult(DeliveryResult.Fail(Unavailable, TimeSpan.Zero.TotalMilliseconds));
}
