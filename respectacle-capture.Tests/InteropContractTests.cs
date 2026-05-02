// InteropContractTests — validates the FFI contract types, P/Invoke signatures,
// managed wrapper validation, and negative test cases for the respectacle-capture library.
//
// These tests cover:
// - Struct layout matches Rust #[repr(C)] order
// - CaptureStatus enum values match Rust exactly (including Timeout=-5, InvalidBuffer=-6)
// - P/Invoke methods use cdecl calling convention and export expected names (S01 + S02)
// - SafeCaptureResult validation rejects invalid metadata (null ptr, zero dims, bad stride)
// - Error paths and boundary conditions (1x1, all error statuses, double dispose)

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Respectacle.Capture.Tests;

public class InteropContractTests
{
    // ── Struct Layout Tests ──────────────────────────────────────────────

    [Fact]
    public void NativeCaptureResult_StructLayout_IsSequential()
    {
        var layout = typeof(NativeCaptureResult).StructLayoutAttribute;
        Assert.NotNull(layout);
        Assert.Equal(LayoutKind.Sequential, layout.Value);
    }

    [Fact]
    public void NativeCaptureResult_Fields_MatchRustOrder()
    {
        var type = typeof(NativeCaptureResult);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

        Assert.True(fields.Length >= 7, $"Expected at least 7 fields, got {fields.Length}");

        Assert.Equal("Status", fields[0].Name);
        Assert.Equal(typeof(CaptureStatus), fields[0].FieldType);

        Assert.Equal("FrameData", fields[1].Name);
        Assert.Equal(typeof(IntPtr), fields[1].FieldType);

        Assert.Equal("Width", fields[2].Name);
        Assert.Equal(typeof(uint), fields[2].FieldType);

        Assert.Equal("Height", fields[3].Name);
        Assert.Equal(typeof(uint), fields[3].FieldType);

        Assert.Equal("Stride", fields[4].Name);
        Assert.Equal(typeof(uint), fields[4].FieldType);

        Assert.Equal("DataLen", fields[5].Name);
        Assert.Equal(typeof(uint), fields[5].FieldType);

        Assert.Equal("ErrorMessage", fields[6].Name);
        Assert.Equal(typeof(IntPtr), fields[6].FieldType);
    }

    // ── CaptureStatus Value Tests ────────────────────────────────────────

    [Fact]
    public void CaptureStatus_Values_MatchRustContract()
    {
        Assert.Equal(0, (int)CaptureStatus.Ok);
        Assert.Equal(-1, (int)CaptureStatus.NotImplemented);
        Assert.Equal(-2, (int)CaptureStatus.CaptureUnavailable);
        Assert.Equal(-3, (int)CaptureStatus.PermissionDenied);
        Assert.Equal(-4, (int)CaptureStatus.InternalError);
        Assert.Equal(-5, (int)CaptureStatus.Timeout);
        Assert.Equal(-6, (int)CaptureStatus.InvalidBuffer);
    }

    // ── P/Invoke Signature Tests ─────────────────────────────────────────

    [Fact]
    public void NativeMethods_AllExports_HaveCdeclCallingConvention()
    {
        var methods = typeof(NativeMethods).GetMethods(BindingFlags.Public | BindingFlags.Static);
        Assert.True(methods.Length >= 9, $"Expected at least 9 P/Invoke methods, got {methods.Length}");

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

        // S01 exports
        Assert.Contains("respectacle_capture_frame", names);
        Assert.Contains("respectacle_free_frame", names);
        Assert.Contains("respectacle_free_error_message", names);
        Assert.Contains("respectacle_free_capture_result", names);

        // S02 exports
        Assert.Contains("respectacle_capture_all_monitors", names);
        Assert.Contains("respectacle_capture_monitor_by_index", names);

        // S03 exports
        Assert.Contains("respectacle_capture_active_window", names);
        Assert.Contains("respectacle_capture_window_under_cursor", names);
        Assert.Contains("respectacle_capture_window_by_handle", names);
    }

    [Fact]
    public void NativeMethods_CaptureAllMonitors_ReturnsNativeCaptureResult()
    {
        var method = typeof(NativeMethods).GetMethod("respectacle_capture_all_monitors",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(NativeCaptureResult), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void NativeMethods_CaptureMonitorByIndex_TakesUintIndex_ReturnsNativeCaptureResult()
    {
        var method = typeof(NativeMethods).GetMethod("respectacle_capture_monitor_by_index",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(NativeCaptureResult), method!.ReturnType);

        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(uint), parameters[0].ParameterType);
    }

    // ── SafeCaptureResult Validation Tests (via synthetic constructor) ───

    [Fact]
    public void SafeCaptureResult_Synthetic_ValidData_Succeeds()
    {
        uint width = 4, height = 2, stride = width * 4;
        uint dataLen = stride * height;
        byte[] pixels = new byte[dataLen];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i & 0xFF);

        var result = new SafeCaptureResult(pixels, width, height, stride);

        Assert.True(result.IsSuccess);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(stride, result.Stride);
        Assert.Equal(dataLen, result.DataLen);
        Assert.NotNull(result.Pixels);
        Assert.Equal((int)dataLen, result.Pixels!.Length);
        result.Dispose();
    }

    [Fact]
    public void SafeCaptureResult_Synthetic_StrideLessThanWidth4_Throws()
    {
        uint width = 10, height = 5, stride = 10; // stride < width*4 = 40
        byte[] pixels = new byte[stride * height];
        Assert.Throws<ArgumentException>(() =>
            new SafeCaptureResult(pixels, width, height, stride));
    }

    [Fact]
    public void SafeCaptureResult_Synthetic_BufferTooSmall_Throws()
    {
        uint width = 4, height = 2, stride = 16;
        byte[] pixels = new byte[10]; // Too small for stride*height = 32
        Assert.Throws<ArgumentException>(() =>
            new SafeCaptureResult(pixels, width, height, stride));
    }

    // ── SafeCaptureResult Validation Tests (via native constructor, null ptrs) ──

    [Fact]
    public void SafeCaptureResult_Native_NullFrameData_Throws()
    {
        var native = new NativeCaptureResult
        {
            Status = CaptureStatus.Ok,
            FrameData = IntPtr.Zero,
            Width = 100,
            Height = 100,
            Stride = 400,
            DataLen = 40000,
            ErrorMessage = IntPtr.Zero,
        };
        Assert.Throws<InvalidOperationException>(() => new SafeCaptureResult(native));
    }

    [Fact]
    public void SafeCaptureResult_Native_NonOkStatus_SetsProperties()
    {
        var native = new NativeCaptureResult
        {
            Status = CaptureStatus.CaptureUnavailable,
            FrameData = IntPtr.Zero,
            Width = 0,
            Height = 0,
            Stride = 0,
            DataLen = 0,
            ErrorMessage = IntPtr.Zero,
        };
        var result = new SafeCaptureResult(native);
        Assert.False(result.IsSuccess);
        Assert.Equal(CaptureStatus.CaptureUnavailable, result.Status);
        Assert.Null(result.Pixels);
        result.Dispose();
    }

    [Fact]
    public void SafeCaptureResult_Native_NonOkStatus_WithErrorMessage()
    {
        string testError = "Test capture failure";
        IntPtr errorPtr = Marshal.StringToCoTaskMemUTF8(testError);

        var native = new NativeCaptureResult
        {
            Status = CaptureStatus.InternalError,
            FrameData = IntPtr.Zero,
            Width = 0,
            Height = 0,
            Stride = 0,
            DataLen = 0,
            ErrorMessage = errorPtr,
        };

        var result = new SafeCaptureResult(native);
        Assert.False(result.IsSuccess);
        Assert.Equal(CaptureStatus.InternalError, result.Status);
        Assert.Equal(testError, result.ErrorMessage);
        result.Dispose();
    }

    // ── Validation via synthetic path ────────────────────────────────────

    [Fact]
    public void SafeCaptureResult_ZeroWidth_Throws()
    {
        byte[] pixels = new byte[0];
        Assert.Throws<ArgumentException>(() =>
            new SafeCaptureResult(pixels, 0, 100, 0));
    }

    [Fact]
    public void SafeCaptureResult_ZeroHeight_Throws()
    {
        byte[] pixels = new byte[0];
        Assert.Throws<ArgumentException>(() =>
            new SafeCaptureResult(pixels, 100, 0, 400));
    }

    [Fact]
    public void SafeCaptureResult_StrideLessThanMin_Throws()
    {
        byte[] pixels = new byte[10000];
        Assert.Throws<ArgumentException>(() =>
            new SafeCaptureResult(pixels, 100, 50, 200));
    }

    [Fact]
    public void SafeCaptureResult_DataLenMismatch_Throws()
    {
        byte[] pixels = new byte[999];
        Assert.Throws<ArgumentException>(() =>
            new SafeCaptureResult(pixels, 100, 50, 400));
    }

    // ── Stride Padding Tests ─────────────────────────────────────────────

    [Fact]
    public void SafeCaptureResult_Synthetic_StridePadding_Accepted()
    {
        uint width = 100, height = 50, stride = 420;
        uint dataLen = stride * height;
        byte[] pixels = new byte[dataLen];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i & 0xFF);

        var result = new SafeCaptureResult(pixels, width, height, stride);
        Assert.True(result.IsSuccess);
        Assert.Equal(stride, result.Stride);
        Assert.Equal(dataLen, result.DataLen);
        result.Dispose();
    }

    // ── Error Path Tests ─────────────────────────────────────────────────

    [Theory]
    [InlineData(CaptureStatus.NotImplemented)]
    [InlineData(CaptureStatus.CaptureUnavailable)]
    [InlineData(CaptureStatus.PermissionDenied)]
    [InlineData(CaptureStatus.InternalError)]
    [InlineData(CaptureStatus.Timeout)]
    [InlineData(CaptureStatus.InvalidBuffer)]
    public void SafeCaptureResult_AllErrorStatuses_DoNotThrow(CaptureStatus status)
    {
        var native = new NativeCaptureResult
        {
            Status = status,
            FrameData = IntPtr.Zero,
            Width = 0,
            Height = 0,
            Stride = 0,
            DataLen = 0,
            ErrorMessage = IntPtr.Zero,
        };
        var result = new SafeCaptureResult(native);
        Assert.False(result.IsSuccess);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Pixels);
        result.Dispose();
    }

    [Fact]
    public void SafeCaptureResult_DisposeIsIdempotent()
    {
        uint width = 4, height = 2, stride = width * 4;
        byte[] pixels = new byte[stride * height];
        var result = new SafeCaptureResult(pixels, width, height, stride);

        result.Dispose();
        result.Dispose();
    }

    // ── End-to-End Synthetic Flow ────────────────────────────────────────

    [Fact]
    public void EndToEnd_SyntheticCapture_ToBitmap()
    {
        uint width = 192, height = 108;
        uint stride = width * 4;
        uint dataLen = stride * height;
        byte[] captureData = new byte[dataLen];
        for (int i = 0; i < captureData.Length; i++) captureData[i] = (byte)(i % 256);

        using var safe = new SafeCaptureResult(captureData, width, height, stride);
        Assert.True(safe.IsSuccess);
        Assert.NotNull(safe.Pixels);

        var converted = BitmapBufferConverter.StripPadding(safe.Pixels!, (int)safe.Width, (int)safe.Height, (int)safe.Stride);
        Assert.Equal((int)width, converted.Width);
        Assert.Equal((int)height, converted.Height);
        Assert.Equal((int)(width * 4 * height), converted.Pixels.Length);
    }

    [Fact]
    public void EndToEnd_SyntheticCapture_WithPadding_ToBitmap()
    {
        uint width = 100, height = 50, stride = 420;
        uint dataLen = stride * height;
        byte[] captureData = new byte[dataLen];

        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                int offset = (int)(y * stride + x * 4);
                captureData[offset + 0] = (byte)(x & 0xFF);
                captureData[offset + 1] = (byte)(y & 0xFF);
                captureData[offset + 2] = 0x80;
                captureData[offset + 3] = 0xFF;
            }
        }

        using var safe = new SafeCaptureResult(captureData, width, height, stride);
        Assert.True(safe.IsSuccess);

        var converted = BitmapBufferConverter.StripPadding(safe.Pixels!, (int)width, (int)height, (int)stride);
        Assert.Equal((int)width, converted.Width);
        Assert.Equal((int)height, converted.Height);
        Assert.Equal((int)(width * 4 * height), converted.Pixels.Length);
    }

    // ── CaptureDiagnostics Tests ─────────────────────────────────────────

    [Fact]
    public void CaptureDiagnostics_Record_HasExpectedFields()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "full_desktop",
            status: "Ok",
            width: 1920,
            height: 1080,
            stride: 7680,
            dataLen: 8294400,
            roundTripMs: 42.5,
            error: null
        );

        Assert.Equal("full_desktop", record.CaptureMode);
        Assert.Equal("Ok", record.Status);
        Assert.Equal(1920, record.Width);
        Assert.Equal(1080, record.Height);
        Assert.Equal(7680, record.Stride);
        Assert.Equal(8294400, record.DataLen);
        Assert.Equal(42.5, record.RoundTripMs);
        Assert.Null(record.Error);
    }

    [Fact]
    public void CaptureDiagnostics_Record_ToKeyValueFormat()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "monitor[0]",
            status: "Ok",
            width: 1920,
            height: 1080,
            stride: 7680,
            dataLen: 8294400,
            roundTripMs: 15.3,
            error: null
        );

        var lines = record.ToKeyValueLines();
        Assert.Contains("CaptureMode=monitor[0]", lines);
        Assert.Contains("Status=Ok", lines);
        Assert.Contains("Width=1920", lines);
        Assert.Contains("Height=1080", lines);
        Assert.Contains("Stride=7680", lines);
        Assert.Contains("DataLen=8294400", lines);
        Assert.Contains("RoundTripMs=15.3", lines);
        // Error line should NOT appear when null
        Assert.DoesNotContain("Error=", lines);
    }

    [Fact]
    public void CaptureDiagnostics_Record_WithError_ToKeyValueFormat()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "monitor[99]",
            status: "CaptureUnavailable",
            width: 0,
            height: 0,
            stride: 0,
            dataLen: 0,
            roundTripMs: 1.2,
            error: "No monitor at index 99"
        );

        var lines = record.ToKeyValueLines();
        Assert.Contains("CaptureMode=monitor[99]", lines);
        Assert.Contains("Status=CaptureUnavailable", lines);
        Assert.Contains("Error=No monitor at index 99", lines);
    }

    [Fact]
    public void CaptureDiagnostics_Record_NeverContainsRawPixels()
    {
        var record = new CaptureDiagnosticRecord(
            captureMode: "primary",
            status: "Ok",
            width: 100,
            height: 100,
            stride: 400,
            dataLen: 40000,
            roundTripMs: 5.0,
            error: null
        );

        var output = string.Join("\n", record.ToKeyValueLines());
        // Ensure no pixel data appears in diagnostics output
        Assert.DoesNotContain("Pixel", output);
        Assert.DoesNotContain("pixel", output);
        Assert.DoesNotContain("0x", output);
    }
}
