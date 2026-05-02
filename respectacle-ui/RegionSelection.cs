// RegionSelection.cs — Immutable region model with normalize/move/resize/clamp semantics.
//
// This is a pure numeric type with no UI dependencies so xUnit can exercise it headless.
// The overlay window (S05) will construct instances from pointer events and bind to the
// layout rect, but all geometry logic lives here.

using System;

namespace Respectacle.UI;

/// <summary>
/// Represents a rectangular selection region in virtual-desktop coordinates.
/// Always stores normalized (positive width/height) values.
/// </summary>
public sealed record RegionSelection
{
    /// <summary>Minimum selectable width/height in pixels. Prevents accidental zero-size clicks.</summary>
    public const int MinimumSize = 4;

    /// <summary>Left edge in virtual-desktop pixels.</summary>
    public int X { get; init; }

    /// <summary>Top edge in virtual-desktop pixels.</summary>
    public int Y { get; init; }

    /// <summary>Width in pixels (always ≥ MinimumSize after validation).</summary>
    public int Width { get; init; }

    /// <summary>Height in pixels (always ≥ MinimumSize after validation).</summary>
    public int Height { get; init; }

    // ── Construction ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a normalized region from two arbitrary drag points.
    /// Handles reversed drags (right-to-left, bottom-to-top) automatically.
    /// </summary>
    public static RegionSelection FromDragPoints(int x1, int y1, int x2, int y2)
    {
        int left = Math.Min(x1, x2);
        int top = Math.Min(y1, y2);
        int right = Math.Max(x1, x2);
        int bottom = Math.Max(y1, y2);

        return new RegionSelection
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top,
        };
    }

    /// <summary>
    /// Creates a region clamped to the given virtual-desktop bounds.
    /// If the region is entirely outside bounds, returns null.
    /// </summary>
    public static RegionSelection? FromDragPointsClamped(
        int x1, int y1, int x2, int y2,
        int boundsX, int boundsY, int boundsWidth, int boundsHeight)
    {
        var raw = FromDragPoints(x1, y1, x2, y2);
        return raw.ClampToBounds(boundsX, boundsY, boundsWidth, boundsHeight);
    }

    // ── Query ───────────────────────────────────────────────────────────

    /// <summary>Right edge (X + Width).</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge (Y + Height).</summary>
    public int Bottom => Y + Height;

    /// <summary>True if width and height meet the minimum selectable size.</summary>
    public bool MeetsMinimumSize => Width >= MinimumSize && Height >= MinimumSize;

    // ── Transformations (return new instances) ──────────────────────────

    /// <summary>
    /// Moves the region by the given delta, clamping the position so the region
    /// stays fully within bounds. Returns null if the region's width or height
    /// exceeds the bounds dimensions.
    /// </summary>
    public RegionSelection? Move(int deltaX, int deltaY, int boundsX, int boundsY, int boundsWidth, int boundsHeight)
    {
        if (Width > boundsWidth || Height > boundsHeight)
            return null;

        int newX = X + deltaX;
        int newY = Y + deltaY;

        // Clamp position so region stays within bounds
        newX = Math.Max(newX, boundsX);
        newY = Math.Max(newY, boundsY);
        newX = Math.Min(newX, boundsX + boundsWidth - Width);
        newY = Math.Min(newY, boundsY + boundsHeight - Height);

        return new RegionSelection { X = newX, Y = newY, Width = Width, Height = Height };
    }

    /// <summary>
    /// Resizes the region by the given delta, anchoring from the specified edge,
    /// then clamps to bounds. Returns null if the result would be entirely outside bounds.
    /// </summary>
    /// <param name="anchor">Which corner stays fixed: "tl", "tr", "bl", "br".</param>
    public RegionSelection? Resize(
        int deltaW, int deltaH, string anchor,
        int boundsX, int boundsY, int boundsWidth, int boundsHeight)
    {
        int newX = X, newY = Y, newW = Width + deltaW, newH = Height + deltaH;

        // Ensure minimum size
        newW = Math.Max(newW, MinimumSize);
        newH = Math.Max(newH, MinimumSize);

        // Adjust position based on anchor
        switch (anchor.ToLowerInvariant())
        {
            case "tl": // top-left fixed — adjust right/bottom
                break;
            case "tr": // top-right fixed — adjust left/bottom
                newX = Right - newW;
                break;
            case "bl": // bottom-left fixed — adjust top/right
                newY = Bottom - newH;
                break;
            case "br": // bottom-right fixed — adjust top/left
                newX = Right - newW;
                newY = Bottom - newH;
                break;
            default:
                throw new ArgumentException($"Unknown anchor: {anchor}. Expected tl, tr, bl, or br.", nameof(anchor));
        }

        return ClampToBounds(newX, newY, newW, newH, boundsX, boundsY, boundsWidth, boundsHeight);
    }

    /// <summary>
    /// Returns a new region clamped to the given bounds. Returns null if
    /// the region is entirely outside bounds or has zero area after clamping.
    /// </summary>
    public RegionSelection? ClampToBounds(int boundsX, int boundsY, int boundsWidth, int boundsHeight)
    {
        return ClampToBounds(X, Y, Width, Height, boundsX, boundsY, boundsWidth, boundsHeight);
    }

    // ── Core clamping ───────────────────────────────────────────────────

    private static RegionSelection? ClampToBounds(
        int x, int y, int w, int h,
        int boundsX, int boundsY, int boundsWidth, int boundsHeight)
    {
        if (boundsWidth <= 0 || boundsHeight <= 0)
            return null;

        int clampedLeft = Math.Max(x, boundsX);
        int clampedTop = Math.Max(y, boundsY);
        int clampedRight = Math.Min(x + w, boundsX + boundsWidth);
        int clampedBottom = Math.Min(y + h, boundsY + boundsHeight);

        int clampedW = clampedRight - clampedLeft;
        int clampedH = clampedBottom - clampedTop;

        if (clampedW <= 0 || clampedH <= 0)
            return null;

        return new RegionSelection
        {
            X = clampedLeft,
            Y = clampedTop,
            Width = clampedW,
            Height = clampedH,
        };
    }

    public override string ToString() => $"Region {{ X={X}, Y={Y}, Width={Width}, Height={Height} }}";
}

/// <summary>
/// Status of a region selection interaction.
/// Used by the overlay to communicate outcome to the main window.
/// </summary>
public enum RegionSelectionStatus
{
    /// <summary>No selection in progress.</summary>
    Idle,
    /// <summary>User is actively dragging or adjusting the region.</summary>
    Selecting,
    /// <summary>User confirmed the selection (Enter or double-click).</summary>
    Confirmed,
    /// <summary>User cancelled the selection (Escape).</summary>
    Cancelled,
}
