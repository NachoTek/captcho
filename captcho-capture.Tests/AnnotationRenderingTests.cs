using Xunit;

namespace captcho.Capture.Tests;

public class AnnotationRenderingTests
{
    [Fact]
    public void Render_PenLineCoversEveryPixelBetweenEndpoints()
    {
        var source = SolidFrame(5, 5, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Pen,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(0, 2), new AnnotationPoint(4, 2) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        for (int x = 0; x < 5; x++)
            Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, x, 2));

        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 2, 1));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(source, 2, 2));
    }

    [Fact]
    public void Render_StrokeWidthCoversAdjacentPixels()
    {
        var source = SolidFrame(5, 5, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Pen,
            new AnnotationColor(0, 0, 255),
            3,
            new[] { new AnnotationPoint(2, 2) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        Assert.Equal(new AnnotationColor(0, 0, 255), Pixel(rendered, 2, 2));
        Assert.Equal(new AnnotationColor(0, 0, 255), Pixel(rendered, 1, 2));
        Assert.Equal(new AnnotationColor(0, 0, 255), Pixel(rendered, 2, 1));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 0, 0));
    }

    [Fact]
    public void Render_ComposesStrokesInOrderWithAlpha()
    {
        var source = SolidFrame(1, 1, new AnnotationColor(0, 0, 0));
        var first = new AnnotationStroke(
            AnnotationTool.Pen,
            new AnnotationColor(255, 0, 0, 128),
            1,
            new[] { new AnnotationPoint(0, 0) });
        var second = new AnnotationStroke(
            AnnotationTool.Pen,
            new AnnotationColor(0, 255, 0, 255),
            1,
            new[] { new AnnotationPoint(0, 0) });

        var rendered = AnnotationRenderer.Render(source, new[] { first, second });

        Assert.Equal(new AnnotationColor(0, 255, 0, 255), Pixel(rendered, 0, 0));
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
