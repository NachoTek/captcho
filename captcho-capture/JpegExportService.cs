// JpegExportService.cs — Encodes ContiguousBitmap to JPEG and writes to disk.
// Returns ExportResult with diagnostics; never throws for expected failures.
//
// Alpha handling (issue #45): JPEG has no alpha channel. Transparent source
// pixels are flattened onto white before encoding, so the output is fully
// defined rather than rendering as black in viewers that ignore alpha.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace captcho.Capture;

/// <summary>
/// Encodes a ContiguousBitmap (BGRA) as JPEG and writes it to disk.
/// Uses System.Drawing for JPEG encoding — no WGC or UI dependency required.
/// All expected failures return ExportResult.Fail instead of throwing.
/// </summary>
public static class JpegExportService
{
    /// <summary>
    /// Saves a ContiguousBitmap as a JPEG file to the specified path using the
    /// supplied quality (inclusive 0–100). Fully transparent pixels flatten
    /// onto white. Creates the destination directory if it doesn't exist.
    /// Returns an ExportResult (never throws for expected failures).
    /// </summary>
    /// <param name="bitmap">Source bitmap to encode.</param>
    /// <param name="destinationPath">Full file path for the output JPEG.</param>
    /// <param name="quality">JPEG quality from 0 through 100 (inclusive).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>ExportResult with success/failure, diagnostics, and timing.</returns>
    public static ExportResult SaveAsJpeg(ContiguousBitmap bitmap, string destinationPath,
        int quality, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Validation phase
        if (bitmap == null)
            return ExportResult.Fail(ExportPhase.Validation, "No bitmap provided", sw.Elapsed);

        if (string.IsNullOrWhiteSpace(destinationPath))
            return ExportResult.Fail(ExportPhase.Validation, "No destination path provided", sw.Elapsed);

        if (quality < ExportSettings.MinimumJpegQuality || quality > ExportSettings.MaximumJpegQuality)
            return ExportResult.Fail(ExportPhase.Validation,
                $"JPEG quality must be from {ExportSettings.MinimumJpegQuality} through {ExportSettings.MaximumJpegQuality}",
                sw.Elapsed);

        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            return ExportResult.Fail(ExportPhase.Validation,
                $"Invalid dimensions: {bitmap.Width}x{bitmap.Height}", sw.Elapsed);

        int expectedLength = bitmap.Width * bitmap.Height * 4; // BGRA
        if (bitmap.Pixels == null || bitmap.Pixels.Length < expectedLength)
            return ExportResult.Fail(ExportPhase.Validation,
                $"Pixel buffer too small: {bitmap.Pixels?.Length ?? 0} bytes for {bitmap.Width}x{bitmap.Height} image",
                sw.Elapsed);

        if (cancellationToken.IsCancellationRequested)
            return ExportResult.Cancelled(sw.Elapsed);

        try
        {
            // Encode phase — create GDI+ bitmap from BGRA pixel data with
            // transparent pixels flattened onto white.
            using var gdiBitmap = CreateGdiBitmap(bitmap, flattenTransparentPixels: true);
            if (gdiBitmap == null)
                return ExportResult.Fail(ExportPhase.Encode, "Failed to create bitmap for encoding", sw.Elapsed);

            if (cancellationToken.IsCancellationRequested)
                return ExportResult.Cancelled(sw.Elapsed);

            // Write phase — ensure directory exists, then save JPEG
            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            long byteCount;
            using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                SaveJpegToStream(gdiBitmap, fs, quality);
                fs.Flush();
                byteCount = fs.Length;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(destinationPath);
                return ExportResult.Cancelled(sw.Elapsed);
            }

            sw.Stop();

            return ExportResult.Ok(destinationPath, bitmap.Width, bitmap.Height,
                byteCount, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            // Clean up partial file on cancellation
            TryDeleteFile(destinationPath);
            return ExportResult.Cancelled(sw.Elapsed);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ExportResult.Fail(ExportPhase.Write,
                SanitizePathError(ex.Message), sw.Elapsed);
        }
        catch (DirectoryNotFoundException ex)
        {
            return ExportResult.Fail(ExportPhase.Write,
                SanitizePathError(ex.Message), sw.Elapsed);
        }
        catch (PathTooLongException)
        {
            return ExportResult.Fail(ExportPhase.Write,
                "File path is too long", sw.Elapsed);
        }
        catch (IOException ex)
        {
            return ExportResult.Fail(ExportPhase.Write,
                SanitizePathError(ex.Message), sw.Elapsed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return ExportResult.Fail(ExportPhase.Write,
                "Invalid file path", sw.Elapsed);
        }
    }

    /// <summary>
    /// Encodes a ContiguousBitmap to a JPEG byte array in memory using the
    /// supplied quality. Returns null on failure (including out-of-range
    /// quality).
    /// </summary>
    public static byte[]? EncodeToJpegBytes(ContiguousBitmap bitmap, int quality)
    {
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Pixels == null)
            return null;

        if (quality < ExportSettings.MinimumJpegQuality || quality > ExportSettings.MaximumJpegQuality)
            return null;

        int expectedLength = bitmap.Width * bitmap.Height * 4;
        if (bitmap.Pixels.Length < expectedLength)
            return null;

        try
        {
            using var gdiBitmap = CreateGdiBitmap(bitmap, flattenTransparentPixels: true);
            if (gdiBitmap == null) return null;

            using var ms = new MemoryStream();
            SaveJpegToStream(gdiBitmap, ms, quality);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the GDI+ bitmap to the stream as a JPEG with the supplied
    /// quality using the JPEG encoder's quality parameter.
    /// </summary>
    private static void SaveJpegToStream(Bitmap gdiBitmap, Stream stream, int quality)
    {
        var codec = GetJpegCodec()
            ?? throw new InvalidOperationException("JPEG encoder unavailable");

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        gdiBitmap.Save(stream, codec, parameters);
    }

    private static ImageCodecInfo? GetJpegCodec() =>
        Array.Find(ImageCodecInfo.GetImageEncoders(),
            codec => string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Creates a System.Drawing.Bitmap from BGRA ContiguousBitmap pixel data.
    /// When <paramref name="flattenTransparentPixels"/> is set, the alpha
    /// channel is used to composite each pixel onto white — JPEG has no alpha,
    /// so this keeps the encoded output defined instead of letting transparent
    /// pixels render as black in viewers that ignore alpha.
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
    private static string SanitizePathError(string message)
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

    private static void TryDeleteFile(string path)
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
