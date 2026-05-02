// InteropContractTests — validates the FFI contract types, P/Invoke signatures,
// managed wrapper validation, bitmap conversion, and negative test cases.
//
// These tests exercise the types from the shared respectacle-capture library,
// plus the Program entry point from cs-tester.
//
// Tests cover:
// - Struct layout matches Rust #[repr(C)] order
// - CaptureStatus enum values match Rust exactly (including Timeout=-5, InvalidBuffer=-6)
// - P/Invoke methods use cdecl calling convention and export expected names (S01 + S02)
// - SafeCaptureResult validation rejects invalid metadata (null ptr, zero dims, bad stride)
// - BitmapBufferConverter creates correctly-sized contiguous data from BGRA input
// - Stride padding is handled correctly (rows are stripped to contiguous BGRA)
// - Error paths and boundary conditions (1x1, all error statuses, double dispose)

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Respectacle.Capture;
using Xunit;

namespace Respectacle.Tester.Tests;

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

        Assert.Contains("respectacle_capture_frame", names);
        Assert.Contains("respectacle_free_frame", names);
        Assert.Contains("respectacle_free_error_message", names);
        Assert.Contains("respectacle_free_capture_result", names);
        Assert.Contains("respectacle_capture_all_monitors", names);
        Assert.Contains("respectacle_capture_monitor_by_index", names);

        // S03 exports
        Assert.Contains("respectacle_capture_active_window", names);
        Assert.Contains("respectacle_capture_window_under_cursor", names);
        Assert.Contains("respectacle_capture_window_by_handle", names);
    }

    // ── Program Entry Point ──────────────────────────────────────────────

    [Fact]
    public void Program_Main_ExistsWithExpectedSignature()
    {
        var programType = typeof(Program);
        var main = programType.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(main);
        Assert.Equal(typeof(int), main!.ReturnType);
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
        uint width = 10, height = 5, stride = 10;
        byte[] pixels = new byte[stride * height];
        Assert.Throws<ArgumentException>(() =>
            new SafeCaptureResult(pixels, width, height, stride));
    }

    [Fact]
    public void SafeCaptureResult_Synthetic_BufferTooSmall_Throws()
    {
        uint width = 4, height = 2, stride = 16;
        byte[] pixels = new byte[10];
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

    // ── Validation via synthetic path (covers same checks without DLL) ───

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

    // ── BitmapBufferConverter Tests ───────────────────────────────────────

    [Fact]
    public void BitmapBufferConverter_ValidBgra_CreatesCorrectDimensions()
    {
        int width = 64, height = 48, stride = width * 4;
        byte[] pixels = new byte[stride * height];

        var bitmap = BitmapBufferConverter.StripPadding(pixels, width, height, stride);

        Assert.NotNull(bitmap);
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
        Assert.Equal(width * 4, bitmap.Stride);
    }

    [Fact]
    public void BitmapBufferConverter_MinimalFrame_1x1()
    {
        byte[] pixels = { 0xFF, 0x00, 0x00, 0xFF };

        var bitmap = BitmapBufferConverter.StripPadding(pixels, 1, 1, 4);

        Assert.Equal(1, bitmap.Width);
        Assert.Equal(1, bitmap.Height);
        Assert.Equal(0xFF, bitmap.Pixels[0]);
        Assert.Equal(0x00, bitmap.Pixels[1]);
        Assert.Equal(0x00, bitmap.Pixels[2]);
        Assert.Equal(0xFF, bitmap.Pixels[3]);
    }

    [Fact]
    public void BitmapBufferConverter_StridePadding_DimensionsMatch()
    {
        int width = 100, height = 50, stride = 420;
        byte[] pixels = new byte[stride * height];

        var bitmap = BitmapBufferConverter.StripPadding(pixels, width, height, stride);

        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
        Assert.Equal(width * 4, bitmap.Stride);
        Assert.Equal(width * 4 * height, bitmap.Pixels.Length);
    }

    [Fact]
    public void BitmapBufferConverter_StridePadding_StripsPaddingCorrectly()
    {
        int width = 2, height = 2, stride = 12;
        byte[] pixels = new byte[stride * height];

        pixels[0] = 0x10; pixels[1] = 0x20; pixels[2] = 0x30; pixels[3] = 0xFF;
        pixels[4] = 0x40; pixels[5] = 0x50; pixels[6] = 0x60; pixels[7] = 0xFF;
        pixels[8] = 0xAA; pixels[9] = 0xBB; pixels[10] = 0xCC; pixels[11] = 0xDD;

        pixels[12] = 0x70; pixels[13] = 0x80; pixels[14] = 0x90; pixels[15] = 0xFF;
        pixels[16] = 0xA0; pixels[17] = 0xB0; pixels[18] = 0xC0; pixels[19] = 0xFF;
        pixels[20] = 0xEE; pixels[21] = 0xFF; pixels[22] = 0x11; pixels[23] = 0x22;

        var bitmap = BitmapBufferConverter.StripPadding(pixels, width, height, stride);

        Assert.Equal(16, bitmap.Pixels.Length);
        Assert.Equal(0x10, bitmap.Pixels[0]);
        Assert.Equal(0x20, bitmap.Pixels[1]);
        Assert.Equal(0x30, bitmap.Pixels[2]);
        Assert.Equal(0xFF, bitmap.Pixels[3]);
        Assert.Equal(0x40, bitmap.Pixels[4]);
        Assert.Equal(0x50, bitmap.Pixels[5]);
        Assert.Equal(0x60, bitmap.Pixels[6]);
        Assert.Equal(0xFF, bitmap.Pixels[7]);
        Assert.Equal(0x70, bitmap.Pixels[8]);
        Assert.Equal(0x80, bitmap.Pixels[9]);
        Assert.Equal(0x90, bitmap.Pixels[10]);
        Assert.Equal(0xFF, bitmap.Pixels[11]);
        Assert.Equal(0xA0, bitmap.Pixels[12]);
        Assert.Equal(0xB0, bitmap.Pixels[13]);
        Assert.Equal(0xC0, bitmap.Pixels[14]);
        Assert.Equal(0xFF, bitmap.Pixels[15]);
    }

    [Fact]
    public void BitmapBufferConverter_NullPixels_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BitmapBufferConverter.StripPadding(null!, 100, 100, 400));
    }

    [Fact]
    public void BitmapBufferConverter_ZeroWidth_Throws()
    {
        byte[] pixels = new byte[400];
        Assert.Throws<ArgumentException>(() =>
            BitmapBufferConverter.StripPadding(pixels, 0, 100, 0));
    }

    [Fact]
    public void BitmapBufferConverter_ZeroHeight_Throws()
    {
        byte[] pixels = new byte[400];
        Assert.Throws<ArgumentException>(() =>
            BitmapBufferConverter.StripPadding(pixels, 100, 0, 400));
    }

    [Fact]
    public void BitmapBufferConverter_StrideTooSmall_Throws()
    {
        byte[] pixels = new byte[40000];
        Assert.Throws<ArgumentException>(() =>
            BitmapBufferConverter.StripPadding(pixels, 100, 50, 100));
    }

    [Fact]
    public void BitmapBufferConverter_BufferTooSmall_Throws()
    {
        byte[] pixels = new byte[100];
        Assert.Throws<ArgumentException>(() =>
            BitmapBufferConverter.StripPadding(pixels, 100, 50, 400));
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

        var bitmap = BitmapBufferConverter.StripPadding(safe.Pixels!, (int)safe.Width, (int)safe.Height, (int)safe.Stride);
        Assert.Equal((int)width, bitmap.Width);
        Assert.Equal((int)height, bitmap.Height);
        Assert.Equal((int)dataLen, bitmap.Pixels.Length);
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

        var bitmap = BitmapBufferConverter.StripPadding(safe.Pixels!, (int)width, (int)height, (int)stride);
        Assert.Equal((int)width, bitmap.Width);
        Assert.Equal((int)height, bitmap.Height);
        Assert.Equal((int)(width * 4 * height), bitmap.Pixels.Length);
    }
}
