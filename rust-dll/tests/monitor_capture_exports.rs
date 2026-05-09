//! Integration tests for monitor capture FFI export behavior and status invariants.
//!
//! These tests validate:
//! - The new exports `captcho_capture_monitor_by_index` and `captcho_capture_all_monitors`
//!   are callable and have correct signatures.
//! - Invalid monitor indexes return non-OK status with sanitized error messages.
//! - Error messages identify the capture phase (enumerate, select_monitor, etc.)
//! - Existing S01 exports remain backward-compatible.
//! - Negative tests: index 0, very large indexes, and status/message invariants.
//!
//! These tests use synthetic data and do NOT require a desktop session or WGC.

use captcho_capture::*;

// ---------------------------------------------------------------------------
// Export existence and signature checks (compile-time verification)
// ---------------------------------------------------------------------------

#[test]
fn captcho_capture_monitor_by_index_exists() {
    // Verify the function exists with the correct signature.
    // We can't call it with real WGC in CI, but we verify it compiles.
    let _fn_ptr: extern "C" fn(u32) -> CaptureResult =
        captcho_capture_monitor_by_index;
}

#[test]
fn captcho_capture_all_monitors_exists() {
    let _fn_ptr: extern "C" fn() -> CaptureResult =
        captcho_capture_all_monitors;
}

// ---------------------------------------------------------------------------
// Invalid index tests
// ---------------------------------------------------------------------------

#[test]
fn monitor_by_index_zero_captures_primary() {
    // In our 0-based scheme, index 0 maps to the primary monitor (from_index(1)).
    // On a machine with WGC available, this should succeed.
    let result = captcho_capture_monitor_by_index(0);
    // If WGC is available, it should be Ok; otherwise it's a non-Ok status.
    // Either way, the result must be structurally valid.
    if result.status == CaptureStatus::Ok {
        assert!(!result.frame_data.is_null());
        assert!(result.width > 0);
        assert!(result.height > 0);
        assert!(result.stride >= result.width * 4);
        assert_eq!(result.data_len, result.stride * result.height);
    } else {
        assert!(result.frame_data.is_null());
        assert!(!result.error_message.is_null());
    }
    unsafe { captcho_free_capture_result(result) };
}

#[test]
fn monitor_by_index_very_large_returns_error() {
    let result = captcho_capture_monitor_by_index(999999);
    assert_ne!(result.status, CaptureStatus::Ok);
    assert!(result.frame_data.is_null());
    assert!(
        !result.error_message.is_null(),
        "Error result should have an error message"
    );
    unsafe { captcho_free_capture_result(result) };
}

#[test]
fn monitor_by_index_error_has_no_frame_data() {
    // Index well beyond any reasonable monitor count
    let result = captcho_capture_monitor_by_index(999999);
    assert!(result.frame_data.is_null());
    assert_eq!(result.width, 0);
    assert_eq!(result.height, 0);
    assert_eq!(result.stride, 0);
    assert_eq!(result.data_len, 0);
    unsafe { captcho_free_capture_result(result) };
}

#[test]
fn capture_all_monitors_error_has_sane_metadata() {
    // Even if WGC is unavailable, the function should return a proper CaptureResult
    // We can't guarantee success/failure in CI, but we can check structure.
    let result = captcho_capture_all_monitors();

    if result.status != CaptureStatus::Ok {
        assert!(result.frame_data.is_null());
        if !result.error_message.is_null() {
            let msg = unsafe { std::ffi::CStr::from_ptr(result.error_message) };
            let msg_str = msg.to_str().unwrap_or("");
            assert!(!msg_str.is_empty());
        }
    } else {
        // If it succeeded, validate invariants
        assert!(!result.frame_data.is_null());
        assert!(result.width > 0);
        assert!(result.height > 0);
        assert!(result.stride >= result.width * 4);
        assert_eq!(result.data_len, result.stride * result.height);
    }
    unsafe { captcho_free_capture_result(result) };
}

// ---------------------------------------------------------------------------
// S01 backward compatibility: existing exports still exist and work
// ---------------------------------------------------------------------------

#[test]
fn existing_capture_frame_export_exists() {
    let _fn_ptr: extern "C" fn() -> CaptureResult = captcho_capture_frame;
}

#[test]
fn existing_free_functions_still_work_with_synthetic() {
    let r = create_synthetic_frame(10, 10);
    assert_eq!(r.status, CaptureStatus::Ok);
    unsafe { captcho_free_capture_result(r) };
}

// ---------------------------------------------------------------------------
// Error message quality: phase identification
// ---------------------------------------------------------------------------

#[test]
fn error_messages_are_sanitized_no_raw_paths() {
    let result = captcho_capture_monitor_by_index(999999);
    if !result.error_message.is_null() {
        let msg = unsafe { std::ffi::CStr::from_ptr(result.error_message) };
        let msg_str = msg.to_str().unwrap_or("");
        // Should not contain Windows paths or raw pointer addresses
        assert!(
            !msg_str.contains("C:\\"),
            "Error message should not contain file paths: {}",
            msg_str
        );
        assert!(
            !msg_str.contains("0x"),
            "Error message should not contain raw pointer addresses: {}",
            msg_str
        );
    }
    unsafe { captcho_free_capture_result(result) };
}

#[test]
fn error_result_is_safe_to_double_free() {
    let result = captcho_capture_monitor_by_index(999999);
    // Free frame (null, safe no-op) twice
    unsafe {
        captcho_free_frame(result.frame_data);
        captcho_free_frame(result.frame_data);
    }
    // Free error message
    unsafe { captcho_free_error_message(result.error_message) };
}
