// PngExportService.cs — Encodes ContiguousBitmap to PNG and writes to disk.
// Returns ExportResult with diagnostics; never throws for expected failures.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Drawing;
using System.Drawing.Imaging;

namespace Respectacle.Capture;

/// <summary>
/// Encodes a ContiguousBitmap (BGRA) as PNG and writes it to disk.
/// Uses System.Drawing for PNG encoding — no WGC or UI dependency required.
/// All expected failures return ExportResult.Fail instead of throwing.
/// </summary>
public static class PngExportService
{
    /// <summary>
    /// Saves a ContiguousBitmap as a PNG file to the specified path.
    /// Creates the destination directory if it doesn't exist.
    /// Returns an ExportResult (never throws for expected failures).
    /// </summary>
    /// <param name="bitmap">Source bitmap to encode.</param>
    /// <param name="destinationPath">Full file path for the output PNG.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>ExportResult with success/failure, diagnostics, and timing.</returns>
    public static ExportResult SaveAsPng(ContiguousBitmap bitmap, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Validation phase
        if (bitmap == null)
            return ExportResult.Fail(ExportPhase.Validation, "No bitmap provided", sw.Elapsed);

        if (string.IsNullOrWhiteSpace(destinationPath))
            return ExportResult.Fail(ExportPhase.Validation, "No destination path provided", sw.Elapsed);

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
            // Encode phase — create GDI+ bitmap from BGRA pixel data
            using var gdiBitmap = CreateGdiBitmap(bitmap);
            if (gdiBitmap == null)
                return ExportResult.Fail(ExportPhase.Encode, "Failed to create bitmap for encoding", sw.Elapsed);

            if (cancellationToken.IsCancellationRequested)
                return ExportResult.Cancelled(sw.Elapsed);

            // Write phase — ensure directory exists, then save PNG
            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            long byteCount;
            using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                gdiBitmap.Save(fs, ImageFormat.Png);
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
    /// Encodes a ContiguousBitmap to a PNG byte array in memory.
    /// Returns null on failure. Useful for clipboard copy operations.
    /// </summary>
    public static byte[]? EncodeToPngBytes(ContiguousBitmap bitmap)
    {
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Pixels == null)
            return null;

        int expectedLength = bitmap.Width * bitmap.Height * 4;
        if (bitmap.Pixels.Length < expectedLength)
            return null;

        try
        {
            using var gdiBitmap = CreateGdiBitmap(bitmap);
            if (gdiBitmap == null) return null;

            using var ms = new MemoryStream();
            gdiBitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a System.Drawing.Bitmap from BGRA ContiguousBitmap pixel data.
    /// </summary>
    internal static Bitmap? CreateGdiBitmap(ContiguousBitmap bitmap)
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

                            // BGRA in ContiguousBitmap -> BGRA in GDI+ (same format for 32bppArgb)
                            for (int x = 0; x < rowBytes; x++)
                            {
                                dstRow[x] = srcRow[x];
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
