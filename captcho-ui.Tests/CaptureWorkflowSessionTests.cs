// CaptureWorkflowSessionTests.cs — Production-side workflow tests for the
// runtime capture/post-capture session.
//
// These tests exercise the real CaptureWorkflowSession (the agreed WinUI-free
// behavioral seam) through fake platform adapters. They are the production-side
// specification for routing a Full Desktop Trigger through capture, frame
// ownership, preview transition, operation guarding, and failure outcomes —
// replacing the prior test-only delayed-capture mediator coverage for this
// route. Tests never reach into private fields; they observe the returned
// WorkflowResult and the recorded state of the fake adapters.

using System;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Fake adapters ───────────────────────────────────────────────────────

/// <summary>
/// Fake capture adapter. Records calls per route and returns configurable
/// results. <see cref="CallCount"/>/<see cref="NextResult"/> drive the Full
/// Desktop route; <see cref="ActiveWindowCallCount"/>/<see cref="NextActiveWindowResult"/>
/// drive the Active Window route; <see cref="SelectionCallCount"/>/
/// <see cref="NextSelectionResult"/>/<see cref="LastSelectionGeometry"/> drive the
/// Selection route.
/// </summary>
internal sealed class FakeCaptureAdapter : IWorkflowCaptureAdapter
{
    public int CallCount { get; private set; }
    public CaptureFrameResult? NextResult { get; set; }

    public int ActiveWindowCallCount { get; private set; }
    public CaptureFrameResult? NextActiveWindowResult { get; set; }

    public int SelectionCallCount { get; private set; }
    public CaptureFrameResult? NextSelectionResult { get; set; }
    public SelectionGeometry? LastSelectionGeometry { get; private set; }

    public CaptureFrameResult CaptureFullDesktop()
    {
        CallCount++;
        return NextResult ?? CaptureFrameResult.Fail("FakeCaptureAdapter: NextResult not configured.");
    }

    public CaptureFrameResult CaptureActiveWindow()
    {
        ActiveWindowCallCount++;
        return NextActiveWindowResult ?? CaptureFrameResult.Fail("FakeCaptureAdapter: NextActiveWindowResult not configured.");
    }

    public CaptureFrameResult CaptureSelection(SelectionGeometry geometry)
    {
        SelectionCallCount++;
        LastSelectionGeometry = geometry;
        return NextSelectionResult ?? CaptureFrameResult.Fail("FakeCaptureAdapter: NextSelectionResult not configured.");
    }
}

/// <summary>
/// Fake preview adapter. Records the frames it was asked to present and
/// returns a configurable display token.
/// </summary>
internal sealed class FakePreviewAdapter : IPreviewAdapter<object>
{
    public int CallCount { get; private set; }
    public ContiguousBitmap? LastPresentedFrame { get; private set; }
    public PreviewPresentResult<object>? NextResult { get; set; }

    public PreviewPresentResult<object> Present(ContiguousBitmap frame)
    {
        CallCount++;
        LastPresentedFrame = frame;
        return NextResult ?? PreviewPresentResult<object>.Ok(new object(), 0);
    }
}

/// <summary>
/// Fake Selection overlay adapter. Returns a configurable geometry (or null
/// for cancellation) and records call count. <see cref="NextGeometry"/>
/// defaults to null (cancellation) so existing Full Desktop / Active Window
/// tests can wire the fake without specifying Selection behaviour.
/// </summary>
internal sealed class FakeSelectionOverlayAdapter : ISelectionOverlayAdapter
{
    public int CallCount { get; private set; }
    public SelectionGeometry? NextGeometry { get; set; }

    public Task<SelectionGeometry?> ShowAsync()
    {
        CallCount++;
        return Task.FromResult(NextGeometry);
    }
}

/// <summary>
/// Wraps an inner IWorkflowCaptureAdapter. Before delegating, signals a
/// started TaskCompletionSource and then blocks on a release
/// TaskCompletionSource. Used to keep the first workflow call in-flight on the
/// thread pool while a second concurrent call is issued from the test thread.
/// </summary>
internal sealed class SlowGateCaptureAdapter : IWorkflowCaptureAdapter
{
    private readonly TaskCompletionSource<bool> _started;
    private readonly TaskCompletionSource<bool> _release;
    private readonly IWorkflowCaptureAdapter _inner;

    public SlowGateCaptureAdapter(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> release,
        IWorkflowCaptureAdapter inner)
    {
        _started = started;
        _release = release;
        _inner = inner;
    }

    public CaptureFrameResult CaptureFullDesktop()
    {
        _started.TrySetResult(true);
        _release.Task.Wait();
        return _inner.CaptureFullDesktop();
    }

    public CaptureFrameResult CaptureActiveWindow()
    {
        _started.TrySetResult(true);
        _release.Task.Wait();
        return _inner.CaptureActiveWindow();
    }

    public CaptureFrameResult CaptureSelection(SelectionGeometry geometry)
    {
        _started.TrySetResult(true);
        _release.Task.Wait();
        return _inner.CaptureSelection(geometry);
    }
}

// ── Success tests ───────────────────────────────────────────────────────

public class CaptureWorkflowSessionSuccessTests
{
    private static ContiguousBitmap TestFrame() =>
        ExportTestHelpers.CreateTestBitmap(8, 4);

    private static FakeCaptureAdapter SuccessCapture(double elapsedMs = 5.0) => new()
    {
        NextResult = CaptureFrameResult.Ok(TestFrame(), "8×4", elapsedMs),
    };

    private static FakePreviewAdapter SuccessPreview(object displayToken, double elapsedMs = 2.0) => new()
    {
        NextResult = PreviewPresentResult<object>.Ok(displayToken, elapsedMs),
    };

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_ReturnsSucceededStatus()
    {
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_RoutesThroughCaptureAdapter()
    {
        var capture = SuccessCapture();
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        await session.CaptureFullDesktopAsync();

        Assert.Equal(1, capture.CallCount);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_RoutesThroughPreviewAdapter()
    {
        var expectedDisplayToken = new object();
        var preview = SuccessPreview(expectedDisplayToken);
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), preview, new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(1, preview.CallCount);
        Assert.Same(expectedDisplayToken, result.PreviewImage);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_ReportsFullDesktopMode()
    {
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal("Full Desktop", result.Mode);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_ReportsDimensionsFromFrame()
    {
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal("8×4", result.Dimensions);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_FrameIsTheCapturedFrame()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(frame, "8×4", 1.0),
        };
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Same(frame, result.Frame);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_AdapterReceivesTheCapturedFrame()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(frame, "8×4", 1.0),
        };
        var preview = SuccessPreview(new object());
        var session = new CaptureWorkflowSession<object>(capture, preview, new FakeSelectionOverlayAdapter());

        await session.CaptureFullDesktopAsync();

        Assert.Same(frame, preview.LastPresentedFrame);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_NoErrorIsReported()
    {
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_LastFrameIsRetainedBySession()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(frame, "8×4", 1.0),
        };
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        await session.CaptureFullDesktopAsync();

        Assert.Same(frame, session.LastFrame);
    }
}

// ── Operation guarding tests ─────────────────────────────────────────────

public class CaptureWorkflowSessionOperationGuardTests
{
    private static FakeCaptureAdapter ImmediateCapture() => new()
    {
        NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(2, 2), "2×2", 0),
    };

    private static FakePreviewAdapter ImmediatePreview() => new()
    {
        NextResult = PreviewPresentResult<object>.Ok(new object(), 0),
    };

    [Fact]
    public async Task ConcurrentTrigger_ReturnsOperationInProgressForSecondCall()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var slowCapture = new SlowGateCaptureAdapter(started, release, ImmediateCapture());
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview(), new FakeSelectionOverlayAdapter());

        var first = session.CaptureFullDesktopAsync();
        await started.Task;

        var secondResult = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.OperationInProgress, secondResult.Status);
        Assert.False(secondResult.IsSuccess);

        release.SetResult(true);
        await first;
    }

    [Fact]
    public async Task OperationInProgress_DoesNotInvokeCaptureAdapterAgain()
    {
        // The concurrent Trigger must be rejected at the operation guard, before
        // it reaches the capture adapter. So when the in-flight first call is
        // released and completes, the inner adapter should have been called
        // exactly once — never twice.
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var innerCapture = ImmediateCapture();
        var slowCapture = new SlowGateCaptureAdapter(started, release, innerCapture);
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview(), new FakeSelectionOverlayAdapter());

        var first = session.CaptureFullDesktopAsync();
        await started.Task;
        var secondResult = await session.CaptureFullDesktopAsync();
        Assert.Equal(WorkflowStatus.OperationInProgress, secondResult.Status);

        release.SetResult(true);
        await first;

        Assert.Equal(1, innerCapture.CallCount);
    }

    [Fact]
    public async Task OperationInProgress_ReportsFullDesktopMode()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var slowCapture = new SlowGateCaptureAdapter(started, release, ImmediateCapture());
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview(), new FakeSelectionOverlayAdapter());

        var first = session.CaptureFullDesktopAsync();
        await started.Task;
        var secondResult = await session.CaptureFullDesktopAsync();

        Assert.Equal("Full Desktop", secondResult.Mode);

        release.SetResult(true);
        await first;
    }

    [Fact]
    public async Task OperationInProgress_SurfacesErrorMessage()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var slowCapture = new SlowGateCaptureAdapter(started, release, ImmediateCapture());
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview(), new FakeSelectionOverlayAdapter());

        var first = session.CaptureFullDesktopAsync();
        await started.Task;
        var secondResult = await session.CaptureFullDesktopAsync();

        Assert.False(string.IsNullOrEmpty(secondResult.Error));

        release.SetResult(true);
        await first;
    }

    [Fact]
    public async Task AfterCompletion_SessionAcceptsNextTrigger()
    {
        var capture = ImmediateCapture();
        var session = new CaptureWorkflowSession<object>(capture, ImmediatePreview(), new FakeSelectionOverlayAdapter());

        var first = await session.CaptureFullDesktopAsync();
        var second = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, first.Status);
        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
        Assert.Equal(2, capture.CallCount);
    }
}

// ── Capture failure tests ───────────────────────────────────────────────

public class CaptureWorkflowSessionCaptureFailureTests
{
    [Fact]
    public async Task CaptureFailure_ReturnsCaptureFailedStatus()
    {
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Fail("Native capture unavailable."),
        };
        var preview = new FakePreviewAdapter();
        var session = new CaptureWorkflowSession<object>(capture, preview, new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.CaptureFailed, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CaptureFailure_SurfacesAdapterError()
    {
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Fail("Native capture unavailable."),
        };
        var session = new CaptureWorkflowSession<object>(capture, new FakePreviewAdapter(), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Contains("Native capture unavailable.", result.Error);
    }

    [Fact]
    public async Task CaptureFailure_DoesNotInvokePreview()
    {
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Fail("failed."),
        };
        var preview = new FakePreviewAdapter();
        var session = new CaptureWorkflowSession<object>(capture, preview, new FakeSelectionOverlayAdapter());

        await session.CaptureFullDesktopAsync();

        Assert.Equal(0, preview.CallCount);
    }

    [Fact]
    public async Task CaptureFailure_HasNoFrame()
    {
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Fail("failed."),
        };
        var session = new CaptureWorkflowSession<object>(capture, new FakePreviewAdapter(), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Null(result.Frame);
    }

    [Fact]
    public async Task CaptureFailure_StillReportsMode()
    {
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Fail("failed."),
        };
        var session = new CaptureWorkflowSession<object>(capture, new FakePreviewAdapter(), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal("Full Desktop", result.Mode);
    }

    [Fact]
    public async Task CaptureFailure_SessionIsRetryableOnNextTrigger()
    {
        var failingCapture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Fail("transient."),
        };
        var session = new CaptureWorkflowSession<object>(failingCapture, new FakePreviewAdapter(), new FakeSelectionOverlayAdapter());

        var first = await session.CaptureFullDesktopAsync();
        Assert.Equal(WorkflowStatus.CaptureFailed, first.Status);

        var recoveryFrame = ExportTestHelpers.CreateTestBitmap(4, 4);
        failingCapture.NextResult = CaptureFrameResult.Ok(recoveryFrame, "4×4", 1.0);
        var second = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
        Assert.Same(recoveryFrame, second.Frame);
    }
}

// ── Preview failure tests ─────────────────────────────────────────────────

public class CaptureWorkflowSessionPreviewFailureTests
{
    [Fact]
    public async Task PreviewFailure_ReturnsPreviewFailedStatus()
    {
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 1.0),
        };
        var preview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(capture, preview, new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.PreviewFailed, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task PreviewFailure_SurfacesAdapterError()
    {
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 1.0),
        };
        var preview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(capture, preview, new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Contains("Display conversion failed.", result.Error);
    }

    [Fact]
    public async Task PreviewFailure_PreservesFrameForRetry()
    {
        // Failure policy from the spec: failures must preserve the composed Frame
        // and saved-file identity so failed actions can be retried without another
        // Capture. Preview failure happens after Capture succeeded, so the Frame
        // must remain available on the result.
        var frame = ExportTestHelpers.CreateTestBitmap(4, 4);
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(frame, "4×4", 1.0),
        };
        var preview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(capture, preview, new FakeSelectionOverlayAdapter());

        var result = await session.CaptureFullDesktopAsync();

        Assert.Same(frame, result.Frame);
    }

    [Fact]
    public async Task PreviewFailure_SessionIsRetryableOnNextTrigger()
    {
        var frame = ExportTestHelpers.CreateTestBitmap(4, 4);
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(frame, "4×4", 1.0),
        };
        var failingPreview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(capture, failingPreview, new FakeSelectionOverlayAdapter());

        var first = await session.CaptureFullDesktopAsync();
        Assert.Equal(WorkflowStatus.PreviewFailed, first.Status);

        failingPreview.NextResult = PreviewPresentResult<object>.Ok(new object(), 0);
        var second = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
    }
}

// ── Active Window route tests ───────────────────────────────────────────
//
// Covers the acceptance criteria of issue #26: Active Window routes through
// the same production workflow as Full Desktop. Tests assert externally
// observable behavior — returned WorkflowResult and recorded adapter calls —
// and never reach into private fields. They mirror the Full Desktop suite so
// the two routes stay symmetric at the seam.

public class CaptureWorkflowSessionActiveWindowTests
{
    private static ContiguousBitmap TestFrame() =>
        ExportTestHelpers.CreateTestBitmap(6, 3);

    private static FakeCaptureAdapter SuccessCapture(double elapsedMs = 4.0) => new()
    {
        NextActiveWindowResult = CaptureFrameResult.Ok(TestFrame(), "6×3", elapsedMs),
    };

    private static FakePreviewAdapter SuccessPreview(object displayToken, double elapsedMs = 1.5) => new()
    {
        NextResult = PreviewPresentResult<object>.Ok(displayToken, elapsedMs),
    };

    [Fact]
    public async Task CaptureActiveWindowAsync_OnSuccess_ReturnsSucceededStatus()
    {
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureActiveWindowAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_OnSuccess_RoutesThroughCaptureAdapter()
    {
        var capture = SuccessCapture();
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        await session.CaptureActiveWindowAsync();

        Assert.Equal(1, capture.ActiveWindowCallCount);
        // The Full Desktop route must not be invoked by an Active Window Trigger.
        Assert.Equal(0, capture.CallCount);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_OnSuccess_RoutesThroughPreviewAdapter()
    {
        var expectedDisplayToken = new object();
        var preview = SuccessPreview(expectedDisplayToken);
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), preview, new FakeSelectionOverlayAdapter());

        var result = await session.CaptureActiveWindowAsync();

        Assert.Equal(1, preview.CallCount);
        Assert.Same(expectedDisplayToken, result.PreviewImage);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_OnSuccess_ReportsActiveWindowMode()
    {
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureActiveWindowAsync();

        Assert.Equal("Active Window", result.Mode);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_OnSuccess_FrameIsTheCapturedFrame()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextActiveWindowResult = CaptureFrameResult.Ok(frame, "6×3", 1.0),
        };
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        var result = await session.CaptureActiveWindowAsync();

        Assert.Same(frame, result.Frame);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_OnSuccess_LastFrameIsRetainedBySession()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextActiveWindowResult = CaptureFrameResult.Ok(frame, "6×3", 1.0),
        };
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        await session.CaptureActiveWindowAsync();

        Assert.Same(frame, session.LastFrame);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_WhenNoEligibleTarget_ReturnsCaptureFailed()
    {
        // "Unavailable target": there is no eligible active window to capture.
        // The adapter surfaces this as a capture failure; the workflow must not
        // produce a Frame and must not retain stale state.
        var capture = new FakeCaptureAdapter
        {
            NextActiveWindowResult = CaptureFrameResult.Fail("No eligible active window."),
        };
        var preview = new FakePreviewAdapter();
        var session = new CaptureWorkflowSession<object>(capture, preview, new FakeSelectionOverlayAdapter());

        var result = await session.CaptureActiveWindowAsync();

        Assert.Equal(WorkflowStatus.CaptureFailed, result.Status);
        Assert.Null(result.Frame);
        Assert.Equal(0, preview.CallCount);
        Assert.Contains("No eligible active window.", result.Error);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_WhenNoEligibleTarget_DoesNotClearPriorFrame()
    {
        // A failed Active Window Trigger (unavailable target) must not wipe a
        // previously captured Frame — the failure policy preserves state so the
        // user can retry without losing the last good capture.
        var firstFrame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextActiveWindowResult = CaptureFrameResult.Ok(firstFrame, "6×3", 1.0),
        };
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()), new FakeSelectionOverlayAdapter());

        await session.CaptureActiveWindowAsync();
        Assert.Same(firstFrame, session.LastFrame);

        capture.NextActiveWindowResult = CaptureFrameResult.Fail("No eligible active window.");
        var failed = await session.CaptureActiveWindowAsync();

        Assert.Equal(WorkflowStatus.CaptureFailed, failed.Status);
        Assert.Same(firstFrame, session.LastFrame);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_OnCaptureFailure_IsRetryableOnNextTrigger()
    {
        var failingCapture = new FakeCaptureAdapter
        {
            NextActiveWindowResult = CaptureFrameResult.Fail("transient."),
        };
        var session = new CaptureWorkflowSession<object>(failingCapture, new FakePreviewAdapter(), new FakeSelectionOverlayAdapter());

        var first = await session.CaptureActiveWindowAsync();
        Assert.Equal(WorkflowStatus.CaptureFailed, first.Status);

        var recoveryFrame = TestFrame();
        failingCapture.NextActiveWindowResult = CaptureFrameResult.Ok(recoveryFrame, "6×3", 1.0);
        var second = await session.CaptureActiveWindowAsync();

        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
        Assert.Same(recoveryFrame, second.Frame);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_OnPreviewFailure_PreservesFrameForRetry()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextActiveWindowResult = CaptureFrameResult.Ok(frame, "6×3", 1.0),
        };
        var preview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(capture, preview, new FakeSelectionOverlayAdapter());

        var result = await session.CaptureActiveWindowAsync();

        Assert.Equal(WorkflowStatus.PreviewFailed, result.Status);
        Assert.Same(frame, result.Frame);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_OnPreviewFailure_ReportsActiveWindowMode()
    {
        var capture = new FakeCaptureAdapter
        {
            NextActiveWindowResult = CaptureFrameResult.Ok(TestFrame(), "6×3", 1.0),
        };
        var preview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(capture, preview, new FakeSelectionOverlayAdapter());

        var result = await session.CaptureActiveWindowAsync();

        Assert.Equal("Active Window", result.Mode);
    }

    [Fact]
    public async Task CaptureActiveWindowAsync_OnPreviewFailure_IsRetryableOnNextTrigger()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextActiveWindowResult = CaptureFrameResult.Ok(frame, "6×3", 1.0),
        };
        var failingPreview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(capture, failingPreview, new FakeSelectionOverlayAdapter());

        var first = await session.CaptureActiveWindowAsync();
        Assert.Equal(WorkflowStatus.PreviewFailed, first.Status);

        failingPreview.NextResult = PreviewPresentResult<object>.Ok(new object(), 0);
        var second = await session.CaptureActiveWindowAsync();

        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
    }
}

// ── Cross-route operation guarding tests ──────────────────────────────────
//
// The session owns ONE operation guard across all Capture Mode routes. These
// tests verify that an Active Window Trigger is rejected while a Full Desktop
// capture is in flight (and vice versa), so concurrent Triggers of different
// modes cannot interleave.

public class CaptureWorkflowSessionCrossRouteGuardTests
{
    private static FakeCaptureAdapter ImmediateCapture() => new()
    {
        NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(2, 2), "2×2", 0),
        NextActiveWindowResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(3, 3), "3×3", 0),
    };

    private static FakePreviewAdapter ImmediatePreview() => new()
    {
        NextResult = PreviewPresentResult<object>.Ok(new object(), 0),
    };

    [Fact]
    public async Task ActiveWindowTrigger_WhileFullDesktopInFlight_IsRejected()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var innerCapture = ImmediateCapture();
        var slowCapture = new SlowGateCaptureAdapter(started, release, innerCapture);
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview(), new FakeSelectionOverlayAdapter());

        var first = session.CaptureFullDesktopAsync();
        await started.Task;

        var secondResult = await session.CaptureActiveWindowAsync();

        Assert.Equal(WorkflowStatus.OperationInProgress, secondResult.Status);
        Assert.Equal("Active Window", secondResult.Mode);

        release.SetResult(true);
        await first;

        // The in-flight Full Desktop route must be the only capture call.
        Assert.Equal(1, innerCapture.CallCount);
        Assert.Equal(0, innerCapture.ActiveWindowCallCount);
    }

    [Fact]
    public async Task FullDesktopTrigger_WhileActiveWindowInFlight_IsRejected()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var innerCapture = ImmediateCapture();
        var slowCapture = new SlowGateCaptureAdapter(started, release, innerCapture);
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview(), new FakeSelectionOverlayAdapter());

        var first = session.CaptureActiveWindowAsync();
        await started.Task;

        var secondResult = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.OperationInProgress, secondResult.Status);
        Assert.Equal("Full Desktop", secondResult.Mode);

        release.SetResult(true);
        await first;

        Assert.Equal(0, innerCapture.CallCount);
        Assert.Equal(1, innerCapture.ActiveWindowCallCount);
    }

    [Fact]
    public async Task AfterActiveWindowCompletes_FullDesktopTriggerIsAccepted()
    {
        var capture = ImmediateCapture();
        var session = new CaptureWorkflowSession<object>(capture, ImmediatePreview(), new FakeSelectionOverlayAdapter());

        var first = await session.CaptureActiveWindowAsync();
        var second = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, first.Status);
        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
        Assert.Equal(1, capture.ActiveWindowCallCount);
        Assert.Equal(1, capture.CallCount);
    }
}

// ── Selection route tests ───────────────────────────────────────────────
//
// Covers the acceptance criteria of issue #27: Selection Target Selection
// returns confirmed geometry or cancellation to the runtime workflow, which
// then owns Capture, the resulting Frame, and preview. Cross-monitor and
// negative-coordinate selections must reach the capture adapter untouched.
// The overlay must not perform Capture or own post-capture state. Tests
// observe the returned WorkflowResult and recorded adapter calls — never
// private fields.

public class CaptureWorkflowSessionSelectionTests
{
    private static ContiguousBitmap TestFrame() =>
        ExportTestHelpers.CreateTestBitmap(10, 7);

    private static SelectionGeometry Geometry(int x, int y, uint w, uint h) => new()
    {
        X = x, Y = y, Width = w, Height = h,
    };

    private static FakeCaptureAdapter SuccessCapture(double elapsedMs = 3.0) => new()
    {
        NextSelectionResult = CaptureFrameResult.Ok(TestFrame(), "10×7", elapsedMs),
    };

    private static FakePreviewAdapter SuccessPreview(object displayToken, double elapsedMs = 1.5) => new()
    {
        NextResult = PreviewPresentResult<object>.Ok(displayToken, elapsedMs),
    };

    private static FakeSelectionOverlayAdapter ConfirmingOverlay(SelectionGeometry geometry) => new()
    {
        NextGeometry = geometry,
    };

    // ── Confirmation → Capture → Preview success path ───────────────────

    [Fact]
    public async Task CaptureSelectionAsync_OnConfirm_ReturnsSucceededStatus()
    {
        var session = new CaptureWorkflowSession<object>(
            SuccessCapture(), SuccessPreview(new object()), ConfirmingOverlay(Geometry(10, 20, 800, 600)));

        var result = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnConfirm_ShowsOverlayExactlyOnce()
    {
        var overlay = ConfirmingOverlay(Geometry(0, 0, 100, 100));
        var session = new CaptureWorkflowSession<object>(
            SuccessCapture(), SuccessPreview(new object()), overlay);

        await session.CaptureSelectionAsync();

        Assert.Equal(1, overlay.CallCount);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnConfirm_RoutesGeometryThroughCaptureAdapter()
    {
        var capture = SuccessCapture();
        var geometry = Geometry(100, 200, 800, 600);
        var session = new CaptureWorkflowSession<object>(
            capture, SuccessPreview(new object()), ConfirmingOverlay(geometry));

        await session.CaptureSelectionAsync();

        Assert.Equal(1, capture.SelectionCallCount);
        Assert.Equal(0, capture.CallCount);
        Assert.Equal(0, capture.ActiveWindowCallCount);
        Assert.Same(geometry, capture.LastSelectionGeometry);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnConfirm_RoutesFrameThroughPreviewAdapter()
    {
        var expectedDisplayToken = new object();
        var preview = SuccessPreview(expectedDisplayToken);
        var session = new CaptureWorkflowSession<object>(
            SuccessCapture(), preview, ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        var result = await session.CaptureSelectionAsync();

        Assert.Equal(1, preview.CallCount);
        Assert.Same(expectedDisplayToken, result.PreviewImage);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnConfirm_ReportsSelectionMode()
    {
        var session = new CaptureWorkflowSession<object>(
            SuccessCapture(), SuccessPreview(new object()), ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        var result = await session.CaptureSelectionAsync();

        Assert.Equal("Selection", result.Mode);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnConfirm_FrameIsTheCapturedFrame()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextSelectionResult = CaptureFrameResult.Ok(frame, "10×7", 1.0),
        };
        var session = new CaptureWorkflowSession<object>(
            capture, SuccessPreview(new object()), ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        var result = await session.CaptureSelectionAsync();

        Assert.Same(frame, result.Frame);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnConfirm_LastFrameIsRetainedBySession()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextSelectionResult = CaptureFrameResult.Ok(frame, "10×7", 1.0),
        };
        var session = new CaptureWorkflowSession<object>(
            capture, SuccessPreview(new object()), ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        await session.CaptureSelectionAsync();

        Assert.Same(frame, session.LastFrame);
    }

    // ── Cross-monitor / negative-coordinate pass-through ────────────────
    //
    // The Selection overlay returns geometry in Virtual Desktop coordinates.
    // Multi-monitor layouts can place monitors at negative X/Y, so the
    // workflow must forward signed origins to the capture adapter untouched.
    // This is the test the spec calls out explicitly for #27.

    [Fact]
    public async Task CaptureSelectionAsync_NegativeOrigin_GeometryReachesCaptureUnchanged()
    {
        var capture = SuccessCapture();
        var session = new CaptureWorkflowSession<object>(
            capture, SuccessPreview(new object()), ConfirmingOverlay(Geometry(-1920, -1080, 1920, 1080)));

        await session.CaptureSelectionAsync();

        Assert.Equal(-1920, capture.LastSelectionGeometry!.X);
        Assert.Equal(-1080, capture.LastSelectionGeometry!.Y);
        Assert.Equal(1920u, capture.LastSelectionGeometry!.Width);
        Assert.Equal(1080u, capture.LastSelectionGeometry!.Height);
    }

    [Fact]
    public async Task CaptureSelectionAsync_CrossMonitorSpan_GeometryReachesCaptureUnchanged()
    {
        // A selection that spans from a monitor at negative X across the
        // origin onto the primary monitor — the spec's "mixed-monitor layout"
        // case. The signed origin and full span must reach capture unchanged.
        var capture = SuccessCapture();
        var session = new CaptureWorkflowSession<object>(
            capture, SuccessPreview(new object()), ConfirmingOverlay(Geometry(-960, 0, 2880, 1080)));

        await session.CaptureSelectionAsync();

        Assert.Equal(-960, capture.LastSelectionGeometry!.X);
        Assert.Equal(2880u, capture.LastSelectionGeometry!.Width);
    }

    // ── Cancellation path ──────────────────────────────────────────────
    //
    // Escape or overlay dismissal must end the operation without a Frame or
    // delivery side effects. Capture is never invoked.

    [Fact]
    public async Task CaptureSelectionAsync_OnCancel_ReturnsCancelledStatus()
    {
        var cancellingOverlay = new FakeSelectionOverlayAdapter(); // NextGeometry defaults to null
        var session = new CaptureWorkflowSession<object>(
            SuccessCapture(), SuccessPreview(new object()), cancellingOverlay);

        var result = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnCancel_DoesNotInvokeCaptureAdapter()
    {
        var cancellingOverlay = new FakeSelectionOverlayAdapter();
        var capture = SuccessCapture();
        var session = new CaptureWorkflowSession<object>(
            capture, SuccessPreview(new object()), cancellingOverlay);

        await session.CaptureSelectionAsync();

        Assert.Equal(0, capture.SelectionCallCount);
        Assert.Equal(0, capture.CallCount);
        Assert.Equal(0, capture.ActiveWindowCallCount);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnCancel_DoesNotInvokePreviewAdapter()
    {
        var cancellingOverlay = new FakeSelectionOverlayAdapter();
        var preview = SuccessPreview(new object());
        var session = new CaptureWorkflowSession<object>(
            SuccessCapture(), preview, cancellingOverlay);

        await session.CaptureSelectionAsync();

        Assert.Equal(0, preview.CallCount);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnCancel_HasNoFrame()
    {
        var cancellingOverlay = new FakeSelectionOverlayAdapter();
        var session = new CaptureWorkflowSession<object>(
            SuccessCapture(), SuccessPreview(new object()), cancellingOverlay);

        var result = await session.CaptureSelectionAsync();

        Assert.Null(result.Frame);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnCancel_ReportsSelectionMode()
    {
        var cancellingOverlay = new FakeSelectionOverlayAdapter();
        var session = new CaptureWorkflowSession<object>(
            SuccessCapture(), SuccessPreview(new object()), cancellingOverlay);

        var result = await session.CaptureSelectionAsync();

        Assert.Equal("Selection", result.Mode);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnCancel_SessionIsRetryableOnNextTrigger()
    {
        var cancellingOverlay = new FakeSelectionOverlayAdapter();
        var session = new CaptureWorkflowSession<object>(
            SuccessCapture(), SuccessPreview(new object()), cancellingOverlay);

        var first = await session.CaptureSelectionAsync();
        Assert.Equal(WorkflowStatus.Cancelled, first.Status);

        // A subsequent confirming Trigger must succeed normally.
        cancellingOverlay.NextGeometry = Geometry(0, 0, 10, 10);
        var second = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
    }

    // ── Capture failure ────────────────────────────────────────────────

    [Fact]
    public async Task CaptureSelectionAsync_OnCaptureFailure_ReturnsCaptureFailedStatus()
    {
        var capture = new FakeCaptureAdapter
        {
            NextSelectionResult = CaptureFrameResult.Fail("Native region capture unavailable."),
        };
        var session = new CaptureWorkflowSession<object>(
            capture, new FakePreviewAdapter(), ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        var result = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.CaptureFailed, result.Status);
        Assert.Null(result.Frame);
        Assert.Contains("Native region capture unavailable.", result.Error);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnCaptureFailure_DoesNotInvokePreview()
    {
        var capture = new FakeCaptureAdapter
        {
            NextSelectionResult = CaptureFrameResult.Fail("failed."),
        };
        var preview = new FakePreviewAdapter();
        var session = new CaptureWorkflowSession<object>(
            capture, preview, ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        await session.CaptureSelectionAsync();

        Assert.Equal(0, preview.CallCount);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnCaptureFailure_IsRetryableOnNextTrigger()
    {
        var failingCapture = new FakeCaptureAdapter
        {
            NextSelectionResult = CaptureFrameResult.Fail("transient."),
        };
        var session = new CaptureWorkflowSession<object>(
            failingCapture, new FakePreviewAdapter(), ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        var first = await session.CaptureSelectionAsync();
        Assert.Equal(WorkflowStatus.CaptureFailed, first.Status);

        var recoveryFrame = TestFrame();
        failingCapture.NextSelectionResult = CaptureFrameResult.Ok(recoveryFrame, "10×7", 1.0);
        var second = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
        Assert.Same(recoveryFrame, second.Frame);
    }

    // ── Preview failure ────────────────────────────────────────────────

    [Fact]
    public async Task CaptureSelectionAsync_OnPreviewFailure_PreservesFrameForRetry()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextSelectionResult = CaptureFrameResult.Ok(frame, "10×7", 1.0),
        };
        var failingPreview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(
            capture, failingPreview, ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        var result = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.PreviewFailed, result.Status);
        Assert.Same(frame, result.Frame);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnPreviewFailure_ReportsSelectionMode()
    {
        var capture = new FakeCaptureAdapter
        {
            NextSelectionResult = CaptureFrameResult.Ok(TestFrame(), "10×7", 1.0),
        };
        var failingPreview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(
            capture, failingPreview, ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        var result = await session.CaptureSelectionAsync();

        Assert.Equal("Selection", result.Mode);
    }

    [Fact]
    public async Task CaptureSelectionAsync_OnPreviewFailure_IsRetryableOnNextTrigger()
    {
        var frame = TestFrame();
        var capture = new FakeCaptureAdapter
        {
            NextSelectionResult = CaptureFrameResult.Ok(frame, "10×7", 1.0),
        };
        var failingPreview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Fail("Display conversion failed."),
        };
        var session = new CaptureWorkflowSession<object>(
            capture, failingPreview, ConfirmingOverlay(Geometry(0, 0, 10, 10)));

        var first = await session.CaptureSelectionAsync();
        Assert.Equal(WorkflowStatus.PreviewFailed, first.Status);

        failingPreview.NextResult = PreviewPresentResult<object>.Ok(new object(), 0);
        var second = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
    }
}

// ── Selection cross-route operation guarding tests ───────────────────────
//
// Extends the cross-route guard coverage from the immediate-capture routes
// to Selection: a Selection Trigger must be rejected while Full Desktop or
// Active Window is in flight (and vice versa), and the in-flight route's
// adapter must be the only one invoked.

public class CaptureWorkflowSessionSelectionCrossRouteGuardTests
{
    private static FakeCaptureAdapter ImmediateCapture() => new()
    {
        NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(2, 2), "2×2", 0),
        NextActiveWindowResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(3, 3), "3×3", 0),
        NextSelectionResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 0),
    };

    private static FakePreviewAdapter ImmediatePreview() => new()
    {
        NextResult = PreviewPresentResult<object>.Ok(new object(), 0),
    };

    private static FakeSelectionOverlayAdapter ImmediateOverlay() => new()
    {
        NextGeometry = new SelectionGeometry { X = 0, Y = 0, Width = 10, Height = 10 },
    };

    [Fact]
    public async Task SelectionTrigger_WhileFullDesktopInFlight_IsRejected()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var innerCapture = ImmediateCapture();
        var slowCapture = new SlowGateCaptureAdapter(started, release, innerCapture);
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview(), ImmediateOverlay());

        var first = session.CaptureFullDesktopAsync();
        await started.Task;

        var secondResult = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.OperationInProgress, secondResult.Status);
        Assert.Equal("Selection", secondResult.Mode);

        release.SetResult(true);
        await first;

        Assert.Equal(1, innerCapture.CallCount);
        Assert.Equal(0, innerCapture.SelectionCallCount);
    }

    [Fact]
    public async Task FullDesktopTrigger_WhileSelectionOverlayShown_IsRejected()
    {
        // The Selection overlay itself is shown on the test thread and returns
        // immediately (the fake does not block), so to test "Full Desktop
        // arriving while Selection is in flight" we make the region Capture
        // hang via SlowGateCaptureAdapter. The overlay has already returned
        // geometry; the in-flight work is the region Capture.
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var innerCapture = ImmediateCapture();
        var slowCapture = new SlowGateCaptureAdapter(started, release, innerCapture);
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview(), ImmediateOverlay());

        var first = session.CaptureSelectionAsync();
        await started.Task;

        var secondResult = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.OperationInProgress, secondResult.Status);
        Assert.Equal("Full Desktop", secondResult.Mode);

        release.SetResult(true);
        await first;

        Assert.Equal(1, innerCapture.SelectionCallCount);
        Assert.Equal(0, innerCapture.CallCount);
    }

    [Fact]
    public async Task SelectionTrigger_WhileActiveWindowInFlight_IsRejected()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var innerCapture = ImmediateCapture();
        var slowCapture = new SlowGateCaptureAdapter(started, release, innerCapture);
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview(), ImmediateOverlay());

        var first = session.CaptureActiveWindowAsync();
        await started.Task;

        var secondResult = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.OperationInProgress, secondResult.Status);
        Assert.Equal("Selection", secondResult.Mode);

        release.SetResult(true);
        await first;

        Assert.Equal(1, innerCapture.ActiveWindowCallCount);
        Assert.Equal(0, innerCapture.SelectionCallCount);
    }

    [Fact]
    public async Task AfterSelectionCompletes_FullDesktopTriggerIsAccepted()
    {
        var capture = ImmediateCapture();
        var session = new CaptureWorkflowSession<object>(capture, ImmediatePreview(), ImmediateOverlay());

        var first = await session.CaptureSelectionAsync();
        var second = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, first.Status);
        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
        Assert.Equal(1, capture.SelectionCallCount);
        Assert.Equal(1, capture.CallCount);
    }
}
