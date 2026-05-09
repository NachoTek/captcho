# S01 Capture Spike — Baseline Notes

## Purpose

This document records the FFI contract, build/run commands, expected output shape,
environment assumptions, and the observed performance baseline for the S01 Core Capture spike.
Downstream slices (especially S02 UI integration and R011 performance work) reference this baseline.

## FFI Contract Summary

The Rust `cdylib` (`captcho_capture.dll`) exports four C-ABI functions:

| Function | Signature | Description |
|---|---|---|
| `captcho_capture_frame` | `() -> CaptureResult` | Captures one frame from the primary monitor via Windows Graphics Capture. Returns a struct with status, pixel pointer, dimensions, stride, and optional error message. |
| `captcho_free_frame` | `(data: *mut u8)` | Frees pixel data allocated by Rust. Null-safe. |
| `captcho_free_error_message` | `(msg: *mut c_char)` | Frees error message string allocated by Rust. Null-safe. |
| `captcho_free_capture_result` | `(result: CaptureResult)` | Frees the entire capture result (frame data + error message). Null-safe. |

### CaptureResult Layout (#[repr(C), Sequential])

| Field | Type | Description |
|---|---|---|
| `status` | `i32` | 0 = Ok, negative = error code |
| `frame_data` | `*mut u8` | Pointer to BGRA pixel data (Rust-owned) |
| `width` | `u32` | Frame width in pixels |
| `height` | `u32` | Frame height in pixels |
| `stride` | `u32` | Row stride in bytes (≥ width × 4) |
| `data_len` | `u32` | Total buffer size = stride × height |
| `error_message` | `*mut c_char` | UTF-8 error string (Rust-owned), null on success |

## Build Commands

```powershell
# Rust: build + test
cargo test --manifest-path rust-dll/Cargo.toml
cargo build --manifest-path rust-dll/Cargo.toml --release

# .NET: build + test
dotnet build captcho.sln
dotnet test cs-tester.Tests/cs-tester.Tests.csproj

# Full build verification
powershell -ExecutionPolicy Bypass -File scripts/verify-s01-build.ps1

# End-to-end verification (build + capture + invariants)
powershell -ExecutionPolicy Bypass -File scripts/verify-s01-e2e.ps1
```

## Expected Success Output

```
PASS: status_ok
PASS: width_positive
PASS: height_positive
PASS: stride_valid
PASS: data_len_consistent
PASS: pixels_not_null
PASS: pixels_length_matches
PASS: bitmap_width_matches
PASS: bitmap_height_matches
Captured: OK 1920x1080 stride=7680 data_len=8294400
CapturedBitmap: 1920x1080 format=BGRA
RoundTripMs: <value>
VerifyResult: PASS
```

Dimensions will vary by monitor resolution and DPI scaling.

## Environment Assumptions

- **OS**: Windows 10 1903+ or Windows 11 (Graphics Capture API requirement)
- **Desktop**: Interactive desktop session required (WGC cannot capture in headless/service context)
- **Rust toolchain**: `cargo` on PATH, MSVC target for x86_64
- **.NET SDK**: .NET 8 SDK on PATH
- **Runtime**: `net8.0-windows` TFM — the C# project uses Windows-specific APIs
- **No WinRT SDK dependency**: Uses CapturedBitmap (lightweight managed container) instead of SoftwareBitmap to avoid UAP SDK issues

## Performance Baseline

> **Note**: The following values are recorded by running `scripts/verify-s01-e2e.ps1`
> on the local development machine. Update this section after a successful e2e run.

| Metric | Value |
|---|---|
| Resolution | 1920×1080 |
| Round-trip time | 365.9 ms |
| Stride | 7680 bytes |
| Data length | 8,294,400 bytes |
| Date | 2026-05-01 |

### Observed Results

All 9 invariants passed on first run. The 365.9ms round-trip includes native WGC capture initialization (first frame is slower due to Graphics Capture session setup), managed pixel copy, and CapturedBitmap conversion. Subsequent captures in a streaming scenario would be faster as the capture session remains active.

```
PASS: status_ok
PASS: width_positive
PASS: height_positive
PASS: stride_valid
PASS: data_len_consistent
PASS: pixels_not_null
PASS: pixels_length_matches
PASS: bitmap_width_matches
PASS: bitmap_height_matches
Captured: OK 1920x1080 stride=7680 data_len=8294400
CapturedBitmap: 1920x1080 format=BGRA
RoundTripMs: 365.9
VerifyResult: PASS
```

## Known Limitations

1. **Single capture only**: The spike captures one frame per invocation. Streaming is not implemented.
2. **Primary monitor only**: Captures from the default/primary display. Multi-monitor support is deferred.
3. **Memory copy overhead**: BGRA data is copied from native Rust memory to managed C# memory. Zero-copy approaches may be explored in performance work.
4. **CapturedBitmap vs SoftwareBitmap**: The spike uses a lightweight managed container. S02 will integrate with the real WinRT SoftwareBitmap when the WinUI 3 project is set up.

## Failure Scenarios

| Scenario | Expected output | Exit code |
|---|---|---|
| Missing DLL | `FAIL: native_capture — DLL not found` | 1 |
| Missing export | `FAIL: native_capture — Export not found` | 1 |
| WGC unavailable | `FAIL: status_ok — status=CaptureUnavailable` | 1 |
| Permission denied | `FAIL: status_ok — status=PermissionDenied` | 1 |
| Capture timeout | `FAIL: status_ok — status=Timeout` | 1 |
| Headless session | `FAIL: status_ok — status=InternalError` or `CaptureUnavailable` | 1 |
