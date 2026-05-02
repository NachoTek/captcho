//! Integration tests for window capture FFI export behavior and status invariants.
//!
//! These tests validate:
//! - The three S03 exports exist with correct C ABI signatures.
//! - Invalid HWND (0, stale) returns non-OK status with null frame data.
//! - Free functions remain null-safe with window capture results.
//! - Existing frame registry / struct layout expectations are unchanged.
//! - Error messages include phase labels but no raw pixels or user paths.
//!
//! These tests use synthetic data where possible and do NOT require an interactive
//! desktop session for most checks. The actual WGC capture calls will return
//! CaptureUnavailable in non-interactive environments (expected, per MEM031).

use respectacle_capture::*;

// ---------------------------------------------------------------------------
// Export existence and signature checks (compile-time verification)
// ---------------------------------------------------------------------------

#[test]
fn respectacle_capture_active_window_exists_with_correct_signature() {
    let _fn_ptr: extern "C" fn() -> CaptureResult = respectacle_capture_active_window;
}

#[test]
fn respectacle_capture_window_under_cursor_exists_with_correct_signature() {
    let _fn_ptr: extern "C" fn() -> CaptureResult = respectacle_capture_window_under_cursor;
}

#[test]
fn respectacle_capture_window_by_handle_exists_with_correct_signature() {
    let _fn_ptr: extern "C" fn(u64) -> CaptureResult = respectacle_capture_window_by_handle;
}

// ---------------------------------------------------------------------------
// Invalid HWND tests for respectacle_capture_window_by_handle
// ---------------------------------------------------------------------------

#[test]
fn capture_window_by_handle_zero_returns_error() {
    let result = respectacle_capture_window_by_handle(0);
    assert_ne!(
        result.status,
        CaptureStatus::Ok,
        "hwnd=0 must not return Ok"
    );
    assert!(
        result.frame_data.is_null(),
        "Error result must have null frame_data"
    );
    assert!(
        !result.error_message.is_null(),
        "Error result must have an error message"
    );
    unsafe { respectacle_free_capture_result(result) };
}

#[test]
fn capture_window_by_handle_zero_has_select_window_phase() {
    let result = respectacle_capture_window_by_handle(0);
    let msg = unsafe { std::ffi::CStr::from_ptr(result.error_message) };
    let msg_str = msg.to_str().unwrap_or("");
    assert!(
        msg_str.contains("select_window") || msg_str.contains("validate_window"),
        "Error message should contain a phase label, got: {}",
        msg_str
    );
    unsafe { respectacle_free_capture_result(result) };
}

#[test]
fn capture_window_by_handle_zero_has_zero_metadata() {
    let result = respectacle_capture_window_by_handle(0);
    assert_eq!(result.width, 0);
    assert_eq!(result.height, 0);
    assert_eq!(result.stride, 0);
    assert_eq!(result.data_len, 0);
    unsafe { respectacle_free_capture_result(result) };
}

#[test]
fn capture_window_by_handle_clearly_invalid_returns_error() {
    // Use a pointer-like value that is very unlikely to be a valid window
    let result = respectacle_capture_window_by_handle(0xDEADBEEFu64);
    assert_ne!(result.status, CaptureStatus::Ok);
    assert!(result.frame_data.is_null());
    assert!(!result.error_message.is_null());
    unsafe { respectacle_free_capture_result(result) };
}

#[test]
fn capture_window_by_handle_small_nonzero_returns_error() {
    // Small nonzero values are not valid HWNDs
    let result = respectacle_capture_window_by_handle(1);
    assert_ne!(result.status, CaptureStatus::Ok);
    assert!(result.frame_data.is_null());
    unsafe { respectacle_free_capture_result(result) };
}

#[test]
fn capture_window_by_handle_max_u64_returns_error() {
    let result = respectacle_capture_window_by_handle(u64::MAX);
    assert_ne!(result.status, CaptureStatus::Ok);
    assert!(result.frame_data.is_null());
    unsafe { respectacle_free_capture_result(result) };
}

// ---------------------------------------------------------------------------
// Active window and window-under-cursor structural validation
// ---------------------------------------------------------------------------

#[test]
fn capture_active_window_returns_valid_structure() {
    let result = respectacle_capture_active_window();
    if result.status == CaptureStatus::Ok {
        // WGC available — validate frame invariants
        assert!(!result.frame_data.is_null());
        assert!(result.width > 0);
        assert!(result.height > 0);
        assert!(result.stride >= result.width * 4);
        assert_eq!(result.data_len, result.stride * result.height);
        assert!(result.error_message.is_null());
    } else {
        // WGC unavailable (CI / non-interactive) — must be a proper error
        assert!(result.frame_data.is_null());
        assert!(!result.error_message.is_null());
    }
    unsafe { respectacle_free_capture_result(result) };
}

#[test]
fn capture_window_under_cursor_returns_valid_structure() {
    let result = respectacle_capture_window_under_cursor();
    if result.status == CaptureStatus::Ok {
        assert!(!result.frame_data.is_null());
        assert!(result.width > 0);
        assert!(result.height > 0);
        assert!(result.stride >= result.width * 4);
        assert_eq!(result.data_len, result.stride * result.height);
        assert!(result.error_message.is_null());
    } else {
        assert!(result.frame_data.is_null());
        assert!(!result.error_message.is_null());
    }
    unsafe { respectacle_free_capture_result(result) };
}

// ---------------------------------------------------------------------------
// Error message quality: phase labels and no sensitive data
// ---------------------------------------------------------------------------

#[test]
fn window_error_messages_contain_phase_labels() {
    // Test with invalid handle — error should reference select_window or validate_window
    let result = respectacle_capture_window_by_handle(0);
    let msg = unsafe { std::ffi::CStr::from_ptr(result.error_message) };
    let msg_str = msg.to_str().unwrap_or("");
    // Phase labels from our implementation
    let has_phase = msg_str.contains("select_window")
        || msg_str.contains("validate_window")
        || msg_str.contains("capture_frame")
        || msg_str.contains("validate[");
    assert!(
        has_phase,
        "Error message should contain a phase label, got: {}",
        msg_str
    );
    unsafe { respectacle_free_capture_result(result) };
}

#[test]
fn window_error_messages_are_sanitized() {
    let result = respectacle_capture_window_by_handle(999);
    if !result.error_message.is_null() {
        let msg = unsafe { std::ffi::CStr::from_ptr(result.error_message) };
        let msg_str = msg.to_str().unwrap_or("");
        // No file paths
        assert!(
            !msg_str.contains("C:\\"),
            "Error message should not contain file paths: {}",
            msg_str
        );
        // No raw pointer addresses (0x... pattern)
        assert!(
            !msg_str.contains("0x"),
            "Error message should not contain raw pointer addresses: {}",
            msg_str
        );
    }
    unsafe { respectacle_free_capture_result(result) };
}

// ---------------------------------------------------------------------------
// Null-safe free functions with window capture results
// ---------------------------------------------------------------------------

#[test]
fn free_error_result_from_window_capture_is_safe() {
    let result = respectacle_capture_window_by_handle(0);
    // Should not panic
    unsafe { respectacle_free_capture_result(result) };
}

#[test]
fn double_free_error_from_window_capture_is_safe() {
    let result = respectacle_capture_window_by_handle(0);
    // Free frame (null) and error message separately
    unsafe {
        respectacle_free_frame(result.frame_data);
        respectacle_free_frame(result.frame_data); // double free null
        respectacle_free_error_message(result.error_message);
    }
}

// ---------------------------------------------------------------------------
// Existing S01/S02 exports remain backward-compatible
// ---------------------------------------------------------------------------

#[test]
fn existing_monitor_exports_still_exist() {
    let _fn_ptr: extern "C" fn() -> CaptureResult = respectacle_capture_frame;
    let _fn_ptr: extern "C" fn(u32) -> CaptureResult = respectacle_capture_monitor_by_index;
    let _fn_ptr: extern "C" fn() -> CaptureResult = respectacle_capture_all_monitors;
}

#[test]
fn existing_free_functions_still_work() {
    let r = create_synthetic_frame(8, 8);
    assert_eq!(r.status, CaptureStatus::Ok);
    unsafe { respectacle_free_capture_result(r) };
}

#[test]
fn existing_synthetic_frame_invariants_hold() {
    let r = create_synthetic_frame(100, 50);
    assert_eq!(r.status, CaptureStatus::Ok);
    assert_eq!(r.data_len, r.stride * r.height);
    unsafe { respectacle_free_capture_result(r) };
}

// ---------------------------------------------------------------------------
// Struct layout unchanged: CaptureResult field offsets and CaptureStatus repr
// ---------------------------------------------------------------------------

#[test]
fn capture_status_values_still_unique() {
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

#[test]
fn capture_result_default_remains_not_implemented() {
    let r = CaptureResult::default();
    assert_eq!(r.status, CaptureStatus::NotImplemented);
    assert!(r.frame_data.is_null());
    assert_eq!(r.width, 0);
    assert_eq!(r.height, 0);
    assert_eq!(r.stride, 0);
    assert_eq!(r.data_len, 0);
    assert!(r.error_message.is_null());
}
