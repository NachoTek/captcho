// BitmapBufferConverterTests — validates stride-padding stripping,
// contiguous BGRA conversion, and negative metadata cases.

using System;
using Xunit;

namespace captcho.Capture.Tests;

public class BitmapBufferConverterTests
{
    // ── No-Padding (contiguous) Cases ────────────────────────────────────

    [Fact]
    public void StripPadding_NoPadding_ReturnsSameBuffer()
    {
        int width = 64, height = 48, stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i & 0xFF);

        var result = BitmapBufferConverter.StripPadding(pixels, width, height, stride);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(width * 4, result.Stride);
        Assert.Equal(width * 4 * height, result.Pixels.Length);
    }

    [Fact]
    public void StripPadding_1x1_NoPadding()
    {
        byte[] pixels = { 0xFF, 0x00, 0x00, 0xFF }; // BGRA
        var result = BitmapBufferConverter.StripPadding(pixels, 1, 1, 4);

        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Equal(0xFF, result.Pixels[0]);
        Assert.Equal(0x00, result.Pixels[1]);
        Assert.Equal(0x00, result.Pixels[2]);
        Assert.Equal(0xFF, result.Pixels[3]);
    }

    // ── Stride Padding Cases ─────────────────────────────────────────────

    [Fact]
    public void StripPadding_WithPadding_StripsCorrectly()
    {
        // width=2 (8 bytes per row), stride=12 (4 bytes padding per row), height=2
        int width = 2, height = 2, stride = 12;
        byte[] pixels = new byte[stride * height];

        // Row 0: pixel(0,0) = [0x10,0x20,0x30,0xFF], pixel(1,0) = [0x40,0x50,0x60,0xFF], pad = [0xAA,0xBB,0xCC,0xDD]
        pixels[0] = 0x10; pixels[1] = 0x20; pixels[2] = 0x30; pixels[3] = 0xFF;
        pixels[4] = 0x40; pixels[5] = 0x50; pixels[6] = 0x60; pixels[7] = 0xFF;
        pixels[8] = 0xAA; pixels[9] = 0xBB; pixels[10] = 0xCC; pixels[11] = 0xDD;

        // Row 1: pixel(0,1) = [0x70,0x80,0x90,0xFF], pixel(1,1) = [0xA0,0xB0,0xC0,0xFF], pad = [0xEE,0xFF,0x11,0x22]
        pixels[12] = 0x70; pixels[13] = 0x80; pixels[14] = 0x90; pixels[15] = 0xFF;
        pixels[16] = 0xA0; pixels[17] = 0xB0; pixels[18] = 0xC0; pixels[19] = 0xFF;
        pixels[20] = 0xEE; pixels[21] = 0xFF; pixels[22] = 0x11; pixels[23] = 0x22;

        var result = BitmapBufferConverter.StripPadding(pixels, width, height, stride);

        Assert.Equal(16, result.Pixels.Length);
        // Verify pixel data without padding
        Assert.Equal(0x10, result.Pixels[0]);
        Assert.Equal(0x20, result.Pixels[1]);
        Assert.Equal(0x30, result.Pixels[2]);
        Assert.Equal(0xFF, result.Pixels[3]);
        Assert.Equal(0x40, result.Pixels[4]);
        Assert.Equal(0x50, result.Pixels[5]);
        Assert.Equal(0x60, result.Pixels[6]);
        Assert.Equal(0xFF, result.Pixels[7]);
        Assert.Equal(0x70, result.Pixels[8]);
        Assert.Equal(0x80, result.Pixels[9]);
        Assert.Equal(0x90, result.Pixels[10]);
        Assert.Equal(0xFF, result.Pixels[11]);
        Assert.Equal(0xA0, result.Pixels[12]);
        Assert.Equal(0xB0, result.Pixels[13]);
        Assert.Equal(0xC0, result.Pixels[14]);
        Assert.Equal(0xFF, result.Pixels[15]);
    }

    [Fact]
    public void StripPadding_WithPadding_OutputStrideIsWidthTimes4()
    {
        int width = 100, height = 50, stride = 420;
        byte[] pixels = new byte[stride * height];
        var result = BitmapBufferConverter.StripPadding(pixels, width, height, stride);

        Assert.Equal(100, result.Width);
        Assert.Equal(50, result.Height);
        Assert.Equal(100 * 4, result.Stride);
        Assert.Equal(100 * 4 * 50, result.Pixels.Length);
    }

    // ── Negative / Boundary Cases ────────────────────────────────────────

    [Fact]
    public void StripPadding_NullPixels_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BitmapBufferConverter.StripPadding(null!, 100, 100, 400));
    }

    [Fact]
    public void StripPadding_ZeroWidth_Throws()
    {
        byte[] pixels = new byte[400];
        Assert.Throws<ArgumentException>(() =>
            BitmapBufferConverter.StripPadding(pixels, 0, 100, 0));
    }

    [Fact]
    public void StripPadding_ZeroHeight_Throws()
    {
        byte[] pixels = new byte[400];
        Assert.Throws<ArgumentException>(() =>
            BitmapBufferConverter.StripPadding(pixels, 100, 0, 400));
    }

    [Fact]
    public void StripPadding_StrideTooSmall_Throws()
    {
        byte[] pixels = new byte[40000];
        Assert.Throws<ArgumentException>(() =>
            BitmapBufferConverter.StripPadding(pixels, 100, 50, 100));
    }

    [Fact]
    public void StripPadding_BufferTooSmall_Throws()
    {
        byte[] pixels = new byte[100];
        Assert.Throws<ArgumentException>(() =>
            BitmapBufferConverter.StripPadding(pixels, 100, 50, 400));
    }

    // ── Overflow guard ───────────────────────────────────────────────────

    [Fact]
    public void StripPadding_LargeDimensions_DoesNotOverflow()
    {
        // Use realistic but large values that fit in int
        int width = 3840;  // 4K width
        int height = 2160; // 4K height
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        var result = BitmapBufferConverter.StripPadding(pixels, width, height, stride);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(stride * height, result.Pixels.Length);
    }
}
