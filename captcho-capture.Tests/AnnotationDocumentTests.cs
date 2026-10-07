using System;
using System.Linq;
using Xunit;

namespace captcho.Capture.Tests;

public class AnnotationDocumentTests
{
    [Fact]
    public void Render_ComposesInProgressPenWithoutChangingSource()
    {
        var source = SolidFrame(5, 3, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Pen,
            new AnnotationColor(255, 0, 0),
            1));

        session.BeginStroke(new AnnotationPoint(1, 1));
        session.AppendStrokePoint(new AnnotationPoint(3, 1));

        var rendered = session.Render();

        Assert.Empty(session.Document.Strokes);
        Assert.NotNull(session.Document.InProgressStroke);
        Assert.Equal(new AnnotationColor(20, 30, 40), Pixel(source, 2, 1));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 2, 1));
    }

    [Fact]
    public void CommitStroke_PreservesStyleAndCancelRemovesOnlyInProgressStroke()
    {
        var source = SolidFrame(6, 2, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Pen,
            new AnnotationColor(255, 0, 0),
            1));

        session.BeginStroke(new AnnotationPoint(1, 0));
        session.AppendStrokePoint(new AnnotationPoint(1, 1));
        session.CommitStroke();

        session.SetToolState(new AnnotationToolState(
            AnnotationTool.Pen,
            new AnnotationColor(0, 0, 255),
            1));
        session.BeginStroke(new AnnotationPoint(4, 0));
        session.AppendStrokePoint(new AnnotationPoint(4, 1));
        session.CancelStroke();

        var committed = Assert.Single(session.Document.Strokes);
        Assert.Equal(new AnnotationColor(255, 0, 0), committed.Color);
        Assert.Equal(1, committed.StrokeWidth);
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(session.Render(), 1, 1));
        Assert.Equal(new AnnotationColor(20, 30, 40), Pixel(session.Render(), 4, 1));
    }

    [Fact]
    public void NewStroke_UsesTheCurrentGlobalToolState()
    {
        var session = new AnnotationSession(SolidFrame(4, 1, new AnnotationColor(0, 0, 0)));

        session.SetPenColor(new AnnotationColor(0, 255, 0));
        session.SetStrokeWidth(3);
        session.BeginStroke(new AnnotationPoint(1, 0));
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationTool.Pen, stroke.Tool);
        Assert.Equal(new AnnotationColor(0, 255, 0), stroke.Color);
        Assert.Equal(3, stroke.StrokeWidth);
    }

    [Fact]
    public void CancelStroke_DoesNotChangeCommittedStrokesOrSource()
    {
        var source = SolidFrame(3, 1, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source);

        session.BeginStroke(new AnnotationPoint(0, 0));
        session.CancelStroke();

        Assert.Empty(session.Document.Strokes);
        Assert.Null(session.Document.InProgressStroke);
        Assert.Equal(source.Pixels, session.Render().Pixels);
    }

    // spec #38: document history

    [Fact]
    public void Undo_RemovesTheLatestCommittedStrokeAndUpdatesTheComposedFrame()
    {
        var source = SolidFrame(6, 1, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Pen,
            new AnnotationColor(255, 0, 0),
            1));

        session.BeginStroke(new AnnotationPoint(1, 0));
        session.CommitStroke();
        session.BeginStroke(new AnnotationPoint(4, 0));
        session.CommitStroke();

        Assert.Equal(2, session.Document.Strokes.Count);
        Assert.True(session.Undo());

        var committed = Assert.Single(session.Document.Strokes);
        Assert.Equal(new AnnotationPoint(1, 0), committed.Points[0]);
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(session.Render(), 1, 0));
        Assert.Equal(new AnnotationColor(20, 30, 40), Pixel(session.Render(), 4, 0));
        Assert.Equal(source.Pixels[0], session.Render().Pixels[0]);
    }

    [Fact]
    public void Redo_RestoresTheMostRecentlyUndoneStrokeInOriginalOrderAndStyle()
    {
        var source = SolidFrame(6, 1, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source);

        session.BeginStroke(new AnnotationPoint(1, 0));
        session.CommitStroke();
        session.SetPenColor(new AnnotationColor(0, 0, 255));
        session.BeginStroke(new AnnotationPoint(4, 0));
        session.CommitStroke();

        session.Undo();
        session.Undo();
        Assert.Empty(session.Document.Strokes);

        Assert.True(session.Redo());
        Assert.True(session.Redo());

        Assert.Equal(2, session.Document.Strokes.Count);
        Assert.Equal(new AnnotationColor(255, 0, 0), session.Document.Strokes[0].Color);
        Assert.Equal(new AnnotationColor(0, 0, 255), session.Document.Strokes[1].Color);
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(session.Render(), 1, 0));
        Assert.Equal(new AnnotationColor(0, 0, 255), Pixel(session.Render(), 4, 0));
    }

    [Fact]
    public void CommittingANewStrokeAfterUndo_ClearsAllRedoHistory()
    {
        var source = SolidFrame(6, 1, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source);

        session.BeginStroke(new AnnotationPoint(1, 0));
        session.CommitStroke();
        session.Undo();
        Assert.True(session.CanRedo);

        session.BeginStroke(new AnnotationPoint(4, 0));
        session.CommitStroke();

        Assert.False(session.CanRedo);
        Assert.False(session.Redo());
        var committed = Assert.Single(session.Document.Strokes);
        Assert.Equal(new AnnotationPoint(4, 0), committed.Points[0]);
    }

    [Fact]
    public void UndoRedo_OnEmptyHistory_DoNothingAndStayInactive()
    {
        var session = new AnnotationSession(SolidFrame(3, 1, new AnnotationColor(0, 0, 0)));

        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.False(session.Undo());
        Assert.False(session.Redo());
        Assert.Empty(session.Document.Strokes);
    }

    [Fact]
    public void Undo_WhileAStrokeIsInProgress_PreservesTheInProgressStrokeAndSourcePixels()
    {
        var source = SolidFrame(5, 1, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source);

        session.BeginStroke(new AnnotationPoint(1, 0));
        session.AppendStrokePoint(new AnnotationPoint(3, 0));

        Assert.False(session.CanUndo);
        Assert.False(session.Undo());
        Assert.False(session.Redo());
        Assert.NotNull(session.Document.InProgressStroke);
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(session.Render(), 2, 0));
    }

    [Fact]
    public void RepeatedUndoAndRedo_AreIdempotentAtTheBoundaries()
    {
        var source = SolidFrame(5, 1, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Pen,
            new AnnotationColor(255, 0, 0),
            1));

        session.BeginStroke(new AnnotationPoint(1, 0));
        session.CommitStroke();

        Assert.True(session.Undo());
        Assert.False(session.Undo());
        Assert.True(session.Redo());
        Assert.False(session.Redo());

        var committed = Assert.Single(session.Document.Strokes);
        Assert.Equal(new AnnotationPoint(1, 0), committed.Points[0]);
        var rendered = session.Render();
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 1, 0));
        Assert.Equal(new AnnotationColor(20, 30, 40), Pixel(rendered, 0, 0));
    }

    // spec #39: shape tools and shared style controls

    [Theory]
    [InlineData(AnnotationTool.Rectangle)]
    [InlineData(AnnotationTool.Ellipse)]
    [InlineData(AnnotationTool.Line)]
    [InlineData(AnnotationTool.Arrow)]
    public void Shape_CommitsGeometryAndStyleIndependentlyAndRenders(AnnotationTool tool)
    {
        var source = SolidFrame(8, 6, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            tool,
            new AnnotationColor(255, 0, 0),
            1));

        session.BeginStroke(new AnnotationPoint(1, 1));
        session.UpdateStrokePoint(new AnnotationPoint(5, 4));
        var inProgress = session.Render();
        Assert.NotNull(session.Document.InProgressStroke);
        Assert.Equal(tool, session.Document.InProgressStroke!.Tool);
        Assert.NotEqual(source.Pixels, inProgress.Pixels);

        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(tool, stroke.Tool);
        Assert.Equal(new AnnotationColor(255, 0, 0), stroke.Color);
        Assert.Equal(1, stroke.StrokeWidth);
        Assert.Equal(new AnnotationPoint(1, 1), stroke.Points[0]);
        Assert.Equal(new AnnotationPoint(5, 4), stroke.Points[^1]);
        Assert.NotEqual(source.Pixels, session.Render().Pixels);
    }

    [Theory]
    [InlineData(AnnotationTool.Rectangle)]
    [InlineData(AnnotationTool.Ellipse)]
    [InlineData(AnnotationTool.Line)]
    [InlineData(AnnotationTool.Arrow)]
    public void Shape_UpdateStrokePointKeepsAnchorAndMovesOnlyTheDragPoint(AnnotationTool tool)
    {
        var session = new AnnotationSession(SolidFrame(8, 6, new AnnotationColor(0, 0, 0)), new AnnotationToolState(
            tool,
            new AnnotationColor(255, 0, 0),
            1));

        session.BeginStroke(new AnnotationPoint(2, 2));
        session.UpdateStrokePoint(new AnnotationPoint(6, 3));
        session.UpdateStrokePoint(new AnnotationPoint(4, 5));

        var stroke = session.Document.InProgressStroke;
        Assert.NotNull(stroke);
        Assert.Equal(2, stroke!.Points.Count);
        Assert.Equal(new AnnotationPoint(2, 2), stroke.Points[0]);
        Assert.Equal(new AnnotationPoint(4, 5), stroke.Points[^1]);
    }

    [Fact]
    public void Shape_AppendStrokePointIsIgnoredSoShapesStayTwoPointGeometry()
    {
        var session = new AnnotationSession(SolidFrame(8, 6, new AnnotationColor(0, 0, 0)), new AnnotationToolState(
            AnnotationTool.Rectangle,
            new AnnotationColor(255, 0, 0),
            1));

        session.BeginStroke(new AnnotationPoint(1, 1));
        session.AppendStrokePoint(new AnnotationPoint(3, 3));
        session.AppendStrokePoint(new AnnotationPoint(5, 5));

        var stroke = session.Document.InProgressStroke;
        Assert.NotNull(stroke);
        Assert.Equal(new AnnotationPoint(1, 1), stroke.Points[0]);
        Assert.Equal(new AnnotationPoint(1, 1), stroke.Points[^1]);
    }

    [Fact]
    public void Pen_UpdateStrokePointIsIgnoredSoFreehandStrokesKeepEveryPoint()
    {
        var session = new AnnotationSession(SolidFrame(8, 1, new AnnotationColor(0, 0, 0)));

        session.BeginStroke(new AnnotationPoint(1, 0));
        session.AppendStrokePoint(new AnnotationPoint(3, 0));
        session.UpdateStrokePoint(new AnnotationPoint(4, 0));

        var stroke = session.Document.InProgressStroke;
        Assert.NotNull(stroke);
        Assert.Equal(new[] { new AnnotationPoint(1, 0), new AnnotationPoint(3, 0) }, stroke!.Points);
    }

    [Fact]
    public void Shape_SnapshotsSharedColorAndStrokeWidthAtBegin()
    {
        var session = new AnnotationSession(SolidFrame(8, 6, new AnnotationColor(20, 30, 40)));

        session.SetPenColor(new AnnotationColor(0, 255, 0));
        session.SetStrokeWidth(7);
        session.SetTool(AnnotationTool.Ellipse);
        session.BeginStroke(new AnnotationPoint(1, 1));
        session.UpdateStrokePoint(new AnnotationPoint(6, 5));
        session.SetPenColor(new AnnotationColor(255, 0, 0));
        session.SetStrokeWidth(2);
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationTool.Ellipse, stroke.Tool);
        Assert.Equal(new AnnotationColor(0, 255, 0), stroke.Color);
        Assert.Equal(7, stroke.StrokeWidth);
    }

    [Fact]
    public void Shape_FillStyleSnapshotsWithTheShapeAndUndoRestoresIt()
    {
        var source = SolidFrame(8, 6, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Rectangle,
            new AnnotationColor(255, 0, 0),
            1));

        session.SetFill(AnnotationFillStyle.Solid);
        session.BeginStroke(new AnnotationPoint(1, 1));
        session.UpdateStrokePoint(new AnnotationPoint(5, 4));
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationFillStyle.Solid, stroke.Fill);

        Assert.True(session.Undo());
        Assert.Empty(session.Document.Strokes);
        Assert.True(session.Redo());
        var restored = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationFillStyle.Solid, restored.Fill);
        Assert.Equal(AnnotationTool.Rectangle, restored.Tool);
    }

    [Fact]
    public void Shape_CommitAfterUndoClearsRedoAndCommittedPixelsAreReversible()
    {
        var source = SolidFrame(8, 6, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Rectangle,
            new AnnotationColor(255, 0, 0),
            1,
            AnnotationFillStyle.Solid));

        session.BeginStroke(new AnnotationPoint(1, 1));
        session.UpdateStrokePoint(new AnnotationPoint(5, 4));
        session.CommitStroke();
        Assert.NotEqual(source.Pixels, session.Render().Pixels);

        Assert.True(session.Undo());
        Assert.Equal(source.Pixels, session.Render().Pixels);

        session.BeginStroke(new AnnotationPoint(2, 2));
        session.UpdateStrokePoint(new AnnotationPoint(4, 3));
        session.CommitStroke();
        Assert.False(session.CanRedo);
    }

    [Theory]
    [InlineData(AnnotationTool.Pen)]
    [InlineData(AnnotationTool.Line)]
    [InlineData(AnnotationTool.Arrow)]
    public void NonFillTools_NeverSnapshotALingeringFillSelection(AnnotationTool tool)
    {
        var session = new AnnotationSession(SolidFrame(8, 6, new AnnotationColor(20, 30, 40)), new AnnotationToolState(
            tool,
            new AnnotationColor(255, 0, 0),
            1));

        // Fill set while a fill-capable tool was active, then the tool changed.
        session.SetFill(AnnotationFillStyle.Solid);
        session.SetTool(tool);
        session.BeginStroke(new AnnotationPoint(1, 1));
        session.UpdateStrokePoint(new AnnotationPoint(5, 4));
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationFillStyle.None, stroke.Fill);
    }

    private static ContiguousBitmap SolidFrame(int width, int height, AnnotationColor color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.Blue;
            pixels[i + 1] = color.Green;
            pixels[i + 2] = color.Red;
            pixels[i + 3] = color.Alpha;
        }

        return BitmapBufferConverter.StripPadding(pixels, width, height, width * 4);
    }

    private static AnnotationColor Pixel(ContiguousBitmap bitmap, int x, int y)
    {
        int offset = y * bitmap.Stride + x * 4;
        return new AnnotationColor(
            bitmap.Pixels[offset + 2],
            bitmap.Pixels[offset + 1],
            bitmap.Pixels[offset],
            bitmap.Pixels[offset + 3]);
    }
}
