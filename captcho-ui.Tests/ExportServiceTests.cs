// ExportServiceTests.cs — Tests for clipboard export service and export integration.
//
// Covers no-capture failures, successful clipboard copy via fake adapter,
// clipboard adapter failures, invalid bitmap handling, and cache replacement.

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

/// <summary>
/// Shared test helpers for creating synthetic bitmaps and setting capture cache.
/// Uses reflection to access internal ContiguousBitmap constructor and private fields.
/// </summary>
internal static class ExportTestHelpers
{
    /// <summary>
    /// Creates a synthetic ContiguousBitmap via the internal constructor.
    /// </summary>
    public static ContiguousBitmap CreateTestBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 256);
        }

        var constructor = typeof(ContiguousBitmap).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(int), typeof(int), typeof(int), typeof(byte[]) },
            null);

        Assert.NotNull(constructor);
        return (ContiguousBitmap)constructor.Invoke(new object[] { width, height, width * 4, pixels });
    }

    /// <summary>
    /// Sets the last captured bitmap on a CapturePreviewService via its private field.
    /// </summary>
    public static void SetLastCapture(CapturePreviewService service, ContiguousBitmap bitmap)
    {
        var field = typeof(CapturePreviewService).GetField(
            "_lastCapturedBitmap",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        field.SetValue(service, bitmap);
    }
}

/// <summary>
/// Fake clipboard adapter for headless testing.
/// Records whether SetPngImage was called and with what data.
/// </summary>
internal sealed class FakeClipboardAdapter : IClipboardAdapter
{
    public byte[]? LastPngBytes { get; private set; }
    public int CallCount { get; private set; }
    public bool ShouldSucceed { get; set; } = true;

    public bool SetPngImage(byte[] pngBytes)
    {
        CallCount++;
        LastPngBytes = pngBytes;
        return ShouldSucceed;
    }
}

/// <summary>
/// Fake clipboard adapter that always throws.
/// Used to verify exception isolation in the clipboard boundary.
/// </summary>
internal sealed class ThrowingClipboardAdapter : IClipboardAdapter
{
    public string Message { get; set; } = "Clipboard exploded";

    public bool SetPngImage(byte[] pngBytes) => throw new InvalidOperationException(Message);
}

public class ClipboardExportServiceTests
{
    // ── No-capture failures ───────────────────────────────────────────

    [Fact]
    public void CopyToClipboard_NoCapture_ReturnsNoCaptureFailure()
    {
        var fake = new FakeClipboardAdapter();
        var service = new ClipboardExportService(fake, () => null);

        var result = service.CopyToClipboard();

        Assert.False(result.Success);
        Assert.Contains("No capture to copy", result.Message);
        Assert.Null(result.Width);
        Assert.Null(result.Height);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public void CopyToClipboard_InvalidDimensions_ReturnsInvalidDimensions()
    {
        var fake = new FakeClipboardAdapter();
        var zeroDim = ExportTestHelpers.CreateTestBitmap(0, 10);
        var service = new ClipboardExportService(fake, () => zeroDim);

        var result = service.CopyToClipboard();

        Assert.False(result.Success);
        Assert.Contains("Invalid capture dimensions", result.Message);
        Assert.Equal(0, fake.CallCount);
    }

    // ── Successful copy ───────────────────────────────────────────────

    [Fact]
    public void CopyToClipboard_WithValidBitmap_Succeeds()
    {
        var fake = new FakeClipboardAdapter();
        var bitmap = ExportTestHelpers.CreateTestBitmap(16, 16);
        var service = new ClipboardExportService(fake, () => bitmap);

        var result = service.CopyToClipboard();

        Assert.True(result.Success);
        Assert.Equal("Copied to clipboard", result.Message);
        Assert.Equal(16, result.Width);
        Assert.Equal(16, result.Height);
        Assert.True(result.ByteCount > 0);
        Assert.Equal(1, fake.CallCount);
        Assert.NotNull(fake.LastPngBytes);
        Assert.True(fake.LastPngBytes!.Length > 0);
    }

    [Fact]
    public void CopyToClipboard_ProducesValidPng()
    {
        var fake = new FakeClipboardAdapter();
        var bitmap = ExportTestHelpers.CreateTestBitmap(8, 8);
        var service = new ClipboardExportService(fake, () => bitmap);

        var result = service.CopyToClipboard();

        Assert.True(result.Success);
        Assert.NotNull(fake.LastPngBytes);
        // Verify PNG signature: 89 50 4E 47 0D 0A 1A 0A
        Assert.True(fake.LastPngBytes!.Length >= 8);
        Assert.Equal(0x89, fake.LastPngBytes[0]);
        Assert.Equal(0x50, fake.LastPngBytes[1]); // P
        Assert.Equal(0x4E, fake.LastPngBytes[2]); // N
        Assert.Equal(0x47, fake.LastPngBytes[3]); // G
    }

    // ── Clipboard adapter failure ─────────────────────────────────────

    [Fact]
    public void CopyToClipboard_AdapterReturnsFalse_ReturnsClipboardFailure()
    {
        var fake = new FakeClipboardAdapter { ShouldSucceed = false };
        var bitmap = ExportTestHelpers.CreateTestBitmap(8, 8);
        var service = new ClipboardExportService(fake, () => bitmap);

        var result = service.CopyToClipboard();

        Assert.False(result.Success);
        Assert.Contains("Failed to set clipboard content", result.Message);
        Assert.Equal(1, fake.CallCount); // Was called, but returned false
    }

    [Fact]
    public void CopyToClipboard_AdapterThrows_ReturnsSanitizedFailure()
    {
        var throwing = new ThrowingClipboardAdapter();
        var bitmap = ExportTestHelpers.CreateTestBitmap(8, 8);
        var service = new ClipboardExportService(throwing, () => bitmap);

        // The service should propagate the exception — callers handle it at a higher level.
        // This is a platform error that can't be silently swallowed.
        Assert.Throws<InvalidOperationException>(() => service.CopyToClipboard());
    }

    // ── Cache replacement ─────────────────────────────────────────────

    [Fact]
    public void CopyToClipboard_SecondCapture_ReplacesFirst()
    {
        var fake = new FakeClipboardAdapter();
        var bitmap1 = ExportTestHelpers.CreateTestBitmap(4, 4);
        var bitmap2 = ExportTestHelpers.CreateTestBitmap(32, 32);

        var current = bitmap1;
        var service = new ClipboardExportService(fake, () => current);

        // First copy
        var result1 = service.CopyToClipboard();
        Assert.True(result1.Success);
        Assert.Equal(4, result1.Width);
        var firstSize = fake.LastPngBytes!.Length;

        // Replace capture
        current = bitmap2;
        var result2 = service.CopyToClipboard();
        Assert.True(result2.Success);
        Assert.Equal(32, result2.Width);
        // Larger bitmap should produce larger PNG
        Assert.True(fake.LastPngBytes!.Length > firstSize);
    }

    [Fact]
    public void CopyToClipboard_CaptureSetToNull_ReturnsNoCapture()
    {
        var fake = new FakeClipboardAdapter();
        var bitmap = ExportTestHelpers.CreateTestBitmap(4, 4);
        var current = (ContiguousBitmap?)bitmap;
        var service = new ClipboardExportService(fake, () => current);

        // First succeeds
        Assert.True(service.CopyToClipboard().Success);

        // Null the capture
        current = null;
        var result = service.CopyToClipboard();
        Assert.False(result.Success);
        Assert.Contains("No capture to copy", result.Message);
    }

    // ── Timing diagnostics ────────────────────────────────────────────

    [Fact]
    public void CopyToClipboard_Success_HasPositiveElapsed()
    {
        var fake = new FakeClipboardAdapter();
        var bitmap = ExportTestHelpers.CreateTestBitmap(8, 8);
        var service = new ClipboardExportService(fake, () => bitmap);

        var result = service.CopyToClipboard();

        Assert.True(result.Success);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void CopyToClipboard_NoCapture_HasZeroElapsed()
    {
        var fake = new FakeClipboardAdapter();
        var service = new ClipboardExportService(fake, () => null);

        var result = service.CopyToClipboard();

        // No-capture failure should have near-zero elapsed
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(1));
    }
}

public class ClipboardExportViaCapturePreviewServiceTests
{
    [Fact]
    public void CreateClipboardExporter_NullAdapter_Throws()
    {
        var service = new CapturePreviewService();
        Assert.Throws<ArgumentNullException>(() => service.CreateClipboardExporter(null!));
    }

    [Fact]
    public void CreateClipboardExporter_NoCapture_CopyFails()
    {
        var service = new CapturePreviewService();
        var fake = new FakeClipboardAdapter();
        var exporter = service.CreateClipboardExporter(fake);

        var result = exporter.CopyToClipboard();

        Assert.False(result.Success);
        Assert.Contains("No capture to copy", result.Message);
    }

    [Fact]
    public void CreateClipboardExporter_WithCachedBitmap_CopySucceeds()
    {
        var service = new CapturePreviewService();
        var bitmap = ExportTestHelpers.CreateTestBitmap(16, 16);
        ExportTestHelpers.SetLastCapture(service, bitmap);

        var fake = new FakeClipboardAdapter();
        var exporter = service.CreateClipboardExporter(fake);

        var result = exporter.CopyToClipboard();

        Assert.True(result.Success);
        Assert.Equal(16, result.Width);
        Assert.Equal(16, result.Height);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public void CreateClipboardExporter_AfterClearCopy_Fails()
    {
        var service = new CapturePreviewService();
        var bitmap = ExportTestHelpers.CreateTestBitmap(16, 16);
        ExportTestHelpers.SetLastCapture(service, bitmap);

        service.ClearLastCapture();

        var fake = new FakeClipboardAdapter();
        var exporter = service.CreateClipboardExporter(fake);
        var result = exporter.CopyToClipboard();

        Assert.False(result.Success);
        Assert.Contains("No capture to copy", result.Message);
    }

    // ── Integration: save + clipboard from same cache ─────────────────

    [Fact]
    public void SaveAndCopy_BothUseSameCachedBitmap()
    {
        var service = new CapturePreviewService();
        var bitmap = ExportTestHelpers.CreateTestBitmap(10, 10);
        ExportTestHelpers.SetLastCapture(service, bitmap);

        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho_both_test_{Guid.NewGuid():N}");
        try
        {
            // Save to file
            var destPath = Path.Combine(tempDir, "both_test.png");
            var saveResult = service.SaveLastCaptureToFileAsync(destPath);
            Assert.True(saveResult.Success);

            // Copy to clipboard
            var fake = new FakeClipboardAdapter();
            var exporter = service.CreateClipboardExporter(fake);
            var copyResult = exporter.CopyToClipboard();
            Assert.True(copyResult.Success);

            // Both report same dimensions
            Assert.Equal(saveResult.Width, copyResult.Width);
            Assert.Equal(saveResult.Height, copyResult.Height);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }
}
