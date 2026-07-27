// WindowPickerTargetResolverTests.cs — Focused contract tests for the
// Selected Window picker's hover, empty-desktop, z-order, and disappearance
// resolution.
//
// The interactive window picker is a Win32 layered window that cannot run in a
// headless test, so its externally observable contract — which eligible window
// is hovered, how empty-desktop points yield no target (the Full Desktop
// fallback), how z-order picks the topmost window, and how a window that has
// disappeared between hover and click can never be confirmed as a stale
// target — is asserted against the pure WindowPickerTargetResolver that the
// production overlay delegates to. These tests cover the hover, empty-desktop,
// cross-monitor, negative-coordinate, and disappearance acceptance criteria of
// issue #29; confirmation-vs-fallback routing and cancellation are covered at
// the workflow seam in CaptureWorkflowSessionSelectedWindowTests.

using System;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class WindowPickerTargetResolverTests
{
    private static readonly IntPtr H1 = new(0x101);
    private static readonly IntPtr H2 = new(0x102);
    private static readonly IntPtr H3 = new(0x103);

    private static PickableWindow Win(IntPtr handle, int left, int top, int right, int bottom, string title = "App") =>
        new(handle, left, top, right, bottom, title);

    // ── Hover over a window ────────────────────────────────────────────

    [Fact]
    public void Resolve_OverWindow_ReturnsThatWindowHandleTitleAndBounds()
    {
        var windows = new[] { Win(H1, 100, 100, 1100, 800, "Notepad") };

        var target = WindowPickerTargetResolver.Resolve(windows, cursorX: 500, cursorY: 400);

        Assert.NotNull(target);
        Assert.Equal(H1, target!.Handle);
        Assert.Equal("Notepad", target.Title);
        Assert.Equal(100, target.X);
        Assert.Equal(100, target.Y);
        Assert.Equal(1000u, target.Width);
        Assert.Equal(700u, target.Height);
    }

    [Fact]
    public void Resolve_AtInclusiveTopLeftCorner_ReturnsTheWindow()
    {
        // Win32 convention: left/top edge is inclusive.
        var windows = new[] { Win(H1, 100, 100, 1100, 800) };

        var target = WindowPickerTargetResolver.Resolve(windows, cursorX: 100, cursorY: 100);

        Assert.NotNull(target);
        Assert.Equal(H1, target!.Handle);
    }

    [Fact]
    public void Resolve_AtExclusiveBottomRightCorner_ReturnsNull()
    {
        // Win32 convention: right/bottom edge is exclusive — the corner point
        // is empty desktop (or a neighbour), never two windows.
        var windows = new[] { Win(H1, 100, 100, 1100, 800) };

        var target = WindowPickerTargetResolver.Resolve(windows, cursorX: 1100, cursorY: 800);

        Assert.Null(target);
    }

    // ── Empty desktop ──────────────────────────────────────────────────
    //
    // A click where no eligible window sits is the Full Desktop fallback. The
    // resolver signals empty desktop by returning null; the overlay maps a
    // null-on-click to EmptyDesktopFallback (covered at the workflow seam).

    [Fact]
    public void Resolve_BesideAllWindows_ReturnsNull_EmptyDesktop()
    {
        var windows = new[] { Win(H1, 100, 100, 1100, 800) };

        var target = WindowPickerTargetResolver.Resolve(windows, cursorX: 50, cursorY: 50);

        Assert.Null(target);
    }

    [Fact]
    public void Resolve_EmptyWindowList_ReturnsNull_EmptyDesktop()
    {
        var target = WindowPickerTargetResolver.Resolve(
            Array.Empty<PickableWindow>(), cursorX: 0, cursorY: 0);

        Assert.Null(target);
    }

    // ── Z-order ────────────────────────────────────────────────────────
    //
    // Windows are supplied top-to-bottom (EnumWindows order). On overlap the
    // topmost — the first containing window in the snapshot — is the one the
    // user sees under the cursor, so it must be the one confirmed.

    [Fact]
    public void Resolve_OverlappingWindows_ReturnsTopmostFirstInSnapshot()
    {
        // H2 is drawn on top of H1 and therefore earlier in the z-order
        // snapshot.
        var windows = new[]
        {
            Win(H2, 100, 100, 1100, 800, "Front"),
            Win(H1, 100, 100, 1100, 800, "Back"),
        };

        var target = WindowPickerTargetResolver.Resolve(windows, cursorX: 500, cursorY: 400);

        Assert.NotNull(target);
        Assert.Equal(H2, target!.Handle);
        Assert.Equal("Front", target.Title);
    }

    [Fact]
    public void Resolve_NonOverlappingWindows_PicksByCursorPosition()
    {
        var windows = new[]
        {
            Win(H1, 0, 0, 500, 400, "Left"),
            Win(H2, 500, 0, 1000, 400, "Right"),
        };

        var left = WindowPickerTargetResolver.Resolve(windows, cursorX: 250, cursorY: 200);
        var right = WindowPickerTargetResolver.Resolve(windows, cursorX: 750, cursorY: 200);

        Assert.NotNull(left);
        Assert.Equal(H1, left!.Handle);
        Assert.Equal("Left", left.Title);

        Assert.NotNull(right);
        Assert.Equal(H2, right!.Handle);
        Assert.Equal("Right", right.Title);
    }

    // ── Cross-monitor window ───────────────────────────────────────────
    //
    // A window spanning two monitors has bounds crossing the monitor boundary.
    // Its full bounds must be hit-tested as one rectangle so either side
    // resolves to the same window (spec #29: highlight the full bounds across
    // monitors).

    [Fact]
    public void Resolve_CrossMonitorWindow_HitTestsFullBoundsOnEitherSide()
    {
        // Window spanning primary (ends at 1920) and a secondary monitor.
        var windows = new[] { Win(H1, 480, 200, 3360, 1000, "Stretched") };

        var leftSide = WindowPickerTargetResolver.Resolve(windows, cursorX: 1000, cursorY: 500);
        var rightSide = WindowPickerTargetResolver.Resolve(windows, cursorX: 2500, cursorY: 500);

        Assert.NotNull(leftSide);
        Assert.Equal(H1, leftSide!.Handle);
        Assert.Equal(480, leftSide.X);
        Assert.Equal(2880u, leftSide.Width);

        Assert.NotNull(rightSide);
        Assert.Equal(H1, rightSide!.Handle);
    }

    // ── Negative-coordinate layouts ────────────────────────────────────
    //
    // A window on a secondary monitor at a negative Virtual Desktop origin
    // must resolve with its signed origin and exact dimensions preserved —
    // matching Selection and Selected Monitor behaviour (spec #29).

    [Fact]
    public void Resolve_NegativeOriginWindow_ReturnsSignedBounds()
    {
        var windows = new[] { Win(H1, -1200, -800, -120, -100, "Secondary") };

        var target = WindowPickerTargetResolver.Resolve(windows, cursorX: -500, cursorY: -400);

        Assert.NotNull(target);
        Assert.Equal(H1, target!.Handle);
        Assert.Equal(-1200, target.X);
        Assert.Equal(-800, target.Y);
        Assert.Equal(1080u, target.Width);
        Assert.Equal(700u, target.Height);
        Assert.Equal("Secondary", target.Title);
    }

    [Fact]
    public void Resolve_NegativeAndPositiveOriginWindows_PicksByCursorPosition()
    {
        var windows = new[]
        {
            Win(H1, -1920, 0, 0, 1080, "Left"),
            Win(H2, 0, 0, 1920, 1080, "Right"),
        };

        var left = WindowPickerTargetResolver.Resolve(windows, cursorX: -1000, cursorY: 500);
        var right = WindowPickerTargetResolver.Resolve(windows, cursorX: 1000, cursorY: 500);

        Assert.Equal(H1, left!.Handle);
        Assert.Equal(H2, right!.Handle);
    }

    // ── Disappearance recovery ─────────────────────────────────────────
    //
    // The overlay re-enumerates at click time. A window that closed between
    // hover and click is absent from the fresh snapshot. Because the resolver
    // only ever returns a handle drawn from the supplied snapshot, a vanished
    // window can never be confirmed as a stale target — the click resolves to
    // empty desktop (or a different, still-present window).

    [Fact]
    public void Resolve_WindowAbsentFromSnapshot_NeverReturnsStaleHandle()
    {
        // Snapshot at hover time: H1 is present under the cursor.
        var hoverSnapshot = new[] { Win(H1, 100, 100, 1100, 800, "Closing") };

        var hovered = WindowPickerTargetResolver.Resolve(hoverSnapshot, cursorX: 500, cursorY: 400);
        Assert.Equal(H1, hovered!.Handle);

        // Snapshot at click time: H1 has disappeared (closed). Resolving the
        // same click point against the fresh snapshot yields empty desktop —
        // never the stale H1 handle.
        var clickSnapshot = Array.Empty<PickableWindow>();

        var resolved = WindowPickerTargetResolver.Resolve(clickSnapshot, cursorX: 500, cursorY: 400);

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_WindowAbsentFromSnapshot_FallsThroughToWindowBehind()
    {
        // H2 was on top of H1. H2 closes before the click. The fresh snapshot
        // still contains H1, so the click resolves to H1 — not the stale H2.
        var clickSnapshot = new[]
        {
            Win(H1, 100, 100, 1100, 800, "Back"),
        };

        var resolved = WindowPickerTargetResolver.Resolve(clickSnapshot, cursorX: 500, cursorY: 400);

        Assert.NotNull(resolved);
        Assert.Equal(H1, resolved!.Handle);
    }

    [Fact]
    public void Resolve_OnlyReturnsHandlesPresentInTheSnapshot()
    {
        // Cross-check: a handle not in the snapshot is never returned even when
        // the cursor sits inside its historical bounds.
        var snapshot = new[] { Win(H2, 2000, 200, 3000, 1000, "Other") };

        var resolved = WindowPickerTargetResolver.Resolve(snapshot, cursorX: 500, cursorY: 400);

        Assert.Null(resolved);
    }
}
