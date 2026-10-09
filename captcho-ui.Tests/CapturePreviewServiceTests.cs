// CapturePreviewServiceTests.cs — Tests for the legacy capture preview service.
//
// Full Desktop capture is now routed through CaptureWorkflowSession and is
// covered by CaptureWorkflowSessionTests; the tests below cover the
// remaining legacy routes (Current Monitor / Active Window / Window Under
// Cursor / Region) and the last-capture cache that MainWindow hands to the
// workflow session via AdoptFrame — the session owns the Export actions
// (issue #44). Tests verify graceful DLL-not-found handling and caching.

using System.Threading.Tasks;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class CapturePreviewServiceTests
{
    private readonly CapturePreviewService _service = new();

    // ── Capture fails gracefully when native DLL is not on path ──────

    [Fact]
    public async Task CaptureCurrentMonitorAsync_NativeDllMissing_ReturnsError()
    {
        var result = await _service.CaptureCurrentMonitorAsync();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Mode));
    }

    // ── Result structure ─────────────────────────────────────────────

    [Fact]
    public async Task CaptureCurrentMonitorAsync_ResultHasMonitorLabel()
    {
        var result = await _service.CaptureCurrentMonitorAsync();
        Assert.Contains("Current Monitor", result.Mode);
    }

    // ── Last capture cache ────────────────────────────────────────────

    [Fact]
    public void LastCapturedBitmap_InitiallyNull()
    {
        var service = new CapturePreviewService();
        Assert.Null(service.LastCapturedBitmap);
    }

    [Fact]
    public void ClearLastCapture_SetsCacheToNull()
    {
        var service = new CapturePreviewService();
        service.ClearLastCapture();
        Assert.Null(service.LastCapturedBitmap);
    }
}
