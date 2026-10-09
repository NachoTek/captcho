// SelectionMagnifierPlacement.cs — Pure, headless-testable magnifier placement
// and sampling resolver for the Selection overlay.
//
// The interactive region overlay is a Win32 layered window that cannot run in a
// headless test. Its externally observable magnifier contract — a fixed
// 200×200-pixel viewport at 4× zoom, a source region centred on the active
// boundary point (and clamped at the Virtual Desktop edges), and a destination
// that picks a visible quadrant so it never obscures the boundary being
// adjusted — is factored into this pure resolver so it can be covered by
// focused contract tests. The production overlay samples the desktop backdrop
// using the rectangles this resolver returns; the runtime CaptureWorkflowSession
// only ever sees the final confirmed geometry.
//
// Coordinate convention matches the rest of the codebase: the selection and the
// magnifier are in Virtual Desktop pixels (X/Y may be negative for monitors
// left/above the primary), and the Virtual Desktop bounds are the same signed
// rectangle reported by GetSystemMetrics(SM_*VIRTUALSCREEN).

using System;

namespace captcho.UI;

/// <summary>
/// Where and what the Selection magnifier draws. The overlay reads desktop
/// backdrop pixels from the <see cref="SourceX"/>/<see cref="SourceY"/> rectangle
/// and scales them into the <see cref="DestinationX"/>/<see cref="DestinationY"/>
/// rectangle of the overlay DIB.
/// </summary>
public sealed record MagnifierPlacement
{
    /// <summary>
    /// Left edge of the magnifier viewport in Virtual Desktop pixels (where to
    /// draw the box). The overlay subtracts the Virtual Desktop origin to land
    /// in DIB space.
    /// </summary>
    public int DestinationX { get; init; }

    /// <summary>Top edge of the magnifier viewport in Virtual Desktop pixels.</summary>
    public int DestinationY { get; init; }

    /// <summary>Destination viewport edge length. Always <see cref="SelectionMagnifierPlacement.Size"/>.</summary>
    public int DestinationWidth => SelectionMagnifierPlacement.Size;

    /// <summary>Destination viewport edge length. Always <see cref="SelectionMagnifierPlacement.Size"/>.</summary>
    public int DestinationHeight => SelectionMagnifierPlacement.Size;

    /// <summary>
    /// Left edge of the sampled desktop region in Virtual Desktop pixels (what
    /// to magnify). The region is <see cref="SourceWidth"/> = Size / Zoom square
    /// so the viewport renders at the configured magnification.
    /// </summary>
    public int SourceX { get; init; }

    /// <summary>Top edge of the sampled desktop region in Virtual Desktop pixels.</summary>
    public int SourceY { get; init; }

    /// <summary>Source edge length. Always <see cref="SelectionMagnifierPlacement.SourceSize"/>.</summary>
    public int SourceWidth => SelectionMagnifierPlacement.SourceSize;

    /// <summary>Source edge length. Always <see cref="SelectionMagnifierPlacement.SourceSize"/>.</summary>
    public int SourceHeight => SelectionMagnifierPlacement.SourceSize;
}

/// <summary>
/// Pure resolver for the Selection overlay's adaptive magnifier. Returns the
/// destination viewport rectangle and the source sampling rectangle for a given
/// active boundary point, or null when the Virtual Desktop is too small to host
/// a 200×200 viewport.
/// </summary>
/// <remarks>
/// <para>
/// The source region is centred on the focus point so the active boundary sits
/// in the middle of the magnified view, then clamped to the Virtual Desktop so
/// it never samples off-screen pixels at the edges.
/// </para>
/// <para>
/// The destination chooses the quadrant (relative to the focus) that points
/// toward the roomier half of the Virtual Desktop: a focus in the left half
/// lands the viewport to its right, a focus in the right half lands it to its
/// left, and likewise vertically. The viewport therefore lands on the interior
/// side of the edge being adjusted (never covering the active boundary point)
/// and stays in the half of the screen that has room. A final clamp keeps it
/// inside the Virtual Desktop for tightly constrained layouts.
/// </para>
/// </remarks>
public static class SelectionMagnifierPlacement
{
    /// <summary>
    /// Destination viewport edge length in pixels. The overlay draws a 200×200
    /// square magnifier.
    /// </summary>
    public const int Size = 200;

    /// <summary>
    /// Magnification factor. The viewport renders a <see cref="SourceSize"/>
    /// region scaled up by this factor.
    /// </summary>
    public const int Zoom = 4;

    /// <summary>
    /// Gap between the focus point and the viewport edge in pixels. Keeps the
    /// active boundary visible next to the magnifier rather than tucked under
    /// its border.
    /// </summary>
    public const int Gap = 16;

    /// <summary>
    /// Source edge length in pixels — the desktop region sampled into the
    /// viewport. Size / Zoom = 50, so a 200px viewport shows 50 desktop pixels
    /// at 4×.
    /// </summary>
    public static int SourceSize => Size / Zoom;

    /// <summary>
    /// Resolves the magnifier placement for the given active boundary point.
    /// Returns null when the Virtual Desktop is smaller than the viewport in
    /// either dimension, so the overlay can skip rendering it.
    /// </summary>
    /// <param name="focusX">
    /// Active boundary point X in Virtual Desktop pixels (may be negative).
    /// Typically the cursor position during a drag.</param>
    /// <param name="focusY">Active boundary point Y in Virtual Desktop pixels.</param>
    /// <param name="vdX">Virtual Desktop left edge (SM_XVIRTUALSCREEN, may be negative).</param>
    /// <param name="vdY">Virtual Desktop top edge (SM_YVIRTUALSCREEN, may be negative).</param>
    /// <param name="vdW">Virtual Desktop width in pixels (SM_CXVIRTUALSCREEN).</param>
    /// <param name="vdH">Virtual Desktop height in pixels (SM_CYVIRTUALSCREEN).</param>
    public static MagnifierPlacement? Resolve(
        int focusX, int focusY, int vdX, int vdY, int vdW, int vdH)
    {
        // The viewport must fit on screen. The overlay relies on null to decide
        // not to render, so a too-small Virtual Desktop in either dimension is
        // rejected entirely.
        if (vdW < Size || vdH < Size)
            return null;

        int srcHalf = SourceSize / 2;

        // Source: centred on the focus, clamped into [vdX, vdX + vdW - SourceSize]
        // so it never reads off-screen pixels at the Virtual Desktop edges.
        int srcX = Math.Clamp(focusX - srcHalf, vdX, vdX + vdW - SourceSize);
        int srcY = Math.Clamp(focusY - srcHalf, vdY, vdY + vdH - SourceSize);

        // Destination quadrant: place the viewport toward the roomier half of the
        // Virtual Desktop so it lands away from the edge being adjusted. The
        // centre is computed in 64-bit to avoid overflow on huge signed spans.
        long centerX = (long)vdX + vdW / 2;
        long centerY = (long)vdY + vdH / 2;

        int destX = focusX < centerX
            ? focusX + Gap                 // left half → viewport to the right
            : focusX - Gap - Size;         // right half → viewport to the left
        int destY = focusY < centerY
            ? focusY + Gap                 // top half → viewport below
            : focusY - Gap - Size;         // bottom half → viewport above

        // Safety net for constrained layouts: keep the viewport inside the
        // Virtual Desktop even when the chosen quadrant would escape it.
        destX = Math.Clamp(destX, vdX, vdX + vdW - Size);
        destY = Math.Clamp(destY, vdY, vdY + vdH - Size);

        // If the clamped destination would still cover the focus point, no
        // placement in this layout can avoid obscuring the active boundary —
        // decline so the overlay hides the magnifier instead of drawing a
        // viewport that blocks the very pixel the user is adjusting.
        if (destX <= focusX && focusX < destX + Size &&
            destY <= focusY && focusY < destY + Size)
        {
            return null;
        }

        return new MagnifierPlacement
        {
            DestinationX = destX,
            DestinationY = destY,
            SourceX = srcX,
            SourceY = srcY,
        };
    }
}
