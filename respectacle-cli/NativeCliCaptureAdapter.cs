// NativeCliCaptureAdapter.cs — Production adapter over SafeCaptureResult, monitor resolver,
// BitmapBufferConverter, and PngExportService.
//
// This adapter is the bridge between the testable CliCaptureService and the real
// native capture/export APIs. It sanitizes errors, handles native timeouts,
// and resolves filename collisions.

using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Respectacle.Capture;

namespace Respectacle.Cli;

/// <summary>
/// Production adapter that calls real native capture, bitmap conversion, and PNG export APIs.
/// Implements ICliCaptureAdapter so CliCaptureService can use it in production.
/// </summary>
public sealed class NativeCliCaptureAdapter : ICliCaptureAdapter
{
    private readonly MonitorInterop? _interop;

    /// <summary>
    /// Creates a production adapter with real native capture and export.
    /// Optionally accepts a MonitorInterop for current-monitor resolution;
    /// if null, creates one via MonitorInterop.CreateNative().
    /// </summary>
    public NativeCliCaptureAdapter(MonitorInterop? interop = null)
    {
        _interop = interop;
    }

    public CaptureAdapterResult CaptureAllMonitors() =>
        WrapCapture(() => SafeCaptureResult.CaptureAllMonitors());

    public CaptureAdapterResult CaptureMonitorByIndex(uint index) =>
        WrapCapture(() => SafeCaptureResult.CaptureMonitorByIndex(index));

    public CaptureAdapterResult CaptureActiveWindow() =>
        WrapCapture(() => SafeCaptureResult.CaptureActiveWindow());

    public CaptureAdapterResult CaptureWindowUnderCursor() =>
        WrapCapture(() => SafeCaptureResult.CaptureWindowUnderCursor());

    public CaptureAdapterResult CaptureRegion(int x, int y, uint width, uint height) =>
        WrapCapture(() => SafeCaptureResult.CaptureRegion(x, y, width, height));

    public MonitorResolveResult ResolveCurrentMonitor()
    {
        var interop = _interop ?? MonitorInterop.CreateNative();
        return MonitorResolver.ResolveCurrentMonitor(interop);
    }

    public BitmapConversionResult ConvertToContiguousBitmap(byte[] pixels, int width, int height, int stride)
    {
        try
        {
            var bitmap = BitmapBufferConverter.StripPadding(pixels, width, height, stride);
            return BitmapConversionResult.Ok(bitmap);
        }
        catch (ArgumentException ex)
        {
            return BitmapConversionResult.Fail(SanitizeMessage(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BitmapConversionResult.Fail(SanitizeMessage(ex.Message));
        }
    }

    public ExportAdapterResult ExportPng(ContiguousBitmap bitmap, string outputPath)
    {
        // Resolve filename collision
        string resolvedPath = ExportFilenameTemplate.ResolveCollision(outputPath);

        var exportResult = PngExportService.SaveAsPng(bitmap, resolvedPath);

        if (exportResult.Success)
        {
            return ExportAdapterResult.Ok(
                exportResult.DestinationPath!,
                exportResult.Width!.Value,
                exportResult.Height!.Value,
                exportResult.ByteCount!.Value,
                exportResult.Elapsed);
        }

        return ExportAdapterResult.Fail(
            exportResult.Phase.ToString(),
            exportResult.Message,
            exportResult.Elapsed);
    }

    /// <summary>
    /// Wraps a native capture call, converting SafeCaptureResult to CaptureAdapterResult.
    /// Catches unexpected exceptions and returns them as capture failures.
    /// </summary>
    private static CaptureAdapterResult WrapCapture(Func<SafeCaptureResult> capture)
    {
        try
        {
            using var result = capture();

            if (!result.IsSuccess)
            {
                string status = result.Status.ToString();
                string? error = result.ErrorMessage;
                string? sanitized = error is not null ? SanitizeMessage(error) : null;

                return CaptureAdapterResult.Fail(status, sanitized);
            }

            return CaptureAdapterResult.Ok(
                result.Pixels!,
                (int)result.Width,
                (int)result.Height,
                (int)result.Stride);
        }
        catch (DllNotFoundException ex)
        {
            return CaptureAdapterResult.Fail("CaptureUnavailable",
                SanitizeMessage(ex.Message));
        }
        catch (EntryPointNotFoundException ex)
        {
            return CaptureAdapterResult.Fail("CaptureUnavailable",
                SanitizeMessage(ex.Message));
        }
        catch (Exception ex)
        {
            return CaptureAdapterResult.Fail("InternalError",
                SanitizeMessage(ex.Message));
        }
    }

    /// <summary>
    /// Sanitizes error messages for safe display — removes filesystem paths,
    /// native pointer values, and stack traces.
    /// </summary>
    private static string SanitizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return "Unknown error";

        // Remove filesystem paths
        var sanitized = Regex.Replace(message, @"[A-Z]:\\[^\s"")\]]*", "<path>");

        // Remove native pointer values
        sanitized = Regex.Replace(sanitized, @"0x[0-9A-Fa-f]+", "<ptr>");

        // Remove stack trace lines
        sanitized = Regex.Replace(sanitized, @"at\s+System\..*", "");
        sanitized = Regex.Replace(sanitized, @"at\s+Respectacle\..*", "");

        // Collapse whitespace
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown error" : sanitized;
    }
}
