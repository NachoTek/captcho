// RegionCapturePreviewServiceTests.cs — Tests for S05 rectangular region capture via the service layer.
//
// Tests verify mode labels (signed coordinates, dimensions), timing structure,
// zero-dimension rejection, fractional rounding, and no-throw error behavior
// when the native DLL is unavailable (typical headless test environment).

using System.Threading.Tasks;
using Respectacle.UI;
using Windows.Foundation;
using Xunit;

namespace Respectacle.UI.Tests;

public class RegionCapturePreviewServiceTests
{
    private readonly CapturePreviewService _service = new();

    // ── Mode labels ──────────────────────────────────────────────────────

    [Fact]
    public async Task CaptureRegionAsync_ResultHasRectangularRegionModeLabel()
    {
        var result = await _service.CaptureRegionAsync(new Rect(100, 200, 800, 600));
        Assert.NotNull(result);
        Assert.StartsWith("Rectangular Region", result.Mode);
    }

    [Fact]
    public async Task CaptureRegionAsync_ModeLabelContainsCoordinates()
    {
        var result = await _service.CaptureRegionAsync(new Rect(100, 200, 800, 600));
        Assert.Contains("X=100", result.Mode);
        Assert.Contains("Y=200", result.Mode);
        Assert.Contains("800×600", result.Mode);
    }

    [Fact]
    public async Task CaptureRegionAsync_NegativeCoordinates_ModeLabelShowsSignedValues()
    {
        var result = await _service.CaptureRegionAsync(new Rect(-1920, -1080, 800, 600));
        Assert.Contains("X=-1920", result.Mode);
        Assert.Contains("Y=-1080", result.Mode);
    }

    [Fact]
    public async Task CaptureRegionAsync_LargeRegion_ModeLabelShowsDimensions()
    {
        var result = await _service.CaptureRegionAsync(new Rect(0, 0, 3840, 2160));
        Assert.Contains("3840×2160", result.Mode);
    }

    // ── Fractional / rounding ────────────────────────────────────────────

    [Fact]
    public async Task CaptureRegionAsync_FractionalCoordinates_RoundsToNearest()
    {
        var result = await _service.CaptureRegionAsync(new Rect(10.4, 20.6, 100.3, 200.7));
        // 10.4 → 10, 20.6 → 21, 100.3 → 100, 200.7 → 201
        Assert.Contains("X=10", result.Mode);
        Assert.Contains("Y=21", result.Mode);
        Assert.Contains("100×201", result.Mode);
    }

    // ── Zero-dimension rejection ─────────────────────────────────────────

    [Fact]
    public async Task CaptureRegionAsync_ZeroWidth_ReturnsError()
    {
        var result = await _service.CaptureRegionAsync(new Rect(100, 200, 0, 600));
        Assert.False(result.IsSuccess);
        Assert.Null(result.ImageSource);
        Assert.Contains("zero dimensions", result.Error);
    }

    [Fact]
    public async Task CaptureRegionAsync_ZeroHeight_ReturnsError()
    {
        var result = await _service.CaptureRegionAsync(new Rect(100, 200, 800, 0));
        Assert.False(result.IsSuccess);
        Assert.Null(result.ImageSource);
        Assert.Contains("zero dimensions", result.Error);
    }

    [Fact]
    public async Task CaptureRegionAsync_ZeroDimensions_ReturnsError()
    {
        var result = await _service.CaptureRegionAsync(new Rect(100, 200, 0, 0));
        Assert.False(result.IsSuccess);
        Assert.Null(result.ImageSource);
        Assert.Contains("zero dimensions", result.Error);
    }

    [Fact]
    public async Task CaptureRegionAsync_ZeroDimensions_StillHasModeLabel()
    {
        var result = await _service.CaptureRegionAsync(new Rect(100, 200, 0, 0));
        Assert.StartsWith("Rectangular Region", result.Mode);
    }

    // ── No-throw on missing native DLL ───────────────────────────────────

    [Fact]
    public async Task CaptureRegionAsync_DoesNotThrow()
    {
        // Must return a result, never throw — even with no native DLL
        var result = await _service.CaptureRegionAsync(new Rect(0, 0, 800, 600));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CaptureRegionAsync_NegativeOrigin_DoesNotThrow()
    {
        var result = await _service.CaptureRegionAsync(new Rect(-1920, 0, 1920, 1080));
        Assert.NotNull(result);
    }

    // ── Timing structure ─────────────────────────────────────────────────

    [Fact]
    public async Task CaptureRegionAsync_ResultHasTimings()
    {
        var result = await _service.CaptureRegionAsync(new Rect(0, 0, 800, 600));
        Assert.True(result.TotalMs >= 0);
    }

    [Fact]
    public async Task CaptureRegionAsync_OnError_TimingStillSet()
    {
        var result = await _service.CaptureRegionAsync(new Rect(0, 0, 800, 600));
        // Whether success or error, TotalMs must be populated
        Assert.True(result.TotalMs >= 0);
        if (!string.IsNullOrEmpty(result.Error))
        {
            // On error path, CaptureMs should also be set
            Assert.True(result.CaptureMs >= 0);
        }
    }

    // ── Error result contract ────────────────────────────────────────────

    [Fact]
    public async Task CaptureRegionAsync_OnNativeError_IsSuccessConsistent()
    {
        var result = await _service.CaptureRegionAsync(new Rect(0, 0, 800, 600));
        if (!string.IsNullOrEmpty(result.Error))
        {
            Assert.False(result.IsSuccess);
            Assert.Null(result.ImageSource);
        }
    }

    [Fact]
    public async Task CaptureRegionAsync_OnNativeError_HasNonEmptyMode()
    {
        var result = await _service.CaptureRegionAsync(new Rect(0, 0, 800, 600));
        // Even on error, the mode must identify the capture type
        Assert.False(string.IsNullOrEmpty(result.Mode));
    }

    // ── 1×1 minimum region ───────────────────────────────────────────────

    [Fact]
    public async Task CaptureRegionAsync_OneByOneRegion_DoesNotReturnZeroDimensionError()
    {
        var result = await _service.CaptureRegionAsync(new Rect(0, 0, 1, 1));
        // 1×1 should be valid dimensions — error, if any, comes from native, not zero-dim check
        Assert.DoesNotContain("zero dimensions", result.Error ?? "");
    }
}
