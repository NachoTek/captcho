// JpegExportServiceTests — Validates JPEG encoding through the export seam:
// file signature, dimension round-trip, quality boundaries (inclusive 0–100),
// defined alpha handling (transparent pixels flatten onto white), quality
// effect on output size, error handling, and cancellation (issue #45).

using System;
using System.IO;
using System.Threading;
using Xunit;

namespace captcho.Capture.Tests;

public class JpegExportServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static ContiguousBitmap MakeOpaqueBitmap(int width, int height, byte fill = 0xFF)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = fill;
        return new ContiguousBitmap(width, height, stride, pixels);
    }

    /// <summary>
    /// Creates a bitmap whose pixels are fully transparent (alpha 0) with
    /// arbitrary color channels, exercising the JPEG alpha-flattening rule.
    /// </summary>
    private static ContiguousBitmap MakeTransparentBitmap(int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = y * stride + x * 4;
                pixels[offset + 0] = 0x00; // B
                pixels[offset + 1] = 0x00; // G
                pixels[offset + 2] = 0x00; // R
                pixels[offset + 3] = 0x00; // A — fully transparent
            }
        }
        return new ContiguousBitmap(width, height, stride, pixels);
    }

    private static string TempJpegPath() =>
        Path.Combine(Path.GetTempPath(), $"captcho-jpeg-{Guid.NewGuid():N}.jpg");

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── Happy path ───────────────────────────────────────────────────────

    [Fact]
    public void SaveAsJpeg_WritesValidJpegWithSignature()
    {
        var bitmap = MakeOpaqueBitmap(8, 8);
        string path = TempJpegPath();
        try
        {
            var result = JpegExportService.SaveAsJpeg(bitmap, path, 90);

            Assert.True(result.Success, $"Export failed: {result.Message}");
            Assert.True(File.Exists(path), "JPEG file was not created");

            // JPEG SOI marker.
            byte[] header = new byte[2];
            using (var fs = File.OpenRead(path))
                Assert.Equal(2, fs.Read(header, 0, 2));
            Assert.Equal(0xFF, header[0]);
            Assert.Equal(0xD8, header[1]);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SaveAsJpeg_DecodableWithCorrectDimensions()
    {
        int w = 32, h = 24;
        var bitmap = MakeOpaqueBitmap(w, h);
        string path = TempJpegPath();
        try
        {
            var result = JpegExportService.SaveAsJpeg(bitmap, path, 90);

            Assert.True(result.Success);
            Assert.Equal(w, result.Width);
            Assert.Equal(h, result.Height);

            using var img = System.Drawing.Image.FromFile(path);
            Assert.Equal(w, img.Width);
            Assert.Equal(h, img.Height);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void SaveAsJpeg_QualityBoundaries_InclusiveZeroThroughOneHundred(int quality)
    {
        var bitmap = MakeOpaqueBitmap(8, 8);
        string path = TempJpegPath();
        try
        {
            var result = JpegExportService.SaveAsJpeg(bitmap, path, quality);

            Assert.True(result.Success, $"Quality {quality} failed: {result.Message}");
            Assert.True(File.Exists(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void SaveAsJpeg_QualityOutOfRange_FailsValidation(int quality)
    {
        var bitmap = MakeOpaqueBitmap(4, 4);
        string path = TempJpegPath();
        try
        {
            var result = JpegExportService.SaveAsJpeg(bitmap, path, quality);

            Assert.False(result.Success);
            Assert.Equal(ExportPhase.Validation, result.Phase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SaveAsJpeg_HigherQuality_ProducesLargerBytesThanLowerQuality()
    {
        // A noise-filled image shows a clear quality/size relationship;
        // uniform fills compress to near-nothing at any quality.
        int w = 64, h = 64;
        var random = new Random(12345);
        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 0xFF; // opaque
        var bitmap = new ContiguousBitmap(w, h, stride, pixels);

        string lowPath = TempJpegPath();
        string highPath = TempJpegPath();
        try
        {
            var low = JpegExportService.SaveAsJpeg(bitmap, lowPath, 10);
            var high = JpegExportService.SaveAsJpeg(bitmap, highPath, 100);

            Assert.True(low.Success);
            Assert.True(high.Success);
            Assert.True(high.ByteCount > low.ByteCount,
                $"High-quality ({high.ByteCount}B) should exceed low-quality ({low.ByteCount}B)");
        }
        finally
        {
            Cleanup(lowPath);
            Cleanup(highPath);
        }
    }

    // ── Alpha handling ───────────────────────────────────────────────────

    [Fact]
    public void SaveAsJpeg_TransparentPixels_FlattenOntoWhite()
    {
        // JPEG has no alpha channel. Captcho's defined alpha handling: fully
        // transparent source pixels become white in the encoded output.
        var bitmap = MakeTransparentBitmap(8, 8);
        string path = TempJpegPath();
        try
        {
            var result = JpegExportService.SaveAsJpeg(bitmap, path, 100);

            Assert.True(result.Success, result.Message);

            using var gdi = new System.Drawing.Bitmap(path);
            var pixel = gdi.GetPixel(4, 4);
            // Quality 100 keeps sampled pixels very close to the flattened value.
            Assert.InRange(pixel.R, 250, 255);
            Assert.InRange(pixel.G, 250, 255);
            Assert.InRange(pixel.B, 250, 255);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SaveAsJpeg_OpaqueBitmap_DecodesWithoutAlpha()
    {
        var bitmap = MakeOpaqueBitmap(8, 8, 0x40);
        string path = TempJpegPath();
        try
        {
            var result = JpegExportService.SaveAsJpeg(bitmap, path, 90);

            Assert.True(result.Success);
            using var img = System.Drawing.Image.FromFile(path);
            Assert.Equal(System.Drawing.Imaging.PixelFormat.Format24bppRgb,
                ((System.Drawing.Bitmap)img).PixelFormat);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ── Validation / failure ─────────────────────────────────────────────

    [Fact]
    public void SaveAsJpeg_NullBitmap_FailsValidation()
    {
        var result = JpegExportService.SaveAsJpeg(null!, TempJpegPath(), 90);

        Assert.False(result.Success);
        Assert.Equal(ExportPhase.Validation, result.Phase);
    }

    [Fact]
    public void SaveAsJpeg_EmptyPath_FailsValidation()
    {
        var bitmap = MakeOpaqueBitmap(4, 4);

        var result = JpegExportService.SaveAsJpeg(bitmap, "", 90);

        Assert.False(result.Success);
        Assert.Equal(ExportPhase.Validation, result.Phase);
    }

    [Fact]
    public void SaveAsJpeg_CancelledToken_ReturnsCancelledAndWritesNoFile()
    {
        var bitmap = MakeOpaqueBitmap(4, 4);
        string path = TempJpegPath();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = JpegExportService.SaveAsJpeg(bitmap, path, 90, cts.Token);

            Assert.False(result.Success);
            Assert.Equal(ExportPhase.Cancelled, result.Phase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Cleanup(path);
        }
    }
}
