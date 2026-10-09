// SelectionMagnifierPlacementTests.cs — Focused contract tests for the
// Selection overlay's adaptive magnifier.
//
// The interactive region overlay is a Win32 layered window that cannot run in a
// headless test, so its externally observable magnifier contract — fixed 200px
// size at 4x zoom, a source region centered on the active boundary point and
// clamped at the Virtual Desktop edges, and a destination that picks a visible
// quadrant so it does not obscure the boundary being adjusted — is asserted
// against the pure SelectionMagnifierPlacement resolver that the production
// overlay delegates to. These tests cover the size/zoom, quadrant, mixed-DPI,
// cross-monitor, negative-coordinate, and constrained-layout acceptance
// criteria of issue #31; live pixel sampling and overlay visibility timing
// remain rendering concerns exercised through the workflow seam.
//
// Coordinate convention matches the rest of the codebase: the selection and the
// magnifier are in Virtual Desktop pixels (X/Y may be negative for monitors
// left/above the primary), and the Virtual Desktop bounds are the same signed
// rectangle reported by GetSystemMetrics(SM_*VIRTUALSCREEN).

using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class SelectionMagnifierPlacementTests
{
    // A 1920×1080 primary-only Virtual Desktop rooted at the origin. Used by
    // most fixtures so the centre is (960, 540) and quadrants are obvious.
    private const int VdX = 0;
    private const int VdY = 0;
    private const int VdW = 1920;
    private const int VdH = 1080;

    // ── Fixed size and zoom ────────────────────────────────────────────

    [Fact]
    public void Size_IsTwoHundredPixels()
    {
        Assert.Equal(200, SelectionMagnifierPlacement.Size);
    }

    [Fact]
    public void Zoom_IsFourX()
    {
        Assert.Equal(4, SelectionMagnifierPlacement.Zoom);
    }

    [Fact]
    public void SourceSize_IsFiftyPixels_SoDestinationIsFourX()
    {
        // A 200px destination showing a 50px source is exactly 4x magnification.
        Assert.Equal(50, SelectionMagnifierPlacement.SourceSize);
        Assert.Equal(
            SelectionMagnifierPlacement.Size,
            SelectionMagnifierPlacement.SourceSize * SelectionMagnifierPlacement.Zoom);
    }

    // ── Source centred on the active boundary point ────────────────────

    [Fact]
    public void Resolve_InOpenLayout_CentresSourceOnFocus()
    {
        // Focus well inside the Virtual Desktop: the 50px source is centred on
        // the focus point so the active boundary sits in the middle of the view.
        var placement = SelectionMagnifierPlacement.Resolve(1000, 1000, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(1000 - 25, placement!.SourceX);
        Assert.Equal(1000 - 25, placement.SourceY);
        Assert.Equal(50, placement.SourceWidth);
        Assert.Equal(50, placement.SourceHeight);
    }

    [Fact]
    public void Resolve_DestinationIsAlwaysFullSize()
    {
        var placement = SelectionMagnifierPlacement.Resolve(1000, 1000, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(200, placement.DestinationWidth);
        Assert.Equal(200, placement.DestinationHeight);
    }

    // ── Source clamped at the Virtual Desktop edges ────────────────────

    [Fact]
    public void Resolve_FocusAtLeftEdge_ClampsSourceToLeftBound()
    {
        // Focus on the left bound: the source cannot extend past the Virtual
        // Desktop, so it is clamped to start at the left edge rather than
        // centre on the focus (which would sample off-screen pixels).
        var placement = SelectionMagnifierPlacement.Resolve(0, 540, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(0, placement!.SourceX);
        Assert.Equal(540 - 25, placement.SourceY);
    }

    [Fact]
    public void Resolve_FocusAtRightEdge_ClampsSourceToRightBound()
    {
        var placement = SelectionMagnifierPlacement.Resolve(1920, 540, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(1920 - 50, placement!.SourceX);
        Assert.Equal(540 - 25, placement.SourceY);
    }

    [Fact]
    public void Resolve_FocusAtTopEdge_ClampsSourceToTopBound()
    {
        var placement = SelectionMagnifierPlacement.Resolve(960, 0, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(0, placement!.SourceY);
        Assert.Equal(960 - 25, placement.SourceX);
    }

    [Fact]
    public void Resolve_FocusAtBottomEdge_ClampsSourceToBottomBound()
    {
        var placement = SelectionMagnifierPlacement.Resolve(960, 1080, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(1080 - 50, placement!.SourceY);
        Assert.Equal(960 - 25, placement.SourceX);
    }

    // ── Quadrant-aware destination placement ───────────────────────────
    //
    // The destination chooses the quadrant (relative to the focus) that points
    // toward the roomier half of the Virtual Desktop, so it lands away from the
    // edge being adjusted and never covers the focus point itself. The four
    // combinations of focus-in-left/right half and top/bottom half exercise all
    // four quadrants.

    [Fact]
    public void Resolve_FocusInTopLeftHalf_PlacesDestinationDownRight()
    {
        // Focus (100,100) is left of centre (960) and above centre (540):
        // magnifier goes right and below the focus.
        var placement = SelectionMagnifierPlacement.Resolve(100, 100, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(100 + SelectionMagnifierPlacement.Gap, placement!.DestinationX);
        Assert.Equal(100 + SelectionMagnifierPlacement.Gap, placement.DestinationY);
    }

    [Fact]
    public void Resolve_FocusInBottomRightHalf_PlacesDestinationUpLeft()
    {
        var placement = SelectionMagnifierPlacement.Resolve(1500, 900, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(1500 - SelectionMagnifierPlacement.Gap - 200, placement!.DestinationX);
        Assert.Equal(900 - SelectionMagnifierPlacement.Gap - 200, placement.DestinationY);
    }

    [Fact]
    public void Resolve_FocusInTopRightHalf_PlacesDestinationDownLeft()
    {
        var placement = SelectionMagnifierPlacement.Resolve(1500, 100, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(1500 - SelectionMagnifierPlacement.Gap - 200, placement!.DestinationX);
        Assert.Equal(100 + SelectionMagnifierPlacement.Gap, placement.DestinationY);
    }

    [Fact]
    public void Resolve_FocusInBottomLeftHalf_PlacesDestinationUpRight()
    {
        var placement = SelectionMagnifierPlacement.Resolve(100, 900, VdX, VdY, VdW, VdH);

        Assert.NotNull(placement);
        Assert.Equal(100 + SelectionMagnifierPlacement.Gap, placement!.DestinationX);
        Assert.Equal(900 - SelectionMagnifierPlacement.Gap - 200, placement.DestinationY);
    }

    [Fact]
    public void Resolve_DestinationNeverCoversTheFocusPoint()
    {
        // Across every quadrant the destination rectangle must not contain the
        // focus point — that is what "does not obscure the boundary being
        // adjusted" reduces to in geometry terms.
        var focusCases = new (int x, int y)[]
        {
            (100, 100), (1500, 100), (100, 900), (1500, 900), (960, 540),
        };

        foreach (var (fx, fy) in focusCases)
        {
            var placement = SelectionMagnifierPlacement.Resolve(fx, fy, VdX, VdY, VdW, VdH);
            Assert.NotNull(placement);
            Assert.False(
                Contains(placement!.DestinationX, placement.DestinationY,
                         placement.DestinationWidth, placement.DestinationHeight, fx, fy),
                $"magnifier at ({placement.DestinationX},{placement.DestinationY}) covered focus ({fx},{fy})");
        }
    }

    // ── Destination clamped into the Virtual Desktop ───────────────────

    [Fact]
    public void Resolve_DestinationNearEdge_ClampsIntoVirtualDesktop()
    {
        // Focus just past the centre of a 400×400 Virtual Desktop: the chosen
        // quadrant would push the destination off the left/top, so it is clamped
        // back to the bounds rather than escaping the screen.
        var placement = SelectionMagnifierPlacement.Resolve(210, 210, 0, 0, 400, 400);

        Assert.NotNull(placement);
        Assert.Equal(0, placement!.DestinationX);
        Assert.Equal(0, placement.DestinationY);
        Assert.Equal(200, placement.DestinationWidth);
    }

    [Fact]
    public void Resolve_DestinationAlwaysStaysInsideVirtualDesktop()
    {
        var focusCases = new (int x, int y)[]
        {
            (0, 0), (1920, 0), (0, 1080), (1920, 1080), (960, 540), (1, 1), (1919, 1079),
        };

        foreach (var (fx, fy) in focusCases)
        {
            var placement = SelectionMagnifierPlacement.Resolve(fx, fy, VdX, VdY, VdW, VdH);
            Assert.NotNull(placement);
            Assert.True(placement!.DestinationX >= VdX, $"destX {placement.DestinationX} < {VdX} at ({fx},{fy})");
            Assert.True(placement.DestinationY >= VdY, $"destY {placement.DestinationY} < {VdY} at ({fx},{fy})");
            Assert.True(placement.DestinationX + placement.DestinationWidth <= VdX + VdW,
                $"dest right {placement.DestinationX + placement.DestinationWidth} > {VdX + VdW} at ({fx},{fy})");
            Assert.True(placement.DestinationY + placement.DestinationHeight <= VdY + VdH,
                $"dest bottom {placement.DestinationY + placement.DestinationHeight} > {VdY + VdH} at ({fx},{fy})");
        }
    }

    // ── Source always stays inside the Virtual Desktop ─────────────────

    [Fact]
    public void Resolve_SourceAlwaysStaysInsideVirtualDesktop()
    {
        var focusCases = new (int x, int y)[]
        {
            (-100, -100), (0, 0), (1920, 1080), (2020, 1180), (960, 540),
        };

        foreach (var (fx, fy) in focusCases)
        {
            var placement = SelectionMagnifierPlacement.Resolve(fx, fy, VdX, VdY, VdW, VdH);
            Assert.NotNull(placement);
            Assert.True(placement!.SourceX >= VdX, $"srcX {placement.SourceX} < {VdX} at ({fx},{fy})");
            Assert.True(placement.SourceY >= VdY, $"srcY {placement.SourceY} < {VdY} at ({fx},{fy})");
            Assert.True(placement.SourceX + placement.SourceWidth <= VdX + VdW,
                $"src right {placement.SourceX + placement.SourceWidth} > {VdX + VdW} at ({fx},{fy})");
            Assert.True(placement.SourceY + placement.SourceHeight <= VdY + VdH,
                $"src bottom {placement.SourceY + placement.SourceHeight} > {VdY + VdH} at ({fx},{fy})");
        }
    }

    // ── Negative-coordinate and spanning-origin layouts ────────────────

    [Fact]
    public void Resolve_NegativeCoordinateVirtualDesktop_PlacesAndSamplesCorrectly()
    {
        // A second monitor to the left/above the primary: Virtual Desktop spans
        // (-1920,-1080)..(1920,1080), centre at (0,0). Focus in the left/above
        // half lands the destination down-and-right of the focus.
        const int vx = -1920, vy = -1080, vw = 3840, vh = 2160;
        var placement = SelectionMagnifierPlacement.Resolve(-1000, -500, vx, vy, vw, vh);

        Assert.NotNull(placement);
        Assert.Equal(-1000 - 25, placement!.SourceX);
        Assert.Equal(-500 - 25, placement.SourceY);
        Assert.Equal(-1000 + SelectionMagnifierPlacement.Gap, placement.DestinationX);
        Assert.Equal(-500 + SelectionMagnifierPlacement.Gap, placement.DestinationY);
    }

    [Fact]
    public void Resolve_FocusOnMonitorSeam_SourceStraddlesOrigin()
    {
        // Focus exactly on the x=0 seam between a left (negative) and right
        // monitor: the 50px source must straddle the seam (srcX = -25) rather
        // than being clamped, because the seam is interior to the Virtual Desktop.
        const int vx = -1920, vy = 0, vw = 3840, vh = 1080;
        var placement = SelectionMagnifierPlacement.Resolve(0, 540, vx, vy, vw, vh);

        Assert.NotNull(placement);
        Assert.Equal(-25, placement!.SourceX);
        Assert.Equal(540 - 25, placement.SourceY);
    }

    [Fact]
    public void Resolve_NegativeCoordinateDestinationStaysInBounds()
    {
        const int vx = -1920, vy = -1080, vw = 3840, vh = 2160;
        var focusCases = new (int x, int y)[]
        {
            (-1920, -1080), (1919, 1079), (0, 0), (-1900, 1000),
        };

        foreach (var (fx, fy) in focusCases)
        {
            var placement = SelectionMagnifierPlacement.Resolve(fx, fy, vx, vy, vw, vh);
            Assert.NotNull(placement);
            Assert.True(placement!.DestinationX >= vx);
            Assert.True(placement.DestinationY >= vy);
            Assert.True(placement.DestinationX + placement.DestinationWidth <= vx + vw);
            Assert.True(placement.DestinationY + placement.DestinationHeight <= vy + vh);
        }
    }

    // ── Constrained layouts ────────────────────────────────────────────

    [Fact]
    public void Resolve_VirtualDesktopSmallerThanMagnifier_ReturnsNull()
    {
        // The magnifier cannot fit at all — the resolver declines so the overlay
        // knows to skip rendering it.
        Assert.Null(SelectionMagnifierPlacement.Resolve(50, 50, 0, 0, 100, 100));
    }

    [Fact]
    public void Resolve_VirtualDesktopExactlyMagnifierSize_ReturnsNull()
    {
        // The viewport would fill the entire Virtual Desktop and therefore
        // cover the focus point no matter where it is — the resolver declines so
        // the overlay hides the magnifier instead of obscuring the active pixel.
        Assert.Null(SelectionMagnifierPlacement.Resolve(100, 100, 0, 0, 200, 200));
    }

    [Fact]
    public void Resolve_FocusUnavoidableInSmallVirtualDesktop_ReturnsNull()
    {
        // A 250×250 Virtual Desktop with the focus centred: every legal 200×200
        // placement still contains the centre, so the magnifier is suppressed.
        Assert.Null(SelectionMagnifierPlacement.Resolve(125, 125, 0, 0, 250, 250));
    }

    [Fact]
    public void Resolve_FocusAvoidableInSmallVirtualDesktop_PlacesAwayFromFocus()
    {
        // Same 250×250 Virtual Desktop, but the focus is in the corner: there is
        // now room to place the viewport without covering it, so a placement is
        // returned and it still does not contain the focus.
        var placement = SelectionMagnifierPlacement.Resolve(10, 10, 0, 0, 250, 250);

        Assert.NotNull(placement);
        Assert.False(
            Contains(placement!.DestinationX, placement.DestinationY,
                     placement.DestinationWidth, placement.DestinationHeight, 10, 10));
    }

    [Fact]
    public void Resolve_NarrowVirtualDesktop_ReturnsNull()
    {
        Assert.Null(SelectionMagnifierPlacement.Resolve(50, 50, 0, 0, 150, 1000));
    }

    [Fact]
    public void Resolve_ShortVirtualDesktop_ReturnsNull()
    {
        Assert.Null(SelectionMagnifierPlacement.Resolve(50, 50, 0, 0, 1000, 150));
    }

    private static bool Contains(int x, int y, int w, int h, int px, int py) =>
        px >= x && px < x + w && py >= y && py < y + h;
}
