// DelayedCaptureCountdownTests.cs — Tests for the countdown controller used by delayed capture.
//
// Verifies normalization, zero-delay fast path, progress/title snapshots,
// cancellation propagation, and boundary values.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Respectacle.UI;
using Xunit;

namespace Respectacle.UI.Tests;

public class DelayedCaptureCountdownTests
{
    // ── Normalization / clamping ──────────────────────────────────────

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(9999, 60)]
    public void Constructor_ClampsDelayToValidRange(int requested, int expected)
    {
        var countdown = new DelayedCaptureCountdown(requested);
        Assert.Equal(expected, countdown.TotalSeconds);
    }

    // ── Zero-delay fast path ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_DelayZero_EmitsNoSnapshots()
    {
        var countdown = new DelayedCaptureCountdown(0);
        var snapshots = new List<CountdownSnapshot>();

        await foreach (var snapshot in countdown.RunAsync(CancellationToken.None))
        {
            snapshots.Add(snapshot);
        }

        Assert.Empty(snapshots);
    }

    // ── Multi-second countdown snapshots ──────────────────────────────

    [Fact]
    public async Task RunAsync_FiveSecondDelay_EmitsCorrectSnapshotCount()
    {
        // Use no-op delay to avoid real waits
        var countdown = new DelayedCaptureCountdown(5, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        Assert.Equal(5, snapshots.Count);
    }

    [Fact]
    public async Task RunAsync_FiveSecondDelay_FirstSnapshotHasFullRemaining()
    {
        var countdown = new DelayedCaptureCountdown(5, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        var first = snapshots[0];
        Assert.Equal(5, first.RemainingSeconds);
        Assert.Equal(5, first.TotalSeconds);
    }

    [Fact]
    public async Task RunAsync_FiveSecondDelay_ProgressIncreasesMonotonically()
    {
        var countdown = new DelayedCaptureCountdown(5, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        for (int i = 1; i < snapshots.Count; i++)
        {
            Assert.True(snapshots[i].ProgressPercent > snapshots[i - 1].ProgressPercent,
                $"Progress at snapshot {i} ({snapshots[i].ProgressPercent}%) should exceed snapshot {i - 1} ({snapshots[i - 1].ProgressPercent}%)");
        }
    }

    [Fact]
    public async Task RunAsync_FiveSecondDelay_ProgressStartsAtZero()
    {
        var countdown = new DelayedCaptureCountdown(5, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        Assert.Equal(0, snapshots[0].ProgressPercent);
    }

    [Fact]
    public async Task RunAsync_FiveSecondDelay_LastSnapshotHasHighestProgress()
    {
        var countdown = new DelayedCaptureCountdown(5, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        var last = snapshots[^1];
        Assert.True(last.ProgressPercent >= 80); // 4/5 = 80%
        Assert.Equal(1, last.RemainingSeconds);
    }

    // ── Status and title text ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SnapshotsContainStatusText()
    {
        var countdown = new DelayedCaptureCountdown(3, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        foreach (var snapshot in snapshots)
        {
            Assert.False(string.IsNullOrEmpty(snapshot.StatusText));
            Assert.Contains(snapshot.RemainingSeconds.ToString(), snapshot.StatusText);
        }
    }

    [Fact]
    public async Task RunAsync_SnapshotsContainTitleText()
    {
        var countdown = new DelayedCaptureCountdown(3, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        foreach (var snapshot in snapshots)
        {
            Assert.False(string.IsNullOrEmpty(snapshot.TitleText));
        }
    }

    [Fact]
    public async Task RunAsync_SnapshotsAreVisible()
    {
        var countdown = new DelayedCaptureCountdown(3, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        foreach (var snapshot in snapshots)
        {
            Assert.True(snapshot.IsVisible);
        }
    }

    // ── Cancellation ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CancelledEarly_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();

        // Cancel after the first snapshot is emitted
        int count = 0;
        var countdown = new DelayedCaptureCountdown(10, async (_secs, _ct) =>
        {
            count++;
            if (count >= 1) await cts.CancelAsync();
            await Task.CompletedTask;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in countdown.RunAsync(cts.Token))
            {
                // consume
            }
        });
    }

    [Fact]
    public async Task RunAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var countdown = new DelayedCaptureCountdown(5, (_secs, _ct) => Task.CompletedTask);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in countdown.RunAsync(cts.Token))
            {
                // consume
            }
        });
    }

    // ── Boundary values ───────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SingleSecond_EmitsOneSnapshot()
    {
        var countdown = new DelayedCaptureCountdown(1, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        Assert.Single(snapshots);
        Assert.Equal(1, snapshots[0].RemainingSeconds);
        Assert.Equal(1, snapshots[0].TotalSeconds);
        Assert.Equal(0, snapshots[0].ProgressPercent);
    }

    [Fact]
    public async Task RunAsync_SixtySeconds_Emits60Snapshots()
    {
        var countdown = new DelayedCaptureCountdown(60, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        Assert.Equal(60, snapshots.Count);
        Assert.Equal(60, snapshots[0].TotalSeconds);
        Assert.Equal(60, snapshots[0].RemainingSeconds);
    }

    [Fact]
    public async Task RunAsync_SixtySeconds_ProgressNeverExceeds100()
    {
        var countdown = new DelayedCaptureCountdown(60, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        foreach (var snapshot in snapshots)
        {
            Assert.True(snapshot.ProgressPercent >= 0);
            Assert.True(snapshot.ProgressPercent < 100); // last tick is 98.3% (59/60 * 100)
        }
    }

    // ── Progress math correctness ─────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(60)]
    public async Task RunAsync_ProgressMatchesExpectedPercent(int delaySeconds)
    {
        var countdown = new DelayedCaptureCountdown(delaySeconds, (_secs, _ct) => Task.CompletedTask);
        var snapshots = await CollectSnapshotsAsync(countdown);

        for (int i = 0; i < snapshots.Count; i++)
        {
            int elapsed = delaySeconds - snapshots[i].RemainingSeconds;
            double expectedPercent = Math.Round((double)elapsed / delaySeconds * 100, 1);
            Assert.Equal(expectedPercent, snapshots[i].ProgressPercent);
        }
    }

    // ── Helper ────────────────────────────────────────────────────────

    private static async Task<List<CountdownSnapshot>> CollectSnapshotsAsync(DelayedCaptureCountdown countdown)
    {
        var snapshots = new List<CountdownSnapshot>();
        await foreach (var snapshot in countdown.RunAsync(CancellationToken.None))
        {
            snapshots.Add(snapshot);
        }
        return snapshots;
    }
}
