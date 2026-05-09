// SoftwareBitmapConverter.cs — Converts ContiguousBitmap (contiguous BGRA bytes)
// into a WriteableBitmap suitable for XAML Image.Source assignment.
//
// Uses WriteableBitmap directly since it can be assigned to Image.Source
// and exposes a writable pixel buffer via PixelBuffer.AsStream().
// This avoids COM IBufferByteAccess casting issues and SoftwareBitmap API
// availability differences across WinRT runtimes.

using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Converts a ContiguousBitmap to a WriteableBitmap for WinUI Image display.
/// Validates buffer dimensions before creating the bitmap.
/// </summary>
public static class SoftwareBitmapConverter
{
    /// <summary>
    /// Converts contiguous BGRA pixel data into a WriteableBitmap
    /// that can be assigned to an XAML Image element's Source property.
    /// </summary>
    /// <param name="bitmap">Contiguous BGRA bitmap from BitmapBufferConverter.</param>
    /// <returns>A WriteableBitmap ready for XAML display.</returns>
    /// <exception cref="ArgumentNullException">bitmap is null.</exception>
    /// <exception cref="ArgumentException">Invalid dimensions or buffer size.</exception>
    public static async Task<WriteableBitmap> ToWriteableBitmapAsync(ContiguousBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (bitmap.Width <= 0)
            throw new ArgumentException($"Width must be positive, got {bitmap.Width}.", nameof(bitmap));
        if (bitmap.Height <= 0)
            throw new ArgumentException($"Height must be positive, got {bitmap.Height}.", nameof(bitmap));

        int expectedLen = bitmap.Stride * bitmap.Height;
        if (bitmap.Pixels.Length < expectedLen)
            throw new ArgumentException(
                $"Pixel buffer ({bitmap.Pixels.Length} bytes) is shorter than stride×height ({expectedLen}).",
                nameof(bitmap));

        var writeableBitmap = new WriteableBitmap(bitmap.Width, bitmap.Height);

        // Write pixel data into the WriteableBitmap's pixel buffer via stream
        using (var stream = writeableBitmap.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(bitmap.Pixels, 0, bitmap.Pixels.Length);
        }

        return writeableBitmap;
    }
}
