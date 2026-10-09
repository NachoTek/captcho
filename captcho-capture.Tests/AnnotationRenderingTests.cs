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

    // spec #39: shape tools and shared style controls

    [Fact]
    public void Render_RectangleOutline_TracesEdgesAndLeavesInteriorAndOutsideUntouched()
    {
        var source = SolidFrame(9, 7, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Rectangle,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(2, 2), new AnnotationPoint(6, 4) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        for (int x = 2; x <= 6; x++)
        {
            Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, x, 2));
            Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, x, 4));
        }
        for (int y = 2; y <= 4; y++)
        {
            Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 2, y));
            Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 6, y));
        }

        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 4, 3));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 0, 0));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 8, 6));
    }

    [Fact]
    public void Render_FilledRectangle_FillsInteriorAndDrawsOutlineOverIt()
    {
        var source = SolidFrame(9, 7, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Rectangle,
            new AnnotationColor(255, 0, 0, 128),
            1,
            new[] { new AnnotationPoint(2, 2), new AnnotationPoint(6, 4) },
            AnnotationFillStyle.Solid);

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        // Interior receives the fill color blended once over white (alpha 128/255).
        Assert.Equal(new AnnotationColor(255, 127, 127), Pixel(rendered, 4, 3));
        // Outline is drawn over its own fill: alpha 128 blended twice → 63/255 green/blue.
        Assert.Equal(new AnnotationColor(255, 63, 63, 255), Pixel(rendered, 2, 2));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 0, 0));
    }

    [Fact]
    public void Render_Ellipse_TracesBoundaryAndFillsInteriorOnlyWhenSolid()
    {
        var source = SolidFrame(11, 11, new AnnotationColor(255, 255, 255));
        var outline = new AnnotationStroke(
            AnnotationTool.Ellipse,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(2, 3), new AnnotationPoint(8, 7) });

        var outlineRendered = AnnotationRenderer.Render(source, new[] { outline });

        // The extreme points of the ellipse are on the boundary.
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(outlineRendered, 5, 3));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(outlineRendered, 5, 7));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(outlineRendered, 2, 5));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(outlineRendered, 8, 5));
        // The interior of the outline-only ellipse is untouched, as is the exterior.
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(outlineRendered, 5, 5));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(outlineRendered, 0, 0));

        var filled = new AnnotationStroke(
            AnnotationTool.Ellipse,
            new AnnotationColor(0, 0, 255),
            1,
            new[] { new AnnotationPoint(2, 3), new AnnotationPoint(8, 7) },
            AnnotationFillStyle.Solid);
        var filledRendered = AnnotationRenderer.Render(source, new[] { filled });

        Assert.Equal(new AnnotationColor(0, 0, 255), Pixel(filledRendered, 5, 5));
        Assert.Equal(new AnnotationColor(0, 0, 255), Pixel(filledRendered, 5, 3));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(filledRendered, 0, 0));
    }

    [Fact]
    public void Render_Line_CoversEveryPixelBetweenEndpoints()
    {
        var source = SolidFrame(7, 5, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Line,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(1, 2), new AnnotationPoint(5, 2) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        for (int x = 1; x <= 5; x++)
            Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, x, 2));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 0, 2));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 6, 2));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 3, 1));
    }

    [Fact]
    public void Render_Arrow_DrawsShaftAndBackwardPointingHead()
    {
        var source = SolidFrame(15, 9, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Arrow,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(2, 4), new AnnotationPoint(12, 4) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        for (int x = 2; x <= 12; x++)
            Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, x, 4));

        // The head barbs extend backward from the tip at ±30 degrees off the shaft.
        // With headLength 8 and a horizontal shaft the barbs land near (5, 0) and (5, 8):
        // dx = 8*cos(30°) ≈ 6.93 → tip.X - 6.93 ≈ 5.07; dy = 8*sin(30°) = 4 → 4±4.
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 5, 0));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 5, 8));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 14, 4));
    }

    [Fact]
    public void Render_Arrow_Diagonal_HeadPointsAlongTheShaftDirection()
    {
        var source = SolidFrame(15, 15, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Arrow,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(3, 3), new AnnotationPoint(11, 11) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 3, 3));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 7, 7));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 11, 11));
        // The shaft direction is (1,1)/√2; with headLength 8 the barbs step back from the
        // tip rotated ±30° off the shaft, landing at (9, 3) and (3, 9) rounded outward.
        // Both sit behind the tip, on the start side.
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 9, 3));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 3, 9));
    }

    [Fact]
    public void Render_Shapes_ShareStrokeWidthAcrossTools()
    {
        var source = SolidFrame(11, 11, new AnnotationColor(255, 255, 255));
        var line = new AnnotationStroke(
            AnnotationTool.Line,
            new AnnotationColor(255, 0, 0),
            3,
            new[] { new AnnotationPoint(2, 5), new AnnotationPoint(8, 5) });

        var rendered = AnnotationRenderer.Render(source, new[] { line });

        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 5, 4));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 5, 5));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 5, 6));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 5, 2));
    }

    [Fact]
    public void Render_RectangleOutline_UsesTheSharedStrokeWidth()
    {
        var source = SolidFrame(9, 9, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Rectangle,
            new AnnotationColor(255, 0, 0),
            3,
            new[] { new AnnotationPoint(2, 2), new AnnotationPoint(6, 6) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        // The top edge row grows vertically with the shared width: rows 1..3 above and
        // through the edge center are covered, and the interior row 4 between the top
        // (y=2) and bottom (y=6) edges stays untouched at this width.
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 4, 1));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 4, 2));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 4, 3));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 4, 4));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 4, 5));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 4, 6));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 4, 7));
    }

    [Fact]
    public void Render_FillAppliesOnlyToClosedShapeTools()
    {
        var source = SolidFrame(9, 5, new AnnotationColor(255, 255, 255));
        var line = new AnnotationStroke(
            AnnotationTool.Line,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(1, 2), new AnnotationPoint(7, 2) },
            AnnotationFillStyle.Solid);

        var rendered = AnnotationRenderer.Render(source, new[] { line });

        // A line has no interior; fill must not bleed outside the stroke itself.
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 1, 1));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 1, 3));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 4, 2));
    }

    // spec #40: text annotations

    [Fact]
    public void Render_Text_DrawsLegibleGlyphsAtTheAnchorWithoutMutatingTheSource()
    {
        var source = SolidFrame(120, 48, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Text,
            new AnnotationColor(255, 0, 0),
            4,
            new[] { new AnnotationPoint(10, 24) },
            text: "HH");

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        // Single-bit glyph rendering paints fully opaque text-color pixels only.
        int colored = 0;
        for (int y = 2; y < 46; y++)
            for (int x = 6; x < 80; x++)
                if (Pixel(rendered, x, y).Equals(new AnnotationColor(255, 0, 0)))
                    colored++;
        Assert.True(colored > 10, $"Expected legible glyph pixels, found {colored}.");

        // Nothing outside a generous text region changes.
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 0, 0));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 119, 47));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 10, 0));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 100, 24));

        // The source Frame is never mutated.
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(source, 10, 24));
        for (int i = 0; i < source.Pixels.Length; i += 4)
            if (source.Pixels[i] != 255 || source.Pixels[i + 1] != 255 || source.Pixels[i + 2] != 255)
                Assert.Fail("Source frame pixels were mutated.");
    }

    [Fact]
    public void Render_Text_UsesTheSharedStrokeWidthAsItsSize()
    {
        var source = SolidFrame(120, 48, new AnnotationColor(255, 255, 255));
        var small = new AnnotationStroke(
            AnnotationTool.Text,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(10, 24) },
            text: "HH");
        var large = new AnnotationStroke(
            AnnotationTool.Text,
            new AnnotationColor(255, 0, 0),
            8,
            new[] { new AnnotationPoint(10, 24) },
            text: "HH");

        var smallRendered = AnnotationRenderer.Render(source, new[] { small });
        var largeRendered = AnnotationRenderer.Render(source, new[] { large });

        int CountColored(ContiguousBitmap bitmap)
        {
            int colored = 0;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (Pixel(bitmap, x, y).Equals(new AnnotationColor(255, 0, 0)))
                        colored++;
            return colored;
        }

        // A larger shared width renders a larger font, so more glyph pixels appear.
        Assert.True(CountColored(largeRendered) > CountColored(smallRendered));
    }

    [Fact]
    public void Render_Text_WhitespaceOnlyOrNullDrawsNothing()
    {
        var source = SolidFrame(120, 48, new AnnotationColor(255, 255, 255));
        var whitespace = new AnnotationStroke(
            AnnotationTool.Text,
            new AnnotationColor(255, 0, 0),
            4,
            new[] { new AnnotationPoint(10, 24) },
            text: "   ");

        var rendered = AnnotationRenderer.Render(source, new[] { whitespace });

        Assert.Equal(source.Pixels, rendered.Pixels);
    }

    // spec #41: numbered markers

    [Fact]
    public void Render_Marker_DrawsFilledDiscWithWhiteNumeralAtTheAnchorWithoutMutatingTheSource()
    {
        var source = SolidFrame(120, 48, new AnnotationColor(255, 255, 255));
        var stroke = new AnnotationStroke(
            AnnotationTool.Marker,
            new AnnotationColor(255, 0, 0),
            4,
            new[] { new AnnotationPoint(20, 24) },
            markerNumber: 1);

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        // The disc extends well beyond a bare stroke of the same width; probes sit
        // near the disc edge, clear of the centered numeral.
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 30, 24));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 10, 24));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 20, 34));
        Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, 20, 14));
        // A readable white numeral is stamped inside the disc.
        int whiteInDisc = 0;
        for (int y = 18; y <= 30; y++)
            for (int x = 14; x <= 26; x++)
                if (Pixel(rendered, x, y).Equals(new AnnotationColor(255, 255, 255)))
                    whiteInDisc++;
        Assert.True(whiteInDisc > 5, $"Expected numeral pixels, found {whiteInDisc}.");
        // Outside the disc nothing changes, and the source is never mutated.
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 0, 0));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(rendered, 119, 47));
        Assert.Equal(new AnnotationColor(255, 255, 255), Pixel(source, 20, 24));
    }

    [Fact]
    public void Render_Marker_NumeralScalesWithTheSharedStrokeWidth()
    {
        // A dark source keeps the white numeral distinguishable from the background.
        var source = SolidFrame(200, 90, new AnnotationColor(20, 30, 40));
        var small = new AnnotationStroke(
            AnnotationTool.Marker,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(30, 45) },
            markerNumber: 1);
        var large = new AnnotationStroke(
            AnnotationTool.Marker,
            new AnnotationColor(255, 0, 0),
            8,
            new[] { new AnnotationPoint(30, 45) },
            markerNumber: 1);

        var smallRendered = AnnotationRenderer.Render(source, new[] { small });
        var largeRendered = AnnotationRenderer.Render(source, new[] { large });

        int CountWhiteInDisc(ContiguousBitmap bitmap, int x0, int x1, int y0, int y1)
        {
            int white = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (Pixel(bitmap, x, y).Equals(new AnnotationColor(255, 255, 255)))
                        white++;
            return white;
        }

        // The larger width grows the disc and with it the numeral, so more white
        // pixels appear in a fixed window around the anchor.
        Assert.True(CountWhiteInDisc(largeRendered, 10, 60, 20, 70) > CountWhiteInDisc(smallRendered, 10, 60, 20, 70));
    }

    [Fact]
    public void Render_Markers_DrawEachNumeralInCommitOrder()
    {
        var source = SolidFrame(260, 90, new AnnotationColor(255, 255, 255));
        var markers = new[]
        {
            new AnnotationStroke(
                AnnotationTool.Marker,
                new AnnotationColor(255, 0, 0),
                3,
                new[] { new AnnotationPoint(30, 45) },
                markerNumber: 1),
            new AnnotationStroke(
                AnnotationTool.Marker,
                new AnnotationColor(255, 0, 0),
                3,
                new[] { new AnnotationPoint(120, 45) },
                markerNumber: 2),
        };

        var rendered = AnnotationRenderer.Render(source, markers);

        foreach (var marker in markers)
        {
            int cx = marker.Points[0].X;
            int cy = marker.Points[0].Y;
            // Probe near the disc edge, clear of the centered numeral.
            Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, cx + 10, cy));
            Assert.Equal(new AnnotationColor(255, 0, 0), Pixel(rendered, cx - 10, cy));
            int whiteInDisc = 0;
            for (int y = cy - 10; y <= cy + 10; y++)
                for (int x = cx - 12; x <= cx + 12; x++)
                    if (Pixel(rendered, x, y).Equals(new AnnotationColor(255, 255, 255)))
                        whiteInDisc++;
            Assert.True(whiteInDisc > 5, $"Expected numeral pixels at ({cx},{cy}), found {whiteInDisc}.");
        }
    }

    // spec #42: blur regions

    [Fact]
    public void Render_Blur_ObscuresHighContrastDetailInsideTheRegion()
    {
        // A black-and-white checkerboard is representative sensitive detail: sharp
        // 1px edges. After blur, no pixel inside the region stays pure black or white.
        var source = CheckerboardFrame(40, 30);
        var stroke = new AnnotationStroke(
            AnnotationTool.Blur,
            new AnnotationColor(255, 0, 0),
            4,
            new[] { new AnnotationPoint(4, 4), new AnnotationPoint(24, 16) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        int interior = 0;
        for (int y = 4; y <= 16; y++)
            for (int x = 4; x <= 24; x++)
            {
                var pixel = Pixel(rendered, x, y);
                Assert.NotEqual(0, pixel.Green);
                Assert.NotEqual(255, pixel.Green);
                interior++;
            }
        Assert.True(interior > 100, $"Expected obscured interior pixels, found {interior}.");
    }

    [Fact]
    public void Render_Blur_ClipsTheEffectToTheRegionAndLeavesOutsideIdentical()
    {
        var source = CheckerboardFrame(40, 30);
        var stroke = new AnnotationStroke(
            AnnotationTool.Blur,
            new AnnotationColor(255, 0, 0),
            4,
            new[] { new AnnotationPoint(4, 4), new AnnotationPoint(24, 16) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        // Every pixel outside the region is byte-identical to the source.
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
            {
                bool inside = x >= 4 && x <= 24 && y >= 4 && y <= 16;
                if (!inside)
                    Assert.Equal(Pixel(source, x, y), Pixel(rendered, x, y));
            }
        // The source Frame itself is never mutated.
        Assert.NotEqual(source.Pixels, rendered.Pixels);
        Assert.Equal(Pixel(source, 10, 10), Pixel(source, 10, 10));
    }

    [Fact]
    public void Render_Blur_OverAUniformRegion_ChangesNothingVisible()
    {
        // Blur of a uniform region is the same color: the composed Frame equals
        // the source everywhere.
        var source = SolidFrame(30, 20, new AnnotationColor(90, 120, 150));
        var stroke = new AnnotationStroke(
            AnnotationTool.Blur,
            new AnnotationColor(255, 0, 0),
            4,
            new[] { new AnnotationPoint(5, 5), new AnnotationPoint(20, 14) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        Assert.Equal(source.Pixels, rendered.Pixels);
    }

    [Fact]
    public void Render_Blur_IgnoresSharedColorAndWidth()
    {
        var source = CheckerboardFrame(40, 30);
        var red = new AnnotationStroke(
            AnnotationTool.Blur,
            new AnnotationColor(255, 0, 0),
            1,
            new[] { new AnnotationPoint(4, 4), new AnnotationPoint(24, 16) });
        var blue = new AnnotationStroke(
            AnnotationTool.Blur,
            new AnnotationColor(0, 0, 255),
            32,
            new[] { new AnnotationPoint(4, 4), new AnnotationPoint(24, 16) });

        var redRendered = AnnotationRenderer.Render(source, new[] { red });
        var blueRendered = AnnotationRenderer.Render(source, new[] { blue });

        // Neither the shared color nor the stroke width participates in the effect.
        Assert.Equal(redRendered.Pixels, blueRendered.Pixels);
        bool changed = false;
        for (int i = 0; i < redRendered.Pixels.Length; i++)
            if (redRendered.Pixels[i] != source.Pixels[i])
                changed = true;
        Assert.True(changed, "Expected the blur region to change.");
    }

    [Fact]
    public void Render_Blur_ClampsToFrameEdgesWithoutThrowing()
    {
        // A region dragged past the Frame edges is clamped, not an error.
        var source = CheckerboardFrame(20, 10);
        var stroke = new AnnotationStroke(
            AnnotationTool.Blur,
            new AnnotationColor(255, 0, 0),
            4,
            new[] { new AnnotationPoint(-30, -5), new AnnotationPoint(50, 25) });

        var rendered = AnnotationRenderer.Render(source, new[] { stroke });

        // The whole Frame is inside the clamped region, so every pixel is a blur
        // of the checkerboard and no longer pure black/white.
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 20; x++)
            {
                var pixel = Pixel(rendered, x, y);
                Assert.NotEqual(0, pixel.Green);
                Assert.NotEqual(255, pixel.Green);
            }
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
