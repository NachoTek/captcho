// SelectionHandleResolver.cs — Pure, headless-testable resize-handle
// hit-testing and directional-cursor mapping for the Selection overlay.
//
// The interactive region overlay is a Win32 layered window that cannot run in a
// headless test. Its externally observable handle contract — which of the eight
// resize handles (four corners, four edges), the move body, or nothing is under
// the cursor, how the forgiving hit zones scale with DPI, and which directional
// cursor each handle shows — is factored into this pure resolver so it can be
// covered by focused contract tests. The production overlay delegates
// hit-testing and cursor selection to this type; the runtime CaptureWorkflowSession
// only ever sees the final confirmed geometry.
//
// Coordinate convention matches the rest of the codebase: the selection is in
// Virtual Desktop pixels (X/Y may be negative for monitors left/above the
// primary), the left/top edges are inclusive and the right/bottom edges are the
// X+Width / Y+Height coordinate of the rectangle.

using System;

namespace captcho.UI;

/// <summary>
/// Which resize handle (or move body, or nothing) is under the cursor over a
/// <see cref="RegionSelection"/>.
/// </summary>
public enum SelectionHandleKind
{
    /// <summary>Cursor is outside the selection and outside every forgiving hit zone — draw a new selection.</summary>
    None,
    /// <summary>Cursor is inside the selection away from the edges — move the whole rectangle.</summary>
    Body,
    /// <summary>Top-left corner handle (north-west/south-east resize).</summary>
    TopLeft,
    /// <summary>Top edge handle (north/south resize).</summary>
    Top,
    /// <summary>Top-right corner handle (north-east/south-west resize).</summary>
    TopRight,
    /// <summary>Right edge handle (east/west resize).</summary>
    Right,
    /// <summary>Bottom-right corner handle (north-west/south-east resize).</summary>
    BottomRight,
    /// <summary>Bottom edge handle (north/south resize).</summary>
    Bottom,
    /// <summary>Bottom-left corner handle (north-east/south-west resize).</summary>
    BottomLeft,
    /// <summary>Left edge handle (east/west resize).</summary>
    Left,
}

/// <summary>
/// The directional system cursor a handle should display. The overlay maps each
/// value to the matching Win32 IDC_ constant; keeping the mapping here keeps it
/// pure and testable without a UI dependency.
/// </summary>
public enum SelectionCursorKind
{
    /// <summary>No handle — crosshair for drawing a new selection.</summary>
    Cross,
    /// <summary>Move (four-headed arrow) for the body.</summary>
    SizeAll,
    /// <summary>North-west/south-east double arrow for TopLeft and BottomRight.</summary>
    SizeNwse,
    /// <summary>North-east/south-west double arrow for TopRight and BottomLeft.</summary>
    SizeNesw,
    /// <summary>Vertical double arrow for Top and Bottom edges.</summary>
    SizeNs,
    /// <summary>Horizontal double arrow for Left and Right edges.</summary>
    SizeWe,
}

/// <summary>
/// Pure resolver for the Selection overlay's resize-handle hit-testing. Returns
/// the <see cref="SelectionHandleKind"/> under a cursor position over a
/// <see cref="RegionSelection"/>, using forgiving hit zones that are larger than
/// the always-visible 8×8 handles and that scale with the monitor DPI factor so
/// resizing stays practical on high-DPI displays.
/// </summary>
public static class SelectionHandleResolver
{
    /// <summary>
    /// Always-visible handle edge length in pixels. The overlay draws an 8×8
    /// square at each corner and edge midpoint.
    /// </summary>
    public const int VisibleHandleSize = 8;

    /// <summary>
    /// Edge length of the forgiving hit zone at the given DPI factor. The hit
    /// zone is larger than <see cref="VisibleHandleSize"/> and scales linearly
    /// with DPI so a physically-smaller handle on a high-DPI monitor stays easy
    /// to grab. At 100% (factor 1.0) the hit zone is 16px — twice the visible
    /// handle — giving an 8px reach on every side of each handle centre.
    /// </summary>
    public static int HitZoneSize(double dpiFactor)
    {
        double factor = dpiFactor < 1.0 ? 1.0 : dpiFactor;
        return (int)Math.Ceiling(VisibleHandleSize * 2.0 * factor);
    }

    /// <summary>
    /// Resolves the resize handle under the cursor. Corners take priority over
    /// edges (so a point in the corner/edge overlap shows the diagonal cursor),
    /// the body is returned for points inside the rectangle away from the edges,
    /// and <see cref="SelectionHandleKind.None"/> is returned for points outside
    /// the selection and outside every forgiving hit zone.
    /// </summary>
    /// <param name="selection">The current selection in Virtual Desktop pixels.</param>
    /// <param name="cursorX">Cursor X in Virtual Desktop pixels (may be negative).</param>
    /// <param name="cursorY">Cursor Y in Virtual Desktop pixels (may be negative).</param>
    /// <param name="dpiFactor">
    /// Monitor DPI as a multiple of 96 (1.0 at 100%, 2.0 at 200%). Scales the
    /// forgiving hit zones. The overlay passes the factor for the monitor the
    /// cursor is on.
    /// </param>
    public static SelectionHandleKind Resolve(
        RegionSelection selection, int cursorX, int cursorY, double dpiFactor)
    {
        int left = selection.X;
        int top = selection.Y;
        int right = selection.Right;
        int bottom = selection.Bottom;
        int tol = HitZoneSize(dpiFactor) / 2;

        // Corners first — a point in the corner/edge overlap resolves to the
        // corner so the diagonal resize cursor wins at the corners.
        bool nearLeft = Math.Abs(cursorX - left) <= tol;
        bool nearRight = Math.Abs(cursorX - right) <= tol;
        bool nearTop = Math.Abs(cursorY - top) <= tol;
        bool nearBottom = Math.Abs(cursorY - bottom) <= tol;

        if (nearLeft && nearTop) return SelectionHandleKind.TopLeft;
        if (nearRight && nearTop) return SelectionHandleKind.TopRight;
        if (nearLeft && nearBottom) return SelectionHandleKind.BottomLeft;
        if (nearRight && nearBottom) return SelectionHandleKind.BottomRight;

        // Edges — within the forgiving perpendicular tolerance and inside the
        // edge's own span. Reach extends on both sides of the edge line so the
        // handle can be grabbed from just inside or just outside the rectangle.
        bool withinX = cursorX >= left && cursorX <= right;
        bool withinY = cursorY >= top && cursorY <= bottom;

        if (nearTop && withinX) return SelectionHandleKind.Top;
        if (nearBottom && withinX) return SelectionHandleKind.Bottom;
        if (nearLeft && withinY) return SelectionHandleKind.Left;
        if (nearRight && withinY) return SelectionHandleKind.Right;

        // Body — strictly inside the rectangle (the edge lines themselves are
        // caught by the edge checks above).
        if (cursorX > left && cursorX < right && cursorY > top && cursorY < bottom)
            return SelectionHandleKind.Body;

        return SelectionHandleKind.None;
    }

    /// <summary>
    /// Maps a handle to the directional cursor users expect. The overlay turns
    /// this <see cref="SelectionCursorKind"/> into the matching Win32 system
    /// cursor when handling WM_SETCURSOR.
    /// </summary>
    public static SelectionCursorKind GetCursor(SelectionHandleKind handle) => handle switch
    {
        SelectionHandleKind.TopLeft => SelectionCursorKind.SizeNwse,
        SelectionHandleKind.BottomRight => SelectionCursorKind.SizeNwse,
        SelectionHandleKind.TopRight => SelectionCursorKind.SizeNesw,
        SelectionHandleKind.BottomLeft => SelectionCursorKind.SizeNesw,
        SelectionHandleKind.Top => SelectionCursorKind.SizeNs,
        SelectionHandleKind.Bottom => SelectionCursorKind.SizeNs,
        SelectionHandleKind.Left => SelectionCursorKind.SizeWe,
        SelectionHandleKind.Right => SelectionCursorKind.SizeWe,
        SelectionHandleKind.Body => SelectionCursorKind.SizeAll,
        _ => SelectionCursorKind.Cross,
    };
}
