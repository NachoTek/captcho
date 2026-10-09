// WindowCapturePreviewServiceTests.cs — Tests for the Window Under Cursor
// capture service method (still routed through CapturePreviewService).
//
// Tests verify mode labels, timing structure, and no-throw error behavior
// when the native DLL or WGC is unavailable (typical headless test environment).
// Active Window coverage now lives in CaptureWorkflowSessionTests, since that
// route was migrated to the runtime workflow session (issue #26).

using System.Threading.Tasks;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class WindowCapturePreviewServiceTests
{
    private readonly CapturePreviewService _service = new();

    // ── Mode labels ──────────────────────────────────────────────────────

    [Fact]
    public async Task CaptureWindowUnderCursorAsync_ResultHasModeLabel()
    {
        var result = await _service.CaptureWindowUnderCursorAsync();
        Assert.NotNull(result);
        Assert.Contains("Window Under Cursor", result.Mode);
    }

    // ── No-throw on missing native DLL ───────────────────────────────────

    [Fact]
    public async Task CaptureWindowUnderCursorAsync_DoesNotThrow()
    {
        var result = await _service.CaptureWindowUnderCursorAsync();
        Assert.NotNull(result);
    }

    // ── Error result contract ────────────────────────────────────────────

    [Fact]
    public async Task CaptureWindowUnderCursorAsync_OnError_HasNonEmptyMode()
    {
        var result = await _service.CaptureWindowUnderCursorAsync();
        Assert.False(string.IsNullOrEmpty(result.Mode));
    }

    // ── Timing structure ─────────────────────────────────────────────────

    [Fact]
    public async Task CaptureWindowUnderCursorAsync_ResultHasTimings()
    {
        var result = await _service.CaptureWindowUnderCursorAsync();
        Assert.True(result.TotalMs >= 0);
    }

    // ── Error result: IsSuccess consistency ──────────────────────────────

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
