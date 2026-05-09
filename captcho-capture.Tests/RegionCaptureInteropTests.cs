// RegionCaptureInteropTests — validates S05 managed interop for rectangular region capture.
//
// Tests cover:
// - P/Invoke declaration exists with correct name, calling convention, and parameter types (int, int, uint, uint)
// - SafeCaptureResult.CaptureRegion factory method handles DLL/entrypoint errors gracefully
// - Invalid dimensions (zero width/height) return non-success without throwing
// - S01–S03 exports and tests continue to pass (backward compatibility)
// - CaptureDiagnosticRecord key=value output for region modes

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using captcho.Capture;
using Xunit;

namespace captcho.Capture.Tests;

public class RegionCaptureInteropTests
{
    // ── P/Invoke Declaration Tests ───────────────────────────────────────

    [Fact]
    public void NativeMethods_CaptureRegion_ExportExists()
    {
        var method = typeof(NativeMethods).GetMethod("captcho_capture_region",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(NativeCaptureResult), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(4, parameters.Length);
        // x and y are signed (int) to support negative virtual-desktop origins
        Assert.Equal(typeof(int), parameters[0].ParameterType);
        Assert.Equal(typeof(int), parameters[1].ParameterType);
        // width and height are unsigned (uint)
        Assert.Equal(typeof(uint), parameters[2].ParameterType);
        Assert.Equal(typeof(uint), parameters[3].ParameterType);
    }

    [Fact]
    public void NativeMethods_CaptureRegion_HasCdeclCallingConvention()
    {
        var method = typeof(NativeMethods).GetMethod("captcho_capture_region",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var attrs = method!.GetCustomAttributes(typeof(DllImportAttribute), false);
        Assert.NotEmpty(attrs);
        foreach (DllImportAttribute attr in attrs)
        {
            Assert.Equal(CallingConvention.Cdecl, attr.CallingConvention);
        }
    }

    [Fact]
    public void NativeMethods_CaptureRegion_ParameterNames_MatchContract()
    {
        var method = typeof(NativeMethods).GetMethod("captcho_capture_region",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal("x", parameters[0].Name);
        Assert.Equal("y", parameters[1].Name);
        Assert.Equal("width", parameters[2].Name);
        Assert.Equal("height", parameters[3].Name);
    }

    // ── Safe Factory Method Existence Tests ──────────────────────────────

    [Fact]
    public void SafeCaptureResult_CaptureRegion_MethodExists()
    {
        var method = typeof(SafeCaptureResult).GetMethod("CaptureRegion",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(SafeCaptureResult), method!.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(int), parameters[0].ParameterType);
        Assert.Equal(typeof(int), parameters[1].ParameterType);
        Assert.Equal(typeof(uint), parameters[2].ParameterType);
        Assert.Equal(typeof(uint), parameters[3].ParameterType);
    }

    // ── Negative Tests ───────────────────────────────────────────────────

    [Fact]
    public void CaptureRegion_ZeroWidth_ReturnsNonSuccess()
    {
        // Zero width is rejected by native code — should return non-success, not throw
        using var result = SafeCaptureResult.CaptureRegion(0, 0, 0, 100);
        Assert.False(result.IsSuccess);
        Assert.NotEqual(CaptureStatus.Ok, result.Status);
        Assert.Null(result.Pixels);
        Assert.Equal(0u, result.Width);
        Assert.Equal(0u, result.Height);
        Assert.Equal(0u, result.Stride);
        Assert.Equal(0u, result.DataLen);
    }

    [Fact]
    public void CaptureRegion_ZeroHeight_ReturnsNonSuccess()
    {
        using var result = SafeCaptureResult.CaptureRegion(0, 0, 100, 0);
        Assert.False(result.IsSuccess);
        Assert.NotEqual(CaptureStatus.Ok, result.Status);
        Assert.Null(result.Pixels);
    }

    [Fact]
    public void CaptureRegion_InvalidCoordinates_ReturnsNonSuccess()
    {
        // Extremely negative coordinates that won't match any virtual desktop
        using var result = SafeCaptureResult.CaptureRegion(-99999, -99999, 1, 1);
        Assert.False(result.IsSuccess);
        Assert.NotEqual(CaptureStatus.Ok, result.Status);
        Assert.Null(result.Pixels);
    }

    // ── Diagnostic Record Tests for S05 ──────────────────────────────────

    [Fact]
    public void CaptureDiagnosticRecord_RegionCapture_KeyValueFormat()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "region[0,0,1920,1080]",
            status: "Ok",
            width: 1920,
            height: 1080,
            stride: 7680,
            dataLen: 8294400,
            roundTripMs: 42.5,
            error: null
        );

        var lines = record.ToKeyValueLines();
        Assert.Contains("CaptureMode=region[0,0,1920,1080]", lines);
        Assert.Contains("Status=Ok", lines);
        Assert.Contains("Width=1920", lines);
        Assert.Contains("Height=1080", lines);
        Assert.Contains("Stride=7680", lines);
        Assert.Contains("DataLen=8294400", lines);
        Assert.Contains("RoundTripMs=42.5", lines);
        Assert.DoesNotContain("Error=", lines);
    }

    [Fact]
    public void CaptureDiagnosticRecord_RegionCapture_WithError()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "region[-100,-200,300,400]",
            status: "InvalidBuffer",
            width: 0,
            height: 0,
            stride: 0,
            dataLen: 0,
            roundTripMs: 3.2,
            error: "region: out-of-desktop bounds"
        );

        var lines = record.ToKeyValueLines();
        Assert.Contains("CaptureMode=region[-100,-200,300,400]", lines);
        Assert.Contains("Status=InvalidBuffer", lines);
        Assert.Contains("Error=region: out-of-desktop bounds", lines);
        Assert.Contains("Width=0", lines);
        Assert.Contains("Height=0", lines);
    }

    // ── S05 Diagnostic Output Never Contains Raw Pixels ──────────────────

    [Fact]
    public void S05_DiagnosticOutput_NeverContainsRawPixels()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "region[100,200,800,600]",
            status: "Ok",
            width: 800,
            height: 600,
            stride: 3200,
            dataLen: 1920000,
            roundTripMs: 25.0,
            error: null
        );

        var output = string.Join("\n", record.ToKeyValueLines());
        Assert.DoesNotContain("Pixel", output);
        Assert.DoesNotContain("0x", output);
    }

    // ── Backward Compatibility: S01–S03 Exports Still Present ────────────

    [Fact]
    public void S05_Exports_DoNotRemove_S01_S02_S03_Exports()
    {
        var methods = typeof(NativeMethods).GetMethods(BindingFlags.Public | BindingFlags.Static);
        var names = new HashSet<string>();
        foreach (var m in methods) names.Add(m.Name);

        // S01
        Assert.Contains("captcho_capture_frame", names);
        Assert.Contains("captcho_free_frame", names);
        Assert.Contains("captcho_free_error_message", names);
        Assert.Contains("captcho_free_capture_result", names);

        // S02
        Assert.Contains("captcho_capture_all_monitors", names);
        Assert.Contains("captcho_capture_monitor_by_index", names);

        // S03
        Assert.Contains("captcho_capture_active_window", names);
        Assert.Contains("captcho_capture_window_under_cursor", names);
        Assert.Contains("captcho_capture_window_by_handle", names);

        // S05
        Assert.Contains("captcho_capture_region", names);
    }

    [Fact]
    public void NativeMethods_TotalExportCount_IncludesAllSlices()
    {
        var methods = typeof(NativeMethods).GetMethods(BindingFlags.Public | BindingFlags.Static);
        // S01: 4, S02: 2, S03: 3, S05: 1 = 10 total
        Assert.True(methods.Length >= 10, $"Expected at least 10 P/Invoke methods, got {methods.Length}");
    }

    [Fact]
    public void NativeMethods_AllExports_HaveCdeclCallingConvention()
    {
        var methods = typeof(NativeMethods).GetMethods(BindingFlags.Public | BindingFlags.Static);
        Assert.True(methods.Length >= 10, $"Expected at least 10 P/Invoke methods, got {methods.Length}");

        foreach (var method in methods)
        {
            var attrs = method.GetCustomAttributes(typeof(DllImportAttribute), false);
            foreach (DllImportAttribute attr in attrs)
            {
                Assert.Equal(CallingConvention.Cdecl, attr.CallingConvention);
            }
        }
    }

    [Fact]
    public void NativeMethods_Exports_ExpectedFunctionNames()
    {
        var methods = typeof(NativeMethods).GetMethods(BindingFlags.Public | BindingFlags.Static);
        var names = new HashSet<string>();
        foreach (var m in methods) names.Add(m.Name);

        // S01
        Assert.Contains("captcho_capture_frame", names);
        Assert.Contains("captcho_free_frame", names);
        Assert.Contains("captcho_free_error_message", names);
        Assert.Contains("captcho_free_capture_result", names);

        // S02
        Assert.Contains("captcho_capture_all_monitors", names);
        Assert.Contains("captcho_capture_monitor_by_index", names);

        // S03
        Assert.Contains("captcho_capture_active_window", names);
        Assert.Contains("captcho_capture_window_under_cursor", names);
        Assert.Contains("captcho_capture_window_by_handle", names);

        // S05
        Assert.Contains("captcho_capture_region", names);
    }
}
