// RegionSelectionTests.cs — Unit tests for RegionSelection geometry model.
//
// All fixtures are inline; no file I/O or .gsd/ paths are referenced.

using Respectacle.UI;
using Xunit;

namespace Respectacle.UI.Tests;

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
