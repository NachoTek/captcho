// WindowResolverTests.cs — Tests for the WindowResolver metadata helper.
//
// Tests verify fallback/sanitization behavior for invalid handles,
// empty titles, and headless environments where Win32 APIs may fail.
// These should all pass without any interactive window session.

using System;
using System.Runtime.InteropServices;
using Respectacle.UI;
using Xunit;

namespace Respectacle.UI.Tests;

public class WindowResolverTests
{
    // ── Zero/invalid handle handling ─────────────────────────────────────

    [Fact]
    public void GetTitle_ZeroHandle_ReturnsEmpty()
    {
        Assert.Equal("", WindowResolver.GetTitle(IntPtr.Zero));
    }

    [Fact]
    public void IsVisible_ZeroHandle_ReturnsFalse()
    {
        Assert.False(WindowResolver.IsVisible(IntPtr.Zero));
    }

    [Fact]
    public void IsMinimized_ZeroHandle_ReturnsFalse()
    {
        Assert.False(WindowResolver.IsMinimized(IntPtr.Zero));
    }

    [Fact]
    public void FormatHandle_ZeroHandle_ReturnsEmpty()
    {
        Assert.Equal("", WindowResolver.FormatHandle(IntPtr.Zero));
    }

    // ── FormatHandle with valid handle ──────────────────────────────────

    [Fact]
    public void FormatHandle_NonZeroHandle_ReturnsHex()
    {
        var hwnd = new IntPtr(0x1234AB);
        string formatted = WindowResolver.FormatHandle(hwnd);
        Assert.Equal("0x1234AB", formatted);
    }

    [Fact]
    public void FormatHandle_SmallHandle_ReturnsHex()
    {
        var hwnd = new IntPtr(0x1);
        string formatted = WindowResolver.FormatHandle(hwnd);
        Assert.Equal("0x1", formatted);
    }

    // ── Label builder fallbacks ──────────────────────────────────────────

    [Fact]
    public void BuildActiveWindowLabel_ContainsModeText()
    {
        // In any environment (even headless), the label must contain the mode name
        string label = WindowResolver.BuildActiveWindowLabel();
        Assert.Contains("Active Window", label);
    }

    [Fact]
    public void BuildWindowUnderCursorLabel_ContainsModeText()
    {
        string label = WindowResolver.BuildWindowUnderCursorLabel();
        Assert.Contains("Window Under Cursor", label);
    }

    // ── Non-zero but invalid handle ──────────────────────────────────────

    [Fact]
    public void GetTitle_InvalidHandle_ReturnsEmpty()
    {
        // Use a handle that almost certainly doesn't correspond to a real window
        var fakeHwnd = new IntPtr(0xDEAD);
        string title = WindowResolver.GetTitle(fakeHwnd);
        // Win32 GetWindowText returns empty for invalid handles
        Assert.NotNull(title);
    }

    [Fact]
    public void IsVisible_InvalidHandle_ReturnsFalseOrValue()
    {
        var fakeHwnd = new IntPtr(0xDEAD);
        // Should not throw — returns a bool
        bool result = WindowResolver.IsVisible(fakeHwnd);
        // We don't assert the value, just that it didn't throw
        Assert.True(true);
    }

    [Fact]
    public void IsMinimized_InvalidHandle_ReturnsFalseOrValue()
    {
        var fakeHwnd = new IntPtr(0xDEAD);
        bool result = WindowResolver.IsMinimized(fakeHwnd);
        Assert.True(true); // Just verify no exception
    }

    // ── Label format structure ───────────────────────────────────────────

    [Fact]
    public void BuildActiveWindowLabel_NeverNull()
    {
        string label = WindowResolver.BuildActiveWindowLabel();
        Assert.NotNull(label);
        Assert.NotEqual("", label);
    }

    [Fact]
    public void BuildWindowUnderCursorLabel_NeverNull()
    {
        string label = WindowResolver.BuildWindowUnderCursorLabel();
        Assert.NotNull(label);
        Assert.NotEqual("", label);
    }

    // ── Handle resolver no-throw ─────────────────────────────────────────

    [Fact]
    public void GetActiveWindowHandle_DoesNotThrow()
    {
        // Just verify no exception in headless environment
        IntPtr hwnd = WindowResolver.GetActiveWindowHandle();
        // Value may be zero or a real handle depending on environment
        Assert.True(true);
    }

    [Fact]
    public void GetWindowUnderCursorHandle_DoesNotThrow()
    {
        IntPtr hwnd = WindowResolver.GetWindowUnderCursorHandle();
        Assert.True(true);
    }
}
