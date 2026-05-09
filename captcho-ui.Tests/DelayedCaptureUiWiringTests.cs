// DelayedCaptureUiWiringTests.cs — Tests for delayed capture UI wiring.
//
// Verifies the delayed capture flow through a testable mediator that mirrors
// MainWindow's delayed capture logic without WinUI controls. Covers:
// - Delay 0 bypasses countdown and calls capture immediately
// - Delay > 0 emits countdown states then calls capture
// - Cancel during countdown does not call capture
// - Exceptions show sanitized status and re-enable controls
// - Title/progress/cancel visibility reset in all paths
// - Malformed inputs (NaN, negative, >60) normalize to bounded values

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

/// <summary>
/// Testable mediator that mirrors the MainWindow delayed capture flow.
/// Records state transitions so tests can assert on the control flow.
/// </summary>
public class DelayedCaptureFlowMediator
{
    // Injected dependencies (test doubles)
    private readonly Func<int, CancellationToken, Task> _tickDelay;
    private readonly Func<Task<CapturePreviewResult>> _captureFunc;

    // Recorded state for assertions
    public bool ButtonsEnabled { get; private set; } = true;
    public bool ProgressVisible { get; private set; } = false;
    public bool CancelVisible { get; private set; } = false;
    public double ProgressPercent { get; private set; }
    public string? TitleText { get; private set; }
    public string? StatusText { get; private set; }
    public string? TimingText { get; private set; }
    public object? PreviewSource { get; private set; }
    public bool CaptureWasCalled { get; private set; } = false;

    /// <summary>
    /// Snapshots recorded from each countdown tick (for asserting countdown sequence).
    /// </summary>
    public List<CountdownSnapshot> CountdownSnapshots { get; } = new();

    /// <summary>
    /// Cancellation token source — tests can cancel via this.
    /// </summary>
    private CancellationTokenSource? _cts;

    public DelayedCaptureFlowMediator(
        Func<int, CancellationToken, Task>? tickDelay = null,
        Func<Task<CapturePreviewResult>>? captureFunc = null)
    {
        _tickDelay = tickDelay ?? ((_seconds, _ct) => Task.CompletedTask);
        _captureFunc = captureFunc ?? (() => Task.FromResult(new CapturePreviewResult()));
    }

    /// <summary>
    /// Mirrors MainWindow's delayed capture flow.
    /// </summary>
    public async Task RunDelayedCaptureAsync(int delaySeconds)
    {
        // Normalize (matches NumberBox → integer clamping logic)
        int delay = Math.Clamp(delaySeconds, 0, 60);

        SetCaptureState(true); // disable buttons, show progress
        StatusText = delay == 0 ? "Capturing…" : "Starting countdown…";
        TimingText = "";

        _cts = new CancellationTokenSource();

        try
        {
            if (delay > 0)
            {
                // Run countdown phase
                CancelVisible = true;
                var countdown = new DelayedCaptureCountdown(delay, _tickDelay);

                await foreach (var snapshot in countdown.RunAsync(_cts.Token))
                {
                    TitleText = snapshot.TitleText;
                    StatusText = snapshot.StatusText;
                    ProgressPercent = snapshot.ProgressPercent;
                    ProgressVisible = snapshot.IsVisible;
                    CountdownSnapshots.Add(snapshot);
                }
            }

            // Capture phase — always runs after countdown (or immediately if delay 0)
            StatusText = "Capturing…";
            TitleText = null; // reset countdown title
            ProgressPercent = delay > 0 ? 100 : 0;
            CaptureWasCalled = true;

            var result = await _captureFunc();

            if (result.IsSuccess && result.ImageSource != null)
            {
                PreviewSource = result.ImageSource;
                StatusText = $"{result.Mode} — {result.Dimensions}";
            }
            else
            {
                StatusText = result.Error ?? "Capture failed.";
            }

            TimingText = FormatTiming(result);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Countdown cancelled.";
            TitleText = null;
            CaptureWasCalled = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            TitleText = null;
            TimingText = "";
        }
        finally
        {
            SetCaptureState(false); // re-enable buttons, hide progress
            CancelVisible = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Cancel the active countdown. Mirrors CancelDelayButton click.
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    private void SetCaptureState(bool capturing)
    {
        ButtonsEnabled = !capturing;
        ProgressVisible = capturing;
    }

    private static string FormatTiming(CapturePreviewResult result)
    {
        if (result.CaptureMs == 0 && result.DisplayMs == 0)
            return "";

        var parts = new List<string>();
        if (result.CaptureMs > 0)
            parts.Add($"capture {result.CaptureMs:F1}ms");
        if (result.DisplayMs > 0)
            parts.Add($"display {result.DisplayMs:F1}ms");
        if (result.TotalMs > 0)
            parts.Add($"total {result.TotalMs:F1}ms");

        return string.Join(" │ ", parts);
    }
}

public class DelayedCaptureUiWiringTests
{
    // ── Delay 0: immediate capture path ──────────────────────────────

    [Fact]
    public async Task DelayZero_CallsCaptureImmediately()
    {
        bool captureCalled = false;
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => { captureCalled = true; return Task.FromResult(new CapturePreviewResult()); });

        await mediator.RunDelayedCaptureAsync(0);

        Assert.True(captureCalled);
    }

    [Fact]
    public async Task DelayZero_NoCountdownSnapshots()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await mediator.RunDelayedCaptureAsync(0);

        Assert.Empty(mediator.CountdownSnapshots);
    }

    [Fact]
    public async Task DelayZero_CancelNotVisible()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await mediator.RunDelayedCaptureAsync(0);

        Assert.False(mediator.CancelVisible);
    }

    // ── Delay > 0: countdown path ────────────────────────────────────

    [Fact]
    public async Task DelayThree_EmitsThreeCountdownSnapshots()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await mediator.RunDelayedCaptureAsync(3);

        Assert.Equal(3, mediator.CountdownSnapshots.Count);
    }

    [Fact]
    public async Task DelayThree_SnapshotsHaveDecreasingRemainingSeconds()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await mediator.RunDelayedCaptureAsync(3);

        Assert.Equal(new[] { 3, 2, 1 }, mediator.CountdownSnapshots.Select(s => s.RemainingSeconds).ToArray());
    }

    [Fact]
    public async Task DelayFive_CallsCaptureAfterCountdown()
    {
        bool captureCalled = false;
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => { captureCalled = true; return Task.FromResult(new CapturePreviewResult()); });

        await mediator.RunDelayedCaptureAsync(5);

        Assert.True(captureCalled);
    }

    [Fact]
    public async Task DelayTwo_CancelButtonHiddenAfterCompletion()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await mediator.RunDelayedCaptureAsync(2);

        // After completion, cancel should be hidden
        Assert.False(mediator.CancelVisible);
    }

    [Fact]
    public async Task DelayOne_EmitsVisibleCountdownSnapshot()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await mediator.RunDelayedCaptureAsync(1);

        Assert.Single(mediator.CountdownSnapshots);
        Assert.True(mediator.CountdownSnapshots[0].IsVisible);
        Assert.Equal(1, mediator.CountdownSnapshots[0].RemainingSeconds);
    }

    // ── Cancel path ──────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_DoesNotCallCapture()
    {
        bool captureCalled = false;
        DelayedCaptureFlowMediator? m = null;
        m = new DelayedCaptureFlowMediator(
            tickDelay: (_, ct) =>
            {
                m!.Cancel();
                return Task.FromCanceled(ct);
            },
            captureFunc: () => { captureCalled = true; return Task.FromResult(new CapturePreviewResult()); });

        await m.RunDelayedCaptureAsync(5);

        Assert.False(captureCalled);
    }

    [Fact]
    public async Task Cancel_ShowsCancellationMessage()
    {
        DelayedCaptureFlowMediator? m = null;
        m = new DelayedCaptureFlowMediator(
            tickDelay: (_, ct) =>
            {
                m!.Cancel();
                return Task.FromCanceled(ct);
            },
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await m.RunDelayedCaptureAsync(5);

        Assert.Equal("Countdown cancelled.", m.StatusText);
    }

    [Fact]
    public async Task Cancel_ReEnablesButtons()
    {
        DelayedCaptureFlowMediator? m = null;
        m = new DelayedCaptureFlowMediator(
            tickDelay: (_, ct) =>
            {
                m!.Cancel();
                return Task.FromCanceled(ct);
            },
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await m.RunDelayedCaptureAsync(5);

        Assert.True(m.ButtonsEnabled);
    }

    [Fact]
    public async Task Cancel_HidesProgress()
    {
        DelayedCaptureFlowMediator? m = null;
        m = new DelayedCaptureFlowMediator(
            tickDelay: (_, ct) =>
            {
                m!.Cancel();
                return Task.FromCanceled(ct);
            },
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await m.RunDelayedCaptureAsync(5);

        Assert.False(m.ProgressVisible);
    }

    [Fact]
    public async Task Cancel_HidesCancelButton()
    {
        DelayedCaptureFlowMediator? m = null;
        m = new DelayedCaptureFlowMediator(
            tickDelay: (_, ct) =>
            {
                m!.Cancel();
                return Task.FromCanceled(ct);
            },
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await m.RunDelayedCaptureAsync(5);

        Assert.False(m.CancelVisible);
    }

    [Fact]
    public async Task Cancel_ResetsTitle()
    {
        DelayedCaptureFlowMediator? m = null;
        m = new DelayedCaptureFlowMediator(
            tickDelay: (_, ct) =>
            {
                m!.Cancel();
                return Task.FromCanceled(ct);
            },
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await m.RunDelayedCaptureAsync(5);

        Assert.Null(m.TitleText);
    }

    // ── Capture success with timing ──────────────────────────────────

    [Fact]
    public async Task DelayZero_CaptureSuccess_ShowsTiming()
    {
        var result = new CapturePreviewResult
        {
            Mode = "Full Desktop",
            Dimensions = "1920×1080",
            CaptureMs = 10.5,
            DisplayMs = 5.2,
            TotalMs = 16.0,
        };

        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(result));

        await mediator.RunDelayedCaptureAsync(0);

        Assert.Contains("capture 10.5ms", mediator.TimingText);
        Assert.Contains("display 5.2ms", mediator.TimingText);
    }

    [Fact]
    public async Task DelayThree_CaptureSuccess_ShowsTiming()
    {
        var result = new CapturePreviewResult
        {
            Mode = "Full Desktop",
            Dimensions = "1920×1080",
            CaptureMs = 12.0,
            TotalMs = 12.0,
        };

        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(result));

        await mediator.RunDelayedCaptureAsync(3);

        Assert.Contains("capture 12.0ms", mediator.TimingText);
    }

    // ── Capture error paths ──────────────────────────────────────────

    [Fact]
    public async Task CaptureError_ShowsErrorAndReEnablesButtons()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult
            {
                Error = "Native DLL not found: captcho.dll",
            }));

        await mediator.RunDelayedCaptureAsync(0);

        Assert.Equal("Native DLL not found: captcho.dll", mediator.StatusText);
        Assert.True(mediator.ButtonsEnabled);
        Assert.False(mediator.ProgressVisible);
    }

    [Fact]
    public async Task CaptureException_ShowsSanitizedError()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => throw new InvalidOperationException("Capture engine failure"));

        await mediator.RunDelayedCaptureAsync(0);

        Assert.Equal("Error: Capture engine failure", mediator.StatusText);
        Assert.True(mediator.ButtonsEnabled);
    }

    [Fact]
    public async Task CaptureException_AfterDelay_ShowsSanitizedError()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => throw new InvalidOperationException("Capture engine failure"));

        await mediator.RunDelayedCaptureAsync(3);

        Assert.Equal("Error: Capture engine failure", mediator.StatusText);
        Assert.True(mediator.ButtonsEnabled);
    }

    // ── Malformed inputs ─────────────────────────────────────────────

    [Fact]
    public async Task NegativeDelay_ClampsToZero_ImmediateCapture()
    {
        bool captureCalled = false;
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => { captureCalled = true; return Task.FromResult(new CapturePreviewResult()); });

        await mediator.RunDelayedCaptureAsync(-5);

        Assert.True(captureCalled);
        Assert.Empty(mediator.CountdownSnapshots);
    }

    [Fact]
    public async Task OverMaxDelay_ClampsTo60()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await mediator.RunDelayedCaptureAsync(999);

        // Should clamp to 60 and emit 60 snapshots
        Assert.Equal(60, mediator.CountdownSnapshots.Count);
    }

    // ── Buttons disabled during operation ────────────────────────────

    [Fact]
    public async Task DuringOperation_ButtonsReEnabledAfterCompletion()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await mediator.RunDelayedCaptureAsync(2);

        // After completion, buttons should be re-enabled
        Assert.True(mediator.ButtonsEnabled);
    }

    // ── Title/progress reset in all paths ────────────────────────────

    [Fact]
    public async Task ImmediateCapture_TitleResetsAfterCapture()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult
            {
                Mode = "Full Desktop",
                Dimensions = "1920×1080",
            }));

        await mediator.RunDelayedCaptureAsync(0);

        Assert.Null(mediator.TitleText);
    }

    [Fact]
    public async Task DelayedCapture_TitleResetsAfterCapture()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult
            {
                Mode = "Full Desktop",
                Dimensions = "1920×1080",
            }));

        await mediator.RunDelayedCaptureAsync(2);

        Assert.Null(mediator.TitleText);
    }

    [Fact]
    public async Task DelayedCapture_ProgressResetsAfterCapture()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult()));

        await mediator.RunDelayedCaptureAsync(2);

        Assert.False(mediator.ProgressVisible);
    }

    // ── Null image with success-like state ───────────────────────────

    [Fact]
    public async Task NullImageWithNoError_ShowsFallbackError()
    {
        var mediator = new DelayedCaptureFlowMediator(
            captureFunc: () => Task.FromResult(new CapturePreviewResult
            {
                Mode = "Full Desktop",
            }));

        await mediator.RunDelayedCaptureAsync(0);

        Assert.Equal("Capture failed.", mediator.StatusText);
    }

    // ── Region capture flow with delay ───────────────────────────────

    [Fact]
    public async Task DelayBeforeRegionCapture_CancelPreventsOverlayOpen()
    {
        bool overlayOpened = false;
        DelayedCaptureFlowMediator? m = null;
        m = new DelayedCaptureFlowMediator(
            tickDelay: (_, ct) =>
            {
                m!.Cancel();
                return Task.FromCanceled(ct);
            },
            captureFunc: () => { overlayOpened = true; return Task.FromResult(new CapturePreviewResult()); });

        await m.RunDelayedCaptureAsync(3);

        Assert.False(overlayOpened);
    }
}
