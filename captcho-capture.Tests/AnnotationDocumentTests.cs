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
