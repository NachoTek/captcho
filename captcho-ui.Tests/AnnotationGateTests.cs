using System;
using System.Linq;
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

    // spec #39: shape tools and shared style controls

    [Theory]
    [InlineData(AnnotationTool.Rectangle)]
    [InlineData(AnnotationTool.Line)]
    [InlineData(AnnotationTool.Arrow)]
    public async Task Workflow_ConfirmedShapeFrameCarriesTheComposedPixels(AnnotationTool tool)
    {
        // 6x1 black frame; a shape from (0,0) to (5,0) crosses pixel (2,0).
        var source = Frame6x1();
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(tool);
            script.BeginStroke(new AnnotationPoint(0, 0));
            script.UpdateStrokePoint(new AnnotationPoint(5, 0));
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
        Assert.Equal(255, result.Frame!.Pixels[2 * 4 + 2]);
        Assert.Equal(255, result.Frame.Pixels[2 * 4 + 3]);
    }

    [Fact]
    public async Task Workflow_ConfirmedEllipseFrameCarriesTheComposedPixels()
    {
        // 6x3 black frame; an ellipse from (0,1) to (5,1) passes through its center row.
        var source = BitmapBufferConverter.StripPadding(new byte[6 * 3 * 4], 6, 3, 24);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Ellipse);
            script.BeginStroke(new AnnotationPoint(0, 1));
            script.UpdateStrokePoint(new AnnotationPoint(5, 1));
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
        Assert.Equal(255, result.Frame!.Pixels[(1 * 6 + 2) * 4 + 2]);
        Assert.Equal(255, result.Frame.Pixels[(1 * 6 + 2) * 4 + 3]);
    }

    [Fact]
    public async Task Workflow_ConfirmedFilledRectangleFrameCarriesFillPixels()
    {
        var source = Frame6x1();
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Rectangle);
            script.SetFill(AnnotationFillStyle.Solid);
            script.BeginStroke(new AnnotationPoint(0, 0));
            script.UpdateStrokePoint(new AnnotationPoint(5, 0));
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
        // Every pixel of the single row is covered by the filled rectangle.
        for (int x = 0; x < 6; x++)
        {
            Assert.Equal(255, result.Frame!.Pixels[x * 4 + 2]);
            Assert.Equal(255, result.Frame.Pixels[x * 4 + 3]);
        }
    }

    [Fact]
    public async Task Workflow_SharedStyleControlsApplyToPenAndShapes()
    {
        var source = Frame6x1();
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            // One shared color/width set once, then used by both tools.
            script.SetPenColor(new AnnotationColor(0, 255, 0));
            script.SetStrokeWidth(2);
            script.SetTool(AnnotationTool.Line);
            script.BeginStroke(new AnnotationPoint(0, 0));
            script.UpdateStrokePoint(new AnnotationPoint(2, 0));
            script.CommitStroke();
            script.SetTool(AnnotationTool.Pen);
            script.BeginStroke(new AnnotationPoint(4, 0));
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
        // Both strokes are green (R 0, G 255, B 0): the shared controls applied to each tool.
        Assert.Equal(255, result.Frame!.Pixels[1 * 4 + 1]);     // G at (1,0)
        Assert.Equal(0, result.Frame.Pixels[1 * 4 + 2]);        // R at (1,0)
        Assert.Equal(255, result.Frame.Pixels[4 * 4 + 1]);      // G at (4,0)
        Assert.Equal(0, result.Frame.Pixels[4 * 4 + 2]);        // R at (4,0)
    }

    [Fact]
    public async Task Workflow_ShapeUndoRedoRoundTrip_ComposesExpectedPixels()
    {
        var source = Frame6x1();
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Line);
            script.BeginStroke(new AnnotationPoint(0, 0));
            script.UpdateStrokePoint(new AnnotationPoint(5, 0));
            script.CommitStroke();
            script.Undo();
            script.Redo();
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
        Assert.Equal(255, result.Frame!.Pixels[2 * 4 + 2]);
        Assert.Equal(255, result.Frame.Pixels[2 * 4 + 3]);
    }

    // spec #40: text annotations

    [Fact]
    public async Task Workflow_ConfirmedTextFrameCarriesTheComposedPixels()
    {
        var source = BitmapBufferConverter.StripPadding(
            new byte[120 * 48 * 4],
            120,
            48,
            480);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Text);
            script.BeginText(new AnnotationPoint(10, 24));
            script.EditInProgressText("HH");
            script.CommitStroke();
        });
        var session = Session(
            SuccessfulCapture(source),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Text, new AnnotationColor(255, 0, 0), 4));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        int colored = 0;
        for (int i = 0; i < result.Frame!.Pixels.Length; i += 4)
            if (result.Frame.Pixels[i + 2] == 255 && result.Frame.Pixels[i + 1] == 0 && result.Frame.Pixels[i] == 0)
                colored++;
        Assert.True(colored > 10, $"Expected composed text pixels, found {colored}.");
        // The source Frame was never mutated by composition.
        for (int i = 0; i < source.Pixels.Length; i += 4)
        {
            Assert.Equal(0, source.Pixels[i]);
            Assert.Equal(0, source.Pixels[i + 1]);
            Assert.Equal(0, source.Pixels[i + 2]);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Workflow_EmptyOrCancelledText_AddsNoDocumentEntry(string content)
    {
        var source = Frame6x1();
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Text);
            script.BeginText(new AnnotationPoint(0, 0));
            script.EditInProgressText(content);
            Assert.False(script.CommitStroke());
            Assert.Empty(script.Document.Strokes);
        });
        var session = Session(
            SuccessfulCapture(source),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Text, new AnnotationColor(255, 0, 0), 4));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        Assert.Equal(source.Pixels, result.Frame!.Pixels);
    }

    [Fact]
    public async Task Workflow_CancelledTextEntry_LeavesNoResidueAndCommittedPixelsStay()
    {
        var source = Frame6x1();
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Text);
            script.BeginText(new AnnotationPoint(0, 0));
            script.EditInProgressText("Hi");
            script.CancelStroke();
            Assert.False(script.CanUndo);
            // A committed pen stroke after the cancelled text still composes.
            script.SetTool(AnnotationTool.Pen);
            script.BeginStroke(new AnnotationPoint(2, 0));
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
        Assert.Equal(255, result.Frame!.Pixels[2 * 4 + 2]);
    }

    [Fact]
    public async Task Workflow_TextUndoRedoRoundTrip_ComposesExpectedPixels()
    {
        var source = BitmapBufferConverter.StripPadding(
            new byte[120 * 48 * 4],
            120,
            48,
            480);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Text);
            script.BeginText(new AnnotationPoint(10, 24));
            script.EditInProgressText("HH");
            script.CommitStroke();
            script.Undo();
            script.Redo();
        });
        var session = Session(
            SuccessfulCapture(source),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Text, new AnnotationColor(255, 0, 0), 4));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        int colored = 0;
        for (int i = 0; i < result.Frame!.Pixels.Length; i += 4)
            if (result.Frame.Pixels[i + 2] == 255 && result.Frame.Pixels[i + 1] == 0 && result.Frame.Pixels[i] == 0)
                colored++;
        Assert.True(colored > 10, $"Expected restored text pixels, found {colored}.");
        Assert.NotEqual(source.Pixels, result.Frame.Pixels);
    }

    // spec #41: numbered markers

    [Fact]
    public async Task Workflow_ConfirmedMarkerFrameCarriesTheComposedPixels()
    {
        var source = BitmapBufferConverter.StripPadding(
            new byte[120 * 48 * 4],
            120,
            48,
            480);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Marker);
            script.BeginMarker(new AnnotationPoint(30, 24));
            script.CommitStroke();
            script.BeginMarker(new AnnotationPoint(90, 24));
            script.CommitStroke();
        });
        var session = Session(
            SuccessfulCapture(source),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Marker, new AnnotationColor(255, 0, 0), 4));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        // Both marker centers are the annotation color on the delivered Frame.
        Assert.Equal(255, result.Frame!.Pixels[(24 * 120 + 30) * 4 + 2]);
        Assert.Equal(255, result.Frame.Pixels[(24 * 120 + 90) * 4 + 2]);
        // The source Frame was never mutated by composition.
        for (int i = 0; i < source.Pixels.Length; i += 4)
        {
            Assert.Equal(0, source.Pixels[i]);
            Assert.Equal(0, source.Pixels[i + 1]);
            Assert.Equal(0, source.Pixels[i + 2]);
        }
    }

    [Fact]
    public async Task Workflow_MarkerUndoRedoRoundTrip_ComposesSequentialNumbers()
    {
        var source = BitmapBufferConverter.StripPadding(
            new byte[160 * 48 * 4],
            160,
            48,
            640);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Marker);
            script.BeginMarker(new AnnotationPoint(30, 24));
            script.CommitStroke();
            script.BeginMarker(new AnnotationPoint(70, 24));
            script.CommitStroke();
            // Undo the second marker; the third commit numbers past the highest remaining.
            script.Undo();
            script.BeginMarker(new AnnotationPoint(110, 24));
            script.CommitStroke();
            Assert.Equal(new int?[] { 1, 2 }, script.Document.Strokes.Select(s => s.MarkerNumber).ToArray());
        });
        var session = Session(
            SuccessfulCapture(source),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Marker, new AnnotationColor(255, 0, 0), 4));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        Assert.Equal(255, result.Frame!.Pixels[(24 * 160 + 30) * 4 + 2]);
        Assert.Equal(255, result.Frame.Pixels[(24 * 160 + 110) * 4 + 2]);
        // The undone marker at (70,24) leaves no residue.
        Assert.Equal(0, result.Frame.Pixels[(24 * 160 + 70) * 4 + 2]);
    }

    // spec #42: blur regions

    [Fact]
    public async Task Workflow_ConfirmedBlurFrame_ObscuresRegionAndPreservesOutside()
    {
        // Checkerboard source: sharp black/white detail inside the blur region,
        // identical detail outside it.
        var source = CheckerboardFrame(40, 30);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Blur);
            script.BeginStroke(new AnnotationPoint(4, 4));
            script.UpdateStrokePoint(new AnnotationPoint(24, 16));
            script.CommitStroke();
        });
        var session = Session(
            SuccessfulCapture(source),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Blur, new AnnotationColor(255, 0, 0), 4));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        // Inside the region the detail is obscured...
        for (int y = 4; y <= 16; y++)
            for (int x = 4; x <= 24; x++)
            {
                int offset = (y * 40 + x) * 4;
                Assert.NotEqual(0, result.Frame!.Pixels[offset + 1]);
                Assert.NotEqual(255, result.Frame.Pixels[offset + 1]);
            }
        // ...outside it the composed Frame matches the source byte for byte.
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
            {
                if (x >= 4 && x <= 24 && y >= 4 && y <= 16)
                    continue;
                int offset = (y * 40 + x) * 4;
                Assert.Equal(source.Pixels[offset], result.Frame!.Pixels[offset]);
            }
        // The source Frame was never mutated: (10,10) sums even → white, (10,11) odd → black.
        Assert.Equal(255, source.Pixels[(10 * 40 + 10) * 4]);
        Assert.Equal(0, source.Pixels[(10 * 40 + 11) * 4]);
    }

    [Fact]
    public async Task Workflow_BlurUndo_ComposesTheUnchangedSourceRegion()
    {
        var source = CheckerboardFrame(40, 30);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetTool(AnnotationTool.Blur);
            script.BeginStroke(new AnnotationPoint(4, 4));
            script.UpdateStrokePoint(new AnnotationPoint(24, 16));
            script.CommitStroke();
            script.Undo();
        });
        var session = Session(
            SuccessfulCapture(source),
            new FakePreviewAdapter(),
            annotation,
            annotationEnabled: true,
            toolState: new AnnotationToolState(AnnotationTool.Blur, new AnnotationColor(255, 0, 0), 4));

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.NotNull(result.Frame);
        Assert.Equal(source.Pixels, result.Frame!.Pixels);
    }

    // spec #43: annotation shadows. The marker's solid disc (radius >= 12) fully
    // occludes a 1px-offset shadow, so pixel evidence lives in the stroke-spanning
    // tools; marker shadow retention is covered by the document-layer tests.

    [Theory]
    [InlineData(AnnotationTool.Pen)]
    [InlineData(AnnotationTool.Line)]
    public async Task Workflow_ConfirmedShadowedFrameCarriesTheShadowPixels(AnnotationTool tool)
    {
        // 9x7 white Frame; a red stroke from (2,2) to (6,2) with a drop shadow
        // paints (4,3) with the observable shadow blend.
        var source = SolidWhiteFrame(9, 7);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetShadow(AnnotationShadowStyle.Drop);
            script.SetTool(tool);
            script.BeginStroke(new AnnotationPoint(2, 2));
            if (tool == AnnotationTool.Pen)
                script.AppendStrokePoint(new AnnotationPoint(6, 2));
            else
                script.UpdateStrokePoint(new AnnotationPoint(6, 2));
            script.CommitStroke();
            Assert.Equal(AnnotationShadowStyle.Drop, Assert.Single(script.Document.Strokes).Shadow);
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
        Assert.Equal(159, result.Frame!.Pixels[(3 * 9 + 4) * 4 + 1]);
        Assert.Equal(255, result.Frame.Pixels[(2 * 9 + 4) * 4 + 2]);
    }

    [Fact]
    public async Task Workflow_ShadowUndo_ComposesTheUnshadowedPixels()
    {
        var source = SolidWhiteFrame(9, 7);
        var annotation = new ScriptedAnnotationOverlayAdapter(script =>
        {
            script.SetShadow(AnnotationShadowStyle.Drop);
            script.BeginStroke(new AnnotationPoint(2, 2));
            script.AppendStrokePoint(new AnnotationPoint(6, 2));
            script.CommitStroke();
            script.Undo();
            Assert.Empty(script.Document.Strokes);
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

    private static ContiguousBitmap SolidWhiteFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        return BitmapBufferConverter.StripPadding(pixels, width, height, width * 4);
    }

    private static ContiguousBitmap CheckerboardFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte value = (x + y) % 2 == 0 ? (byte)255 : (byte)0;
                int offset = (y * width + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }

        return BitmapBufferConverter.StripPadding(pixels, width, height, width * 4);
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
