// CoordinateHelper.cs — Pure translation between overlay-relative and virtual-desktop coordinates.
//
// On multi-monitor systems the virtual-desktop origin may have negative x/y values.
// The overlay window positions itself in virtual-desktop space but receives pointer
// events relative to its own top-left. This helper bridges the two coordinate systems
// without any UI element references.

using System;

namespace captcho.UI;

/// <summary>
/// Pure static helpers for translating between overlay-relative coordinates
/// and virtual-desktop absolute coordinates.
/// </summary>
public static class CoordinateHelper
{
    /// <summary>
    /// Converts an overlay-relative point to virtual-desktop absolute coordinates.
    /// Works correctly when the virtual-desktop origin has negative x/y (multi-monitor).
    /// </summary>
    /// <param name="overlayX">X position relative to the overlay window's top-left.</param>
    /// <param name="overlayY">Y position relative to the overlay window's top-left.</param>
    /// <param name="overlayVirtualX">The overlay window's left edge in virtual-desktop coordinates.</param>
    /// <param name="overlayVirtualY">The overlay window's top edge in virtual-desktop coordinates.</param>
    /// <returns>Virtual-desktop absolute coordinates (x, y).</returns>
    public static (int X, int Y) OverlayToVirtual(
        int overlayX, int overlayY,
        int overlayVirtualX, int overlayVirtualY)
    {
        return (overlayVirtualX + overlayX, overlayVirtualY + overlayY);
    }

    /// <summary>
    /// Converts a virtual-desktop absolute point to overlay-relative coordinates.
    /// </summary>
    /// <returns>Overlay-relative coordinates (x, y).</returns>
    public static (int X, int Y) VirtualToOverlay(
        int virtualX, int virtualY,
        int overlayVirtualX, int overlayVirtualY)
    {
        return (virtualX - overlayVirtualX, virtualY - overlayVirtualY);
    }

    /// <summary>
    /// Computes the overlay window's virtual-desktop position so that it covers
    /// the entire virtual desktop (union of all monitors).
    /// </summary>
    /// <param name="virtualDesktopX">Virtual-desktop left edge (may be negative).</param>
    /// <param name="virtualDesktopY">Virtual-desktop top edge (may be negative).</param>
    /// <param name="virtualDesktopWidth">Virtual-desktop total width in pixels.</param>
    /// <param name="virtualDesktopHeight">Virtual-desktop total height in pixels.</param>
    /// <returns>The overlay window position in virtual-desktop coordinates.</returns>
    public static (int X, int Y, int Width, int Height) GetOverlayBounds(
        int virtualDesktopX, int virtualDesktopY,
        int virtualDesktopWidth, int virtualDesktopHeight)
    {
        if (virtualDesktopWidth <= 0)
            throw new ArgumentException("Virtual desktop width must be positive.", nameof(virtualDesktopWidth));
        if (virtualDesktopHeight <= 0)
            throw new ArgumentException("Virtual desktop height must be positive.", nameof(virtualDesktopHeight));

        return (virtualDesktopX, virtualDesktopY, virtualDesktopWidth, virtualDesktopHeight);
    }

    /// <summary>
    /// Verifies that a point falls within the given bounds rectangle.
    /// Handles bounds with negative origin (multi-monitor left/above primary).
    /// </summary>
    public static bool IsPointInBounds(
        int pointX, int pointY,
        int boundsX, int boundsY, int boundsWidth, int boundsHeight)
    {
        return pointX >= boundsX
            && pointY >= boundsY
            && pointX < boundsX + boundsWidth
            && pointY < boundsY + boundsHeight;
    }

    /// <summary>
    /// Rounds double coordinates to integer, using midpoint rounding (away from zero).
    /// Useful when converting DPI-scaled pointer positions to pixel coordinates.
    /// </summary>
    public static (int X, int Y) RoundToPixel(double x, double y)
    {
        return (
            (int)Math.Round(x, MidpointRounding.AwayFromZero),
            (int)Math.Round(y, MidpointRounding.AwayFromZero)
        );
    }
}
