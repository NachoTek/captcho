// RegionSelectionTests.cs — Unit tests for RegionSelection geometry model.
//
// All fixtures are inline; no file I/O or .gsd/ paths are referenced.

using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class RegionSelectionTests
{
    // ── Normalization from drag points ──────────────────────────────────

    [Fact]
    public void FromDragPoints_NormalDirection_ReturnsCorrectRegion()
    {
        // Drag top-left to bottom-right
        var region = RegionSelection.FromDragPoints(100, 50, 300, 200);

        Assert.Equal(100, region.X);
        Assert.Equal(50, region.Y);
        Assert.Equal(200, region.Width);
        Assert.Equal(150, region.Height);
    }

    [Fact]
    public void FromDragPoints_ReversedX_NormalizesWidth()
    {
        // Drag right-to-left
        var region = RegionSelection.FromDragPoints(300, 50, 100, 200);

        Assert.Equal(100, region.X);
        Assert.Equal(50, region.Y);
        Assert.Equal(200, region.Width);
    }

    [Fact]
    public void FromDragPoints_ReversedY_NormalizesHeight()
    {
        // Drag bottom-to-top
        var region = RegionSelection.FromDragPoints(100, 200, 300, 50);

        Assert.Equal(100, region.X);
        Assert.Equal(50, region.Y);
        Assert.Equal(150, region.Height);
    }

    [Fact]
    public void FromDragPoints_BothReversed_Normalizes()
    {
        // Drag bottom-right to top-left
        var region = RegionSelection.FromDragPoints(300, 200, 100, 50);

        Assert.Equal(100, region.X);
        Assert.Equal(50, region.Y);
        Assert.Equal(200, region.Width);
        Assert.Equal(150, region.Height);
    }

    [Fact]
    public void FromDragPoints_SamePoint_ZeroSize()
    {
        var region = RegionSelection.FromDragPoints(100, 100, 100, 100);

        Assert.Equal(100, region.X);
        Assert.Equal(100, region.Y);
        Assert.Equal(0, region.Width);
        Assert.Equal(0, region.Height);
        Assert.False(region.MeetsMinimumSize);
    }

    // ── Zero and negative dimensions ────────────────────────────────────

    [Fact]
    public void MeetsMinimumSize_SmallRegion_ReturnsFalse()
    {
        var region = RegionSelection.FromDragPoints(0, 0, 2, 2);
        Assert.False(region.MeetsMinimumSize);
    }

    [Fact]
    public void MeetsMinimumSize_ExactlyMinimum_ReturnsTrue()
    {
        var min = RegionSelection.MinimumSize;
        var region = RegionSelection.FromDragPoints(0, 0, min, min);
        Assert.True(region.MeetsMinimumSize);
    }

    [Fact]
    public void MeetsMinimumSize_LargeRegion_ReturnsTrue()
    {
        var region = RegionSelection.FromDragPoints(0, 0, 500, 400);
        Assert.True(region.MeetsMinimumSize);
    }

    // ── Movement ────────────────────────────────────────────────────────

    [Fact]
    public void Move_WithinBounds_ReturnsMovedRegion()
    {
        var region = RegionSelection.FromDragPoints(100, 100, 300, 300);
        var moved = region.Move(50, -25, 0, 0, 1000, 1000);

        Assert.NotNull(moved);
        Assert.Equal(150, moved!.X);
        Assert.Equal(75, moved.Y);
        Assert.Equal(200, moved.Width);
        Assert.Equal(200, moved.Height);
    }

    [Fact]
    public void Move_ExceedsRightBoundary_ClampsToEdge()
    {
        var region = RegionSelection.FromDragPoints(800, 100, 900, 200);
        var moved = region.Move(200, 0, 0, 0, 1000, 1000);

        Assert.NotNull(moved);
        Assert.Equal(1000, moved!.Right); // clamped to right edge
        Assert.Equal(900, moved.X);       // pushed back from 1000 to 900
    }

    [Fact]
    public void Move_ExceedsLeftBoundary_ClampsToEdge()
    {
        var region = RegionSelection.FromDragPoints(50, 50, 150, 150);
        var moved = region.Move(-100, 0, 0, 0, 1000, 1000);

        Assert.NotNull(moved);
        Assert.Equal(0, moved!.X); // clamped to left edge
    }

    [Fact]
    public void Move_LargerThanBounds_ReturnsNull()
    {
        // Region 200x200 cannot fit in 100x100 bounds
        var region = RegionSelection.FromDragPoints(0, 0, 200, 200);
        var moved = region.Move(0, 0, 0, 0, 100, 100);

        Assert.Null(moved);
    }

    [Fact]
    public void Move_SameSizeAsBounds_ClampsToOrigin()
    {
        // Region exactly the same size as bounds snaps to (0,0)
        var region = RegionSelection.FromDragPoints(500, 500, 600, 600);
        var moved = region.Move(0, 0, 0, 0, 100, 100);

        Assert.NotNull(moved);
        Assert.Equal(0, moved!.X);
        Assert.Equal(0, moved.Y);
        Assert.Equal(100, moved.Width);
        Assert.Equal(100, moved.Height);
    }

    [Fact]
    public void Move_NegativeBoundsOrigin_WorksCorrectly()
    {
        // Virtual desktop where left monitor has negative x coordinates
        var region = RegionSelection.FromDragPoints(-500, 0, -300, 200);
        var moved = region.Move(100, 0, -1000, -500, 2000, 1500);

        Assert.NotNull(moved);
        Assert.Equal(-400, moved!.X);
        Assert.Equal(0, moved.Y);
    }

    // ── Resizing ────────────────────────────────────────────────────────

    [Fact]
    public void Resize_TopLeftAnchor_ExpandsRightAndDown()
    {
        var region = RegionSelection.FromDragPoints(100, 100, 300, 300);
        var resized = region.Resize(50, 50, "tl", 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(100, resized!.X);
        Assert.Equal(100, resized.Y);
        Assert.Equal(250, resized.Width);
        Assert.Equal(250, resized.Height);
    }

    [Fact]
    public void Resize_BottomRightAnchor_ExpandsLeftAndUp()
    {
        var region = RegionSelection.FromDragPoints(100, 100, 300, 300);
        var resized = region.Resize(50, 50, "br", 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        // Bottom-right is fixed at (300,300), so X moves left, Y moves up
        Assert.Equal(50, resized!.X);
        Assert.Equal(50, resized.Y);
        Assert.Equal(250, resized.Width);
        Assert.Equal(250, resized.Height);
    }

    [Fact]
    public void Resize_TopRightAnchor_AdjustsLeftAndBottom()
    {
        var region = RegionSelection.FromDragPoints(100, 100, 300, 300);
        var resized = region.Resize(50, 50, "tr", 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        // Top-right fixed at (300, 100)
        Assert.Equal(50, resized!.X);     // X moves left
        Assert.Equal(100, resized.Y);     // Y stays (top)
        Assert.Equal(250, resized.Width);
        Assert.Equal(250, resized.Height);
    }

    [Fact]
    public void Resize_BottomLeftAnchor_AdjustsRightAndTop()
    {
        var region = RegionSelection.FromDragPoints(100, 100, 300, 300);
        var resized = region.Resize(50, 50, "bl", 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        // Bottom-left fixed at (100, 300)
        Assert.Equal(100, resized!.X);    // X stays (left)
        Assert.Equal(50, resized.Y);      // Y moves up
        Assert.Equal(250, resized.Width);
        Assert.Equal(250, resized.Height);
    }

    [Fact]
    public void Resize_ShrinksBelowMinimum_ClampsToMinimumSize()
    {
        var region = RegionSelection.FromDragPoints(100, 100, 200, 200);
        // Shrink by more than the size minus minimum
        var resized = region.Resize(-250, -250, "tl", 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.True(resized!.Width >= RegionSelection.MinimumSize);
        Assert.True(resized.Height >= RegionSelection.MinimumSize);
    }

    [Fact]
    public void Resize_InvalidAnchor_Throws()
    {
        var region = RegionSelection.FromDragPoints(0, 0, 100, 100);
        Assert.Throws<ArgumentException>(() =>
            region.Resize(10, 10, "center", 0, 0, 1000, 1000));
    }

    [Fact]
    public void Resize_ExceedsBounds_ClampsToEdge()
    {
        var region = RegionSelection.FromDragPoints(900, 100, 950, 200);
        // Expand right by 200 with top-left anchor — should clamp to bounds edge
        var resized = region.Resize(200, 0, "tl", 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.True(resized!.Right <= 1000);
    }

    // ── Handle-driven resizing (ResizeHandle) ───────────────────────────
    //
    // Pointer-driven resize moves the edge(s) belonging to a handle to the
    // cursor position while the opposite edge(s) stay fixed, then enforces the
    // minimum size and keeps the rectangle inside the Virtual Desktop bounds.
    // Used by the visible resize handles (issue #30). The delta-based Resize
    // above remains for keyboard nudging.

    private static RegionSelection SampleSelection() => RegionSelection.FromDragPoints(100, 100, 300, 300);

    [Fact]
    public void ResizeHandle_TopLeft_MovesLeftAndTopEdges()
    {
        // Drag the top-left corner up-and-left to (50,50); right/bottom fixed.
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.TopLeft, 50, 50, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(50, resized!.X);
        Assert.Equal(50, resized.Y);
        Assert.Equal(250, resized.Width);
        Assert.Equal(250, resized.Height);
    }

    [Fact]
    public void ResizeHandle_TopRight_MovesRightAndTopEdges()
    {
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.TopRight, 400, 50, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(100, resized!.X);     // left fixed
        Assert.Equal(50, resized.Y);       // top moved up
        Assert.Equal(300, resized.Width);  // 400-100
        Assert.Equal(250, resized.Height); // 300-50
    }

    [Fact]
    public void ResizeHandle_BottomLeft_MovesLeftAndBottomEdges()
    {
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.BottomLeft, 50, 400, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(50, resized!.X);       // left moved
        Assert.Equal(100, resized.Y);       // top fixed
        Assert.Equal(250, resized.Width);   // 300-50
        Assert.Equal(300, resized.Height);  // 400-100
    }

    [Fact]
    public void ResizeHandle_BottomRight_MovesRightAndBottomEdges()
    {
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.BottomRight, 400, 400, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(100, resized!.X);
        Assert.Equal(100, resized.Y);
        Assert.Equal(300, resized.Width);
        Assert.Equal(300, resized.Height);
    }

    [Fact]
    public void ResizeHandle_Top_MovesOnlyTopEdge()
    {
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.Top, 200, 50, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(100, resized!.X);
        Assert.Equal(50, resized.Y);
        Assert.Equal(200, resized.Width);  // width unchanged
        Assert.Equal(250, resized.Height); // 300-50
    }

    [Fact]
    public void ResizeHandle_Bottom_MovesOnlyBottomEdge()
    {
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.Bottom, 200, 400, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(100, resized!.X);
        Assert.Equal(100, resized!.Y);
        Assert.Equal(200, resized.Width);
        Assert.Equal(300, resized.Height);
    }

    [Fact]
    public void ResizeHandle_Left_MovesOnlyLeftEdge()
    {
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.Left, 50, 200, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(50, resized!.X);
        Assert.Equal(100, resized.Y);
        Assert.Equal(250, resized.Width);  // 300-50
        Assert.Equal(200, resized.Height); // unchanged
    }

    [Fact]
    public void ResizeHandle_Right_MovesOnlyRightEdge()
    {
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.Right, 400, 200, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(100, resized!.X);
        Assert.Equal(100, resized.Y);
        Assert.Equal(300, resized.Width);  // 400-100
        Assert.Equal(200, resized.Height); // unchanged
    }

    [Fact]
    public void ResizeHandle_ShrinkPastOppositeEdge_ClampsToMinimumSize()
    {
        // Drag top-left corner past the bottom-right corner: width/height must
        // clamp to MinimumSize, never invert or collapse to zero.
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.TopLeft, 350, 350, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.True(resized!.Width >= RegionSelection.MinimumSize);
        Assert.True(resized.Height >= RegionSelection.MinimumSize);
        Assert.True(resized.Right <= 300); // right edge never exceeded
    }

    [Fact]
    public void ResizeHandle_DragOutsideBoundsLeft_ClampsToBoundsEdge()
    {
        // Drag top-left corner off the left/top of the virtual desktop: the
        // rectangle must not escape the Virtual Desktop.
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.TopLeft, -50, -50, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(0, resized!.X); // clamped to left bound
        Assert.Equal(0, resized.Y);  // clamped to top bound
    }

    [Fact]
    public void ResizeHandle_DragOutsideBoundsRight_ClampsToBoundsEdge()
    {
        var resized = SampleSelection().ResizeHandle(SelectionHandleKind.BottomRight, 1200, 1200, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(1000, resized!.Right);  // clamped to right bound
        Assert.Equal(1000, resized.Bottom);  // clamped to bottom bound
    }

    [Fact]
    public void ResizeHandle_SelectionFillsEntireBounds_CannotEscape()
    {
        // Maximum-bounds case: the selection already fills the whole Virtual
        // Desktop. Dragging a handle outward must keep it clamped to the bounds
        // (no escape, no invalid geometry).
        var full = RegionSelection.FromDragPoints(0, 0, 1000, 1000);
        var resized = full.ResizeHandle(SelectionHandleKind.BottomRight, 5000, 5000, 0, 0, 1000, 1000);

        Assert.NotNull(resized);
        Assert.Equal(0, resized!.X);
        Assert.Equal(0, resized.Y);
        Assert.Equal(1000, resized.Right);   // clamped to bounds
        Assert.Equal(1000, resized.Bottom);
    }

    [Fact]
    public void ResizeHandle_None_ReturnsNull()
    {
        // None is not a resize handle — the overlay starts a new drag instead.
        Assert.Null(SampleSelection().ResizeHandle(SelectionHandleKind.None, 50, 50, 0, 0, 1000, 1000));
    }

    [Fact]
    public void ResizeHandle_Body_ReturnsNull()
    {
        // Body is a move, not a resize — the overlay moves the rectangle instead.
        Assert.Null(SampleSelection().ResizeHandle(SelectionHandleKind.Body, 200, 200, 0, 0, 1000, 1000));
    }

    [Fact]
    public void ResizeHandle_NegativeOriginSelection_ExpandsWithoutEscaping()
    {
        // Selection entirely on a left monitor: (-300,-200) to (-100,0); bounds
        // start at (-1000,-500). Dragging the top-left corner further out keeps
        // it inside the Virtual Desktop.
        var sel = RegionSelection.FromDragPoints(-300, -200, -100, 0);
        var resized = sel.ResizeHandle(SelectionHandleKind.TopLeft, -500, -400, -1000, -500, 2000, 1500);

        Assert.NotNull(resized);
        Assert.Equal(-500, resized!.X);
        Assert.Equal(-400, resized.Y);
        Assert.Equal(400, resized.Width);  // -100 - (-500)
        Assert.Equal(400, resized.Height); // 0 - (-400)
    }

    [Fact]
    public void ResizeHandle_SelectionSpanningOrigin_WorksAcrossBoundary()
    {
        // Selection crossing x=0 between a left monitor and the primary.
        var sel = RegionSelection.FromDragPoints(-100, 100, 200, 300);
        var resized = sel.ResizeHandle(SelectionHandleKind.BottomRight, 400, 500, -1000, -500, 2000, 1500);

        Assert.NotNull(resized);
        Assert.Equal(-100, resized!.X);
        Assert.Equal(100, resized.Y);
        Assert.Equal(500, resized.Width);  // 400 - (-100)
        Assert.Equal(400, resized.Height); // 500 - 100
    }

    // ── Bounds clamping ─────────────────────────────────────────────────

    [Fact]
    public void ClampToBounds_FullyInside_ReturnsSameRegion()
    {
        var region = RegionSelection.FromDragPoints(50, 50, 150, 150);
        var clamped = region.ClampToBounds(0, 0, 1000, 1000);

        Assert.NotNull(clamped);
        Assert.Equal(50, clamped!.X);
        Assert.Equal(50, clamped.Y);
        Assert.Equal(100, clamped.Width);
        Assert.Equal(100, clamped.Height);
    }

    [Fact]
    public void ClampToBounds_OverflowsRight_Clamps()
    {
        var region = RegionSelection.FromDragPoints(950, 50, 1100, 150);
        var clamped = region.ClampToBounds(0, 0, 1000, 1000);

        Assert.NotNull(clamped);
        Assert.Equal(950, clamped!.X);
        Assert.Equal(1000, clamped.Right); // clamped to edge
        Assert.Equal(50, clamped.Width);
    }

    [Fact]
    public void ClampToBounds_OverflowsLeftWithNegativeCoords_Clamps()
    {
        var region = RegionSelection.FromDragPoints(-100, 50, 50, 150);
        var clamped = region.ClampToBounds(-200, 0, 400, 500);

        Assert.NotNull(clamped);
        Assert.Equal(-100, clamped!.X);
    }

    [Fact]
    public void ClampToBounds_EntirelyOutside_ReturnsNull()
    {
        var region = RegionSelection.FromDragPoints(2000, 2000, 2100, 2100);
        var clamped = region.ClampToBounds(0, 0, 1000, 1000);

        Assert.Null(clamped);
    }

    [Fact]
    public void ClampToBounds_NegativeVirtualDesktopOrigin_Works()
    {
        // Virtual desktop spanning from (-1920, 0) to (1920, 1080)
        var region = RegionSelection.FromDragPoints(-1920, 0, -960, 540);
        var clamped = region.ClampToBounds(-1920, 0, 3840, 1080);

        Assert.NotNull(clamped);
        Assert.Equal(-1920, clamped!.X);
        Assert.Equal(0, clamped.Y);
        Assert.Equal(960, clamped.Width);
        Assert.Equal(540, clamped.Height);
    }

    [Fact]
    public void ClampToBounds_ZeroSizeBounds_ReturnsNull()
    {
        var region = RegionSelection.FromDragPoints(0, 0, 100, 100);
        var clamped = region.ClampToBounds(0, 0, 0, 0);

        Assert.Null(clamped);
    }

    // ── FromDragPointsClamped ───────────────────────────────────────────

    [Fact]
    public void FromDragPointsClamped_WithinBounds_ReturnsRegion()
    {
        var region = RegionSelection.FromDragPointsClamped(100, 100, 500, 400, 0, 0, 1920, 1080);

        Assert.NotNull(region);
        Assert.Equal(100, region!.X);
        Assert.Equal(100, region.Y);
        Assert.Equal(400, region.Width);
        Assert.Equal(300, region.Height);
    }

    [Fact]
    public void FromDragPointsClamped_OutsideBounds_ReturnsNull()
    {
        var region = RegionSelection.FromDragPointsClamped(2000, 2000, 3000, 3000, 0, 0, 1920, 1080);

        Assert.Null(region);
    }

    // ── Record equality ─────────────────────────────────────────────────

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = RegionSelection.FromDragPoints(10, 20, 110, 120);
        var b = RegionSelection.FromDragPoints(10, 20, 110, 120);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ── Right/Bottom computed properties ────────────────────────────────

    [Fact]
    public void Right_ReturnsXPlusWidth()
    {
        var region = RegionSelection.FromDragPoints(10, 20, 110, 70);
        Assert.Equal(110, region.Right);
    }

    [Fact]
    public void Bottom_ReturnsYPlusHeight()
    {
        var region = RegionSelection.FromDragPoints(10, 20, 110, 70);
        Assert.Equal(70, region.Bottom);
    }

    // ── Negative virtual-desktop edge cases ─────────────────────────────

    [Fact]
    public void Move_IntoNegativeOrigin_ClampsCorrectly()
    {
        // Region starts at x=-500, move left by 100 — should clamp to bounds left edge at -1000
        var region = RegionSelection.FromDragPoints(-500, 100, -300, 300);
        var moved = region.Move(-800, 0, -1000, -500, 2000, 1500);

        Assert.NotNull(moved);
        Assert.Equal(-1000, moved!.X); // clamped to left bound
    }

    [Fact]
    public void Selection_LargerThanBounds_ClampsToEntireBounds()
    {
        // Selection bigger than the entire bounds
        var region = RegionSelection.FromDragPoints(-5000, -5000, 5000, 5000);
        var clamped = region.ClampToBounds(-1920, 0, 3840, 1080);

        Assert.NotNull(clamped);
        Assert.Equal(-1920, clamped!.X);
        Assert.Equal(0, clamped.Y);
        Assert.Equal(3840, clamped.Width);
        Assert.Equal(1080, clamped.Height);
    }
}
