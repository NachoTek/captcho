// GdiExportHelpers.cs — Shared GDI+ conversion and I/O diagnostics for the
// PNG and JPEG export services (issue #45).
//
// Both encoders build a System.Drawing Bitmap from the same BGRA
// ContiguousBitmap layout and surface the same sanitized write failures, so
// the conversion (with its optional JPEG alpha flattening) and the
// message/file cleanup helpers live here once instead of being duplicated
// per format.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace captcho.Capture;

/// <summary>
/// GDI+ conversion and shared diagnostics for the file export services.
/// Internal: only the export services in this assembly consume it.
/// </summary>
internal static class GdiExportHelpers
{
    /// <summary>
    /// Creates a System.Drawing.Bitmap from BGRA ContiguousBitmap pixel data.
    /// When <paramref name="flattenTransparentPixels"/> is set, the alpha
    /// channel is used to composite each pixel onto white — JPEG has no
    /// alpha, so this keeps the encoded output defined instead of letting
    /// transparent pixels render as black in viewers that ignore alpha.
    /// Returns null on conversion failure; never throws.
    /// </summary>
    internal static Bitmap? CreateGdiBitmap(ContiguousBitmap bitmap, bool flattenTransparentPixels)
    {
        try
        {
            // Create a Bitmap with 32bpp BGRA pixel format
            var gdiBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);

            // Lock bits for fast pixel copy
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bmpData = gdiBitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                // Copy row by row to handle stride differences
                int srcStride = bitmap.Stride; // width * 4 from ContiguousBitmap
                int dstStride = bmpData.Stride;
                int rowBytes = bitmap.Width * 4;

                unsafe
                {
                    byte* dstPtr = (byte*)bmpData.Scan0;
                    fixed (byte* srcPtr = bitmap.Pixels)
                    {
                        for (int y = 0; y < bitmap.Height; y++)
                        {
                            byte* srcRow = srcPtr + (y * srcStride);
                            byte* dstRow = dstPtr + (y * dstStride);

                            if (flattenTransparentPixels)
                            {
                                // Composite each BGRA pixel onto opaque white:
                                // out = src * alpha + white * (1 - alpha). The
                                // result carries alpha 255, which GDI+ renders
                                // identically when the JPEG encoder drops the
                                // channel.
                                for (int x = 0; x < rowBytes; x += 4)
                                {
                                    byte alpha = srcRow[x + 3];
                                    if (alpha == 255)
                                    {
                                        dstRow[x] = srcRow[x];
                                        dstRow[x + 1] = srcRow[x + 1];
                                        dstRow[x + 2] = srcRow[x + 2];
                                        dstRow[x + 3] = 255;
                                    }
                                    else if (alpha == 0)
                                    {
                                        dstRow[x] = 255;
                                        dstRow[x + 1] = 255;
                                        dstRow[x + 2] = 255;
                                        dstRow[x + 3] = 255;
                                    }
                                    else
                                    {
                                        dstRow[x] = CompositeColorChannel(srcRow[x], alpha);
                                        dstRow[x + 1] = CompositeColorChannel(srcRow[x + 1], alpha);
                                        dstRow[x + 2] = CompositeColorChannel(srcRow[x + 2], alpha);
                                        dstRow[x + 3] = 255;
                                    }
                                }
                            }
                            else
                            {
                                // BGRA in ContiguousBitmap -> BGRA in GDI+ (same format for 32bppArgb)
                                for (int x = 0; x < rowBytes; x++)
                                {
                                    dstRow[x] = srcRow[x];
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                gdiBitmap.UnlockBits(bmpData);
            }

            return gdiBitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Composites one color channel onto opaque white: src*a + 255*(1-a).</summary>
    private static byte CompositeColorChannel(byte source, byte alpha)
    {
        // Rounded half-up to keep sampled pixels closest to the exact value.
        return (byte)((source * alpha + 255 * (255 - alpha) + 127) / 255);
    }

    /// <summary>
    /// Sanitizes filesystem error messages — removes full paths, keeps the gist.
    /// </summary>
    internal static string SanitizePathError(string message)
    {
        if (string.IsNullOrEmpty(message)) return "File write failed";

        // Remove any full filesystem paths from the message
        // Match drive letters and common path patterns
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"[A-Z]:\\[^\s""]*",
            "<path>");

        // If message became empty after sanitization, return a generic one
        if (string.IsNullOrWhiteSpace(sanitized))
            return "File write failed";

        return sanitized;
    }

    /// <summary>
    /// Attempts to delete a file, ignoring failures. Best-effort cleanup for
    /// cancelled writes — never masks the reported outcome.
    /// </summary>
    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort — don't mask the cancellation
        }
    }
}
