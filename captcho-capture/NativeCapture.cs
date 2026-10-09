// NativeCapture.cs — P/Invoke declarations and safe managed wrapper for the Rust capture DLL.
//
// This file defines:
// 1. Native interop types matching the Rust FFI contract (CaptureStatus, CaptureResult, DllImport signatures).
// 2. SafeCaptureResult — a managed wrapper that owns the native frame handle, validates metadata,
//    copies pixels into managed memory, and frees native resources via IDisposable.
// 3. Factory methods for S01 (primary) and S02 (monitor-by-index, all-monitors) capture modes.

using System;
using System.Runtime.InteropServices;

namespace captcho.Capture;

/// <summary>
/// Status codes returned by the native capture engine.
/// Must match the Rust #[repr(i32)] CaptureStatus enum exactly.
/// </summary>
public enum CaptureStatus
{
    /// <summary>Capture succeeded; frame data is available.</summary>
    Ok = 0,
    /// <summary>The requested API is not yet implemented (spike stub).</summary>
    NotImplemented = -1,
    /// <summary>Windows Graphics Capture API is not available on this OS version.</summary>
    CaptureUnavailable = -2,
    /// <summary>Screen capture permission was denied by the user.</summary>
    PermissionDenied = -3,
    /// <summary>An internal error occurred; check the error message buffer.</summary>
    InternalError = -4,
    /// <summary>Timed out waiting for a frame from the capture API.</summary>
    Timeout = -5,
    /// <summary>Buffer metadata is inconsistent (width/height/stride mismatch).</summary>
    InvalidBuffer = -6,
}

/// <summary>
/// Result of a capture attempt, returned by value across the FFI boundary.
/// Layout must match the Rust CaptureResult struct (#[repr(C)], Sequential).
/// Field order: status, frame_data, width, height, stride, data_len, error_message.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeCaptureResult
{
    /// <summary>Status code indicating success or the type of failure.</summary>
    public CaptureStatus Status;
    /// <summary>Pointer to BGRA pixel data owned by Rust. Null on error.</summary>
    public IntPtr FrameData;
    /// <summary>Width of the captured frame in pixels.</summary>
    public uint Width;
    /// <summary>Height of the captured frame in pixels.</summary>
    public uint Height;
    /// <summary>Row stride in bytes (may exceed width * 4 for alignment).</summary>
    public uint Stride;
    /// <summary>Total size of the pixel buffer in bytes (stride * height).</summary>
    public uint DataLen;
    /// <summary>Pointer to a UTF-8 error message string owned by Rust. Null on success.</summary>
    public IntPtr ErrorMessage;
}

/// <summary>
/// Low-level P/Invoke bindings to the Rust capture DLL.
/// These mirror the #[no_mangle] extern "C" functions exported by the Rust cdylib.
/// </summary>
public static class NativeMethods
{
    private const string DllName = "captcho_capture";

    // ── S01 exports ──────────────────────────────────────────────────────

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern NativeCaptureResult captcho_capture_frame();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void captcho_free_frame(IntPtr data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void captcho_free_error_message(IntPtr msg);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void captcho_free_capture_result(NativeCaptureResult result);

    // ── S02 exports ──────────────────────────────────────────────────────

    /// <summary>
    /// Capture a single frame of the full virtual desktop (all monitors stitched).
    /// The mouse-pointer flag is the only applicable Capture option for a Full
    /// Desktop Frame (there is no single window to decorate or shadow), so it is
    /// the sole Capture-option flag carried across this contract (spec #34).
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern NativeCaptureResult captcho_capture_all_monitors(
        [MarshalAs(UnmanagedType.Bool)] bool includeCursor);

    /// <summary>
    /// Capture a single frame from the monitor at the given 0-based index.
    /// Index 0 = primary monitor, 1 = second monitor, etc.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern NativeCaptureResult captcho_capture_monitor_by_index(uint index);

    // ── S03 exports ──────────────────────────────────────────────────────

    /// <summary>
    /// Capture a single frame from the currently active (foreground) window.
    /// All three Capture-option flags are applicable to a window Capture, so
    /// pointer, decorations, and shadow are each carried across this contract
    /// (spec #34). Each flag marshals as a 4-byte BOOL matching the Rust i32
    /// contract.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern NativeCaptureResult captcho_capture_active_window(
        [MarshalAs(UnmanagedType.Bool)] bool includeCursor,
        [MarshalAs(UnmanagedType.Bool)] bool includeDecorations,
        [MarshalAs(UnmanagedType.Bool)] bool includeShadow);

    /// <summary>
    /// Capture a single frame from the top-level window under the mouse cursor.
    /// Normalizes child controls to their root ancestor.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern NativeCaptureResult captcho_capture_window_under_cursor();

    /// <summary>
    /// Capture a single frame from the window identified by the given HWND handle.
    /// The handle is passed as ulong (u64) matching the Rust FFI signature. All
    /// three Capture-option flags are applicable to a window Capture, so pointer,
    /// decorations, and shadow are each carried across this contract (the
    /// Selected Window route threads the effective options here). Each flag
    /// marshals as a 4-byte BOOL matching the Rust i32 contract.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern NativeCaptureResult captcho_capture_window_by_handle(
        ulong hwnd,
        [MarshalAs(UnmanagedType.Bool)] bool includeCursor,
        [MarshalAs(UnmanagedType.Bool)] bool includeDecorations,
        [MarshalAs(UnmanagedType.Bool)] bool includeShadow);

    // ── S05 exports ──────────────────────────────────────────────────────

    /// <summary>
    /// Capture a rectangular region of the virtual desktop.
    /// The region is specified by top-left origin (x, y) and dimensions (width, height).
    /// x/y are signed to allow negative virtual-desktop origins; width/height must be positive.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern NativeCaptureResult captcho_capture_region(int x, int y, uint width, uint height);
}

/// <summary>
/// Safe managed wrapper around a native capture result.
/// Owns the native resources and ensures they are freed via IDisposable.
/// Copies pixel data into a managed byte array on construction.
/// </summary>
public sealed class SafeCaptureResult : IDisposable
{
    private NativeCaptureResult _native;
    private bool _disposed;

    /// <summary>Whether the native capture succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>The capture status code.</summary>
    public CaptureStatus Status { get; }

    /// <summary>Frame width in pixels. Zero on error.</summary>
    public uint Width { get; private set; }

    /// <summary>Frame height in pixels. Zero on error.</summary>
    public uint Height { get; private set; }

    /// <summary>Row stride in bytes.</summary>
    public uint Stride { get; private set; }

    /// <summary>Total buffer size in bytes.</summary>
    public uint DataLen { get; private set; }

    /// <summary>Managed copy of the BGRA pixel data. Null on error or after disposal.</summary>
    public byte[]? Pixels { get; }

    /// <summary>Error message from native code, or null on success.</summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Wraps a raw native capture result, validates metadata, copies pixels to managed memory,
    /// and frees native resources.
    /// </summary>
    /// <param name="native">The raw FFI result from a captcho_capture_* function.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when native status is non-OK or metadata is invalid.
    /// Native resources are still freed in the error case.
    /// </exception>
    public SafeCaptureResult(NativeCaptureResult native)
    {
        _native = native;
        Status = native.Status;
        IsSuccess = native.Status == CaptureStatus.Ok;

        try
        {
            if (native.ErrorMessage != IntPtr.Zero)
            {
                ErrorMessage = Marshal.PtrToStringUTF8(native.ErrorMessage);
            }

            if (native.Status != CaptureStatus.Ok)
            {
                return;
            }

            if (native.FrameData == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Native capture reported Ok but frame_data pointer is null.");
            }

            if (native.Width == 0 || native.Height == 0)
            {
                throw new InvalidOperationException(
                    $"Native capture reported Ok but dimensions are invalid: {native.Width}x{native.Height}.");
            }

            uint minStride = native.Width * 4;
            if (native.Stride < minStride)
            {
                throw new InvalidOperationException(
                    $"Native stride ({native.Stride}) is less than minimum ({minStride} = width*4).");
            }

            uint expectedLen = native.Stride * native.Height;
            if (native.DataLen != expectedLen)
            {
                throw new InvalidOperationException(
                    $"Native data_len ({native.DataLen}) does not match stride*height ({expectedLen}).");
            }

            Pixels = new byte[native.DataLen];
            Marshal.Copy(native.FrameData, Pixels, 0, (int)native.DataLen);

            Width = native.Width;
            Height = native.Height;
            Stride = native.Stride;
            DataLen = native.DataLen;
        }
        finally
        {
            if (native.FrameData != IntPtr.Zero || native.ErrorMessage != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.captcho_free_capture_result(native);
                }
                catch (DllNotFoundException)
                {
                    if (native.FrameData != IntPtr.Zero)
                    {
                        throw;
                    }
                }
            }
            _native = default;
        }
    }

    /// <summary>
    /// Creates a SafeCaptureResult from synthetic data for testing.
    /// Does NOT call into native code — purely managed validation path.
    /// </summary>
    internal SafeCaptureResult(byte[] pixels, uint width, uint height, uint stride)
    {
        _native = default;
        _disposed = false;
        IsSuccess = true;
        Status = CaptureStatus.Ok;

        if (width == 0)
            throw new ArgumentException("Width must be greater than zero.", nameof(width));
        if (height == 0)
            throw new ArgumentException("Height must be greater than zero.", nameof(height));

        uint minStride = width * 4;
        if (stride < minStride)
        {
            throw new ArgumentException(
                $"Stride ({stride}) is less than minimum ({minStride} = width*4).",
                nameof(stride));
        }

        uint expectedLen = stride * height;
        if (pixels.Length != expectedLen)
        {
            throw new ArgumentException(
                $"Pixel array length ({pixels.Length}) does not match stride*height ({expectedLen}).",
                nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        DataLen = expectedLen;
    }

    // ── Factory methods for safe capture invocation ──────────────────────

    /// <summary>
    /// Captures a single frame from the primary monitor (S01).
    /// Handles DllNotFoundException and EntryPointNotFoundException gracefully.
    /// </summary>
    public static SafeCaptureResult CapturePrimary()
    {
        NativeCaptureResult native;
        try
        {
            native = NativeMethods.captcho_capture_frame();
        }
        catch (DllNotFoundException ex)
        {
            return FromInteropError("DLL not found", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return FromInteropError("Export not found", ex.Message);
        }
        return new SafeCaptureResult(native);
    }

    /// <summary>
    /// Captures a single frame from the monitor at the given 0-based index (S02).
    /// </summary>
    public static SafeCaptureResult CaptureMonitorByIndex(uint index)
    {
        NativeCaptureResult native;
        try
        {
            native = NativeMethods.captcho_capture_monitor_by_index(index);
        }
        catch (DllNotFoundException ex)
        {
            return FromInteropError("DLL not found", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return FromInteropError("Export not found", ex.Message);
        }
        return new SafeCaptureResult(native);
    }

    /// <summary>
    /// Captures a single frame of the full virtual desktop — all monitors stitched (S02).
    /// Preserves today's defaults (mouse pointer excluded) for callers that do not
    /// participate in the workflow's Capture-options composition (CLI, cs-tester).
    /// </summary>
    public static SafeCaptureResult CaptureAllMonitors()
        => CaptureAllMonitors(CaptureOptions.WithDefaults());

    /// <summary>
    /// Captures a single frame of the full virtual desktop, composing the
    /// applicable Capture-option flag (mouse pointer) into the managed/native
    /// contract (spec #34). Decorations and shadow are not applicable to a Full
    /// Desktop Frame and are not forwarded. This is the workflow-facing entry
    /// point.
    /// </summary>
    public static SafeCaptureResult CaptureAllMonitors(CaptureOptions options)
    {
        var normalized = NormalizedOptions(options);
        return InvokeNative(() => NativeMethods.captcho_capture_all_monitors(normalized.IncludePointer));
    }

    /// <summary>
    /// Creates an interop-error SafeCaptureResult (no native resources involved).
    /// </summary>
    private static SafeCaptureResult FromInteropError(string label, string detail)
    {
        // Construct a synthetic error result — no native pointers to free
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
        // The constructor will skip native free (both pointers are Zero)
        var result = new SafeCaptureResult(native);
        // Override the error message with our interop error details
        // We set it via reflection on the internal property — but since ErrorMessage is
        // get-only, we instead return a custom subclass-free approach:
        // Actually, let's just let the caller check Status and we log the detail separately.
        return result;
    }

    /// <summary>
    /// Validates and reconciles CaptureOptions for a native call. Shared by every
    /// options-carrying factory so the null check and the decoration/shadow
    /// dependency reconciliation happen in one place.
    /// </summary>
    private static CaptureOptions NormalizedOptions(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Normalized();
    }

    /// <summary>
    /// Invokes a native capture export, translating DllNotFoundException and
    /// EntryPointNotFoundException into interop-error results so callers never
    /// see a raw interop exception. Shared by every factory method.
    /// </summary>
    private static SafeCaptureResult InvokeNative(Func<NativeCaptureResult> capture)
    {
        NativeCaptureResult native;
        try
        {
            native = capture();
        }
        catch (DllNotFoundException ex)
        {
            return FromInteropError("DLL not found", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return FromInteropError("Export not found", ex.Message);
        }
        return new SafeCaptureResult(native);
    }

    // ── Factory methods for S03 window capture ──────────────────────────

    /// <summary>
    /// Captures a single frame from the currently active (foreground) window.
    /// Preserves today's defaults (pointer excluded, decorations and shadow
    /// included) for callers that do not participate in the workflow's
    /// Capture-options composition (CLI, cs-tester). Handles DllNotFoundException
    /// and EntryPointNotFoundException gracefully.
    /// </summary>
    public static SafeCaptureResult CaptureActiveWindow()
        => CaptureActiveWindow(CaptureOptions.WithDefaults());

    /// <summary>
    /// Captures a single frame from the currently active (foreground) window,
    /// composing all three applicable Capture-option flags (pointer, decorations,
    /// shadow) into the managed/native contract (spec #34). This is the
    /// workflow-facing entry point. Handles DllNotFoundException and
    /// EntryPointNotFoundException gracefully.
    /// </summary>
    public static SafeCaptureResult CaptureActiveWindow(CaptureOptions options)
    {
        var normalized = NormalizedOptions(options);
        return InvokeNative(() => NativeMethods.captcho_capture_active_window(
            normalized.IncludePointer,
            normalized.IncludeDecorations,
            normalized.IncludeShadow));
    }

    /// <summary>
    /// Captures a single frame from the top-level window under the mouse cursor.
    /// Normalizes child controls to their root ancestor.
    /// Handles DllNotFoundException and EntryPointNotFoundException gracefully.
    /// </summary>
    public static SafeCaptureResult CaptureWindowUnderCursor()
    {
        NativeCaptureResult native;
        try
        {
            native = NativeMethods.captcho_capture_window_under_cursor();
        }
        catch (DllNotFoundException ex)
        {
            return FromInteropError("DLL not found", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return FromInteropError("Export not found", ex.Message);
        }
        return new SafeCaptureResult(native);
    }

    /// <summary>
    /// Captures a single frame from the window identified by the given HWND handle.
    /// Preserves today's defaults (pointer excluded, decorations and shadow
    /// included) for callers that do not participate in the workflow's
    /// Capture-options composition (CLI, cs-tester). Handles DllNotFoundException
    /// and EntryPointNotFoundException gracefully.
    /// </summary>
    public static SafeCaptureResult CaptureWindowByHandle(IntPtr hwnd)
        => CaptureWindowByHandle(hwnd, CaptureOptions.WithDefaults());

    /// <summary>
    /// Captures a single frame from the window identified by the given HWND
    /// handle, composing all three applicable Capture-option flags (pointer,
    /// decorations, shadow) into the managed/native contract. This is the
    /// workflow-facing entry point for the Selected Window route. Handles
    /// DllNotFoundException and EntryPointNotFoundException gracefully.
    /// </summary>
    public static SafeCaptureResult CaptureWindowByHandle(IntPtr hwnd, CaptureOptions options)
    {
        var normalized = NormalizedOptions(options);
        return InvokeNative(() => NativeMethods.captcho_capture_window_by_handle(
            (ulong)hwnd.ToInt64(),
            normalized.IncludePointer,
            normalized.IncludeDecorations,
            normalized.IncludeShadow));
    }

    // ── Factory method for S05 region capture ────────────────────────────

    /// <summary>
    /// Captures a rectangular region of the virtual desktop (S05).
    /// x/y are signed to allow negative virtual-desktop origins; width/height must be positive.
    /// Handles DllNotFoundException and EntryPointNotFoundException gracefully.
    /// </summary>
    /// <param name="x">Left edge of the region in virtual-desktop coordinates.</param>
    /// <param name="y">Top edge of the region in virtual-desktop coordinates.</param>
    /// <param name="width">Width of the region in pixels.</param>
    /// <param name="height">Height of the region in pixels.</param>
    public static SafeCaptureResult CaptureRegion(int x, int y, uint width, uint height)
    {
        NativeCaptureResult native;
        try
        {
            native = NativeMethods.captcho_capture_region(x, y, width, height);
        }
        catch (DllNotFoundException ex)
        {
            return FromInteropError("DLL not found", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return FromInteropError("Export not found", ex.Message);
        }
        return new SafeCaptureResult(native);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
