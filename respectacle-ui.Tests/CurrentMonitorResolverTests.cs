// CurrentMonitorResolverTests.cs — Tests for the cursor-to-monitor-index resolver.
//
// The resolver uses Win32 APIs, so most tests verify it returns a valid index
// (doesn't crash) and that the fallback behavior works on any system.

using System;
using Respectacle.UI;
using Xunit;

namespace Respectacle.UI.Tests;

public class CurrentMonitorResolverTests
{
    // ── Basic contract: returns a valid index on any system ───────────

    [Fact]
    public void GetCurrentMonitorIndex_ReturnsNonNegativeIndex()
    {
        uint index = CurrentMonitorResolver.GetCurrentMonitorIndex();
        Assert.True(index >= 0, $"Monitor index should be non-negative, got {index}");
    }

    [Fact]
    public void GetCurrentMonitorIndex_ReturnsReasonableIndex()
    {
        // Should return 0 or a small number (number of monitors)
        uint index = CurrentMonitorResolver.GetCurrentMonitorIndex();
        Assert.True(index < 16, $"Monitor index should be < 16 for any reasonable system, got {index}");
    }

    [Fact]
    public void GetCurrentMonitorIndex_IsDeterministic()
    {
        // Calling twice in rapid succession should return the same value
        // (cursor hasn't moved between calls)
        uint first = CurrentMonitorResolver.GetCurrentMonitorIndex();
        uint second = CurrentMonitorResolver.GetCurrentMonitorIndex();
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetCurrentMonitorIndex_DoesNotThrow()
    {
        // Should never throw — always falls back gracefully
        var exception = Record.Exception(() => CurrentMonitorResolver.GetCurrentMonitorIndex());
        Assert.Null(exception);
    }
}
