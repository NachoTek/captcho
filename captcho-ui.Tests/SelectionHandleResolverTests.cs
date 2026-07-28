// SelectionHandleResolverTests.cs — Focused contract tests for the Selection
// overlay's resize-handle hit-testing.
//
// The interactive region overlay is a Win32 layered window that cannot run in a
// headless test, so its externally observable contract — which of the eight
// resize handles (four corners, four edges) is under the cursor, how the
// forgiving hit zones scale with DPI, and how body/none resolve — is asserted
// against the pure SelectionHandleResolver that the production overlay
// delegates to. These tests cover the visible-handle, DPI-aware-hit-zone, and
// mixed-layout acceptance criteria of issue #30; pointer-driven resizing
// outcomes are covered by RegionSelectionTests.ResizeHandle_* and confirmation
// routing remains at the workflow seam.

using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class SelectionHandleResolverTests
{
    // A 200×200 selection at (100,100) → corners at (100,100),(300,100),
    // (100,300),(300,300). Used by most fixtures.
    private static RegionSelection Selection() =>
        RegionSelection.FromDragPoints(100, 100, 300, 300);

    private const double DefaultDpi = 1.0;

    // ── Visible handle vs forgiving hit zone ───────────────────────────

    [Fact]
    public void HitZoneSize_At100Percent_LargerThanVisibleHandle()
    {
        // The hit zone must be larger than the 8px visible handle so users do
        // not have to pixel-aim the small handle.
        Assert.True(SelectionHandleResolver.HitZoneSize(DefaultDpi) >
                    SelectionHandleResolver.VisibleHandleSize);
    }

    [Fact]
    public void HitZoneSize_ScalesWithDpiFactor()
    {
        // On a 200% monitor the fixed 8px handle is physically half the size,
        // so the hit zone must grow to stay equally forgiving in physical terms.
        int at100 = SelectionHandleResolver.HitZoneSize(1.0);
        int at200 = SelectionHandleResolver.HitZoneSize(2.0);

        Assert.True(at200 > at100);
        Assert.Equal(at100 * 2, at200);
    }

    // ── Corner handles ─────────────────────────────────────────────────
    //
    // Each corner exposes a diagonal resize handle. The hit zone is a forgiving
    // square (half the hit-zone size on each side of the corner point).

    [Fact]
    public void Resolve_OnTopLeftCorner_ReturnsTopLeft()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 100, 100, DefaultDpi);
        Assert.Equal(SelectionHandleKind.TopLeft, kind);
    }

    [Fact]
    public void Resolve_OnTopRightCorner_ReturnsTopRight()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 300, 100, DefaultDpi);
        Assert.Equal(SelectionHandleKind.TopRight, kind);
    }

    [Fact]
    public void Resolve_OnBottomLeftCorner_ReturnsBottomLeft()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 100, 300, DefaultDpi);
        Assert.Equal(SelectionHandleKind.BottomLeft, kind);
    }

    [Fact]
    public void Resolve_OnBottomRightCorner_ReturnsBottomRight()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 300, 300, DefaultDpi);
        Assert.Equal(SelectionHandleKind.BottomRight, kind);
    }

    [Fact]
    public void Resolve_NearCornerButOutsideSelection_StillHitsCorner()
    {
        // Forgiving hit zone extends outside the rectangle: a point up-and-left
        // of the top-left corner is still grabbed as TopLeft.
        var kind = SelectionHandleResolver.Resolve(Selection(), 93, 93, DefaultDpi);
        // HitZoneSize(1.0) = 16 → half = 8; |93-100| = 7 ≤ 8.
        Assert.Equal(SelectionHandleKind.TopLeft, kind);
    }

    // ── Edge handles ───────────────────────────────────────────────────
    //
    // Each edge midpoint exposes a directional resize handle along that edge.

    [Fact]
    public void Resolve_OnTopEdgeMidpoint_ReturnsTop()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 200, 100, DefaultDpi);
        Assert.Equal(SelectionHandleKind.Top, kind);
    }

    [Fact]
    public void Resolve_OnBottomEdgeMidpoint_ReturnsBottom()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 200, 300, DefaultDpi);
        Assert.Equal(SelectionHandleKind.Bottom, kind);
    }

    [Fact]
    public void Resolve_OnLeftEdgeMidpoint_ReturnsLeft()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 100, 200, DefaultDpi);
        Assert.Equal(SelectionHandleKind.Left, kind);
    }

    [Fact]
    public void Resolve_OnRightEdgeMidpoint_ReturnsRight()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 300, 200, DefaultDpi);
        Assert.Equal(SelectionHandleKind.Right, kind);
    }

    [Fact]
    public void Resolve_NearEdgeButOutsideSelection_StillHitsEdge()
    {
        // Forgiving: a few pixels above the top edge is still grabbed as Top.
        var kind = SelectionHandleResolver.Resolve(Selection(), 200, 93, DefaultDpi);
        Assert.Equal(SelectionHandleKind.Top, kind);
    }

    // ── Corner priority over edge ──────────────────────────────────────
    //
    // A point that lies in both a corner zone and an edge zone resolves to the
    // corner so the diagonal cursor wins at the corners.

    [Fact]
    public void Resolve_PointInCornerAndEdgeOverlap_ReturnsCorner()
    {
        // (105,100): within 8px of top-left corner (5px in X) AND on the top
        // edge line. Corner must win.
        var kind = SelectionHandleResolver.Resolve(Selection(), 105, 100, DefaultDpi);
        Assert.Equal(SelectionHandleKind.TopLeft, kind);
    }

    // ── Body (move) ────────────────────────────────────────────────────

    [Fact]
    public void Resolve_DeepInsideSelection_ReturnsBody()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 200, 200, DefaultDpi);
        Assert.Equal(SelectionHandleKind.Body, kind);
    }

    // ── None (outside) ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_FarOutsideSelection_ReturnsNone()
    {
        var kind = SelectionHandleResolver.Resolve(Selection(), 500, 500, DefaultDpi);
        Assert.Equal(SelectionHandleKind.None, kind);
    }

    [Fact]
    public void Resolve_OutsideBeyondHitZone_ReturnsNone()
    {
        // Just past the forgiving hit zone of the top-left corner → None
        // (triggers redraw, not resize).
        var kind = SelectionHandleResolver.Resolve(Selection(), 90, 90, DefaultDpi);
        // |90-100| = 10 > 8 (half of HitZoneSize 16) → not corner/edge/body.
        Assert.Equal(SelectionHandleKind.None, kind);
    }

    // ── DPI-aware hit zones change the reachable area ──────────────────

    [Fact]
    public void Resolve_PointReachableOnlyAtHighDpi_ReturnsHandleAtHighDpi()
    {
        // (115,115): at 100% the corner half-zone is 8px so this is Body
        // (|115-100|=15>8). At 200% the half-zone is 16px so it is TopLeft.
        var at100 = SelectionHandleResolver.Resolve(Selection(), 115, 115, 1.0);
        var at200 = SelectionHandleResolver.Resolve(Selection(), 115, 115, 2.0);

        Assert.Equal(SelectionHandleKind.Body, at100);
        Assert.Equal(SelectionHandleKind.TopLeft, at200);
    }

    // ── Small selections ───────────────────────────────────────────────
    //
    // When the selection is smaller than the forgiving hit zones the corner
    // zones overlap; corners win and the whole area is still grabbable.

    [Fact]
    public void Resolve_SelectionSmallerThanHitZone_CenterStillReturnsCorner()
    {
        // 10×10 selection; center (105,105) is within 8px of the top-left
        // corner (5px), so TopLeft wins.
        var tiny = RegionSelection.FromDragPoints(100, 100, 110, 110);
        var kind = SelectionHandleResolver.Resolve(tiny, 105, 105, DefaultDpi);
        Assert.Equal(SelectionHandleKind.TopLeft, kind);
    }

    // ── Negative-coordinate / mixed-monitor layouts ────────────────────
    //
    // Selections on monitors left/above the primary use negative Virtual
    // Desktop coordinates; handle resolution must work there identically.

    [Fact]
    public void Resolve_NegativeOriginSelection_CornersResolveCorrectly()
    {
        // Selection entirely on a left monitor: (-300,-200) to (-100,0).
        var sel = RegionSelection.FromDragPoints(-300, -200, -100, 0);

        Assert.Equal(SelectionHandleKind.TopLeft,
            SelectionHandleResolver.Resolve(sel, -300, -200, DefaultDpi));
        Assert.Equal(SelectionHandleKind.BottomRight,
            SelectionHandleResolver.Resolve(sel, -100, 0, DefaultDpi));
        Assert.Equal(SelectionHandleKind.Top,
            SelectionHandleResolver.Resolve(sel, -200, -200, DefaultDpi));
    }

    [Fact]
    public void Resolve_SelectionSpanningOrigin_AcrossMonitorBoundary_Works()
    {
        // Selection crossing x=0 between a negative-origin left monitor and the
        // primary: (-100,100) to (200,300).
        var sel = RegionSelection.FromDragPoints(-100, 100, 200, 300);

        Assert.Equal(SelectionHandleKind.BottomRight,
            SelectionHandleResolver.Resolve(sel, 200, 300, DefaultDpi));
        Assert.Equal(SelectionHandleKind.Body,
            SelectionHandleResolver.Resolve(sel, 50, 200, DefaultDpi));
    }

    [Fact]
    public void Resolve_NegativeOriginSelection_HighDpi_HitZoneScales()
    {
        var sel = RegionSelection.FromDragPoints(-300, -200, -100, 0);
        // (-283,-183): |dx|=17,|dy|=17. At 100% half-zone 8 → None-ish; at 200%
        // half-zone 16 → still None (17>16). Pick a reachable point instead.
        // (-285,-185): |dx|=15,|dy|=15. At 100% (8) → None; at 200% (16) → TopLeft.
        var at100 = SelectionHandleResolver.Resolve(sel, -285, -185, 1.0);
        var at200 = SelectionHandleResolver.Resolve(sel, -285, -185, 2.0);

        Assert.NotEqual(SelectionHandleKind.TopLeft, at100);
        Assert.Equal(SelectionHandleKind.TopLeft, at200);
    }

    // ── Cursor mapping ─────────────────────────────────────────────────
    //
    // Each handle maps to the directional system cursor users expect.

    [Theory]
    [InlineData(SelectionHandleKind.TopLeft, SelectionCursorKind.SizeNwse)]
    [InlineData(SelectionHandleKind.BottomRight, SelectionCursorKind.SizeNwse)]
    [InlineData(SelectionHandleKind.TopRight, SelectionCursorKind.SizeNesw)]
    [InlineData(SelectionHandleKind.BottomLeft, SelectionCursorKind.SizeNesw)]
    [InlineData(SelectionHandleKind.Top, SelectionCursorKind.SizeNs)]
    [InlineData(SelectionHandleKind.Bottom, SelectionCursorKind.SizeNs)]
    [InlineData(SelectionHandleKind.Left, SelectionCursorKind.SizeWe)]
    [InlineData(SelectionHandleKind.Right, SelectionCursorKind.SizeWe)]
    [InlineData(SelectionHandleKind.Body, SelectionCursorKind.SizeAll)]
    [InlineData(SelectionHandleKind.None, SelectionCursorKind.Cross)]
    public void GetCursor_ForEachHandle_ReturnsExpectedDirection(
        SelectionHandleKind handle, SelectionCursorKind expected)
    {
        Assert.Equal(expected, SelectionHandleResolver.GetCursor(handle));
    }
}
