// captcho Capture Tester — console runner for the Rust capture DLL.
//
// Usage:
//   dotnet run --project cs-tester [--capture] [--capture-full] [--capture-monitor <index>]
//                                    [--capture-active-window] [--capture-window-under-cursor]
//                                    [--capture-window-handle <hwnd>]
//                                    [--verify] [--verify-s02] [--verify-s03] [--help]
//
// Options:
//   --capture              Attempt a single primary-monitor frame capture.
//   --capture-full         Capture all monitors (full virtual desktop).
//   --capture-monitor <i>  Capture monitor at 0-based index.
//   --verify               S01 verify: capture primary + invariant checks.
//   --verify-s02           S02 verify: capture full desktop + monitor[0] + diagnostics.
//   --help                 Show usage message.

using System;
using System.Diagnostics;
using captcho.Capture;

namespace captcho.Tester;

public class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0] == "--help"))
        {
            PrintUsage();
            return 0;
        }

        var cmd = args[0];

        if (cmd == "--capture" && args.Length == 1)
            return RunCapture();

        if (cmd == "--capture-full" && args.Length == 1)
            return RunCaptureFullDesktop();

        if (cmd == "--capture-monitor" && args.Length == 2)
            return RunCaptureMonitor(args[1]);

        if (cmd == "--verify" && args.Length == 1)
            return RunVerify();

        if (cmd == "--verify-s02" && args.Length == 1)
            return RunVerifyS02();

        if (cmd == "--capture-active-window" && args.Length == 1)
            return RunCaptureActiveWindow();

        if (cmd == "--capture-window-under-cursor" && args.Length == 1)
            return RunCaptureWindowUnderCursor();

        if (cmd == "--capture-window-handle" && args.Length == 2)
            return RunCaptureWindowByHandle(args[1]);

        if (cmd == "--verify-s03" && args.Length == 1)
            return RunVerifyS03();

        Console.Error.WriteLine($"Unknown arguments: {string.Join(" ", args)}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("captcho Capture Tester");
        Console.WriteLine();
        Console.WriteLine("Usage: captcho.tester [options]");
        Console.WriteLine();
        Console.WriteLine("  --capture                        Capture primary monitor, print metadata.");
        Console.WriteLine("  --capture-full                   Capture full desktop (all monitors stitched).");
        Console.WriteLine("  --capture-monitor <i>            Capture monitor at 0-based index.");
        Console.WriteLine("  --capture-active-window          Capture the currently active (foreground) window.");
        Console.WriteLine("  --capture-window-under-cursor    Capture the top-level window under the cursor.");
        Console.WriteLine("  --capture-window-handle <hwnd>   Capture window by HWND handle (decimal or 0x hex).");
        Console.WriteLine("  --verify                         S01: capture primary + invariant checks.");
        Console.WriteLine("  --verify-s02                     S02: capture full desktop + monitor[0] + diagnostics.");
        Console.WriteLine("  --verify-s03                     S03: capture active window + window-under-cursor + diagnostics.");
        Console.WriteLine("  --help                           Show this help message.");
    }

    // ── S01 Commands ─────────────────────────────────────────────────────

    private static int RunCapture()
    {
        var diag = CaptureDiagnostics.Measure("primary", () => SafeCaptureResult.CapturePrimary());

        if (diag.Status != CaptureStatus.Ok.ToString())
        {
            Console.WriteLine($"Captured: FAIL status={diag.Status}");
            if (diag.Error != null) Console.WriteLine($"Error: {diag.Error}");
            Console.WriteLine($"RoundTripMs: {diag.RoundTripMs}");
            return 2;
        }

        // Convert to contiguous bitmap
        var sw = Stopwatch.StartNew();
        var bitmap = BitmapBufferConverter.StripPadding(
            diag.Status == "Ok" ? new byte[0] : new byte[0], // placeholder — we need pixels
            diag.Width, diag.Height, diag.Stride);
        sw.Stop();

        // Actually we need the raw SafeCaptureResult for pixels. Let's restructure.
        // The Measure function already disposed the result, so let's do it manually:
        return RunCaptureManual();
    }

    private static int RunCaptureManual()
    {
        var totalSw = Stopwatch.StartNew();

        // Phase 1: Native capture
        using var safe = SafeCaptureResult.CapturePrimary();

        if (!safe.IsSuccess)
        {
            totalSw.Stop();
            Console.WriteLine($"Captured: FAIL status={safe.Status}");
            if (safe.ErrorMessage != null) Console.WriteLine($"Error: {safe.ErrorMessage}");
            Console.WriteLine($"RoundTripMs: {totalSw.Elapsed.TotalMilliseconds:F1}");
            return 2;
        }

        // Phase 2: Bitmap conversion
        var bitmapSw = Stopwatch.StartNew();
        var bitmap = BitmapBufferConverter.StripPadding(
            safe.Pixels!, (int)safe.Width, (int)safe.Height, (int)safe.Stride);
        bitmapSw.Stop();
        totalSw.Stop();

        // Phase 3: Print structured output
        Console.WriteLine($"Captured: OK {safe.Width}x{safe.Height} stride={safe.Stride} data_len={safe.DataLen}");
        Console.WriteLine($"ContiguousBitmap: {bitmap.Width}x{bitmap.Height} stride={bitmap.Stride}");
        Console.WriteLine($"RoundTripMs: {totalSw.Elapsed.TotalMilliseconds:F1} (bitmap={bitmapSw.Elapsed.TotalMilliseconds:F1})");

        return 0;
    }

    // ── S02 Commands ─────────────────────────────────────────────────────

    private static int RunCaptureFullDesktop()
    {
        var totalSw = Stopwatch.StartNew();

        using var safe = SafeCaptureResult.CaptureAllMonitors();

        if (!safe.IsSuccess)
        {
            totalSw.Stop();
            PrintDiagnosticLine("full_desktop", safe, totalSw.Elapsed.TotalMilliseconds);
            return 2;
        }

        var bitmapSw = Stopwatch.StartNew();
        var bitmap = BitmapBufferConverter.StripPadding(
            safe.Pixels!, (int)safe.Width, (int)safe.Height, (int)safe.Stride);
        bitmapSw.Stop();
        totalSw.Stop();

        PrintCaptureSuccess("full_desktop", safe, bitmap, totalSw.Elapsed.TotalMilliseconds, bitmapSw.Elapsed.TotalMilliseconds);
        return 0;
    }

    private static int RunCaptureMonitor(string indexArg)
    {
        if (!uint.TryParse(indexArg, out uint index))
        {
            Console.Error.WriteLine($"Error: Monitor index must be a non-negative integer, got '{indexArg}'.");
            return 1;
        }

        var totalSw = Stopwatch.StartNew();

        using var safe = SafeCaptureResult.CaptureMonitorByIndex(index);

        if (!safe.IsSuccess)
        {
            totalSw.Stop();
            PrintDiagnosticLine($"monitor[{index}]", safe, totalSw.Elapsed.TotalMilliseconds);
            return 2;
        }

        var bitmapSw = Stopwatch.StartNew();
        var bitmap = BitmapBufferConverter.StripPadding(
            safe.Pixels!, (int)safe.Width, (int)safe.Height, (int)safe.Stride);
        bitmapSw.Stop();
        totalSw.Stop();

        PrintCaptureSuccess($"monitor[{index}]", safe, bitmap, totalSw.Elapsed.TotalMilliseconds, bitmapSw.Elapsed.TotalMilliseconds);
        return 0;
    }

    // ── Verify Commands ──────────────────────────────────────────────────

    private static int RunVerify()
    {
        int failures = 0;

        bool Check(string name, bool condition, string failReason)
        {
            if (condition)
            {
                Console.WriteLine($"PASS: {name}");
                return true;
            }
            Console.WriteLine($"FAIL: {name} — {failReason}");
            failures++;
            return false;
        }

        var totalSw = Stopwatch.StartNew();

        using var safe = SafeCaptureResult.CapturePrimary();

        Check("status_ok", safe.IsSuccess,
            safe.Status != CaptureStatus.Ok ? $"status={safe.Status}" : "unknown");

        if (!safe.IsSuccess)
        {
            totalSw.Stop();
            if (safe.ErrorMessage != null) Console.WriteLine($"Error: {safe.ErrorMessage}");
            Console.WriteLine($"RoundTripMs: {totalSw.Elapsed.TotalMilliseconds:F1}");
            Console.WriteLine($"VerifyResult: FAIL ({failures} failures)");
            return 1;
        }

        Check("width_positive", safe.Width > 0, $"width={safe.Width}");
        Check("height_positive", safe.Height > 0, $"height={safe.Height}");
        Check("stride_valid", safe.Stride >= safe.Width * 4,
            $"stride={safe.Stride} < width*4={safe.Width * 4}");

        uint expectedLen = safe.Stride * safe.Height;
        Check("data_len_consistent", safe.DataLen == expectedLen,
            $"data_len={safe.DataLen} != stride*height={expectedLen}");

        Check("pixels_not_null", safe.Pixels != null, "Pixels is null");
        if (safe.Pixels != null)
        {
            Check("pixels_length_matches", (uint)safe.Pixels.Length == safe.DataLen,
                $"pixels.Length={safe.Pixels.Length} != data_len={safe.DataLen}");
        }

        ContiguousBitmap? bitmap = null;
        try
        {
            if (safe.Pixels != null)
            {
                bitmap = BitmapBufferConverter.StripPadding(
                    safe.Pixels, (int)safe.Width, (int)safe.Height, (int)safe.Stride);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: bitmap_conversion — {ex.Message}");
            failures++;
        }

        if (bitmap != null)
        {
            Check("bitmap_width_matches", bitmap.Width == (int)safe.Width,
                $"bitmap.Width={bitmap.Width} != width={safe.Width}");
            Check("bitmap_height_matches", bitmap.Height == (int)safe.Height,
                $"bitmap.Height={bitmap.Height} != height={safe.Height}");
        }

        totalSw.Stop();
        Console.WriteLine($"Captured: OK {safe.Width}x{safe.Height} stride={safe.Stride} data_len={safe.DataLen}");
        if (bitmap != null) Console.WriteLine($"CapturedBitmap: {safe.Width}x{safe.Height} format=BGRA");
        Console.WriteLine($"RoundTripMs: {totalSw.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"VerifyResult: {(failures == 0 ? "PASS" : $"FAIL ({failures} failures)")}");

        return failures == 0 ? 0 : 1;
    }

    private static int RunVerifyS02()
    {
        int failures = 0;

        bool Check(string name, bool condition, string failReason)
        {
            if (condition)
            {
                Console.WriteLine($"PASS: {name}");
                return true;
            }
            Console.WriteLine($"FAIL: {name} — {failReason}");
            failures++;
            return false;
        }

        // ── Test 1: Full desktop capture ────────────────────────────────
        Console.WriteLine("=== Full Desktop Capture ===");

        var diag1 = CaptureDiagnostics.Measure("full_desktop", () => SafeCaptureResult.CaptureAllMonitors());

        Check("full_desktop_status_ok", diag1.Status == "Ok",
            $"status={diag1.Status}");
        if (diag1.Status == "Ok")
        {
            Check("full_desktop_width_positive", diag1.Width > 0, $"width={diag1.Width}");
            Check("full_desktop_height_positive", diag1.Height > 0, $"height={diag1.Height}");
            Check("full_desktop_stride_valid", diag1.Stride >= diag1.Width * 4,
                $"stride={diag1.Stride} < width*4={diag1.Width * 4}");
            Check("full_desktop_data_len_consistent", diag1.DataLen == (long)diag1.Stride * diag1.Height,
                $"data_len={diag1.DataLen} != stride*height={(long)diag1.Stride * diag1.Height}");
        }
        PrintDiagRecord(diag1);

        // ── Test 2: Monitor[0] capture ──────────────────────────────────
        Console.WriteLine("=== Monitor[0] Capture ===");

        var diag2 = CaptureDiagnostics.Measure("monitor[0]", () => SafeCaptureResult.CaptureMonitorByIndex(0));

        Check("monitor0_status_ok", diag2.Status == "Ok",
            $"status={diag2.Status}");
        if (diag2.Status == "Ok")
        {
            Check("monitor0_width_positive", diag2.Width > 0, $"width={diag2.Width}");
            Check("monitor0_height_positive", diag2.Height > 0, $"height={diag2.Height}");
            Check("monitor0_stride_valid", diag2.Stride >= diag2.Width * 4,
                $"stride={diag2.Stride} < width*4={diag2.Width * 4}");
            Check("monitor0_data_len_consistent", diag2.DataLen == (long)diag2.Stride * diag2.Height,
                $"data_len={diag2.DataLen} != stride*height={(long)diag2.Stride * diag2.Height}");
        }
        PrintDiagRecord(diag2);

        // ── Summary ─────────────────────────────────────────────────────
        Console.WriteLine($"VerifyResult: {(failures == 0 ? "PASS" : $"FAIL ({failures} failures)")}");
        return failures == 0 ? 0 : 1;
    }

    // ── S03 Commands ─────────────────────────────────────────────────────

    private static int RunCaptureActiveWindow()
    {
        var totalSw = Stopwatch.StartNew();

        using var safe = SafeCaptureResult.CaptureActiveWindow();

        if (!safe.IsSuccess)
        {
            totalSw.Stop();
            PrintDiagnosticLine("active_window", safe, totalSw.Elapsed.TotalMilliseconds);
            return 2;
        }

        var bitmapSw = Stopwatch.StartNew();
        var bitmap = BitmapBufferConverter.StripPadding(
            safe.Pixels!, (int)safe.Width, (int)safe.Height, (int)safe.Stride);
        bitmapSw.Stop();
        totalSw.Stop();

        PrintCaptureSuccess("active_window", safe, bitmap, totalSw.Elapsed.TotalMilliseconds, bitmapSw.Elapsed.TotalMilliseconds);
        return 0;
    }

    private static int RunCaptureWindowUnderCursor()
    {
        var totalSw = Stopwatch.StartNew();

        using var safe = SafeCaptureResult.CaptureWindowUnderCursor();

        if (!safe.IsSuccess)
        {
            totalSw.Stop();
            PrintDiagnosticLine("window_under_cursor", safe, totalSw.Elapsed.TotalMilliseconds);
            return 2;
        }

        var bitmapSw = Stopwatch.StartNew();
        var bitmap = BitmapBufferConverter.StripPadding(
            safe.Pixels!, (int)safe.Width, (int)safe.Height, (int)safe.Stride);
        bitmapSw.Stop();
        totalSw.Stop();

        PrintCaptureSuccess("window_under_cursor", safe, bitmap, totalSw.Elapsed.TotalMilliseconds, bitmapSw.Elapsed.TotalMilliseconds);
        return 0;
    }

    private static int RunCaptureWindowByHandle(string hwndArg)
    {
        long hwndValue;
        if (hwndArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(hwndArg.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out hwndValue))
            {
                Console.Error.WriteLine($"Error: Invalid hex HWND '{hwndArg}'.");
                return 1;
            }
        }
        else if (!long.TryParse(hwndArg, out hwndValue))
        {
            Console.Error.WriteLine($"Error: HWND must be a decimal integer or 0x-prefixed hex, got '{hwndArg}'.");
            return 1;
        }

        var totalSw = Stopwatch.StartNew();

        using var safe = SafeCaptureResult.CaptureWindowByHandle(new IntPtr(hwndValue));

        if (!safe.IsSuccess)
        {
            totalSw.Stop();
            PrintDiagnosticLine($"window[0x{hwndValue:X}]", safe, totalSw.Elapsed.TotalMilliseconds);
            return 2;
        }

        var bitmapSw = Stopwatch.StartNew();
        var bitmap = BitmapBufferConverter.StripPadding(
            safe.Pixels!, (int)safe.Width, (int)safe.Height, (int)safe.Stride);
        bitmapSw.Stop();
        totalSw.Stop();

        PrintCaptureSuccess($"window[0x{hwndValue:X}]", safe, bitmap, totalSw.Elapsed.TotalMilliseconds, bitmapSw.Elapsed.TotalMilliseconds);
        return 0;
    }

    private static int RunVerifyS03()
    {
        int failures = 0;

        bool Check(string name, bool condition, string failReason)
        {
            if (condition)
            {
                Console.WriteLine($"PASS: {name}");
                return true;
            }
            Console.WriteLine($"FAIL: {name} — {failReason}");
            failures++;
            return false;
        }

        // ── Test 1: Active window capture ───────────────────────────────
        Console.WriteLine("=== Active Window Capture ===");

        var diag1 = CaptureDiagnostics.Measure("active_window", () => SafeCaptureResult.CaptureActiveWindow());

        Check("active_window_status", diag1.Status == "Ok" || diag1.Status == "CaptureUnavailable",
            $"status={diag1.Status}");

        if (diag1.Status == "Ok")
        {
            Check("active_window_width_positive", diag1.Width > 0, $"width={diag1.Width}");
            Check("active_window_height_positive", diag1.Height > 0, $"height={diag1.Height}");
            Check("active_window_stride_valid", diag1.Stride >= diag1.Width * 4,
                $"stride={diag1.Stride} < width*4={diag1.Width * 4}");
            Check("active_window_data_len_consistent", diag1.DataLen == (long)diag1.Stride * diag1.Height,
                $"data_len={diag1.DataLen} != stride*height={(long)diag1.Stride * diag1.Height}");
        }
        else
        {
            Console.WriteLine($"NOTE: active_window returned {diag1.Status} — expected in non-interactive sessions");
        }
        PrintDiagRecord(diag1);

        // ── Test 2: Window under cursor capture ─────────────────────────
        Console.WriteLine("=== Window Under Cursor Capture ===");

        var diag2 = CaptureDiagnostics.Measure("window_under_cursor", () => SafeCaptureResult.CaptureWindowUnderCursor());

        Check("window_under_cursor_status", diag2.Status == "Ok" || diag2.Status == "CaptureUnavailable",
            $"status={diag2.Status}");

        if (diag2.Status == "Ok")
        {
            Check("window_under_cursor_width_positive", diag2.Width > 0, $"width={diag2.Width}");
            Check("window_under_cursor_height_positive", diag2.Height > 0, $"height={diag2.Height}");
            Check("window_under_cursor_stride_valid", diag2.Stride >= diag2.Width * 4,
                $"stride={diag2.Stride} < width*4={diag2.Width * 4}");
            Check("window_under_cursor_data_len_consistent", diag2.DataLen == (long)diag2.Stride * diag2.Height,
                $"data_len={diag2.DataLen} != stride*height={(long)diag2.Stride * diag2.Height}");
        }
        else
        {
            Console.WriteLine($"NOTE: window_under_cursor returned {diag2.Status} — expected in non-interactive sessions");
        }
        PrintDiagRecord(diag2);

        // ── Test 3: Window by handle (zero HWND = expected failure) ─────
        Console.WriteLine("=== Window By Handle (zero HWND) ===");

        var diag3 = CaptureDiagnostics.Measure("window_by_handle[0]", () => SafeCaptureResult.CaptureWindowByHandle(IntPtr.Zero));

        Check("zero_handle_non_ok", diag3.Status != "Ok",
            $"zero HWND should not succeed, got status={diag3.Status}");
        PrintDiagRecord(diag3);

        // ── Summary ─────────────────────────────────────────────────────
        Console.WriteLine($"VerifyResult: {(failures == 0 ? "PASS" : $"FAIL ({failures} failures)")}");
        return failures == 0 ? 0 : 1;
    }

    // ── Output Helpers ───────────────────────────────────────────────────

    private static void PrintDiagnosticLine(string mode, SafeCaptureResult safe, double roundTripMs)
    {
        Console.WriteLine($"CaptureMode={mode}");
        Console.WriteLine($"Status={safe.Status}");
        Console.WriteLine($"RoundTripMs={roundTripMs:F1}");
        if (safe.ErrorMessage != null) Console.WriteLine($"Error={safe.ErrorMessage}");
    }

    private static void PrintCaptureSuccess(string mode, SafeCaptureResult safe, ContiguousBitmap bitmap,
        double totalMs, double bitmapMs)
    {
        Console.WriteLine($"CaptureMode={mode}");
        Console.WriteLine($"Status=Ok");
        Console.WriteLine($"Width={safe.Width}");
        Console.WriteLine($"Height={safe.Height}");
        Console.WriteLine($"Stride={safe.Stride}");
        Console.WriteLine($"DataLen={safe.DataLen}");
        Console.WriteLine($"ContiguousWidth={bitmap.Width}");
        Console.WriteLine($"ContiguousHeight={bitmap.Height}");
        Console.WriteLine($"ContiguousStride={bitmap.Stride}");
        Console.WriteLine($"RoundTripMs={totalMs:F1} (bitmap={bitmapMs:F1})");
    }

    private static void PrintDiagRecord(CaptureDiagnosticRecord record)
    {
        foreach (var line in record.ToKeyValueLines())
        {
            Console.WriteLine(line);
        }
    }
}
