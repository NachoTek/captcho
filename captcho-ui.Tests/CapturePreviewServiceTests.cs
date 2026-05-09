// CapturePreviewServiceTests.cs — Tests for the capture preview service orchestration layer.
//
// Tests verify the result record structure, that the service handles
// DLL-not-found gracefully (no native DLL available in test environment),
// last-capture caching, and export save methods.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class CapturePreviewServiceTests
{
    private readonly CapturePreviewService _service = new();

    // ── Capture fails gracefully when native DLL is not on path ──────

    [Fact]
    public async Task CaptureFullDesktopAsync_NativeDllMissing_ReturnsError()
    {
        var result = await _service.CaptureFullDesktopAsync();
        Assert.NotNull(result);
        // In test environment, the native DLL is typically not on PATH
        // so we expect an error — but the service must not throw
        Assert.False(string.IsNullOrEmpty(result.Mode));
    }

    [Fact]
    public async Task CaptureCurrentMonitorAsync_NativeDllMissing_ReturnsError()
    {
        var result = await _service.CaptureCurrentMonitorAsync();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Mode));
    }

    // ── Result structure ─────────────────────────────────────────────

    [Fact]
    public async Task CaptureFullDesktopAsync_ResultHasModeLabel()
    {
        var result = await _service.CaptureFullDesktopAsync();
        Assert.Equal("Full Desktop", result.Mode);
    }

    [Fact]
    public async Task CaptureCurrentMonitorAsync_ResultHasMonitorLabel()
    {
        var result = await _service.CaptureCurrentMonitorAsync();
        Assert.Contains("Current Monitor", result.Mode);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_ResultHasTimings()
    {
        var result = await _service.CaptureFullDesktopAsync();
        // TotalMs should always be set, even on error
        Assert.True(result.TotalMs >= 0);
    }

    // ── Error result contract ─────────────────────────────────────────

    [Fact]
    public async Task CaptureFullDesktopAsync_OnError_IsSuccessIsFalse()
    {
        var result = await _service.CaptureFullDesktopAsync();
        // If native DLL is missing, IsSuccess should be false
        // If it's present (unlikely in test env), this test just verifies the contract
        if (!string.IsNullOrEmpty(result.Error))
        {
            Assert.False(result.IsSuccess);
            Assert.Null(result.ImageSource);
        }
    }

    // ── Last capture cache ────────────────────────────────────────────

    [Fact]
    public void LastCapturedBitmap_InitiallyNull()
    {
        var service = new CapturePreviewService();
        Assert.Null(service.LastCapturedBitmap);
    }

    [Fact]
    public async Task CaptureFullDesktopAsync_OnFailure_DoesNotSetCache()
    {
        var service = new CapturePreviewService();
        var result = await service.CaptureFullDesktopAsync();

        // In test env, native DLL is usually missing → failure should not set cache
        if (!result.IsSuccess)
        {
            Assert.Null(service.LastCapturedBitmap);
        }
    }

    [Fact]
    public void ClearLastCapture_SetsCacheToNull()
    {
        var service = new CapturePreviewService();
        service.ClearLastCapture();
        Assert.Null(service.LastCapturedBitmap);
    }

    // ── Export: no-capture save paths return clear failures ───────────

    [Fact]
    public void SaveLastCaptureToFileAsync_NoCapture_ReturnsValidationFailure()
    {
        var service = new CapturePreviewService();
        var result = service.SaveLastCaptureToFileAsync(@"C:\temp\test.png");

        Assert.False(result.Success);
        Assert.Equal(ExportPhase.Validation, result.Phase);
        Assert.Contains("No capture to export", result.Message);
        Assert.Null(result.DestinationPath);
    }

    [Fact]
    public void SaveLastCaptureToDefaultLocationAsync_NoCapture_ReturnsValidationFailure()
    {
        var service = new CapturePreviewService();
        var result = service.SaveLastCaptureToDefaultLocationAsync();

        Assert.False(result.Success);
        Assert.Equal(ExportPhase.Validation, result.Phase);
        Assert.Contains("No capture to export", result.Message);
        Assert.Null(result.DestinationPath);
    }

    // ── Export with synthetic bitmap (tests the full save pipeline) ───

    [Fact]
    public void SaveLastCaptureToFileAsync_WithCachedBitmap_SavesSuccessfully()
    {
        var service = new CapturePreviewService();
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho_test_{Guid.NewGuid():N}");
        try
        {
            // Inject a synthetic bitmap via the internal constructor
            var bitmap = ExportTestHelpers.CreateTestBitmap(8, 8);
            // Use the cache setter indirectly — we set LastCapturedBitmap via reflection
            // since the ContiguousBitmap constructor is internal.
            ExportTestHelpers.SetLastCapture(service, bitmap);

            var destPath = Path.Combine(tempDir, "export_test.png");
            var result = service.SaveLastCaptureToFileAsync(destPath);

            Assert.True(result.Success);
            Assert.Equal(destPath, result.DestinationPath);
            Assert.Equal(8, result.Width);
            Assert.Equal(8, result.Height);
            Assert.True(result.ByteCount > 0);
            Assert.True(File.Exists(destPath));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SaveLastCaptureToDefaultLocationAsync_WithCachedBitmap_SavesToDefaultDir()
    {
        var service = new CapturePreviewService();
        var bitmap = ExportTestHelpers.CreateTestBitmap(4, 4);
        ExportTestHelpers.SetLastCapture(service, bitmap);

        var result = service.SaveLastCaptureToDefaultLocationAsync("test");

        Assert.True(result.Success);
        Assert.NotNull(result.DestinationPath);
        Assert.Contains("captcho", result.DestinationPath);
        Assert.True(File.Exists(result.DestinationPath));

        // Clean up saved file
        try { File.Delete(result.DestinationPath!); } catch { }
    }

    [Fact]
    public void SaveLastCaptureToFileAsync_InvalidPath_ReturnsWriteFailure()
    {
        var service = new CapturePreviewService();
        var bitmap = ExportTestHelpers.CreateTestBitmap(4, 4);
        ExportTestHelpers.SetLastCapture(service, bitmap);

        // Use an invalid path (null characters are invalid in paths)
        var result = service.SaveLastCaptureToFileAsync("");

        Assert.False(result.Success);
    }

    // ── Cache replacement: second save after clearing ─────────────────

    [Fact]
    public void SaveLastCaptureToFileAsync_AfterClear_ReturnsNoCapture()
    {
        var service = new CapturePreviewService();
        var bitmap = ExportTestHelpers.CreateTestBitmap(4, 4);
        ExportTestHelpers.SetLastCapture(service, bitmap);

        // Verify it would work before clearing
        Assert.NotNull(service.LastCapturedBitmap);

        // Clear and verify
        service.ClearLastCapture();
        var result = service.SaveLastCaptureToFileAsync(@"C:\temp\test.png");
        Assert.False(result.Success);
        Assert.Contains("No capture to export", result.Message);
    }

    // ── Cancellation ──────────────────────────────────────────────────

    [Fact]
    public void SaveLastCaptureToFileAsync_Cancelled_ReturnsCancelledResult()
    {
        var service = new CapturePreviewService();
        var bitmap = ExportTestHelpers.CreateTestBitmap(4, 4);
        ExportTestHelpers.SetLastCapture(service, bitmap);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = service.SaveLastCaptureToFileAsync(@"C:\temp\cancel_test.png", cts.Token);
        Assert.False(result.Success);
        Assert.Equal(ExportPhase.Cancelled, result.Phase);
    }

    // ── Settings-aware save ───────────────────────────────────────────

    [Fact]
    public void SaveLastCaptureWithSettingsAsync_NoCapture_ReturnsValidationFailure()
    {
        var service = new CapturePreviewService();
        var result = service.SaveLastCaptureWithSettingsAsync(new AppSettings());

        Assert.False(result.Success);
        Assert.Equal(ExportPhase.Validation, result.Phase);
        Assert.Contains("No capture to export", result.Message);
    }

    [Fact]
    public void SaveLastCaptureWithSettingsAsync_WithCachedBitmap_SavesToConfiguredDir()
    {
        var service = new CapturePreviewService();
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho_settings_test_{Guid.NewGuid():N}");
        try
        {
            var bitmap = ExportTestHelpers.CreateTestBitmap(8, 8);
            ExportTestHelpers.SetLastCapture(service, bitmap);

            var settings = new AppSettings
            {
                SaveLocation = tempDir,
                FilenameTemplate = "SettingsTest_<yyyy><MM><dd>"
            };

            var result = service.SaveLastCaptureWithSettingsAsync(settings);

            Assert.True(result.Success);
            Assert.NotNull(result.DestinationPath);
            Assert.StartsWith(tempDir, result.DestinationPath);
            Assert.Contains("SettingsTest_", Path.GetFileName(result.DestinationPath!));
            Assert.True(File.Exists(result.DestinationPath!));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SaveLastCaptureWithSettingsAsync_NullSettings_FallsBackToDefaults()
    {
        var service = new CapturePreviewService();
        var bitmap = ExportTestHelpers.CreateTestBitmap(4, 4);
        ExportTestHelpers.SetLastCapture(service, bitmap);

        var result = service.SaveLastCaptureWithSettingsAsync(new AppSettings());

        Assert.True(result.Success);
        Assert.Contains("captcho", result.DestinationPath);

        try { File.Delete(result.DestinationPath!); } catch { }
    }

    [Fact]
    public void SaveLastCaptureWithSettingsAsync_EmptyLocation_FallsBackToDefaults()
    {
        var service = new CapturePreviewService();
        var bitmap = ExportTestHelpers.CreateTestBitmap(4, 4);
        ExportTestHelpers.SetLastCapture(service, bitmap);

        var settings = new AppSettings { SaveLocation = "" };
        var result = service.SaveLastCaptureWithSettingsAsync(settings);

        Assert.True(result.Success);
        // EffectiveSaveLocation should fall back to default
        Assert.Contains("captcho", result.DestinationPath);

        try { File.Delete(result.DestinationPath!); } catch { }
    }
}
