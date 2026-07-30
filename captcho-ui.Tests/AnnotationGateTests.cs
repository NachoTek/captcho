using System;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

internal sealed class FakeAnnotationOverlayAdapter : IAnnotationOverlayAdapter
{
    public int CallCount { get; private set; }
    public ContiguousBitmap? LastSourceFrame { get; private set; }
    public AnnotationPresentResult NextResult { get; set; } = AnnotationPresentResult.Cancelled();

    public Task<AnnotationPresentResult> ShowAsync(ContiguousBitmap sourceFrame)
    {
        CallCount++;
        LastSourceFrame = sourceFrame;
        return Task.FromResult(NextResult);
    }
}

public class AnnotationGateTests
{
    [Fact]
    public async Task EnabledAnnotation_ConfirmedFrameReplacesPreviewDeliveryFrame()
    {
        var source = Frame(8, 4);
        var composed = Frame(8, 4);
        var capture = SuccessfulCapture(source);
        var preview = new FakePreviewAdapter();
        var annotation = new FakeAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Confirmed(composed, 3),
        };
        var session = Session(capture, preview, annotation, annotationEnabled: true);

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.Same(source, annotation.LastSourceFrame);
        Assert.Same(composed, result.Frame);
        Assert.Same(composed, session.LastFrame);
        Assert.Equal(0, preview.CallCount);
        Assert.Equal(3, result.DisplayMs);
    }

    [Fact]
    public async Task EnabledAnnotation_CancelAbandonsCapturedFrame()
    {
        var preview = new FakePreviewAdapter();
        var annotation = new FakeAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Cancelled(2),
        };
        var session = Session(SuccessfulCapture(Frame(8, 4)), preview, annotation, annotationEnabled: true);

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.Null(result.Frame);
        Assert.Null(session.LastFrame);
        Assert.Equal(0, preview.CallCount);
    }

    [Fact]
    public async Task DisabledAnnotation_PreservesExistingPreviewPath()
    {
        var source = Frame(8, 4);
        var displayToken = new object();
        var preview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Ok(displayToken, 2),
        };
        var annotation = new FakeAnnotationOverlayAdapter();
        var session = Session(SuccessfulCapture(source), preview, annotation, annotationEnabled: false);

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.Same(displayToken, result.PreviewImage);
        Assert.Same(source, result.Frame);
        Assert.Equal(1, preview.CallCount);
        Assert.Equal(0, annotation.CallCount);
    }

    [Fact]
    public async Task AnnotationPresentationFailure_PreservesSourceFrameForRetry()
    {
        var source = Frame(8, 4);
        var preview = new FakePreviewAdapter();
        var annotation = new FakeAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Fail("Annotation unavailable."),
        };
        var session = Session(SuccessfulCapture(source), preview, annotation, annotationEnabled: true);

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.AnnotationFailed, result.Status);
        Assert.Same(source, result.Frame);
        Assert.Null(session.LastFrame);
        Assert.Contains("Annotation unavailable.", result.Error);
        Assert.Equal(0, preview.CallCount);
    }

    [Fact]
    public void AnnotationResultContract_DistinguishesConfirmedCancelledAndFailed()
    {
        var composed = Frame(2, 2);

        var confirmed = AnnotationPresentResult.Confirmed(composed, 4);
        var cancelled = AnnotationPresentResult.Cancelled(5);
        var failed = AnnotationPresentResult.Fail("Could not present Annotation.");

        Assert.Equal(AnnotationOutcome.Confirmed, confirmed.Outcome);
        Assert.Same(composed, confirmed.Frame);
        Assert.Equal(AnnotationOutcome.Cancelled, cancelled.Outcome);
        Assert.Null(cancelled.Frame);
        Assert.Equal(AnnotationOutcome.Failed, failed.Outcome);
        Assert.Equal("Could not present Annotation.", failed.Error);
    }

    private static CaptureWorkflowSession<object> Session(
        FakeCaptureAdapter capture,
        FakePreviewAdapter preview,
        FakeAnnotationOverlayAdapter annotation,
        bool annotationEnabled) =>
        new(
            capture,
            preview,
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            annotation,
            () => annotationEnabled);

    private static FakeCaptureAdapter SuccessfulCapture(ContiguousBitmap frame) => new()
    {
        NextResult = CaptureFrameResult.Ok(frame, $"{frame.Width}x{frame.Height}", 1),
    };

    private static ContiguousBitmap Frame(int width, int height) =>
        ExportTestHelpers.CreateTestBitmap(width, height);
}
