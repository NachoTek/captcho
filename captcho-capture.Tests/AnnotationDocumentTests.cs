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
    [InlineData(AnnotationTool.Blur)]
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
    [InlineData(AnnotationTool.Blur)]
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

    // spec #40: text annotations

    [Fact]
    public void Text_CommitRetainsContentPositionAndStyleAsDocumentEntry()
    {
        var source = SolidFrame(8, 6, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Text,
            new AnnotationColor(255, 0, 0),
            5));

        session.BeginText(new AnnotationPoint(2, 1));
        session.EditInProgressText("Hello");
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationTool.Text, stroke.Tool);
        Assert.Equal("Hello", stroke.Text);
        Assert.Equal(new AnnotationPoint(2, 1), stroke.Points[0]);
        Assert.Equal(new AnnotationColor(255, 0, 0), stroke.Color);
        Assert.Equal(5, stroke.StrokeWidth);
        Assert.Equal(AnnotationFillStyle.None, stroke.Fill);
        Assert.Null(session.Document.InProgressText);

        Assert.True(session.Undo());
        Assert.Empty(session.Document.Strokes);
        Assert.True(session.Redo());
        var restored = Assert.Single(session.Document.Strokes);
        Assert.Equal("Hello", restored.Text);
        Assert.Equal(new AnnotationPoint(2, 1), restored.Points[0]);
        Assert.Equal(new AnnotationColor(255, 0, 0), restored.Color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Text_EmptyOrWhitespaceCommit_AddsNoDocumentEntry(string content)
    {
        var source = SolidFrame(8, 6, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Text,
            new AnnotationColor(255, 0, 0),
            5));

        session.BeginText(new AnnotationPoint(2, 1));
        session.EditInProgressText(content);

        Assert.False(session.CommitStroke());

        Assert.Empty(session.Document.Strokes);
        Assert.Null(session.Document.InProgressText);
        Assert.Null(session.Document.InProgressStroke);
        Assert.Equal(source.Pixels, session.Render().Pixels);
    }

    [Fact]
    public void Text_CancelDuringEntry_AddsNoDocumentEntryAndBlocksUndo()
    {
        var source = SolidFrame(8, 6, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source);

        session.BeginText(new AnnotationPoint(2, 1));
        session.EditInProgressText("Hi");

        session.CancelStroke();

        Assert.Empty(session.Document.Strokes);
        Assert.Null(session.Document.InProgressText);
        Assert.False(session.CanUndo);
        Assert.Equal(source.Pixels, session.Render().Pixels);
    }

    [Fact]
    public void Text_EditBeforeCommit_UpdatesInProgressEntryOnly()
    {
        var source = SolidFrame(60, 40, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Text,
            new AnnotationColor(255, 0, 0),
            4));

        session.BeginText(new AnnotationPoint(5, 5));
        session.EditInProgressText("A");

        Assert.Empty(session.Document.Strokes);
        Assert.NotNull(session.Document.InProgressStroke);
        Assert.Equal("A", session.Document.InProgressStroke!.Text);
        Assert.NotEqual(source.Pixels, session.Render().Pixels);

        session.EditInProgressText("AB");

        Assert.Equal("AB", session.Document.InProgressStroke!.Text);
    }

    [Fact]
    public void Text_EntrySnapshotsSharedStyleAtBegin()
    {
        var session = new AnnotationSession(SolidFrame(60, 40, new AnnotationColor(20, 30, 40)));

        session.SetPenColor(new AnnotationColor(0, 255, 0));
        session.SetStrokeWidth(7);
        session.SetTool(AnnotationTool.Text);
        session.BeginText(new AnnotationPoint(1, 1));
        session.SetPenColor(new AnnotationColor(255, 0, 0));
        session.SetStrokeWidth(2);
        session.EditInProgressText("Hi");
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationTool.Text, stroke.Tool);
        Assert.Equal(new AnnotationColor(0, 255, 0), stroke.Color);
        Assert.Equal(7, stroke.StrokeWidth);
    }

    [Fact]
    public void Text_EntryBlocksUndoRedoWhileInProgress()
    {
        var session = new AnnotationSession(SolidFrame(8, 6, new AnnotationColor(0, 0, 0)));

        session.BeginStroke(new AnnotationPoint(1, 0));
        session.CommitStroke();
        session.BeginText(new AnnotationPoint(2, 1));
        session.EditInProgressText("Hi");

        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.False(session.Undo());
        Assert.False(session.Redo());
        Assert.Single(session.Document.Strokes);
        Assert.NotNull(session.Document.InProgressText);
    }

    [Fact]
    public void BeginStroke_WithTextTool_StartsATextEntryInsteadOfAPointStroke()
    {
        var session = new AnnotationSession(SolidFrame(60, 40, new AnnotationColor(0, 0, 0)), new AnnotationToolState(
            AnnotationTool.Text,
            new AnnotationColor(255, 0, 0),
            4));

        session.BeginStroke(new AnnotationPoint(3, 2));
        session.AppendStrokePoint(new AnnotationPoint(5, 5));
        session.UpdateStrokePoint(new AnnotationPoint(6, 6));

        Assert.Equal(string.Empty, session.Document.InProgressText);
        Assert.Null(session.Document.InProgressStroke);
        Assert.Empty(session.Document.Strokes);
    }

    [Fact]
    public void EditInProgressText_OutsideATextEntry_IsANoOp()
    {
        var session = new AnnotationSession(SolidFrame(8, 1, new AnnotationColor(0, 0, 0)));

        session.BeginStroke(new AnnotationPoint(1, 0));
        session.EditInProgressText("Hi");

        var stroke = session.Document.InProgressStroke;
        Assert.NotNull(stroke);
        Assert.Null(stroke!.Text);
        Assert.Equal(new[] { new AnnotationPoint(1, 0) }, stroke.Points);
    }

    // spec #41: numbered markers

    [Fact]
    public void Marker_CommitAssignsSequentialNumbersAndRetainsGeometryAndStyle()
    {
        var source = SolidFrame(120, 48, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Marker,
            new AnnotationColor(255, 0, 0),
            3));

        session.BeginMarker(new AnnotationPoint(10, 10));
        session.CommitStroke();
        session.BeginMarker(new AnnotationPoint(60, 30));
        session.CommitStroke();

        Assert.Equal(2, session.Document.Strokes.Count);
        Assert.All(session.Document.Strokes, s => Assert.Equal(AnnotationTool.Marker, s.Tool));
        Assert.Equal(1, session.Document.Strokes[0].MarkerNumber);
        Assert.Equal(2, session.Document.Strokes[1].MarkerNumber);
        Assert.Equal(new AnnotationPoint(10, 10), session.Document.Strokes[0].Points[0]);
        Assert.Equal(new AnnotationPoint(60, 30), session.Document.Strokes[1].Points[0]);
        Assert.All(session.Document.Strokes, s => Assert.Single(s.Points));
        Assert.All(session.Document.Strokes, s => Assert.Equal(new AnnotationColor(255, 0, 0), s.Color));
        Assert.All(session.Document.Strokes, s => Assert.Equal(3, s.StrokeWidth));
        Assert.All(session.Document.Strokes, s => Assert.Null(s.Text));
        Assert.All(session.Document.Strokes, s => Assert.Equal(AnnotationFillStyle.None, s.Fill));
    }

    [Fact]
    public void Marker_UndoRemovesTheLatestAndTheNextCommitNumbersPastTheHighestRemaining()
    {
        var source = SolidFrame(120, 48, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Marker,
            new AnnotationColor(255, 0, 0),
            3));

        session.BeginMarker(new AnnotationPoint(10, 10));
        session.CommitStroke();
        session.BeginMarker(new AnnotationPoint(40, 10));
        session.CommitStroke();
        session.BeginMarker(new AnnotationPoint(70, 10));
        session.CommitStroke();

        Assert.True(session.Undo());

        Assert.Equal(new int?[] { 1, 2 }, session.Document.Strokes.Select(s => s.MarkerNumber).ToArray());
        session.BeginMarker(new AnnotationPoint(95, 10));
        session.CommitStroke();

        Assert.Equal(new int?[] { 1, 2, 3 }, session.Document.Strokes.Select(s => s.MarkerNumber).ToArray());
        Assert.Equal(new AnnotationPoint(95, 10), session.Document.Strokes[^1].Points[0]);
    }

    [Fact]
    public void Marker_RedoRestoresTheOriginalNumber()
    {
        var source = SolidFrame(120, 48, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Marker,
            new AnnotationColor(255, 0, 0),
            3));

        session.BeginMarker(new AnnotationPoint(10, 10));
        session.CommitStroke();
        session.BeginMarker(new AnnotationPoint(40, 10));
        session.CommitStroke();

        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.True(session.Redo());

        var restored = Assert.Single(session.Document.Strokes);
        Assert.Equal(1, restored.MarkerNumber);
        Assert.Equal(new AnnotationPoint(10, 10), restored.Points[0]);
    }

    [Fact]
    public void Marker_NumbersOnlyCountMarkersNotOtherTools()
    {
        var source = SolidFrame(120, 48, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Marker,
            new AnnotationColor(255, 0, 0),
            3));

        session.BeginMarker(new AnnotationPoint(10, 10));
        session.CommitStroke();
        session.SetTool(AnnotationTool.Pen);
        session.BeginStroke(new AnnotationPoint(20, 20));
        session.CommitStroke();
        session.SetTool(AnnotationTool.Text);
        session.BeginText(new AnnotationPoint(30, 30));
        session.EditInProgressText("Hi");
        session.CommitStroke();
        session.SetTool(AnnotationTool.Marker);
        session.BeginMarker(new AnnotationPoint(50, 30));
        session.CommitStroke();

        Assert.Equal(4, session.Document.Strokes.Count);
        Assert.Equal(1, session.Document.Strokes[0].MarkerNumber);
        Assert.Null(session.Document.Strokes[1].MarkerNumber);
        Assert.Null(session.Document.Strokes[2].MarkerNumber);
        Assert.Equal(2, session.Document.Strokes[3].MarkerNumber);
    }

    [Fact]
    public void Marker_InProgressEntryPreviewsItsNumberAndIgnoresDragUpdates()
    {
        var source = SolidFrame(120, 48, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Marker,
            new AnnotationColor(255, 0, 0),
            3));

        session.SetTool(AnnotationTool.Marker);
        session.BeginStroke(new AnnotationPoint(30, 24));
        session.AppendStrokePoint(new AnnotationPoint(50, 24));
        session.UpdateStrokePoint(new AnnotationPoint(60, 24));

        var stroke = session.Document.InProgressStroke;
        Assert.NotNull(stroke);
        Assert.Equal(AnnotationTool.Marker, stroke!.Tool);
        Assert.Equal(1, stroke.MarkerNumber);
        Assert.Equal(new[] { new AnnotationPoint(30, 24) }, stroke.Points);
        // The in-progress entry blocks document history like any other entry.
        Assert.False(session.Undo());
        Assert.False(session.Redo());

        session.CancelStroke();

        Assert.Empty(session.Document.Strokes);
        Assert.Null(session.Document.InProgressStroke);
        Assert.Equal(source.Pixels, session.Render().Pixels);
    }

    [Fact]
    public void Marker_SnapshotsSharedStyleAtBegin()
    {
        var session = new AnnotationSession(SolidFrame(120, 48, new AnnotationColor(20, 30, 40)));

        session.SetPenColor(new AnnotationColor(0, 255, 0));
        session.SetStrokeWidth(7);
        session.SetTool(AnnotationTool.Marker);
        session.BeginMarker(new AnnotationPoint(30, 24));
        session.SetPenColor(new AnnotationColor(255, 0, 0));
        session.SetStrokeWidth(2);
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationTool.Marker, stroke.Tool);
        Assert.Equal(new AnnotationColor(0, 255, 0), stroke.Color);
        Assert.Equal(7, stroke.StrokeWidth);
    }

    // spec #42: blur regions

    [Fact]
    public void Blur_CommitsRegionAsReversibleDocumentEntryWithoutMutatingTheSource()
    {
        // Checkerboard source so the blur region has detail to obscure.
        var source = CheckerboardFrame(40, 30);
        var session = new AnnotationSession(source, new AnnotationToolState(
            AnnotationTool.Blur,
            new AnnotationColor(255, 0, 0),
            4));

        session.BeginStroke(new AnnotationPoint(2, 2));
        session.UpdateStrokePoint(new AnnotationPoint(20, 12));
        Assert.NotNull(session.Document.InProgressStroke);
        Assert.NotEqual(source.Pixels, session.Render().Pixels);

        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationTool.Blur, stroke.Tool);
        Assert.Equal(new AnnotationPoint(2, 2), stroke.Points[0]);
        Assert.Equal(new AnnotationPoint(20, 12), stroke.Points[^1]);
        // Applicable shared style only: blur ignores color and width like the
        // other non-stroke tools ignore fill; it never snapshots a fill.
        Assert.Equal(AnnotationFillStyle.None, stroke.Fill);
        Assert.Null(stroke.Text);
        Assert.Null(stroke.MarkerNumber);

        // Undo removes the effect and reveals the unchanged source.
        Assert.True(session.Undo());
        Assert.Empty(session.Document.Strokes);
        Assert.Equal(source.Pixels, session.Render().Pixels);
        Assert.True(session.Redo());
        Assert.Single(session.Document.Strokes);
    }

    [Fact]
    public void Blur_SnapshotsSharedStyleAtBeginLikeEveryTool()
    {
        var session = new AnnotationSession(SolidFrame(40, 30, new AnnotationColor(20, 30, 40)));

        session.SetPenColor(new AnnotationColor(0, 255, 0));
        session.SetStrokeWidth(9);
        session.SetTool(AnnotationTool.Blur);
        session.BeginStroke(new AnnotationPoint(2, 2));
        session.UpdateStrokePoint(new AnnotationPoint(20, 12));
        session.SetPenColor(new AnnotationColor(255, 0, 0));
        session.SetStrokeWidth(2);
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(new AnnotationColor(0, 255, 0), stroke.Color);
        Assert.Equal(9, stroke.StrokeWidth);
    }

    // spec #43: annotation shadows

    [Theory]
    [InlineData(AnnotationTool.Pen)]
    [InlineData(AnnotationTool.Rectangle)]
    [InlineData(AnnotationTool.Ellipse)]
    [InlineData(AnnotationTool.Line)]
    [InlineData(AnnotationTool.Arrow)]
    [InlineData(AnnotationTool.Text)]
    [InlineData(AnnotationTool.Marker)]
    public void Shadow_SnapshotsWithTheEntryAndUndoRedoRestoresIt(AnnotationTool tool)
    {
        var source = SolidFrame(120, 60, new AnnotationColor(20, 30, 40));
        var session = new AnnotationSession(source, new AnnotationToolState(
            tool,
            new AnnotationColor(255, 0, 0),
            4));

        session.SetShadow(AnnotationShadowStyle.Drop);
        if (tool == AnnotationTool.Text)
        {
            session.BeginText(new AnnotationPoint(10, 20));
            session.EditInProgressText("Hi");
            Assert.Equal(AnnotationShadowStyle.Drop, session.Document.InProgressStroke!.Shadow);
        }
        else if (tool == AnnotationTool.Marker)
        {
            session.BeginMarker(new AnnotationPoint(20, 20));
        }
        else
        {
            session.BeginStroke(new AnnotationPoint(10, 20));
            if (tool != AnnotationTool.Pen)
                session.UpdateStrokePoint(new AnnotationPoint(60, 40));
        }
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationShadowStyle.Drop, stroke.Shadow);

        Assert.True(session.Undo());
        Assert.Empty(session.Document.Strokes);
        Assert.True(session.Redo());
        var restored = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationShadowStyle.Drop, restored.Shadow);
    }

    [Fact]
    public void Blur_NeverSnapshotsALingeringShadowSelection()
    {
        var session = new AnnotationSession(SolidFrame(40, 30, new AnnotationColor(20, 30, 40)), new AnnotationToolState(
            AnnotationTool.Blur,
            new AnnotationColor(255, 0, 0),
            4));

        // Shadow enabled while a shadow-capable tool was active, then the tool changed.
        session.SetShadow(AnnotationShadowStyle.Drop);
        session.SetTool(AnnotationTool.Blur);
        session.BeginStroke(new AnnotationPoint(2, 2));
        session.UpdateStrokePoint(new AnnotationPoint(20, 12));
        session.CommitStroke();

        var stroke = Assert.Single(session.Document.Strokes);
        Assert.Equal(AnnotationShadowStyle.None, stroke.Shadow);
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
