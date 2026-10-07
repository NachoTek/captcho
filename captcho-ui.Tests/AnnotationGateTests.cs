using System;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

internal class FakeAnnotationOverlayAdapter : IAnnotationOverlayAdapter
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

internal sealed class FakeConfiguredAnnotationOverlayAdapter : FakeAnnotationOverlayAdapter, IAnnotationOverlayStateAdapter
{
    public AnnotationToolState? InitialToolState { get; private set; }

    public Task<AnnotationPresentResult> ShowAsync(
        ContiguousBitmap sourceFrame,
        AnnotationToolState initialToolState)
    {
        InitialToolState = initialToolState;
        return base.ShowAsync(sourceFrame);
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

    [Fact]
    public async Task EnabledAnnotation_ReceivesCommittedToolDefaultsForNewSession()
    {
        var defaults = new AnnotationToolState(
            AnnotationTool.Pen,
            new AnnotationColor(12, 34, 56),
            9);
        var annotation = new FakeConfiguredAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Confirmed(Frame(2, 2)),
        };
        var session = new CaptureWorkflowSession<object>(
            SuccessfulCapture(Frame(2, 2)),
            new FakePreviewAdapter(),
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            annotation,
            () => true,
            () => defaults);

        await session.CaptureFullDesktopAsync();

        Assert.Equal(defaults, annotation.InitialToolState);
    }

    [Fact]
    public async Task Workflow_ConfirmedPenFrameCarriesTheComposedPixels()
    {
        var source = BitmapBufferConverter.StripPadding(
            new byte[4 * 4],
            4,
            1,
            16);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.BeginStroke(new AnnotationPoint(0, 0));
            script.AppendStrokePoint(new AnnotationPoint(3, 0));
            script.CommitStroke();
        });
        var session = Session(
            SuccessfulCapture(source),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Pen, new AnnotationColor(255, 0, 0), 1));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        Assert.Equal(255, result.Frame!.Pixels[2]);
        Assert.Equal(255, result.Frame.Pixels[3]);
    }

    // spec #38: undo and redo committed annotations

    [Fact]
    public async Task Workflow_ConfirmedFrameAfterUndo_ComposesWithoutTheUndoneStroke()
    {
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.BeginStroke(new AnnotationPoint(0, 0));
            script.CommitStroke();
            script.BeginStroke(new AnnotationPoint(3, 0));
            script.CommitStroke();
            script.Undo();
        });
        var session = Session(
            SuccessfulCapture(Frame6x1()),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Pen, new AnnotationColor(255, 0, 0), 1));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        Assert.Equal(255, result.Frame!.Pixels[2]);
        Assert.Equal(0, result.Frame.Pixels[3 * 4 + 2]);
        Assert.Equal(0, result.Frame.Pixels[3 * 4 + 3]);
    }

    [Fact]
    public async Task Workflow_ConfirmedFrameAfterRedo_ComposesWithTheRestoredStroke()
    {
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.BeginStroke(new AnnotationPoint(0, 0));
            script.CommitStroke();
            script.BeginStroke(new AnnotationPoint(3, 0));
            script.CommitStroke();
            script.Undo();
            script.Redo();
        });
        var session = Session(
            SuccessfulCapture(Frame6x1()),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Pen, new AnnotationColor(255, 0, 0), 1));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        Assert.Equal(255, result.Frame!.Pixels[2]);
        Assert.Equal(255, result.Frame!.Pixels[3 * 4 + 2]);
    }

    [Fact]
    public async Task Workflow_CommittingAfterUndo_ClearsTheRedoBranch()
    {
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.BeginStroke(new AnnotationPoint(0, 0));
            script.CommitStroke();
            script.Undo();
            script.BeginStroke(new AnnotationPoint(3, 0));
            script.CommitStroke();
            Assert.False(script.Redo());
        });
        var session = Session(
            SuccessfulCapture(Frame6x1()),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Pen, new AnnotationColor(255, 0, 0), 1));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        Assert.Equal(0, result.Frame!.Pixels[2]);
        Assert.Equal(0, result.Frame.Pixels[3]);
        Assert.Equal(255, result.Frame.Pixels[3 * 4 + 2]);
    }

    [Fact]
    public async Task Workflow_EmptyHistory_ConfirmedFrameEqualsTheSourcePixels()
    {
        var source = Frame6x1();
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            Assert.False(script.CanUndo);
            Assert.False(script.CanRedo);
            Assert.False(script.Undo());
            Assert.False(script.Redo());
        });
        var session = Session(
            SuccessfulCapture(source),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Pen, new AnnotationColor(255, 0, 0), 1));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        Assert.Equal(source.Pixels, result.Frame!.Pixels);
    }

    private static ContiguousBitmap Frame6x1() =>
        BitmapBufferConverter.StripPadding(
            new byte[6 * 4],
            6,
            1,
            24);

    private static CaptureWorkflowSession<object> Session(
        FakeCaptureAdapter capture,
        FakePreviewAdapter preview,
        IAnnotationOverlayAdapter annotation,
        bool annotationEnabled,
        AnnotationToolState? toolState = null) =>
        new(
            capture,
            preview,
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            annotation,
            () => annotationEnabled,
            () => toolState ?? AnnotationToolState.WithDefaults());

    private static FakeCaptureAdapter SuccessfulCapture(ContiguousBitmap frame) => new()
    {
        NextResult = CaptureFrameResult.Ok(frame, $"{frame.Width}x{frame.Height}", 1),
    };

    private static ContiguousBitmap Frame(int width, int height) =>
        ExportTestHelpers.CreateTestBitmap(width, height);
}

internal sealed class ScriptedAnnotationOverlayAdapter : IAnnotationOverlayStateAdapter
{
    private readonly Action<AnnotationSession> _script;

    public ScriptedAnnotationOverlayAdapter(Action<AnnotationSession> script) => _script = script;

    public Task<AnnotationPresentResult> ShowAsync(ContiguousBitmap sourceFrame) =>
        ShowAsync(sourceFrame, AnnotationToolState.WithDefaults());

    public Task<AnnotationPresentResult> ShowAsync(
        ContiguousBitmap sourceFrame,
        AnnotationToolState initialToolState)
    {
        var annotation = new AnnotationSession(sourceFrame, initialToolState);
        _script(annotation);
        return Task.FromResult(AnnotationPresentResult.Confirmed(annotation.Render()));
    }
}
