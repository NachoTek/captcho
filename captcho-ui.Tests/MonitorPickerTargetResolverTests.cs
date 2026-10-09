// MonitorPickerTargetResolverTests.cs — Focused contract tests for the
// Selected Monitor picker's hover/gap resolution.
//
// The interactive monitor picker is a Win32 layered window that cannot run in
// a headless test, so its externally observable contract — which monitor is
// hovered, how gaps are rejected, and how bounds and labels survive mixed-DPI
// and negative-coordinate layouts — is asserted against the pure
// MonitorPickerTargetResolver that the production overlay delegates to. These
// tests cover the hover, gap, and mixed-layout acceptance criteria of issue
// #28; confirmation-vs-gap-click routing and cancellation are covered at the
// workflow seam in CaptureWorkflowSessionSelectedMonitorTests.

using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class MonitorPickerTargetResolverTests
{
    // ── Single-monitor hover ───────────────────────────────────────────

    [Fact]
    public void Resolve_OverMonitor_ReturnsThatMonitorBoundsAndLabel()
    {
        var monitors = new[] { new MonitorRect(0, 0, 1920, 1080) };

        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: 500, cursorY: 400);

        Assert.NotNull(target);
        Assert.Equal(0, target!.X);
        Assert.Equal(0, target.Y);
        Assert.Equal(1920u, target.Width);
        Assert.Equal(1080u, target.Height);
        Assert.Equal("Monitor 1 (1920×1080)", target.Label);
    }

    [Fact]
    public void Resolve_AtInclusiveTopLeftCorner_ReturnsTheMonitor()
    {
        // Win32 convention: left/top edge is inclusive.
        var monitors = new[] { new MonitorRect(0, 0, 1920, 1080) };

        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: 0, cursorY: 0);

        Assert.NotNull(target);
        Assert.Equal(0, target!.X);
    }

    // ── Gap rejection ──────────────────────────────────────────────────
    //
    // Clicks in monitor-layout gaps must be ignored. The resolver signals a
    // gap by returning null; the overlay confirms only on a non-null hover.

    [Fact]
    public void Resolve_InGapBetweenMonitors_ReturnsNull()
    {
        // Two monitors with a 100px gap between them (primary ends at 1920,
        // secondary starts at 2020).
        var monitors = new[]
        {
            new MonitorRect(0, 0, 1920, 1080),
            new MonitorRect(2020, 0, 3940, 1080),
        };

        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: 1970, cursorY: 100);

        Assert.Null(target);
    }

    [Fact]
    public void Resolve_OutsideAllMonitors_ReturnsNull()
    {
        var monitors = new[] { new MonitorRect(0, 0, 1920, 1080) };

        // Below the primary monitor — empty Virtual Desktop space.
        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: 100, cursorY: 2000);

        Assert.Null(target);
    }

    [Fact]
    public void Resolve_AtExclusiveBottomRightCorner_ReturnsNull()
    {
        // Win32 convention: right/bottom edge is exclusive — the corner point
        // belongs to a neighbour (or a gap), never two monitors.
        var monitors = new[] { new MonitorRect(0, 0, 1920, 1080) };

        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: 1920, cursorY: 1080);

        Assert.Null(target);
    }

    [Fact]
    public void Resolve_EmptyMonitorList_ReturnsNull()
    {
        var target = MonitorPickerTargetResolver.Resolve(
            Array.Empty<MonitorRect>(), cursorX: 0, cursorY: 0);

        Assert.Null(target);
    }

    // ── Multi-monitor hover ────────────────────────────────────────────

    [Fact]
    public void Resolve_OverSecondMonitor_ReturnsSecondBoundsAndLabel()
    {
        var monitors = new[]
        {
            new MonitorRect(0, 0, 1920, 1080),
            new MonitorRect(1920, 0, 3840, 1080),
        };

        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: 3000, cursorY: 500);

        Assert.NotNull(target);
        Assert.Equal(1920, target!.X);
        Assert.Equal(0, target.Y);
        Assert.Equal(1920u, target.Width);
        Assert.Equal("Monitor 2 (1920×1080)", target.Label);
    }

    [Fact]
    public void Resolve_VerticalStack_PicksMonitorByCursorPosition()
    {
        // Two monitors stacked vertically — the cursor selects by Y position.
        var monitors = new[]
        {
            new MonitorRect(0, 0, 1920, 1080),
            new MonitorRect(0, 1080, 1920, 2160),
        };

        var upper = MonitorPickerTargetResolver.Resolve(monitors, cursorX: 100, cursorY: 50);
        var lower = MonitorPickerTargetResolver.Resolve(monitors, cursorX: 100, cursorY: 1500);

        Assert.NotNull(upper);
        Assert.Equal(0, upper!.Y);
        Assert.Equal("Monitor 1 (1920×1080)", upper.Label);

        Assert.NotNull(lower);
        Assert.Equal(1080, lower!.Y);
        Assert.Equal("Monitor 2 (1920×1080)", lower.Label);
    }

    // ── Mixed-DPI / negative-coordinate layouts ────────────────────────
    //
    // The spec (#28) requires labels and highlights to remain correctly placed
    // in mixed-DPI and negative-coordinate layouts. The resolver must return
    // the signed origin and exact pixel dimensions untouched.

    [Fact]
    public void Resolve_NegativeOriginMonitor_ReturnsSignedBounds()
    {
        // Secondary monitor to the left of the primary — negative X origin.
        var monitors = new[]
        {
            new MonitorRect(-1920, 0, 0, 1080),
            new MonitorRect(0, 0, 1920, 1080),
        };

        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: -960, cursorY: 500);

        Assert.NotNull(target);
        Assert.Equal(-1920, target!.X);
        Assert.Equal(0, target.Y);
        Assert.Equal(1920u, target.Width);
        Assert.Equal(1080u, target.Height);
    }

    [Fact]
    public void Resolve_NegativeOriginAndAbove_ReturnsSignedXAndY()
    {
        // Secondary monitor up and to the left — both X and Y negative.
        var monitors = new[]
        {
            new MonitorRect(-1920, -1080, 0, 0),
            new MonitorRect(0, 0, 1920, 1080),
        };

        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: -100, cursorY: -100);

        Assert.NotNull(target);
        Assert.Equal(-1920, target!.X);
        Assert.Equal(-1080, target.Y);
        Assert.Equal(1920u, target.Width);
        Assert.Equal(1080u, target.Height);
    }

    [Fact]
    public void Resolve_MixedDpiNonRoundDimensions_ReturnsExactBounds()
    {
        // A 150% scaled monitor producing non-round pixel dimensions. The
        // resolver must not round or clamp — exact bounds flow to the workflow.
        var monitors = new[]
        {
            new MonitorRect(0, 0, 1280, 720),
            new MonitorRect(1280, 0, 3840, 2160),
        };

        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: 2560, cursorY: 1000);

        Assert.NotNull(target);
        Assert.Equal(1280, target!.X);
        Assert.Equal(2560u, target.Width);
        Assert.Equal(2160u, target.Height);
        Assert.Equal("Monitor 2 (2560×2160)", target.Label);
    }

    [Fact]
    public void Resolve_NegativeOriginMonitor_UsesOneBasedIndexInLabel()
    {
        // Left-most monitor sorts first and is therefore "Monitor 1" even
        // though it has negative coordinates.
        var monitors = new[]
        {
            new MonitorRect(0, 0, 1920, 1080),
            new MonitorRect(-1920, 0, 0, 1080),
        };

        var target = MonitorPickerTargetResolver.Resolve(monitors, cursorX: -100, cursorY: 100);

        Assert.NotNull(target);
        Assert.Equal(-1920, target!.X);
        Assert.Equal("Monitor 1 (1920×1080)", target.Label);
    }
}
