// DelayedCaptureCountdown.cs — Testable countdown controller for delayed capture.
//
// Normalizes delay seconds to the inclusive 0–60 range and emits immutable
// countdown snapshots that the UI can bind to title/status/progress controls.
// Delay 0 completes immediately with no emitted snapshots.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Respectacle.UI;

/// <summary>
/// Immutable snapshot of countdown state at a point in time.
/// </summary>
public sealed class CountdownSnapshot
{
    /// <summary>Seconds remaining in the countdown.</summary>
    public int RemainingSeconds { get; init; }

    /// <summary>Total seconds the countdown was configured for (after normalization).</summary>
    public int TotalSeconds { get; init; }

    /// <summary>Progress percent (0–&lt;100) based on elapsed vs total seconds.</summary>
    public double ProgressPercent { get; init; }

    /// <summary>User-facing status text (e.g., "Capturing in 5 seconds…").</summary>
    public string StatusText { get; init; } = "";

    /// <summary>Window title text (e.g., "Capture in 5s").</summary>
    public string TitleText { get; init; } = "";

    /// <summary>Whether the countdown UI should be visible.</summary>
    public bool IsVisible { get; init; }
}

/// <summary>
/// Reusable, deterministic countdown primitive for delayed capture.
/// Keeps countdown math out of WinUI controls so it can be tested headlessly.
/// </summary>
public sealed class DelayedCaptureCountdown
{
    private readonly Func<int, CancellationToken, Task> _tickDelay;

    /// <summary>
    /// Normalized total delay in seconds (clamped to 0–60 inclusive).
    /// </summary>
    public int TotalSeconds { get; }

    /// <summary>
    /// Creates a countdown controller.
    /// </summary>
    /// <param name="requestedDelaySeconds">
    /// Requested delay in seconds. Clamped to the inclusive 0–60 range.
    /// </param>
    /// <param name="tickDelayFunc">
    /// Optional injectable delay function. Receives (seconds, cancellationToken)
    /// per tick. Defaults to <c>Task.Delay</c>. Inject a no-op in tests.
    /// </param>
    public DelayedCaptureCountdown(
        int requestedDelaySeconds,
        Func<int, CancellationToken, Task>? tickDelayFunc = null)
    {
        TotalSeconds = Math.Clamp(requestedDelaySeconds, 0, 60);
        _tickDelay = tickDelayFunc ?? DefaultDelay;
    }

    /// <summary>
    /// Runs the countdown, yielding one snapshot per second.
    /// Delay 0 yields nothing and completes immediately.
    /// Throws <see cref="OperationCanceledException"/> if the token is canceled.
    /// </summary>
    public async IAsyncEnumerable<CountdownSnapshot> RunAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (TotalSeconds == 0)
        {
            yield break;
        }

        for (int remaining = TotalSeconds; remaining > 0; remaining--)
        {
            ct.ThrowIfCancellationRequested();

            yield return CreateSnapshot(remaining);

            // Wait one tick (1 second by default), except after the last snapshot
            // where we only wait if there are more ticks coming.
            // Actually, we always wait a tick — the final tick's delay represents
            // the last second counting down.
            await _tickDelay(1, ct).ConfigureAwait(false);
        }
    }

    private CountdownSnapshot CreateSnapshot(int remaining)
    {
        int elapsed = TotalSeconds - remaining;
        double progressPercent = Math.Round((double)elapsed / TotalSeconds * 100, 1);

        return new CountdownSnapshot
        {
            RemainingSeconds = remaining,
            TotalSeconds = TotalSeconds,
            ProgressPercent = progressPercent,
            StatusText = remaining == 1
                ? "Capturing in 1 second…"
                : $"Capturing in {remaining} seconds…",
            TitleText = $"Capture in {remaining}s",
            IsVisible = true,
        };
    }

    private static Task DefaultDelay(int seconds, CancellationToken ct)
    {
        return Task.Delay(seconds * 1000, ct);
    }
}
