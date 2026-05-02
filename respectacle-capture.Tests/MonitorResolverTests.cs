// MonitorResolverTests.cs — Headless tests for the shared monitor resolver.
//
// Tests monitor geometry matching, fallback behavior, and error handling
// using fake monitor rectangles and cursor positions. No Win32 calls are made.
// Covers negative virtual-desktop coordinates, edge boundaries, zero monitors,
// and the injectable interop failure paths.

using System;
using System.Collections.Generic;
using Respectacle.Capture;
using Xunit;

namespace Respectacle.Capture.Tests;

public class MonitorResolverTests
{
    // ── Helper: create a simple fake interop ────────────────────────────

    private static MonitorInterop FakeInterop(
        (int x, int y)? cursor,
        IReadOnlyList<MonitorRect> monitors)
    {
        return new MonitorInterop
        {
            GetCursorPosition = () => cursor,
            EnumerateMonitors = () => monitors,
        };
    }

    // ── Single monitor ─────────────────────────────────────────────────

    [Fact]
    public void ResolveMonitorForPoint_SingleMonitor_CursorInside_ReturnsZero()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, 0, 1920, 1080),
        };

        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 500, 500);
        Assert.Equal(0u, result.Index);
        Assert.False(result.IsFallback);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void ResolveMonitorForPoint_SingleMonitor_CursorOutside_ReturnsFallbackZero()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, 0, 1920, 1080),
        };

        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 2000, 500);
        Assert.Equal(0u, result.Index);
        Assert.True(result.IsFallback);
        Assert.NotNull(result.FailureReason);
    }

    // ── Dual monitors ──────────────────────────────────────────────────

    [Fact]
    public void ResolveMonitorForPoint_DualMonitor_CursorOnPrimary_ReturnsZero()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, 0, 1920, 1080),
            new(1920, 0, 3840, 1080),
        };

        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 100, 100);
        Assert.Equal(0u, result.Index);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ResolveMonitorForPoint_DualMonitor_CursorOnSecondary_ReturnsOne()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, 0, 1920, 1080),
            new(1920, 0, 3840, 1080),
        };

        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 2000, 500);
        Assert.Equal(1u, result.Index);
        Assert.False(result.IsFallback);
    }

    // ── Negative virtual-desktop coordinates (monitor left of primary) ──

    [Fact]
    public void ResolveMonitorForPoint_NegativeOrigin_CursorOnLeftMonitor_ReturnsZero()
    {
        // Secondary monitor to the left: origin (-1920, 0) to (0, 1080)
        var monitors = new List<MonitorRect>
        {
            new(-1920, 0, 0, 1080),
            new(0, 0, 1920, 1080),
        };

        // Cursor at (-500, 300) → left monitor
        var result = MonitorResolver.ResolveMonitorForPoint(monitors, -500, 300);
        Assert.Equal(0u, result.Index);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ResolveMonitorForPoint_NegativeOrigin_CursorOnRightMonitor_ReturnsOne()
    {
        var monitors = new List<MonitorRect>
        {
            new(-1920, 0, 0, 1080),
            new(0, 0, 1920, 1080),
        };

        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 500, 300);
        Assert.Equal(1u, result.Index);
        Assert.False(result.IsFallback);
    }

    // ── Edge boundary conditions ────────────────────────────────────────

    [Fact]
    public void ResolveMonitorForPoint_CursorOnLeftEdge_IncludedInMonitor()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, 0, 1920, 1080),
        };

        // Left/top edges are inclusive
        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 0, 0);
        Assert.Equal(0u, result.Index);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ResolveMonitorForPoint_CursorOnRightEdge_ExcludedFromMonitor()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, 0, 1920, 1080),
            new(1920, 0, 3840, 1080),
        };

        // Right/bottom edges are exclusive — cursor at x=1920 hits second monitor
        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 1920, 0);
        Assert.Equal(1u, result.Index);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ResolveMonitorForPoint_CursorOnBottomEdge_ExcludedFromMonitor()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, 0, 1920, 1080),
        };

        // Bottom edge (y=1080) is outside the monitor (exclusive)
        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 500, 1080);
        Assert.Equal(0u, result.Index);
        Assert.True(result.IsFallback);
    }

    // ── Zero monitors ──────────────────────────────────────────────────

    [Fact]
    public void ResolveMonitorForPoint_ZeroMonitors_ReturnsFallbackZero()
    {
        var monitors = new List<MonitorRect>();

        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 100, 100);
        Assert.Equal(0u, result.Index);
        Assert.True(result.IsFallback);
        Assert.Equal("No monitors enumerated", result.FailureReason);
    }

    // ── Sorting stability ──────────────────────────────────────────────

    [Fact]
    public void ResolveMonitorForPoint_UnorderedMonitors_SortedCorrectly()
    {
        // Monitors given in reverse order — resolver should sort them
        var monitors = new List<MonitorRect>
        {
            new(1920, 0, 3840, 1080),  // right
            new(0, 0, 1920, 1080),      // left (primary)
        };

        // Cursor on the left monitor should be index 0 after sorting
        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 500, 500);
        Assert.Equal(0u, result.Index);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ResolveMonitorForPoint_ThreeMonitors_MiddleSelected()
    {
        // Three monitors: left (negative), center (primary), right
        var monitors = new List<MonitorRect>
        {
            new(-1920, 0, 0, 1080),
            new(0, 0, 1920, 1080),
            new(1920, 0, 3840, 1080),
        };

        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 1000, 500);
        Assert.Equal(1u, result.Index);
        Assert.False(result.IsFallback);
    }

    // ── ResolveCurrentMonitor with interop ─────────────────────────────

    [Fact]
    public void ResolveCurrentMonitor_CursorPositionReturnsNull_ReturnsFailure()
    {
        var interop = FakeInterop(cursor: null, monitors: new List<MonitorRect>());
        var result = MonitorResolver.ResolveCurrentMonitor(interop);

        Assert.Equal(0u, result.Index);
        Assert.True(result.IsFallback);
        Assert.Contains("GetCursorPos", result.FailureReason);
    }

    [Fact]
    public void ResolveCurrentMonitor_CursorInsideSingleMonitor_ReturnsZero()
    {
        var interop = FakeInterop(
            cursor: (500, 500),
            monitors: new List<MonitorRect> { new(0, 0, 1920, 1080) });

        var result = MonitorResolver.ResolveCurrentMonitor(interop);
        Assert.Equal(0u, result.Index);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ResolveCurrentMonitor_CursorOutsideAllMonitors_ReturnsFallback()
    {
        var interop = FakeInterop(
            cursor: (5000, 5000),
            monitors: new List<MonitorRect> { new(0, 0, 1920, 1080) });

        var result = MonitorResolver.ResolveCurrentMonitor(interop);
        Assert.Equal(0u, result.Index);
        Assert.True(result.IsFallback);
        Assert.Contains("outside", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveCurrentMonitor_EmptyMonitorList_ReturnsFallback()
    {
        var interop = FakeInterop(
            cursor: (100, 100),
            monitors: new List<MonitorRect>());

        var result = MonitorResolver.ResolveCurrentMonitor(interop);
        Assert.Equal(0u, result.Index);
        Assert.True(result.IsFallback);
        Assert.Equal("No monitors enumerated", result.FailureReason);
    }

    // ── Interop exception handling ──────────────────────────────────────

    [Fact]
    public void ResolveCurrentMonitor_GetCursorPositionThrows_ReturnsFailure()
    {
        var interop = new MonitorInterop
        {
            GetCursorPosition = () => throw new InvalidOperationException("test error"),
            EnumerateMonitors = () => new List<MonitorRect>(),
        };

        var result = MonitorResolver.ResolveCurrentMonitor(interop);
        Assert.Equal(0u, result.Index);
        Assert.True(result.IsFallback);
        Assert.Contains("test error", result.FailureReason);
    }

    [Fact]
    public void ResolveCurrentMonitor_EnumerateMonitorsThrows_ReturnsFailure()
    {
        var interop = new MonitorInterop
        {
            GetCursorPosition = () => (100, 100),
            EnumerateMonitors = () => throw new InvalidOperationException("enum failed"),
        };

        var result = MonitorResolver.ResolveCurrentMonitor(interop);
        Assert.Equal(0u, result.Index);
        Assert.True(result.IsFallback);
        Assert.Contains("enum failed", result.FailureReason);
    }

    // ── Vertical stacking (monitors above/below) ────────────────────────

    [Fact]
    public void ResolveMonitorForPoint_VerticalStack_TopMonitor_IndexZero()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, -1080, 1920, 0),     // above
            new(0, 0, 1920, 1080),      // primary (below)
        };

        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 500, -500);
        Assert.Equal(0u, result.Index);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ResolveMonitorForPoint_VerticalStack_BottomMonitor_IndexOne()
    {
        var monitors = new List<MonitorRect>
        {
            new(0, -1080, 1920, 0),     // above
            new(0, 0, 1920, 1080),      // primary (below)
        };

        var result = MonitorResolver.ResolveMonitorForPoint(monitors, 500, 500);
        Assert.Equal(1u, result.Index);
        Assert.False(result.IsFallback);
    }

    // ── MonitorRect record equality ─────────────────────────────────────

    [Fact]
    public void MonitorRect_RecordEquality_Works()
    {
        var a = new MonitorRect(0, 0, 1920, 1080);
        var b = new MonitorRect(0, 0, 1920, 1080);
        Assert.Equal(a, b);
    }

    // ── MonitorResolveResult factory methods ────────────────────────────

    [Fact]
    public void MonitorResolveResult_Success_IsNotFallback()
    {
        var r = MonitorResolveResult.Success(2);
        Assert.Equal(2u, r.Index);
        Assert.False(r.IsFallback);
        Assert.Null(r.FailureReason);
    }

    [Fact]
    public void MonitorResolveResult_Fallback_HasReason()
    {
        var r = MonitorResolveResult.Fallback("test reason");
        Assert.Equal(0u, r.Index);
        Assert.True(r.IsFallback);
        Assert.Equal("test reason", r.FailureReason);
    }

    [Fact]
    public void MonitorResolveResult_Failure_HasReason()
    {
        var r = MonitorResolveResult.Failure("api failed");
        Assert.Equal(0u, r.Index);
        Assert.True(r.IsFallback);
        Assert.Equal("api failed", r.FailureReason);
    }
}
