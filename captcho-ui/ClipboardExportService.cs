// ClipboardExportService.cs — Testable clipboard boundary for copying PNG data.
//
// Defines IClipboardAdapter so headless tests can fake the clipboard,
// while the real WindowsClipboardAdapter wraps DataPackage for UAT.
// Export methods return structured ExportResult — never throw for expected failures.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Abstraction over platform clipboard so export paths can be tested without WinRT.
/// </summary>
public interface IClipboardAdapter
{
    /// <summary>
    /// Sets the clipboard content to a PNG image from the given byte array.
    /// Returns true on success, false on failure.
    /// </summary>
    bool SetPngImage(byte[] pngBytes);
}

/// <summary>
/// Result of a clipboard copy operation, reusing ExportResult-style diagnostics.
/// </summary>
public sealed class ClipboardExportResult
{
    public bool Success { get; }
    public string Message { get; }
    public TimeSpan Elapsed { get; }
    public int? Width { get; }
    public int? Height { get; }
    public long? ByteCount { get; }

    private ClipboardExportResult(bool success, string message, TimeSpan elapsed,
        int? width, int? height, long? byteCount)
    {
        Success = success;
        Message = message;
        Elapsed = elapsed;
        Width = width;
        Height = height;
        ByteCount = byteCount;
    }

    public static ClipboardExportResult Ok(int width, int height, long byteCount, TimeSpan elapsed)
    {
        return new ClipboardExportResult(true, "Copied to clipboard", elapsed, width, height, byteCount);
    }

    public static ClipboardExportResult Fail(string sanitizedMessage, TimeSpan elapsed)
    {
        return new ClipboardExportResult(false, sanitizedMessage, elapsed, null, null, null);
    }
}

/// <summary>
/// Default clipboard adapter that does nothing (placeholder for environments without clipboard).
/// Production code should inject WindowsClipboardAdapter or a platform-specific implementation.
/// </summary>
public sealed class NullClipboardAdapter : IClipboardAdapter
{
    public bool SetPngImage(byte[] pngBytes) => false;
}

/// <summary>
/// Handles clipboard copy of the latest captured bitmap via an injectable IClipboardAdapter.
/// Isolated from CapturePreviewService so both can be tested independently.
/// </summary>
public sealed class ClipboardExportService
{
    private readonly IClipboardAdapter _clipboard;
    private readonly Func<ContiguousBitmap?> _lastCaptureProvider;

    /// <summary>
    /// Creates a ClipboardExportService with the given clipboard adapter and capture provider.
    /// </summary>
    /// <param name="clipboard">Platform clipboard adapter.</param>
    /// <param name="lastCaptureProvider">Function that returns the current cached bitmap (or null).</param>
    public ClipboardExportService(IClipboardAdapter clipboard, Func<ContiguousBitmap?> lastCaptureProvider)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _lastCaptureProvider = lastCaptureProvider ?? throw new ArgumentNullException(nameof(lastCaptureProvider));
    }

    /// <summary>
    /// Encodes the last captured bitmap to PNG and copies it to the clipboard.
    /// Returns a structured result — never throws for expected failures.
    /// </summary>
    public ClipboardExportResult CopyToClipboard()
    {
        var sw = Stopwatch.StartNew();

        var bitmap = _lastCaptureProvider();
        if (bitmap == null)
            return ClipboardExportResult.Fail("No capture to copy", sw.Elapsed);

        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            return ClipboardExportResult.Fail(
                $"Invalid capture dimensions: {bitmap.Width}x{bitmap.Height}", sw.Elapsed);

        byte[]? pngBytes = PngExportService.EncodeToPngBytes(bitmap);
        if (pngBytes == null || pngBytes.Length == 0)
            return ClipboardExportResult.Fail("Failed to encode image for clipboard", sw.Elapsed);

        bool ok = _clipboard.SetPngImage(pngBytes);
        sw.Stop();

        if (!ok)
            return ClipboardExportResult.Fail("Failed to set clipboard content", sw.Elapsed);

        return ClipboardExportResult.Ok(bitmap.Width, bitmap.Height, pngBytes.Length, sw.Elapsed);
    }
}
