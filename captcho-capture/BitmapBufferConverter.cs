// BitmapBufferConverter.cs — Converts BGRA pixel buffers with stride padding
// into contiguous BGRA data suitable for WinRT SoftwareBitmap or direct rendering.
//
// This is a UI-agnostic converter: it strips per-row padding and returns
// validated dimensions with contiguous pixel data. The WinUI 3 project will
// use this to prepare data for SoftwareBitmap.CreateCopyFromBuffer.

using System;

namespace captcho.Capture;

/// <summary>
/// Result of converting a padded BGRA buffer to contiguous form.
/// Contains only the pixel data, dimensions, and the contiguous stride (width * 4).
/// </summary>
public sealed class ContiguousBitmap
{
    /// <summary>Pixel width.</summary>
    public int Width { get; }
    /// <summary>Pixel height.</summary>
    public int Height { get; }
    /// <summary>Contiguous row stride in bytes (always width * 4).</summary>
    public int Stride { get; }
    /// <summary>Contiguous BGRA pixel data (no per-row padding).</summary>
    public byte[] Pixels { get; }

    internal ContiguousBitmap(int width, int height, int stride, byte[] pixels)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }
}

/// <summary>
/// UI-agnostic converter that strips stride padding from BGRA buffers.
/// Input: raw buffer from SafeCaptureResult (may have stride > width*4).
/// Output: ContiguousBitmap with stride == width*4.
/// </summary>
public static class BitmapBufferConverter
{
    /// <summary>
    /// Strips per-row stride padding from a BGRA pixel buffer, producing contiguous data.
    /// If stride equals width*4, copies as-is. Otherwise, strips padding per row.
    /// </summary>
    /// <param name="pixels">Raw BGRA pixel buffer from native capture.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="stride">Row stride in bytes (may exceed width * 4).</param>
    /// <returns>Contiguous BGRA data with stride == width * 4.</returns>
    /// <exception cref="ArgumentNullException">pixels is null.</exception>
    /// <exception cref="ArgumentException">Invalid dimensions, stride, or buffer size.</exception>
    public static ContiguousBitmap StripPadding(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        if (width <= 0)
            throw new ArgumentException($"Width must be greater than zero, got {width}.", nameof(width));
        if (height <= 0)
            throw new ArgumentException($"Height must be greater than zero, got {height}.", nameof(height));

        int minStride = width * 4; // BGRA = 4 bytes per pixel
        if (stride < minStride)
            throw new ArgumentException(
                $"Stride ({stride}) must be at least {minStride} (width * 4 bytes per pixel).",
                nameof(stride));

        long expectedLen = (long)stride * height;
        if (pixels.Length < expectedLen)
            throw new ArgumentException(
                $"Pixel buffer length ({pixels.Length}) is less than stride*height ({expectedLen}).",
                nameof(pixels));

        byte[] bitmapPixels;
        if (stride == minStride)
        {
            // No padding — copy as-is
            int copyLen = minStride * height;
            bitmapPixels = new byte[copyLen];
            Array.Copy(pixels, bitmapPixels, copyLen);
        }
        else
        {
            // Strip per-row padding to produce contiguous BGRA data
            int contiguousLen = minStride * height;
            bitmapPixels = new byte[contiguousLen];
            int srcOffset = 0;
            int dstOffset = 0;
            for (int y = 0; y < height; y++)
            {
                Array.Copy(pixels, srcOffset, bitmapPixels, dstOffset, minStride);
                srcOffset += stride;
                dstOffset += minStride;
            }
        }

        return new ContiguousBitmap(width, height, minStride, bitmapPixels);
    }
}
