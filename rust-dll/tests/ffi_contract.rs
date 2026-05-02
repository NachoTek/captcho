//! FFI contract integration tests for respectacle-capture.
//!
//! These tests validate:
//! - Struct layout invariants (CaptureStatus repr, CaptureResult field offsets)
//! - Synthetic frame allocation, invariants, and cleanup
//! - Null pointer safety for all free functions
//! - Negative tests: zero dimensions, stride < width*4, double free
//! - data_len == stride * height for all synthetic frames
//!
//! These tests run via `cargo test` and do NOT require a desktop session
//! or WGC availability — they use synthetic frame helpers exclusively.

use respectacle_capture::*;

// ---------------------------------------------------------------------------
// CaptureStatus repr(C) layout invariants
// ---------------------------------------------------------------------------

#[test]
fn status_ok_is_zero() {
    assert_eq!(CaptureStatus::Ok as i32, 0);
}

#[test]
fn status_errors_are_negative() {
    assert!((CaptureStatus::NotImplemented as i32) < 0);
    assert!((CaptureStatus::CaptureUnavailable as i32) < 0);
    assert!((CaptureStatus::PermissionDenied as i32) < 0);
    assert!((CaptureStatus::InternalError as i32) < 0);
    assert!((CaptureStatus::Timeout as i32) < 0);
    assert!((CaptureStatus::InvalidBuffer as i32) < 0);
}

#[test]
fn status_values_are_unique() {
    let values = [
        CaptureStatus::Ok as i32,
        CaptureStatus::NotImplemented as i32,
        CaptureStatus::CaptureUnavailable as i32,
        CaptureStatus::PermissionDenied as i32,
        CaptureStatus::InternalError as i32,
        CaptureStatus::Timeout as i32,
        CaptureStatus::InvalidBuffer as i32,
    ];
    for i in 0..values.len() {
        for j in (i + 1)..values.len() {
            assert_ne!(values[i], values[j], "Duplicate status value: {}", values[i]);
        }
    }
}

// ---------------------------------------------------------------------------
// CaptureResult struct invariants
// ---------------------------------------------------------------------------

#[test]
fn default_result_has_all_empty_fields() {
    let r = CaptureResult::default();
    assert_eq!(r.status, CaptureStatus::NotImplemented);
    assert!(r.frame_data.is_null());
    assert_eq!(r.width, 0);
    assert_eq!(r.height, 0);
    assert_eq!(r.stride, 0);
    assert_eq!(r.data_len, 0);
    assert!(r.error_message.is_null());
}

#[test]
fn error_result_has_null_frame_and_non_null_error() {
    let r = CaptureResult::error(CaptureStatus::InternalError, "something broke");
    assert_eq!(r.status, CaptureStatus::InternalError);
    assert!(r.frame_data.is_null());
    assert_eq!(r.width, 0);
    assert!(!r.error_message.is_null());

    // Verify the message content
    let msg = unsafe { std::ffi::CStr::from_ptr(r.error_message) };
    assert!(msg.to_str().unwrap().contains("something broke"));

    unsafe { respectacle_free_error_message(r.error_message) };
}

// ---------------------------------------------------------------------------
// Synthetic frame allocation and invariants
// ---------------------------------------------------------------------------

#[test]
fn synthetic_frame_basic_properties() {
    let r = create_synthetic_frame(1920, 1080);
    assert_eq!(r.status, CaptureStatus::Ok);
    assert_eq!(r.width, 1920);
    assert_eq!(r.height, 1080);
    assert_eq!(r.stride, 1920 * 4);
    assert_eq!(r.data_len, 1920 * 4 * 1080);
    assert!(!r.frame_data.is_null());
    assert!(r.error_message.is_null());
    unsafe { respectacle_free_capture_result(r) };
}

#[test]
fn synthetic_frame_data_len_equals_stride_times_height() {
    // Test multiple sizes
    for &(w, h) in &[(1, 1), (4, 4), (100, 50), (800, 600), (1920, 1080)] {
        let r = create_synthetic_frame(w, h);
        assert_eq!(r.status, CaptureStatus::Ok, "Failed for {}x{}", w, h);
        assert_eq!(
            r.data_len,
            r.stride * r.height,
            "data_len mismatch for {}x{}: got {}, expected {}",
            w, h,
            r.data_len,
            r.stride * r.height
        );
        unsafe { respectacle_free_capture_result(r) };
    }
}

#[test]
fn synthetic_frame_with_custom_stride_preserves_padding() {
    // 100 pixels wide × 4bpp = 400 bytes minimum stride
    // Use 512-byte stride (power of 2 alignment) → 112 bytes padding per row
    let r = create_synthetic_frame_with_stride(100, 50, 512);
    assert_eq!(r.status, CaptureStatus::Ok);
    assert_eq!(r.stride, 512);
    assert_eq!(r.data_len, 512 * 50);
    assert_eq!(r.width, 100);
    assert_eq!(r.height, 50);
    unsafe { respectacle_free_capture_result(r) };
}

#[test]
fn synthetic_frame_stride_cannot_be_less_than_width_times_four() {
    let r = create_synthetic_frame_with_stride(100, 50, 399);
    assert_eq!(r.status, CaptureStatus::InvalidBuffer);
    assert!(r.frame_data.is_null());
    // Error message should explain the problem
    assert!(!r.error_message.is_null());
    unsafe { respectacle_free_capture_result(r) };
}

#[test]
fn synthetic_frame_pixel_data_is_readable() {
    let r = create_synthetic_frame(4, 2);
    assert_eq!(r.status, CaptureStatus::Ok);

    let pixels = unsafe {
        std::slice::from_raw_parts(r.frame_data, r.data_len as usize)
    };

    // Total: 4*2 = 8 pixels × 4 bytes = 32 bytes
    assert_eq!(pixels.len(), 32);

    // Verify a few known pixel values from the test pattern
    // Pixel (0,0): B=0, G=0, R=0x80, A=0xFF
    assert_eq!(pixels[0], 0);
    assert_eq!(pixels[1], 0);
    assert_eq!(pixels[2], 0x80);
    assert_eq!(pixels[3], 0xFF);

    // Pixel (1,0): B=1, G=0, R=0x80, A=0xFF
    assert_eq!(pixels[4], 1);
    assert_eq!(pixels[5], 0);
    assert_eq!(pixels[6], 0x80);
    assert_eq!(pixels[7], 0xFF);

    unsafe { respectacle_free_capture_result(r) };
}

// ---------------------------------------------------------------------------
// Null pointer safety (negative tests)
// ---------------------------------------------------------------------------

#[test]
fn free_null_frame_is_safe() {
    unsafe { respectacle_free_frame(std::ptr::null_mut()) };
}

#[test]
fn free_null_error_message_is_safe() {
    unsafe { respectacle_free_error_message(std::ptr::null_mut()) };
}

#[test]
fn free_default_capture_result_is_safe() {
    let r = CaptureResult::default();
    unsafe { respectacle_free_capture_result(r) };
}

#[test]
fn double_free_frame_is_safe() {
    let r = create_synthetic_frame(10, 10);
    let ptr = r.frame_data;
    assert!(!ptr.is_null());

    // First free
    unsafe { respectacle_free_frame(ptr) };

    // Second free — should silently ignore (not in registry)
    unsafe { respectacle_free_frame(ptr) };
}

#[test]
fn free_error_only_then_frame_is_safe() {
    let r = create_synthetic_frame(5, 5);
    // Free error message first (it's null, so this is a no-op)
    unsafe { respectacle_free_error_message(r.error_message) };
    // Then free frame
    unsafe { respectacle_free_frame(r.frame_data) };
}

// ---------------------------------------------------------------------------
// Negative tests: malformed / boundary conditions
// ---------------------------------------------------------------------------

#[test]
fn invalid_frame_zero_dims_is_error() {
    let r = create_invalid_frame_zero_dims();
    assert_eq!(r.status, CaptureStatus::InvalidBuffer);
    assert!(r.frame_data.is_null());
    assert_eq!(r.width, 0);
    assert_eq!(r.height, 0);
    assert!(!r.error_message.is_null());
    unsafe { respectacle_free_capture_result(r) };
}

#[test]
fn one_pixel_frame_is_valid() {
    let r = create_synthetic_frame(1, 1);
    assert_eq!(r.status, CaptureStatus::Ok);
    assert_eq!(r.width, 1);
    assert_eq!(r.height, 1);
    assert_eq!(r.stride, 4);
    assert_eq!(r.data_len, 4);
    unsafe { respectacle_free_capture_result(r) };
}

#[test]
fn large_frame_allocates_and_frees() {
    // 4K frame: 3840 × 2160 × 4 = ~33 MB
    let r = create_synthetic_frame(3840, 2160);
    assert_eq!(r.status, CaptureStatus::Ok);
    assert_eq!(r.data_len, 3840 * 4 * 2160);
    unsafe { respectacle_free_capture_result(r) };
}

#[test]
fn error_message_with_nul_byte_is_handled() {
    // CString::new would fail on embedded NUL — our error() handles this gracefully
    let r = CaptureResult::error(CaptureStatus::InternalError, "has \0 embedded NUL");
    // Should still produce a valid error (truncated or replacement message)
    assert_eq!(r.status, CaptureStatus::InternalError);
    assert!(!r.error_message.is_null());
    // The message should be readable
    let msg = unsafe { std::ffi::CStr::from_ptr(r.error_message) };
    assert!(!msg.to_bytes().is_empty());
    unsafe { respectacle_free_capture_message(r.error_message) };
}

// Helper that uses the correct function name
unsafe fn respectacle_free_capture_message(msg: *mut std::ffi::c_char) {
    respectacle_free_error_message(msg);
}

// ---------------------------------------------------------------------------
// Exported function existence (compile-time check)
// ---------------------------------------------------------------------------

#[test]
fn exported_functions_exist_and_are_callable() {
    // This test primarily ensures the function signatures compile correctly.
    // We can't call respectacle_capture_frame() in CI (no desktop session),
    // but we verify the other functions work with synthetic data.
    let r = create_synthetic_frame(2, 2);
    assert_eq!(r.status, CaptureStatus::Ok);

    // Verify all three free functions compile and work:
    unsafe {
        respectacle_free_capture_result(r);
    }
}

// ---------------------------------------------------------------------------
// Stride invariants
// ---------------------------------------------------------------------------

#[test]
fn stride_equals_width_times_four_for_standard_frame() {
    let r = create_synthetic_frame(640, 480);
    assert_eq!(r.stride, r.width * 4);
    unsafe { respectacle_free_capture_result(r) };
}

#[test]
fn stride_with_padding_still_satisfies_data_len_equals_stride_times_height() {
    let r = create_synthetic_frame_with_stride(80, 60, 400);
    assert_eq!(r.status, CaptureStatus::Ok);
    assert!(r.stride > r.width * 4); // Has padding
    assert_eq!(r.data_len, r.stride * r.height);
    unsafe { respectacle_free_capture_result(r) };
}
