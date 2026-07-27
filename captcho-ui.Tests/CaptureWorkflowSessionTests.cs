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
/// Fake capture adapter for Full Desktop. Records calls and returns
/// configurable results.
/// </summary>
internal sealed class FakeCaptureAdapter : IWorkflowCaptureAdapter
{
    public int CallCount { get; private set; }
    public CaptureFrameResult? NextResult { get; set; }

    public CaptureFrameResult CaptureFullDesktop()
    {
        CallCount++;
        return NextResult ?? CaptureFrameResult.Fail("FakeCaptureAdapter: NextResult not configured.");
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
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_RoutesThroughCaptureAdapter()
    {
        var capture = SuccessCapture();
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()));

        await session.CaptureFullDesktopAsync();

        Assert.Equal(1, capture.CallCount);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_RoutesThroughPreviewAdapter()
    {
        var expectedDisplayToken = new object();
        var preview = SuccessPreview(expectedDisplayToken);
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), preview);

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(1, preview.CallCount);
        Assert.Same(expectedDisplayToken, result.PreviewImage);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_ReportsFullDesktopMode()
    {
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal("Full Desktop", result.Mode);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_ReportsDimensionsFromFrame()
    {
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()));

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
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()));

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
        var session = new CaptureWorkflowSession<object>(capture, preview);

        await session.CaptureFullDesktopAsync();

        Assert.Same(frame, preview.LastPresentedFrame);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnSuccess_NoErrorIsReported()
    {
        var session = new CaptureWorkflowSession<object>(SuccessCapture(), SuccessPreview(new object()));

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
        var session = new CaptureWorkflowSession<object>(capture, SuccessPreview(new object()));

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
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview());

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
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview());

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
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview());

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
        var session = new CaptureWorkflowSession<object>(slowCapture, ImmediatePreview());

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
        var session = new CaptureWorkflowSession<object>(capture, ImmediatePreview());

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
        var session = new CaptureWorkflowSession<object>(capture, preview);

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
        var session = new CaptureWorkflowSession<object>(capture, new FakePreviewAdapter());

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
        var session = new CaptureWorkflowSession<object>(capture, preview);

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
        var session = new CaptureWorkflowSession<object>(capture, new FakePreviewAdapter());

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
        var session = new CaptureWorkflowSession<object>(capture, new FakePreviewAdapter());

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
        var session = new CaptureWorkflowSession<object>(failingCapture, new FakePreviewAdapter());

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
        var session = new CaptureWorkflowSession<object>(capture, preview);

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
        var session = new CaptureWorkflowSession<object>(capture, preview);

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
        var session = new CaptureWorkflowSession<object>(capture, preview);

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
        var session = new CaptureWorkflowSession<object>(capture, failingPreview);

        var first = await session.CaptureFullDesktopAsync();
        Assert.Equal(WorkflowStatus.PreviewFailed, first.Status);

        failingPreview.NextResult = PreviewPresentResult<object>.Ok(new object(), 0);
        var second = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, second.Status);
    }
}
