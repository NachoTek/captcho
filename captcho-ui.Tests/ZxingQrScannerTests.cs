// ZxingQrScannerTests.cs — Real-decoder round-trip tests for the ZXing.Net
// adapter, mirroring the WindowsOcrEngineTests smoke shape but fully
// deterministic: QR test Frames are generated with ZXing's own QRCodeWriter
// (pure managed work — no WinRT, no camera, no fixtures on disk), then
// rendered into contiguous BGRA Frames and decoded back through the adapter.
//
// Covers one code, multiple codes, no code (solid color), and malformed
// input (zero dimensions / short buffer), plus the never-throw contract.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace captcho.UI.Tests;

public class ZxingQrScannerTests
{
    [Fact]
    public async Task ScanAsync_NullFrame_ThrowsArgumentNullException()
    {
        var scanner = new ZxingQrScanner();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => scanner.ScanAsync(null!));
    }

    [Fact]
    public async Task ScanAsync_OneCode_RoundTripsTheValue()
    {
        const string value = "https://captcho.example/one";
        var frame = QrTestFrameComposer.ComposeSingle(value);

        var result = await new ZxingQrScanner().ScanAsync(frame);

        Assert.Equal(QrOutcome.Found, result.Outcome);
        Assert.Contains(value, result.Values);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ScanAsync_TwoCodes_ReturnsEveryValueWithoutDropping()
    {
        var frame = QrTestFrameComposer.ComposeMultiple(
            "first-value",
            "second-value");

        var result = await new ZxingQrScanner().ScanAsync(frame);

        Assert.Equal(QrOutcome.Found, result.Outcome);
        Assert.Contains("first-value", result.Values);
        Assert.Contains("second-value", result.Values);
        Assert.Equal(2, result.Values.Count);
    }

    [Fact]
    public async Task ScanAsync_NoCode_SolidColorFrameIsNoCodeNotAnError()
    {
        var frame = SolidFrame(64, 64, 0x80, 0x40, 0xC0, 0xFF);

        var result = await new ZxingQrScanner().ScanAsync(frame);

        Assert.Equal(QrOutcome.NotFound, result.Outcome);
        Assert.Empty(result.Values);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ScanAsync_MalformedZeroDimension_FailsInsteadOfThrowing()
    {
        var zeroWidth = ExportTestHelpers.CreateTestBitmap(0, 4);

        var result = await new ZxingQrScanner().ScanAsync(zeroWidth);

        Assert.Equal(QrOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task ScanAsync_MalformedShortBuffer_FailsInsteadOfThrowing()
    {
        var frame = FrameWithShortBuffer(16, 16);

        var result = await new ZxingQrScanner().ScanAsync(frame);

        Assert.Equal(QrOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task ScanAsync_RandomNoiseFrameIsNoCodeOrFailureNeverThrows()
    {
        // Random noise is adversarial input for the detector: it must land
        // on no-code or a clean failure — never an exception.
        var frame = NoiseFrame(96, 96, seed: 48);

        var result = await new ZxingQrScanner().ScanAsync(frame);

        Assert.True(
            result.Outcome is QrOutcome.NotFound or QrOutcome.Failed,
            $"Expected a terminal outcome, got {result.Outcome}: {result.Error}");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a solid-color contiguous BGRA Frame of the given size.
    /// </summary>
    private static ContiguousBitmap SolidFrame(int width, int height, byte b, byte g, byte r, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }

        return CreateBitmap(width, height, pixels);
    }

    /// <summary>
    /// Builds a pseudo-random noise Frame — adversarial input with no
    /// structure. Deterministic per seed.
    /// </summary>
    private static ContiguousBitmap NoiseFrame(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF; // opaque
        }

        return CreateBitmap(width, height, pixels);
    }

    /// <summary>
    /// Builds a Frame whose pixel buffer is shorter than stride×height.
    /// </summary>
    private static ContiguousBitmap FrameWithShortBuffer(int width, int height)
    {
        var pixels = new byte[(width * height * 4) / 2];
        return CreateBitmap(width, height, pixels);
    }

    /// <summary>
    /// Creates a ContiguousBitmap via the internal constructor (same
    /// reflection route as ExportTestHelpers, kept local so malformed
    /// buffers can be composed for the failure tests).
    /// </summary>
    private static ContiguousBitmap CreateBitmap(int width, int height, byte[] pixels)
    {
        var constructor = typeof(ContiguousBitmap).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(int), typeof(int), typeof(int), typeof(byte[]) },
            null);

        Assert.NotNull(constructor);
        return (ContiguousBitmap)constructor.Invoke(new object[] { width, height, width * 4, pixels });
    }
}

/// <summary>
/// Composes deterministic QR test Frames with ZXing's own QRCodeWriter:
/// encodes each value into a BitMatrix, renders it into a BGRA Canvas, and
/// returns the contiguous Frame. Pure managed work — CI-deterministic with
/// no fixtures on disk.
/// </summary>
internal static class QrTestFrameComposer
{
    /// <summary>
    /// Renders a single centered QR code for the value on a white canvas.
    /// </summary>
    public static ContiguousBitmap ComposeSingle(string value)
    {
        var matrix = Encode(value, 21);
        return RenderSingle(matrix, scale: 8, padding: 32);
    }

    /// <summary>
    /// Renders multiple QR codes side by side on one white canvas, so a
    /// single scan must return every value.
    /// </summary>
    public static ContiguousBitmap ComposeMultiple(params string[] values)
    {
        var matrices = values.Select(v => Encode(v, 21)).ToArray();
        return RenderMultiple(matrices, scale: 8, padding: 32, gap: 32);
    }

    /// <summary>
    /// Encodes a value into a QR BitMatrix at the requested module size per
    /// side (ZXing picks the version that fits).
    /// </summary>
    private static BitMatrix Encode(string value, int size)
    {
        var writer = new QRCodeWriter();
        return writer.encode(
            value,
            BarcodeFormat.QR_CODE,
            size,
            size,
            new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = 0,
            });
    }

    /// <summary>
    /// Renders one matrix centered on a white BGRA canvas.
    /// </summary>
    private static ContiguousBitmap RenderSingle(BitMatrix matrix, int scale, int padding)
    {
        int codeWidth = matrix.Width * scale;
        int width = codeWidth + padding * 2;
        int height = matrix.Height * scale + padding * 2;

        var canvas = new byte[width * height * 4];
        FillWhite(canvas);

        int offsetY = padding;
        for (int y = 0; y < matrix.Height; y++)
        {
            int offsetX = padding;
            for (int x = 0; x < matrix.Width; x++)
            {
                if (matrix[x, y])
                    FillModule(canvas, width, offsetX, offsetY, scale, dark: true);
                offsetX += scale;
            }

            offsetY += scale;
        }

        return Build(width, height, canvas);
    }

    /// <summary>
    /// Renders several matrices left to right on one white BGRA canvas.
    /// </summary>
    private static ContiguousBitmap RenderMultiple(BitMatrix[] matrices, int scale, int padding, int gap)
    {
        int codeHeight = matrices.Max(m => m.Height) * scale;
        int totalCodeWidth = matrices.Sum(m => m.Width * scale);
        int width = totalCodeWidth + (matrices.Length - 1) * gap + padding * 2;
        int height = codeHeight + padding * 2;

        var canvas = new byte[width * height * 4];
        FillWhite(canvas);

        int offsetX = padding;
        foreach (var matrix in matrices)
        {
            int offsetY = padding;
            for (int y = 0; y < matrix.Height; y++)
            {
                int moduleX = offsetX;
                for (int x = 0; x < matrix.Width; x++)
                {
                    if (matrix[x, y])
                        FillModule(canvas, width, moduleX, offsetY, scale, dark: true);
                    moduleX += scale;
                }

                offsetY += scale;
            }

            offsetX += matrix.Width * scale + gap;
        }

        return Build(width, height, canvas);
    }

    private static void FillWhite(byte[] canvas)
    {
        for (int i = 0; i < canvas.Length; i += 4)
        {
            canvas[i] = 0xFF;     // B
            canvas[i + 1] = 0xFF; // G
            canvas[i + 2] = 0xFF; // R
            canvas[i + 3] = 0xFF; // A
        }
    }

    private static void FillModule(byte[] canvas, int canvasWidth, int offsetX, int offsetY, int scale, bool dark)
    {
        byte b = dark ? (byte)0x00 : (byte)0xFF;
        for (int dy = 0; dy < scale; dy++)
        {
            int rowStart = ((offsetY + dy) * canvasWidth + offsetX) * 4;
            for (int dx = 0; dx < scale; dx++)
            {
                int i = rowStart + dx * 4;
                canvas[i] = b;
                canvas[i + 1] = b;
                canvas[i + 2] = b;
                canvas[i + 3] = 0xFF;
            }
        }
    }

    private static ContiguousBitmap Build(int width, int height, byte[] pixels)
    {
        var constructor = typeof(ContiguousBitmap).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(int), typeof(int), typeof(int), typeof(byte[]) },
            null);

        Assert.NotNull(constructor);
        return (ContiguousBitmap)constructor.Invoke(new object[] { width, height, width * 4, pixels });
    }
}
