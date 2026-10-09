// PngExportService.cs — Encodes ContiguousBitmap to PNG and writes to disk.
// Returns ExportResult with diagnostics; never throws for expected failures.
// Shared GDI+ conversion and I/O diagnostics live in GdiExportHelpers.

using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace captcho.Capture;

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
            using var gdiBitmap = GdiExportHelpers.CreateGdiBitmap(bitmap, flattenTransparentPixels: false);
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
                GdiExportHelpers.TryDeleteFile(destinationPath);
                return ExportResult.Cancelled(sw.Elapsed);
            }

            sw.Stop();

            return ExportResult.Ok(destinationPath, bitmap.Width, bitmap.Height,
                byteCount, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            // Clean up partial file on cancellation
            GdiExportHelpers.TryDeleteFile(destinationPath);
            return ExportResult.Cancelled(sw.Elapsed);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ExportResult.Fail(ExportPhase.Write,
                GdiExportHelpers.SanitizePathError(ex.Message), sw.Elapsed);
        }
        catch (DirectoryNotFoundException ex)
        {
            return ExportResult.Fail(ExportPhase.Write,
                GdiExportHelpers.SanitizePathError(ex.Message), sw.Elapsed);
        }
        catch (PathTooLongException)
        {
            return ExportResult.Fail(ExportPhase.Write,
                "File path is too long", sw.Elapsed);
        }
        catch (IOException ex)
        {
            return ExportResult.Fail(ExportPhase.Write,
                GdiExportHelpers.SanitizePathError(ex.Message), sw.Elapsed);
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
            using var gdiBitmap = GdiExportHelpers.CreateGdiBitmap(bitmap, flattenTransparentPixels: false);
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
}
