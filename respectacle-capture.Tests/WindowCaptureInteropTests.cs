// WindowCaptureInteropTests — validates S03 managed interop for window capture modes.
//
// Tests cover:
// - P/Invoke declarations exist with correct names, calling convention, and parameter types
// - SafeCaptureResult factory methods handle DLL/entrypoint errors gracefully
// - CaptureWindowByHandle(IntPtr.Zero) returns non-success without throwing
// - CaptureDiagnosticRecord key=value output for window modes
// - S03 factory methods are present on SafeCaptureResult with correct signatures

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Respectacle.Capture;
using Xunit;

namespace Respectacle.Capture.Tests;

public class WindowCaptureInteropTests
{
    // ── P/Invoke Declaration Tests ───────────────────────────────────────

    [Fact]
    public void NativeMethods_CaptureActiveWindow_ExportExists()
    {
        var method = typeof(NativeMethods).GetMethod("respectacle_capture_active_window",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(NativeCaptureResult), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void NativeMethods_CaptureWindowUnderCursor_ExportExists()
    {
        var method = typeof(NativeMethods).GetMethod("respectacle_capture_window_under_cursor",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(NativeCaptureResult), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void NativeMethods_CaptureWindowByHandle_ExportExists()
    {
        var method = typeof(NativeMethods).GetMethod("respectacle_capture_window_by_handle",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(NativeCaptureResult), method!.ReturnType);

        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(ulong), parameters[0].ParameterType);
    }

    [Fact]
    public void NativeMethods_S03Exports_HaveCdeclCallingConvention()
    {
        var names = new[] { "respectacle_capture_active_window", "respectacle_capture_window_under_cursor", "respectacle_capture_window_by_handle" };

        foreach (var name in names)
        {
            var method = typeof(NativeMethods).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);
            var attrs = method!.GetCustomAttributes(typeof(DllImportAttribute), false);
            Assert.NotEmpty(attrs);
            foreach (DllImportAttribute attr in attrs)
            {
                Assert.Equal(CallingConvention.Cdecl, attr.CallingConvention);
            }
        }
    }

    // ── Safe Factory Method Existence Tests ──────────────────────────────

    [Fact]
    public void SafeCaptureResult_CaptureActiveWindow_MethodExists()
    {
        var method = typeof(SafeCaptureResult).GetMethod("CaptureActiveWindow",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(SafeCaptureResult), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void SafeCaptureResult_CaptureWindowUnderCursor_MethodExists()
    {
        var method = typeof(SafeCaptureResult).GetMethod("CaptureWindowUnderCursor",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(SafeCaptureResult), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void SafeCaptureResult_CaptureWindowByHandle_MethodExists()
    {
        var method = typeof(SafeCaptureResult).GetMethod("CaptureWindowByHandle",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(SafeCaptureResult), method!.ReturnType);

        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(IntPtr), parameters[0].ParameterType);
    }

    // ── Negative Tests ───────────────────────────────────────────────────

    [Fact]
    public void CaptureWindowByHandle_ZeroHwnd_ReturnsNonSuccess()
    {
        // Zero HWND is rejected by native code — should return non-success, not throw
        using var result = SafeCaptureResult.CaptureWindowByHandle(IntPtr.Zero);
        Assert.False(result.IsSuccess);
        Assert.NotEqual(CaptureStatus.Ok, result.Status);
        Assert.Null(result.Pixels);
        // Width/Height/Stride/DataLen should be zero on error
        Assert.Equal(0u, result.Width);
        Assert.Equal(0u, result.Height);
        Assert.Equal(0u, result.Stride);
        Assert.Equal(0u, result.DataLen);
    }

    [Fact]
    public void CaptureWindowByHandle_InvalidHwnd_ReturnsNonSuccess()
    {
        // Small non-zero HWND that doesn't correspond to a real window
        using var result = SafeCaptureResult.CaptureWindowByHandle(new IntPtr(0xDEADBEEF));
        Assert.False(result.IsSuccess);
        Assert.NotEqual(CaptureStatus.Ok, result.Status);
        Assert.Null(result.Pixels);
    }

    // ── Diagnostic Record Tests for S03 ──────────────────────────────────

    [Fact]
    public void CaptureDiagnosticRecord_ActiveWindow_KeyValueFormat()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "active_window",
            status: "Ok",
            width: 1920,
            height: 1080,
            stride: 7680,
            dataLen: 8294400,
            roundTripMs: 35.2,
            error: null
        );

        var lines = record.ToKeyValueLines();
        Assert.Contains("CaptureMode=active_window", lines);
        Assert.Contains("Status=Ok", lines);
        Assert.Contains("Width=1920", lines);
        Assert.Contains("Height=1080", lines);
        Assert.Contains("Stride=7680", lines);
        Assert.Contains("DataLen=8294400", lines);
        Assert.Contains("RoundTripMs=35.2", lines);
        Assert.DoesNotContain("Error=", lines);
    }

    [Fact]
    public void CaptureDiagnosticRecord_WindowUnderCursor_WithError()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "window_under_cursor",
            status: "CaptureUnavailable",
            width: 0,
            height: 0,
            stride: 0,
            dataLen: 0,
            roundTripMs: 2.1,
            error: "No capturable window under cursor"
        );

        var lines = record.ToKeyValueLines();
        Assert.Contains("CaptureMode=window_under_cursor", lines);
        Assert.Contains("Status=CaptureUnavailable", lines);
        Assert.Contains("Error=No capturable window under cursor", lines);
        Assert.Contains("Width=0", lines);
        Assert.Contains("Height=0", lines);
        Assert.Contains("Stride=0", lines);
        Assert.Contains("DataLen=0", lines);
    }

    [Fact]
    public void CaptureDiagnosticRecord_WindowByHandle_KeyValueFormat()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "window[0x12345]",
            status: "CaptureUnavailable",
            width: 0,
            height: 0,
            stride: 0,
            dataLen: 0,
            roundTripMs: 1.5,
            error: "Invalid window handle"
        );

        var lines = record.ToKeyValueLines();
        Assert.Contains("CaptureMode=window[0x12345]", lines);
        Assert.Contains("Status=CaptureUnavailable", lines);
        Assert.Contains("Error=Invalid window handle", lines);
    }

    // ── S03 Output Never Contains Raw Pixels ─────────────────────────────

    [Fact]
    public void S03_DiagnosticOutput_NeverContainsRawPixels()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "active_window",
            status: "Ok",
            width: 800,
            height: 600,
            stride: 3200,
            dataLen: 1920000,
            roundTripMs: 10.0,
            error: null
        );

        var output = string.Join("\n", record.ToKeyValueLines());
        Assert.DoesNotContain("Pixel", output);
        Assert.DoesNotContain("0x", output);
    }

    // ── Backward Compatibility: S01/S02 Exports Still Present ────────────

    [Fact]
    public void S03_Exports_DoNotRemove_S01_S02_Exports()
    {
        var methods = typeof(NativeMethods).GetMethods(BindingFlags.Public | BindingFlags.Static);
        var names = new HashSet<string>();
        foreach (var m in methods) names.Add(m.Name);

        // S01
        Assert.Contains("respectacle_capture_frame", names);
        Assert.Contains("respectacle_free_frame", names);
        Assert.Contains("respectacle_free_error_message", names);
        Assert.Contains("respectacle_free_capture_result", names);

        // S02
        Assert.Contains("respectacle_capture_all_monitors", names);
        Assert.Contains("respectacle_capture_monitor_by_index", names);

        // S03
        Assert.Contains("respectacle_capture_active_window", names);
        Assert.Contains("respectacle_capture_window_under_cursor", names);
        Assert.Contains("respectacle_capture_window_by_handle", names);
    }

    [Fact]
    public void NativeMethods_TotalExportCount_IncludesAllSlices()
    {
        var methods = typeof(NativeMethods).GetMethods(BindingFlags.Public | BindingFlags.Static);
        // S01: 4, S02: 2, S03: 3 = 9 total
        Assert.True(methods.Length >= 9, $"Expected at least 9 P/Invoke methods, got {methods.Length}");
    }
}
