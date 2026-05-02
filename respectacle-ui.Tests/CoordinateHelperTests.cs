// CoordinateHelperTests.cs — Unit tests for overlay/virtual-desktop coordinate translation.
//
// All fixtures are inline; no file I/O or .gsd/ paths are referenced.

using Respectacle.UI;
using Xunit;

namespace Respectacle.UI.Tests;

public class CoordinateHelperTests
{
    // ── OverlayToVirtual ────────────────────────────────────────────────

    [Fact]
    public void OverlayToVirtual_PrimaryMonitorOrigin_ReturnsDirectCoordinates()
    {
        // Overlay at (0,0) — primary monitor origin
        var (x, y) = CoordinateHelper.OverlayToVirtual(100, 200, 0, 0);

        Assert.Equal(100, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void OverlayToVirtual_NegativeOverlayPosition_TranslatesCorrectly()
    {
        // Overlay at (-1920, 0) — left of primary on multi-monitor
        var (x, y) = CoordinateHelper.OverlayToVirtual(500, 300, -1920, 0);

        Assert.Equal(-1420, x);
        Assert.Equal(300, y);
    }

    [Fact]
    public void OverlayToVirtual_NegativeYOrigin_TranslatesCorrectly()
    {
        // Overlay at (0, -500) — monitor above primary
        var (x, y) = CoordinateHelper.OverlayToVirtual(100, 200, 0, -500);

        Assert.Equal(100, x);
        Assert.Equal(-300, y);
    }

    [Fact]
    public void OverlayToVirtual_BothNegativeOrigins_TranslatesCorrectly()
    {
        var (x, y) = CoordinateHelper.OverlayToVirtual(100, 100, -1920, -300);

        Assert.Equal(-1820, x);
        Assert.Equal(-200, y);
    }

    [Fact]
    public void OverlayToVirtual_ZeroOffset_ReturnsSamePoint()
    {
        var (x, y) = CoordinateHelper.OverlayToVirtual(0, 0, 0, 0);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    // ── VirtualToOverlay ────────────────────────────────────────────────

    [Fact]
    public void VirtualToOverlay_PrimaryMonitorOrigin_ReturnsDirectCoordinates()
    {
        var (x, y) = CoordinateHelper.VirtualToOverlay(100, 200, 0, 0);

        Assert.Equal(100, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void VirtualToOverlay_NegativeOverlayPosition_TranslatesCorrectly()
    {
        // Virtual point (-1420, 300) relative to overlay at (-1920, 0)
        var (x, y) = CoordinateHelper.VirtualToOverlay(-1420, 300, -1920, 0);

        Assert.Equal(500, x);
        Assert.Equal(300, y);
    }

    [Fact]
    public void VirtualToOverlay_NegativeYOrigin_TranslatesCorrectly()
    {
        // Virtual point (100, -300) relative to overlay at (0, -500)
        var (x, y) = CoordinateHelper.VirtualToOverlay(100, -300, 0, -500);

        Assert.Equal(100, x);
        Assert.Equal(200, y);
    }

    // ── Round-trip: Overlay → Virtual → Overlay ─────────────────────────

    [Fact]
    public void RoundTrip_PrimaryMonitor_ReturnsOriginal()
    {
        const int overlayX = 500, overlayY = 300;
        var (vx, vy) = CoordinateHelper.OverlayToVirtual(overlayX, overlayY, 0, 0);
        var (rx, ry) = CoordinateHelper.VirtualToOverlay(vx, vy, 0, 0);

        Assert.Equal(overlayX, rx);
        Assert.Equal(overlayY, ry);
    }

    [Fact]
    public void RoundTrip_NegativeOrigin_ReturnsOriginal()
    {
        const int overlayX = 500, overlayY = 300;
        const int overlayVX = -1920, overlayVY = -500;
        var (vx, vy) = CoordinateHelper.OverlayToVirtual(overlayX, overlayY, overlayVX, overlayVY);
        var (rx, ry) = CoordinateHelper.VirtualToOverlay(vx, vy, overlayVX, overlayVY);

        Assert.Equal(overlayX, rx);
        Assert.Equal(overlayY, ry);
    }

    [Fact]
    public void RoundTrip_LargeNegativeOrigin_ReturnsOriginal()
    {
        const int overlayX = 1000, overlayY = 800;
        const int overlayVX = -3840, overlayVY = -2160;
        var (vx, vy) = CoordinateHelper.OverlayToVirtual(overlayX, overlayY, overlayVX, overlayVY);
        var (rx, ry) = CoordinateHelper.VirtualToOverlay(vx, vy, overlayVX, overlayVY);

        Assert.Equal(overlayX, rx);
        Assert.Equal(overlayY, ry);
    }

    // ── GetOverlayBounds ────────────────────────────────────────────────

    [Fact]
    public void GetOverlayBounds_PositiveOrigin_ReturnsExactBounds()
    {
        var (x, y, w, h) = CoordinateHelper.GetOverlayBounds(0, 0, 1920, 1080);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void GetOverlayBounds_NegativeOrigin_ReturnsExactBounds()
    {
        var (x, y, w, h) = CoordinateHelper.GetOverlayBounds(-1920, -300, 3840, 1380);

        Assert.Equal(-1920, x);
        Assert.Equal(-300, y);
        Assert.Equal(3840, w);
        Assert.Equal(1380, h);
    }

    [Fact]
    public void GetOverlayBounds_ZeroWidth_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CoordinateHelper.GetOverlayBounds(0, 0, 0, 1080));
    }

    [Fact]
    public void GetOverlayBounds_ZeroHeight_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CoordinateHelper.GetOverlayBounds(0, 0, 1920, 0));
    }

    [Fact]
    public void GetOverlayBounds_NegativeWidth_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CoordinateHelper.GetOverlayBounds(0, 0, -100, 1080));
    }

    // ── IsPointInBounds ─────────────────────────────────────────────────

    [Fact]
    public void IsPointInBounds_PointInside_ReturnsTrue()
    {
        Assert.True(CoordinateHelper.IsPointInBounds(50, 50, 0, 0, 100, 100));
    }

    [Fact]
    public void IsPointInBounds_PointOutside_ReturnsFalse()
    {
        Assert.False(CoordinateHelper.IsPointInBounds(200, 200, 0, 0, 100, 100));
    }

    [Fact]
    public void IsPointInBounds_PointOnTopLeftEdge_ReturnsTrue()
    {
        Assert.True(CoordinateHelper.IsPointInBounds(0, 0, 0, 0, 100, 100));
    }

    [Fact]
    public void IsPointInBounds_PointOnBottomRightEdge_ReturnsFalse()
    {
        // Bottom-right edge is exclusive (x < boundsX + width)
        Assert.False(CoordinateHelper.IsPointInBounds(100, 100, 0, 0, 100, 100));
    }

    [Fact]
    public void IsPointInBounds_PointJustInsideBottomRight_ReturnsTrue()
    {
        Assert.True(CoordinateHelper.IsPointInBounds(99, 99, 0, 0, 100, 100));
    }

    [Fact]
    public void IsPointInBounds_NegativeOrigin_PointInside_ReturnsTrue()
    {
        Assert.True(CoordinateHelper.IsPointInBounds(-1800, 50, -1920, 0, 3840, 1080));
    }

    [Fact]
    public void IsPointInBounds_NegativeOrigin_PointOutsideLeft_ReturnsFalse()
    {
        Assert.False(CoordinateHelper.IsPointInBounds(-2000, 50, -1920, 0, 3840, 1080));
    }

    // ── RoundToPixel ────────────────────────────────────────────────────

    [Fact]
    public void RoundToPixel_WholeNumbers_ReturnsSame()
    {
        var (x, y) = CoordinateHelper.RoundToPixel(100.0, 200.0);

        Assert.Equal(100, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void RoundToPixel_HalfValues_RoundsAwayFromZero()
    {
        var (x, y) = CoordinateHelper.RoundToPixel(100.5, 200.5);

        Assert.Equal(101, x);  // MidpointRounding.AwayFromZero rounds 0.5 up
        Assert.Equal(201, y);
    }

    [Fact]
    public void RoundToPixel_NegativeValues_RoundsAwayFromZero()
    {
        var (x, y) = CoordinateHelper.RoundToPixel(-100.5, -200.5);

        Assert.Equal(-101, x); // Away from zero → -101
        Assert.Equal(-201, y);
    }

    [Fact]
    public void RoundToPixel_SmallFraction_Truncates()
    {
        var (x, y) = CoordinateHelper.RoundToPixel(100.2, 200.8);

        Assert.Equal(100, x);
        Assert.Equal(201, y);
    }
}
