// WindowCapturePreviewServiceTests.cs — Tests for S03 capture service methods.
//
// Tests verify mode labels, timing structure, and no-throw error behavior
// when the native DLL or WGC is unavailable (typical headless test environment).

using System.Threading.Tasks;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class WindowCapturePreviewServiceTests
{
    private readonly CapturePreviewService _service = new();

    // ── Mode labels ──────────────────────────────────────────────────────

    [Fact]
    public async Task CaptureActiveWindowAsync_ResultHasModeLabel()
    {
        var result = await _service.CaptureActiveWindowAsync();
        Assert.NotNull(result);
        Assert.Contains("Active Window", result.Mode);
    }

    [Fact]
    public async Task CaptureWindowUnderCursorAsync_ResultHasModeLabel()
    {
        var result = await _service.CaptureWindowUnderCursorAsync();
        Assert.NotNull(result);
        Assert.Contains("Window Under Cursor", result.Mode);
    }

    // ── No-throw on missing native DLL ───────────────────────────────────

    [Fact]
    public async Task CaptureActiveWindowAsync_DoesNotThrow()
    {
        // Must return a result, never throw — even with no native DLL
        var result = await _service.CaptureActiveWindowAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CaptureWindowUnderCursorAsync_DoesNotThrow()
    {
        var result = await _service.CaptureWindowUnderCursorAsync();
        Assert.NotNull(result);
    }

    // ── Error result contract ────────────────────────────────────────────

    [Fact]
    public async Task CaptureActiveWindowAsync_OnError_HasNonEmptyMode()
    {
        var result = await _service.CaptureActiveWindowAsync();
        // Even on error, the mode must identify the capture type
        Assert.False(string.IsNullOrEmpty(result.Mode));
    }

    [Fact]
    public async Task CaptureWindowUnderCursorAsync_OnError_HasNonEmptyMode()
    {
        var result = await _service.CaptureWindowUnderCursorAsync();
        Assert.False(string.IsNullOrEmpty(result.Mode));
    }

    // ── Timing structure ─────────────────────────────────────────────────

    [Fact]
    public async Task CaptureActiveWindowAsync_ResultHasTimings()
    {
        var result = await _service.CaptureActiveWindowAsync();
        Assert.True(result.TotalMs >= 0);
    }

    [Fact]
    public async Task CaptureWindowUnderCursorAsync_ResultHasTimings()
    {
        var result = await _service.CaptureWindowUnderCursorAsync();
        Assert.True(result.TotalMs >= 0);
    }

    // ── Error result: IsSuccess consistency ──────────────────────────────

    [Fact]
    public async Task CaptureActiveWindowAsync_OnError_IsSuccessConsistent()
    {
        var result = await _service.CaptureActiveWindowAsync();
        if (!string.IsNullOrEmpty(result.Error))
        {
            Assert.False(result.IsSuccess);
            Assert.Null(result.ImageSource);
        }
    }

    [Fact]
    public async Task CaptureWindowUnderCursorAsync_OnError_IsSuccessConsistent()
    {
        var result = await _service.CaptureWindowUnderCursorAsync();
        if (!string.IsNullOrEmpty(result.Error))
        {
            Assert.False(result.IsSuccess);
            Assert.Null(result.ImageSource);
        }
    }
}
