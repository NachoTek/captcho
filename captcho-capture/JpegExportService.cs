// JpegExportService.cs — Encodes ContiguousBitmap to JPEG and writes to disk.
// Returns ExportResult with diagnostics; never throws for expected failures.
// Shared GDI+ conversion and I/O diagnostics live in GdiExportHelpers.
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

        // Encode phase — resolve the JPEG codec up front; a missing codec is
        // a reportable encode failure, not an exception mid-write.
        var codec = GetJpegCodec();
        if (codec is null)
            return ExportResult.Fail(ExportPhase.Encode, "JPEG encoder unavailable", sw.Elapsed);

        try
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

            // Create GDI+ bitmap from BGRA pixel data with transparent pixels
            // flattened onto white.
            using var gdiBitmap = GdiExportHelpers.CreateGdiBitmap(bitmap, flattenTransparentPixels: true);
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
                gdiBitmap.Save(fs, codec, parameters);
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

    private static ImageCodecInfo? GetJpegCodec() =>
        Array.Find(ImageCodecInfo.GetImageEncoders(),
            codec => string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
}
