// PngExportServiceTests — Validates PNG encoding, file signature,
// dimension correctness, error handling, cancellation, and timing.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace Respectacle.Capture.Tests;

public class PngExportServiceTests
{
    // ── Helper: create a synthetic ContiguousBitmap ─────────────────────

    private static ContiguousBitmap MakeBitmap(int width, int height, byte fill = 0xFF)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = fill;
        return new ContiguousBitmap(width, height, stride, pixels);
    }

    private static ContiguousBitmap MakeGradientBitmap(int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                pixels[offset + 0] = (byte)(x % 256);     // B
                pixels[offset + 1] = (byte)(y % 256);     // G
                pixels[offset + 2] = (byte)((x + y) % 256); // R
                pixels[offset + 3] = 0xFF;                 // A
            }
        }
        return new ContiguousBitmap(width, height, stride, pixels);
    }

    private string GetTempPngPath()
    {
        return Path.Combine(Path.GetTempPath(), $"respectacle-test-{Guid.NewGuid():N}.png");
    }

    // ── Happy Path: Save and Verify PNG ──────────────────────────────────

    [Fact]
    public void SaveAsPng_SmallBitmap_WritesValidPng()
    {
        var bitmap = MakeBitmap(4, 4);
        string path = GetTempPngPath();
        try
        {
            var result = PngExportService.SaveAsPng(bitmap, path);
            Assert.True(result.Success, $"Export failed: {result.Message}");
            Assert.True(File.Exists(path), "PNG file was not created");

            // Verify PNG signature (first 8 bytes)
            byte[] header = new byte[8];
            using (var fs = File.OpenRead(path))
                fs.Read(header, 0, 8);

            byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Assert.Equal(pngSignature, header);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveAsPng_CorrectDimensions()
    {
        int w = 32, h = 24;
        var bitmap = MakeBitmap(w, h);
        string path = GetTempPngPath();
        try
        {
            var result = PngExportService.SaveAsPng(bitmap, path);
            Assert.True(result.Success);
            Assert.Equal(w, result.Width);
            Assert.Equal(h, result.Height);
            Assert.Equal(path, result.DestinationPath);
            Assert.True(result.ByteCount > 0, "ByteCount should be positive");
            Assert.True(result.Elapsed > TimeSpan.Zero);

            // Decode with GDI+ and verify dimensions
            using var img = System.Drawing.Image.FromFile(path);
            Assert.Equal(w, img.Width);
            Assert.Equal(h, img.Height);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveAsPng_GradientBitmap_Decodable()
    {
        var bitmap = MakeGradientBitmap(16, 16);
        string path = GetTempPngPath();
        try
        {
            var result = PngExportService.SaveAsPng(bitmap, path);
            Assert.True(result.Success, $"Failed: {result.Message}");

            // Decode and verify pixel content
            using var gdiBmp = new System.Drawing.Bitmap(path);
            Assert.Equal(16, gdiBmp.Width);
            Assert.Equal(16, gdiBmp.Height);

            // Sample a few pixels (not exhaustive — just prove decode works)
            var pixel = gdiBmp.GetPixel(0, 0);
            Assert.Equal(0xFF, pixel.A); // Full alpha
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveAsPng_CreatesDirectory()
    {
        var bitmap = MakeBitmap(2, 2);
        string dir = Path.Combine(Path.GetTempPath(), $"respectacle-dir-test-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "test.png");
        try
        {
            var result = PngExportService.SaveAsPng(bitmap, path);
            Assert.True(result.Success, $"Failed: {result.Message}");
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
    }

    // ── Timing Assertion ─────────────────────────────────────────────────

    [Fact]
    public void SaveAsPng_SmallBitmap_CompletesQuickly()
    {
        var bitmap = MakeBitmap(64, 64);
        string path = GetTempPngPath();
        try
        {
            var sw = Stopwatch.StartNew();
            var result = PngExportService.SaveAsPng(bitmap, path);
            sw.Stop();

            Assert.True(result.Success);
            // 100ms budget for encoding a tiny bitmap on any reasonable hardware
            Assert.True(sw.ElapsedMilliseconds < 100,
                $"Save took {sw.ElapsedMilliseconds}ms, expected < 100ms");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── EncodeToPngBytes ─────────────────────────────────────────────────

    [Fact]
    public void EncodeToPngBytes_ReturnsValidPngData()
    {
        var bitmap = MakeBitmap(8, 8);
        byte[]? data = PngExportService.EncodeToPngBytes(bitmap);
        Assert.NotNull(data);

        // Verify PNG signature
        byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        for (int i = 0; i < 8; i++)
            Assert.Equal(pngSignature[i], data![i]);
    }

    // ── Negative Tests: Invalid Inputs ───────────────────────────────────

    [Fact]
    public void SaveAsPng_NullBitmap_FailsValidation()
    {
        string path = GetTempPngPath();
        try
        {
            var result = PngExportService.SaveAsPng(null!, path);
            Assert.False(result.Success);
            Assert.Equal(ExportPhase.Validation, result.Phase);
            Assert.Contains("No bitmap", result.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveAsPng_EmptyPath_FailsValidation()
    {
        var bitmap = MakeBitmap(2, 2);
        var result = PngExportService.SaveAsPng(bitmap, "");
        Assert.False(result.Success);
        Assert.Equal(ExportPhase.Validation, result.Phase);
    }

    [Fact]
    public void SaveAsPng_NullPath_FailsValidation()
    {
        var bitmap = MakeBitmap(2, 2);
        var result = PngExportService.SaveAsPng(bitmap, null!);
        Assert.False(result.Success);
        Assert.Equal(ExportPhase.Validation, result.Phase);
    }

    [Fact]
    public void SaveAsPng_ZeroWidth_FailsValidation()
    {
        // Create a bitmap with zero width — this should fail validation
        // We can't construct via StripPadding, so test the guard directly
        int w = 0, h = 4, stride = 0;
        byte[] pixels = Array.Empty<byte>();
        var bitmap = new ContiguousBitmap(w, h, stride, pixels);
        string path = GetTempPngPath();

        try
        {
            var result = PngExportService.SaveAsPng(bitmap, path);
            Assert.False(result.Success);
            Assert.Equal(ExportPhase.Validation, result.Phase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveAsPng_UndersizedPixelBuffer_FailsValidation()
    {
        // ContiguousBitmap with pixels array smaller than width*height*4
        int w = 10, h = 10, stride = 40;
        byte[] pixels = new byte[10]; // way too small
        var bitmap = new ContiguousBitmap(w, h, stride, pixels);
        string path = GetTempPngPath();

        try
        {
            var result = PngExportService.SaveAsPng(bitmap, path);
            Assert.False(result.Success);
            Assert.Equal(ExportPhase.Validation, result.Phase);
            Assert.Contains("too small", result.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Negative Tests: Filesystem Errors ────────────────────────────────

    [Fact]
    public void SaveAsPng_InvalidPath_FailsWrite()
    {
        var bitmap = MakeBitmap(2, 2);
        // Use an invalid path (contains null char or similar)
        var result = PngExportService.SaveAsPng(bitmap, "Z:\\nonexistent\\deep\\path\\test.png");
        // Should fail but not crash — sanitized result
        Assert.False(result.Success);
        // Should be in Write or Validation phase, not crash
        Assert.True(result.Phase == ExportPhase.Write || result.Phase == ExportPhase.Validation);
    }

    // ── Cancellation ─────────────────────────────────────────────────────

    [Fact]
    public void SaveAsPng_AlreadyCancelled_ReturnsCancelled()
    {
        var bitmap = MakeBitmap(4, 4);
        string path = GetTempPngPath();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = PngExportService.SaveAsPng(bitmap, path, cts.Token);
            Assert.False(result.Success);
            Assert.Equal(ExportPhase.Cancelled, result.Phase);
            Assert.Equal("Export cancelled", result.Message);
            Assert.False(File.Exists(path), "Partial file should be cleaned up on cancellation");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Error Message Sanitization ───────────────────────────────────────

    [Fact]
    public void SaveAsPng_FailuresContainNoRawPixelData()
    {
        var bitmap = MakeBitmap(2, 2);
        // Various failure paths — none should leak pixel data
        var result1 = PngExportService.SaveAsPng(null!, "test.png");
        var result2 = PngExportService.SaveAsPng(bitmap, "");
        var result3 = PngExportService.SaveAsPng(bitmap, "Z:\\no\\path\\test.png");

        foreach (var r in new[] { result1, result2, result3 })
        {
            Assert.DoesNotContain("0x", r.Message); // no hex dumps
            Assert.True(r.Message.Length < 500, "Error messages should be short");
        }
    }

    // ── EncodeToPngBytes Negative ────────────────────────────────────────

    [Fact]
    public void EncodeToPngBytes_NullBitmap_ReturnsNull()
    {
        Assert.Null(PngExportService.EncodeToPngBytes(null!));
    }

    [Fact]
    public void EncodeToPngBytes_ZeroDimensions_ReturnsNull()
    {
        var bitmap = new ContiguousBitmap(0, 0, 0, Array.Empty<byte>());
        Assert.Null(PngExportService.EncodeToPngBytes(bitmap));
    }

    [Fact]
    public void EncodeToPngBytes_UndersizedBuffer_ReturnsNull()
    {
        var bitmap = new ContiguousBitmap(10, 10, 40, new byte[10]);
        Assert.Null(PngExportService.EncodeToPngBytes(bitmap));
    }

    // ── ExportResult Construction ─────────────────────────────────────────

    [Fact]
    public void ExportResult_Ok_HasExpectedFields()
    {
        var r = ExportResult.Ok("/path/to/file.png", 1920, 1080, 123456, TimeSpan.FromMilliseconds(50));
        Assert.True(r.Success);
        Assert.Equal("/path/to/file.png", r.DestinationPath);
        Assert.Equal(1920, r.Width);
        Assert.Equal(1080, r.Height);
        Assert.Equal(123456, r.ByteCount);
        Assert.Equal("Saved file.png", r.Message);
    }

    [Fact]
    public void ExportResult_Fail_HasExpectedFields()
    {
        var r = ExportResult.Fail(ExportPhase.Encode, "Encode error", TimeSpan.FromSeconds(1));
        Assert.False(r.Success);
        Assert.Equal(ExportPhase.Encode, r.Phase);
        Assert.Equal("Encode error", r.Message);
        Assert.Null(r.DestinationPath);
        Assert.Null(r.Width);
    }

    [Fact]
    public void ExportResult_Cancelled_HasExpectedFields()
    {
        var r = ExportResult.Cancelled(TimeSpan.FromMilliseconds(10));
        Assert.False(r.Success);
        Assert.Equal(ExportPhase.Cancelled, r.Phase);
        Assert.Equal("Export cancelled", r.Message);
    }
}
