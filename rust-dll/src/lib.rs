//! captcho Capture Engine — Rust cdylib with Windows Graphics Capture
//!
//! This module implements the C ABI contract that the C# host calls via P/Invoke.
//! It captures a single frame from the primary monitor using Windows Graphics Capture
//! (via the `windows-capture` crate), copies BGRA pixel data into Rust-owned memory,
//! and exposes it through a `CaptureResult` struct.
//!
//! # Exported Functions
//!
//! - `captcho_capture_frame()` — Capture one frame from the primary monitor.
//! - `captcho_free_frame(data)` — Free frame pixel data.
//! - `captcho_free_error_message(msg)` — Free error message string.
//! - `captcho_free_capture_result(result)` — Free entire capture result (frame + error).
//!
//! # Memory Ownership
//!
//! All pointers returned across the FFI boundary are owned by Rust. The C# caller
//! MUST call the matching free function when done. Failure to do so leaks memory.
//! All free functions are null-safe.

use std::ffi::CString;
use std::sync::{Arc, Mutex, atomic::{AtomicBool, Ordering}};

pub mod monitor_utils;

// ---------------------------------------------------------------------------
// FFI-safe types
// ---------------------------------------------------------------------------

/// Opaque handle to captured frame buffer owned by Rust.
/// The C# side never interprets this — it only passes it back to `captcho_free_frame`.
pub type FrameHandle = *mut u8;

/// Status codes returned across the FFI boundary.
/// Non-negative = success; negative = error.
#[repr(i32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureStatus {
    /// Capture succeeded; frame data is available.
    Ok = 0,
    /// The requested API is not yet implemented (spike stub).
    NotImplemented = -1,
    /// Windows Graphics Capture API is not available on this OS version.
    CaptureUnavailable = -2,
    /// Screen capture permission was denied by the user.
    PermissionDenied = -3,
    /// An internal error occurred; check the error message buffer.
    InternalError = -4,
    /// Timed out waiting for a frame from the capture API.
    Timeout = -5,
    /// Buffer metadata is inconsistent (width/height/stride mismatch).
    InvalidBuffer = -6,
}

/// Result of a capture attempt, returned by value across the FFI boundary.
/// Rust owns all allocated memory; the caller must call `captcho_free_frame`
/// or `captcho_free_capture_result` to release when done.
#[repr(C)]
#[derive(Debug)]
pub struct CaptureResult {
    /// Status code indicating success or the type of failure.
    pub status: CaptureStatus,
    /// Pointer to BGRA pixel data owned by Rust. Null on error.
    pub frame_data: FrameHandle,
    /// Width of the captured frame in pixels.
    pub width: u32,
    /// Height of the captured frame in pixels.
    pub height: u32,
    /// Row stride in bytes (may exceed `width * 4` for alignment).
    pub stride: u32,
    /// Total size of the pixel buffer in bytes (`stride * height`).
    pub data_len: u32,
    /// Pointer to a UTF-8 error message string owned by Rust. Null on success.
    /// Call `captcho_free_error_message` to release.
    pub error_message: *mut std::ffi::c_char,
}

impl Default for CaptureResult {
    fn default() -> Self {
        Self {
            status: CaptureStatus::NotImplemented,
            frame_data: std::ptr::null_mut(),
            width: 0,
            height: 0,
            stride: 0,
            data_len: 0,
            error_message: std::ptr::null_mut(),
        }
    }
}

impl CaptureResult {
    /// Create an error result with a message.
    ///
    /// The returned struct owns the error_message allocation.
    /// The caller must eventually call `captcho_free_error_message`.
    pub fn error(status: CaptureStatus, msg: impl Into<String>) -> Self {
        let msg_str = msg.into();
        let c_msg = CString::new(msg_str.as_str()).unwrap_or_else(|_| {
            CString::new("(invalid error message)").unwrap()
        });
        Self {
            status,
            error_message: c_msg.into_raw(),
            ..Self::default()
        }
    }

    /// Create a success result from a frame buffer.
    ///
    /// Takes ownership of the Vec; the caller must free via `captcho_free_frame`.
    fn from_frame(data: Vec<u8>, width: u32, height: u32, stride: u32) -> Self {
        let data_len = data.len() as u32;
        let mut data = std::mem::ManuallyDrop::new(data);
        Self {
            status: CaptureStatus::Ok,
            frame_data: data.as_mut_ptr(),
            width,
            height,
            stride,
            data_len,
            error_message: std::ptr::null_mut(),
        }
    }
}

// ---------------------------------------------------------------------------
// Frame allocation registry
// ---------------------------------------------------------------------------

// Global registry of outstanding frame allocations.
// This allows us to reconstruct Vec<u8> from a raw pointer in `captcho_free_frame`.
use std::collections::HashMap;

static FRAME_REGISTRY: std::sync::LazyLock<parking_lot::Mutex<HashMap<usize, (usize, usize)>>> =
    std::sync::LazyLock::new(|| parking_lot::Mutex::new(HashMap::new()));

fn register_frame(ptr: *mut u8, len: usize, cap: usize) {
    let mut reg = FRAME_REGISTRY.lock();
    reg.insert(ptr as usize, (len, cap));
}

fn unregister_frame(ptr: *mut u8) -> Option<(usize, usize)> {
    let mut reg = FRAME_REGISTRY.lock();
    reg.remove(&(ptr as usize))
}

// ---------------------------------------------------------------------------
// Capture implementation using windows-capture v2
// ---------------------------------------------------------------------------

/// One-shot capture handler that grabs the first frame and stops.
struct OneShotCapture {
    /// The captured frame data (set once).
    result: Arc<Mutex<Option<Result<CapturedFrameData, String>>>>,
    /// Signal to stop the capture session.
    done: Arc<AtomicBool>,
}

/// Intermediate frame data before converting to CaptureResult.
struct CapturedFrameData {
    pixels: Vec<u8>,
    width: u32,
    height: u32,
    stride: u32,
}

impl windows_capture::capture::GraphicsCaptureApiHandler for OneShotCapture {
    type Flags = Arc<Mutex<Option<Result<CapturedFrameData, String>>>>;
    type Error = String;

    fn new(
        ctx: windows_capture::capture::Context<Self::Flags>,
    ) -> Result<Self, Self::Error> {
        Ok(Self {
            result: ctx.flags,
            done: Arc::new(AtomicBool::new(false)),
        })
    }

    fn on_frame_arrived(
        &mut self,
        frame: &mut windows_capture::frame::Frame,
        capture_control: windows_capture::graphics_capture_api::InternalCaptureControl,
    ) -> Result<(), Self::Error> {
        // Only capture the first frame
        if self.done.load(Ordering::Relaxed) {
            return Ok(());
        }
        self.done.store(true, Ordering::Relaxed);

        let width = frame.width();
        let height = frame.height();

        // Get the frame buffer
        let mut buffer = match frame.buffer() {
            Ok(b) => b,
            Err(e) => {
                let mut result = self.result.lock().unwrap();
                *result = Some(Err(format!("Failed to get frame buffer: {e}")));
                capture_control.stop();
                return Ok(());
            }
        };

        let row_pitch = buffer.row_pitch();

        // Copy pixel data into a contiguous Vec.
        // The buffer may have row padding (row_pitch >= width * 4).
        // We preserve the stride so C# can handle padding correctly.
        let raw = buffer.as_raw_buffer();
        let total_bytes = (row_pitch as usize) * (height as usize);

        let mut pixels = Vec::with_capacity(total_bytes);
        pixels.extend_from_slice(raw);

        // Validate consistency
        let data_len = pixels.len() as u32;
        if data_len != row_pitch * height {
            let mut result = self.result.lock().unwrap();
            *result = Some(Err(format!(
                "Buffer metadata inconsistency: data_len={}, expected stride*height={}",
                data_len,
                row_pitch * height
            )));
            capture_control.stop();
            return Ok(());
        }

        let mut result = self.result.lock().unwrap();
        *result = Some(Ok(CapturedFrameData {
            pixels,
            width,
            height,
            stride: row_pitch,
        }));

        capture_control.stop();
        Ok(())
    }

    fn on_closed(&mut self) -> Result<(), Self::Error> {
        if !self.done.load(Ordering::Relaxed) {
            let mut result = self.result.lock().unwrap();
            *result = Some(Err("Capture item closed before a frame arrived".into()));
        }
        Ok(())
    }
}

// ---------------------------------------------------------------------------
// Window capture helpers
// ---------------------------------------------------------------------------

/// Validate that a window handle is suitable for capture.
///
/// Returns `Ok(())` if the window appears capturable, or `Err` with a phase-labeled
/// description of why it isn't.
fn validate_window_for_capture(hwnd: windows::Win32::Foundation::HWND, phase: &str) -> Result<(), String> {
    use windows::Win32::UI::WindowsAndMessaging::{
        GetAncestor, GetDesktopWindow, IsIconic, IsWindow, IsWindowVisible, GA_ROOT,
    };

    // Null / invalid handle
    if hwnd.is_invalid() || hwnd.0.is_null() {
        return Err(format!("{}: window handle is null or invalid", phase));
    }

    // IsWindow check
    if !unsafe { IsWindow(Some(hwnd)) }.as_bool() {
        return Err(format!("{}: handle is not a valid window", phase));
    }

    // Desktop window
    let desktop = unsafe { GetDesktopWindow() };
    if hwnd == desktop {
        return Err(format!("{}: desktop window is not capturable", phase));
    }

    // Walk to top-level ancestor for child HWNDs
    let top_level = unsafe { GetAncestor(hwnd, GA_ROOT) };
    if top_level.is_invalid() || top_level.0.is_null() {
        return Err(format!("{}: failed to resolve top-level ancestor", phase));
    }

    // Visibility
    if !unsafe { IsWindowVisible(top_level) }.as_bool() {
        return Err(format!("{}: window is not visible", phase));
    }

    // Minimized
    if unsafe { IsIconic(top_level) }.as_bool() {
        return Err(format!("{}: window is minimized", phase));
    }

    Ok(())
}

/// Capture a single frame from a window using Windows Graphics Capture.
///
/// `phase_label` is used in error messages to identify which phase failed.
fn do_capture_window(window: windows_capture::window::Window, phase_label: &str) -> CaptureResult {
    use windows_capture::capture::GraphicsCaptureApiHandler;
    use windows_capture::settings::{
        ColorFormat, CursorCaptureSettings, DirtyRegionSettings, DrawBorderSettings,
        MinimumUpdateIntervalSettings, SecondaryWindowSettings, Settings,
    };

    // Shared result slot
    let result_slot: Arc<Mutex<Option<Result<CapturedFrameData, String>>>> =
        Arc::new(Mutex::new(None));

    // Build capture settings — window is converted via TryInto<GraphicsCaptureItemType>
    let settings = Settings::new(
        window,
        CursorCaptureSettings::WithoutCursor,
        DrawBorderSettings::WithoutBorder,
        SecondaryWindowSettings::Default,
        MinimumUpdateIntervalSettings::Default,
        DirtyRegionSettings::Default,
        ColorFormat::Bgra8,
        result_slot.clone(),
    );

    // Start the capture on a background thread
    let capture_control = match OneShotCapture::start_free_threaded(settings) {
        Ok(ctrl) => ctrl,
        Err(e) => {
            return CaptureResult::error(
                CaptureStatus::CaptureUnavailable,
                format!("capture_frame[{}]: Failed to start WGC window capture: {}", phase_label, e),
            );
        }
    };

    // Wait for the capture to complete
    if let Err(e) = capture_control.wait() {
        return CaptureResult::error(
            CaptureStatus::InternalError,
            format!("capture_frame[{}]: Capture thread error: {}", phase_label, e),
        );
    }

    // Extract the result
    let frame_data = {
        let mut slot = result_slot.lock().unwrap();
        match slot.take() {
            Some(Ok(data)) => data,
            Some(Err(msg)) => {
                return CaptureResult::error(
                    CaptureStatus::InternalError,
                    format!("capture_frame[{}]: {}", phase_label, msg),
                );
            }
            None => {
                return CaptureResult::error(
                    CaptureStatus::Timeout,
                    format!("capture_frame[{}]: No frame received", phase_label),
                );
            }
        }
    };

    // Validate buffer consistency
    let expected_len = (frame_data.stride as u64) * (frame_data.height as u64);
    if (frame_data.pixels.len() as u64) != expected_len {
        return CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!(
                "validate[{}]: Buffer size mismatch: got {} bytes, expected {} (stride={} height={})",
                phase_label,
                frame_data.pixels.len(),
                expected_len,
                frame_data.stride,
                frame_data.height
            ),
        );
    }

    let width = frame_data.width;
    let height = frame_data.height;
    let stride = frame_data.stride;
    let len = frame_data.pixels.len();

    // Transfer ownership to the FFI result
    let result = CaptureResult::from_frame(frame_data.pixels, width, height, stride);
    register_frame(result.frame_data, len, len);
    result
}

/// Capture a single frame from the primary monitor using Windows Graphics Capture.
///
/// This function:
/// 1. Finds the primary monitor
/// 2. Starts a WGC session with BGRA8 format
/// 3. Waits for the first frame
/// 4. Copies pixel data into Rust-owned memory
/// 5. Stops the capture and returns the result
fn do_capture_primary_monitor() -> CaptureResult {
    use windows_capture::monitor::Monitor;

    let primary = match Monitor::primary() {
        Ok(m) => m,
        Err(e) => return CaptureResult::error(CaptureStatus::CaptureUnavailable, format!("No primary monitor found: {e}")),
    };

    do_capture_monitor(primary, "primary_monitor")
}

/// Capture a single frame from the specified monitor using Windows Graphics Capture.
///
/// `phase_label` is used in error messages to identify which phase failed.
fn do_capture_monitor(monitor: windows_capture::monitor::Monitor, phase_label: &str) -> CaptureResult {
    use windows_capture::capture::GraphicsCaptureApiHandler;
    use windows_capture::settings::{
        ColorFormat, CursorCaptureSettings, DirtyRegionSettings, DrawBorderSettings,
        MinimumUpdateIntervalSettings, SecondaryWindowSettings, Settings,
    };

    // Shared result slot
    let result_slot: Arc<Mutex<Option<Result<CapturedFrameData, String>>>> =
        Arc::new(Mutex::new(None));

    // Build capture settings
    let settings = Settings::new(
        monitor,
        CursorCaptureSettings::WithoutCursor,
        DrawBorderSettings::WithoutBorder,
        SecondaryWindowSettings::Default,
        MinimumUpdateIntervalSettings::Default,
        DirtyRegionSettings::Default,
        ColorFormat::Bgra8,
        result_slot.clone(),
    );

    // Start the capture on a background thread
    let capture_control = match OneShotCapture::start_free_threaded(settings) {
        Ok(ctrl) => ctrl,
        Err(e) => {
            return CaptureResult::error(
                CaptureStatus::CaptureUnavailable,
                format!("capture_frame[{}]: Failed to start WGC capture: {e}", phase_label),
            );
        }
    };

    // Wait for the capture to complete
    let wait_result = capture_control.wait();
    if let Err(e) = wait_result {
        return CaptureResult::error(
            CaptureStatus::InternalError,
            format!("capture_frame[{}]: Capture thread error: {e}", phase_label),
        );
    }

    // Extract the result
    let frame_data = {
        let mut slot = result_slot.lock().unwrap();
        match slot.take() {
            Some(Ok(data)) => data,
            Some(Err(msg)) => {
                return CaptureResult::error(CaptureStatus::InternalError, format!("capture_frame[{}]: {}", phase_label, msg));
            }
            None => {
                return CaptureResult::error(
                    CaptureStatus::Timeout,
                    format!("capture_frame[{}]: No frame received", phase_label),
                );
            }
        }
    };

    // Validate buffer consistency
    let expected_len = (frame_data.stride as u64) * (frame_data.height as u64);
    if (frame_data.pixels.len() as u64) != expected_len {
        return CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!(
                "validate[{}]: Buffer size mismatch: got {} bytes, expected {} (stride={} height={})",
                phase_label,
                frame_data.pixels.len(),
                expected_len,
                frame_data.stride,
                frame_data.height
            ),
        );
    }

    let width = frame_data.width;
    let height = frame_data.height;
    let stride = frame_data.stride;
    let len = frame_data.pixels.len();

    // Transfer ownership to the FFI result
    let result = CaptureResult::from_frame(frame_data.pixels, width, height, stride);
    register_frame(result.frame_data, len, len);
    result
}

/// Get the bounds of a monitor (position + size) in virtual-desktop coordinates
/// using Win32 `GetMonitorInfoW`.
fn get_monitor_bounds(monitor: &windows_capture::monitor::Monitor) -> Result<monitor_utils::MonitorBounds, String> {
    use windows::Win32::Graphics::Gdi::{GetMonitorInfoW, MONITORINFOEXW, MONITORINFO, HMONITOR};

    let mut monitor_info = MONITORINFOEXW {
        monitorInfo: MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFOEXW>() as u32,
            ..Default::default()
        },
        szDevice: [0; 32],
    };

    let hmonitor = HMONITOR(monitor.as_raw_hmonitor());
    let result = unsafe {
        GetMonitorInfoW(hmonitor, &mut monitor_info as *mut _ as _)
    };

    if result.as_bool() {
        let rect = monitor_info.monitorInfo.rcMonitor;
        Ok(monitor_utils::MonitorBounds {
            x: rect.left,
            y: rect.top,
            width: (rect.right - rect.left) as u32,
            height: (rect.bottom - rect.top) as u32,
        })
    } else {
        Err("Failed to get monitor info via GetMonitorInfoW".to_string())
    }
}

// ---------------------------------------------------------------------------
// Test-only helpers for constructing synthetic frames
// ---------------------------------------------------------------------------

/// Create a synthetic frame buffer for testing.
///
/// Allocates a BGRA buffer of the given dimensions with valid stride.
/// The buffer is filled with a test pattern (each pixel = [B, G, R, 0xFF]).
///
/// Returns the CaptureResult owning the synthetic frame data.
/// The caller must free it with `captcho_free_frame` or `captcho_free_capture_result`.
#[cfg(any(test, feature = "test-helpers"))]
pub fn create_synthetic_frame(width: u32, height: u32) -> CaptureResult {
    let bpp = 4u32; // BGRA = 4 bytes per pixel
    let stride = width * bpp;
    let data_len = stride * height;
    let mut pixels = Vec::with_capacity(data_len as usize);

    for y in 0..height {
        for x in 0..width {
            // Simple test pattern: blue channel varies by x, green by y, red is constant
            let b = (x % 256) as u8;
            let g = (y % 256) as u8;
            let r = 0x80u8;
            let a = 0xFFu8;
            pixels.push(b);
            pixels.push(g);
            pixels.push(r);
            pixels.push(a);
        }
    }

    let len = pixels.len();
    let result = CaptureResult::from_frame(pixels, width, height, stride);
    register_frame(result.frame_data, len, len);
    result
}

/// Create a synthetic frame with a custom stride (including padding).
///
/// This is useful for testing that the C# side handles stride > width*4 correctly.
#[cfg(any(test, feature = "test-helpers"))]
pub fn create_synthetic_frame_with_stride(width: u32, height: u32, stride: u32) -> CaptureResult {
    let bpp = 4u32;
    if stride < width * bpp {
        return CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!(
                "Stride {} is less than minimum {} (width={} * bpp=4)",
                stride,
                width * bpp,
                width
            ),
        );
    }

    let data_len = stride * height;
    let mut pixels = Vec::with_capacity(data_len as usize);

    for _y in 0..height {
        // Write actual pixel data for this row
        for _x in 0..width {
            pixels.push(0x40u8); // B
            pixels.push(0x80u8); // G
            pixels.push(0xC0u8); // R
            pixels.push(0xFFu8); // A
        }
        // Write padding bytes if stride > width*4
        let padding = stride - (width * bpp);
        for _ in 0..padding {
            pixels.push(0u8);
        }
    }

    let len = pixels.len();
    let result = CaptureResult::from_frame(pixels, width, height, stride);
    register_frame(result.frame_data, len, len);
    result
}

/// Create a synthetic frame that deliberately has inconsistent metadata.
/// Returns an error result — this is for testing negative paths only.
#[cfg(any(test, feature = "test-helpers"))]
pub fn create_invalid_frame_zero_dims() -> CaptureResult {
    CaptureResult::error(
        CaptureStatus::InvalidBuffer,
        "Cannot create frame with zero dimensions",
    )
}

// ---------------------------------------------------------------------------
// FFI exports
// ---------------------------------------------------------------------------

/// Capture a single frame from the primary monitor.
///
/// Returns a `CaptureResult` by value (C struct return).
/// On success, `frame_data` points to BGRA pixel data owned by Rust.
/// The caller MUST call `captcho_free_frame` or `captcho_free_capture_result`
/// when done with the pixels.
///
/// # Safety
/// This function is safe to call from C#. The returned struct contains
/// pointers that must be freed using the matching free functions.
#[no_mangle]
pub extern "C" fn captcho_capture_frame() -> CaptureResult {
    // Catch panics so they never cross the FFI boundary
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        do_capture_primary_monitor()
    })) {
        Ok(result) => result,
        Err(_) => CaptureResult::error(
            CaptureStatus::InternalError,
            "Unexpected panic during capture",
        ),
    }
}

/// Free a frame buffer previously returned by `captcho_capture_frame`.
///
/// # Safety
/// `data` must be a pointer previously returned in `CaptureResult.frame_data`,
/// or null. Passing any other value is undefined behavior.
#[no_mangle]
pub unsafe extern "C" fn captcho_free_frame(data: FrameHandle) {
    if data.is_null() {
        return;
    }

    if let Some((len, cap)) = unregister_frame(data) {
        // Reconstruct the Vec and let it drop, freeing the memory.
        let _ = Vec::from_raw_parts(data, len, cap);
    }
    // If the pointer isn't in the registry, it was already freed or not ours.
    // Silently ignore rather than crashing across FFI.
}

/// Capture a single frame from the monitor at the given 0-based index.
///
/// Index 0 = primary monitor, 1 = second monitor, etc.
/// If the index is out of range or no monitors are available, returns an error
/// `CaptureResult` with a sanitized error message and null frame data.
///
/// # Safety
/// This function is safe to call from C#. The returned struct contains
/// pointers that must be freed using the matching free functions.
#[no_mangle]
pub extern "C" fn captcho_capture_monitor_by_index(index: u32) -> CaptureResult {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        do_capture_monitor_by_index(index)
    })) {
        Ok(result) => result,
        Err(_) => CaptureResult::error(
            CaptureStatus::InternalError,
            "Unexpected panic during monitor capture",
        ),
    }
}

/// Capture a single frame from the monitor at the given 0-based index.
fn do_capture_monitor_by_index(index: u32) -> CaptureResult {
    use windows_capture::monitor::Monitor;

    // Convert our 0-based index to windows-capture's 1-based index.
    // index 0 → from_index(1) (primary), index 1 → from_index(2), etc.
    let one_based = match index.checked_add(1) {
        Some(v) => v as usize,
        None => {
            return CaptureResult::error(
                CaptureStatus::CaptureUnavailable,
                format!("select_monitor: index {} is out of range", index),
            );
        }
    };

    let monitor = match Monitor::from_index(one_based) {
        Ok(m) => m,
        Err(e) => {
            return CaptureResult::error(
                CaptureStatus::CaptureUnavailable,
                format!("select_monitor: No monitor at index {} ({})", index, e),
            );
        }
    };

    do_capture_monitor(monitor, &format!("monitor[{}]", index))
}

/// Capture a single frame of the full virtual desktop (all monitors stitched).
///
/// Enumerates all monitors, captures each one individually using WGC,
/// then stitches the per-monitor frames into a single BGRA buffer
/// representing the entire virtual desktop.
///
/// # Safety
/// This function is safe to call from C#. The returned struct contains
/// pointers that must be freed using the matching free functions.
#[no_mangle]
pub extern "C" fn captcho_capture_all_monitors() -> CaptureResult {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        do_capture_all_monitors()
    })) {
        Ok(result) => result,
        Err(_) => CaptureResult::error(
            CaptureStatus::InternalError,
            "Unexpected panic during full-desktop capture",
        ),
    }
}

/// Capture and stitch all monitors into a single virtual-desktop frame.
fn do_capture_all_monitors() -> CaptureResult {
    use windows_capture::monitor::Monitor;

    // 1. Enumerate monitors
    let monitors = match Monitor::enumerate() {
        Ok(m) => m,
        Err(e) => {
            return CaptureResult::error(
                CaptureStatus::CaptureUnavailable,
                format!("enumerate: Failed to enumerate monitors: {e}"),
            );
        }
    };

    if monitors.is_empty() {
        return CaptureResult::error(
            CaptureStatus::CaptureUnavailable,
            "enumerate: No monitors found",
        );
    }

    // 2. Get bounds for each monitor
    let monitor_bounds: Vec<monitor_utils::MonitorBounds> = match monitors.iter().map(get_monitor_bounds).collect::<Result<Vec<_>, _>>() {
        Ok(bounds) => bounds,
        Err(e) => {
            return CaptureResult::error(
                CaptureStatus::InternalError,
                format!("enumerate: Failed to get monitor bounds: {e}"),
            );
        }
    };

    // 3. Compute virtual desktop bounds
    let desktop_bounds = match monitor_utils::compute_virtual_desktop_bounds(&monitor_bounds) {
        Some(b) => b,
        None => {
            return CaptureResult::error(
                CaptureStatus::InternalError,
                "enumerate: Failed to compute virtual desktop bounds",
            );
        }
    };

    // 4. Capture each monitor
    let mut frame_data_list: Vec<(monitor_utils::MonitorBounds, Vec<u8>, u32)> = Vec::with_capacity(monitors.len());
    for (i, monitor) in monitors.iter().enumerate() {
        let result = do_capture_monitor(*monitor, &format!("all_monitors[{}]", i));
        if result.status != CaptureStatus::Ok {
            // Return the first capture failure; free any already-captured frames
            // (the result we're returning already owns nothing since it's an error)
            // The frames already captured in frame_data_list will be dropped when
            // the Vec is dropped, which is fine.
            return result;
        }

        // Extract pixel data from the successful result
        let pixel_count = result.data_len as usize;
        let pixels = unsafe {
            std::slice::from_raw_parts(result.frame_data, pixel_count)
        }.to_vec();

        // Unregister and free the FFI result's frame since we've copied it
        unregister_frame(result.frame_data);
        unsafe {
            let _ = Vec::from_raw_parts(result.frame_data, pixel_count, pixel_count);
        }

        frame_data_list.push((monitor_bounds[i], pixels, result.stride));
    }

    // 5. Stitch into virtual desktop buffer
    let dst_stride = desktop_bounds.width * 4;
    let dst_len = dst_stride as usize * desktop_bounds.height as usize;
    let mut dst = vec![0u8; dst_len];

    let stitch_result = monitor_utils::stitch_frames(
        frame_data_list.iter().map(|(bounds, pixels, stride)| (*bounds, pixels.as_slice(), *stride)),
        &desktop_bounds,
        &mut dst,
    );

    if let Err(e) = stitch_result {
        return CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!("stitch: {}", e),
        );
    }

    // 6. Validate the final stitched buffer
    if let Err(e) = monitor_utils::validate_buffer_invariants(
        desktop_bounds.width,
        desktop_bounds.height,
        dst_stride,
        dst.len() as u32,
    ) {
        return CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!("validate: {}", e),
        );
    }

    // 7. Transfer to FFI result
    let len = dst.len();
    let result = CaptureResult::from_frame(dst, desktop_bounds.width, desktop_bounds.height, dst_stride);
    register_frame(result.frame_data, len, len);
    result
}

/// Validate captured buffer metadata (dimensions, stride, data_len consistency).
///
/// Returns `Ok(())` if all invariants hold, or `Err(CaptureResult)` with a
/// `validate:region:`-prefixed error message describing the problem.
fn validate_captured_buffer(captured: &CaptureResult) -> Result<(), CaptureResult> {
    if captured.width == 0 || captured.height == 0 {
        return Err(CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            "validate:region: Captured desktop has zero dimensions",
        ));
    }

    let min_stride = captured.width
        .checked_mul(4)
        .ok_or_else(|| CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!("validate:region: Stride overflow (width={})", captured.width),
        ))?;

    if captured.stride < min_stride {
        return Err(CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!(
                "validate:region: Captured stride {} < minimum {} (width={})",
                captured.stride, min_stride, captured.width
            ),
        ));
    }

    let expected_len = (captured.stride as u64)
        .checked_mul(captured.height as u64)
        .ok_or_else(|| CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!(
                "validate:region: Buffer length overflow (stride={} height={})",
                captured.stride, captured.height
            ),
        ))?;

    if (captured.data_len as u64) != expected_len {
        return Err(CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!(
                "validate:region: Buffer size mismatch: data_len={}, expected {} (stride={} height={})",
                captured.data_len, expected_len, captured.stride, captured.height
            ),
        ));
    }

    Ok(())
}

/// Capture a rectangular region of the virtual desktop.
///
/// The region is specified in virtual-desktop coordinates:
/// - (0, 0) is the top-left of the primary monitor
/// - Secondary monitors may extend into negative coordinates
///
/// The implementation:
/// 1. Validates region dimensions (zero rejection)
/// 2. Enumerates monitors and computes virtual desktop bounds
/// 3. Validates the region fits within the desktop
/// 4. Captures the full virtual desktop via the all-monitors path
/// 5. Validates captured buffer metadata before crop
/// 6. Crops the requested rectangle with tight BGRA stride
/// 7. Frees the intermediate full-desktop buffer
/// 8. Returns the cropped frame registered in the frame registry
///
/// # Safety
/// This function is safe to call from C#. The returned struct contains
/// pointers that must be freed using the matching free functions.
#[no_mangle]
pub extern "C" fn captcho_capture_region(x: i32, y: i32, width: u32, height: u32) -> CaptureResult {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        do_capture_region(x, y, width, height)
    })) {
        Ok(result) => result,
        Err(_) => CaptureResult::error(
            CaptureStatus::InternalError,
            "Unexpected panic during region capture",
        ),
    }
}

/// Implementation for rectangular region capture.
fn do_capture_region(x: i32, y: i32, width: u32, height: u32) -> CaptureResult {
    use windows_capture::monitor::Monitor;

    // Phase 1: early zero-dimension rejection (no WGC needed)
    if width == 0 || height == 0 {
        return CaptureResult::error(
            CaptureStatus::InvalidBuffer,
            format!(
                "region: zero dimensions (x={}, y={}, width={}, height={})",
                x, y, width, height
            ),
        );
    }

    let region = monitor_utils::CropRegion { x, y, width, height };

    // Phase 2: enumerate monitors and compute virtual desktop bounds
    let monitors = match Monitor::enumerate() {
        Ok(m) => m,
        Err(e) => {
            return CaptureResult::error(
                CaptureStatus::CaptureUnavailable,
                format!("capture:region: Failed to enumerate monitors: {e}"),
            );
        }
    };

    if monitors.is_empty() {
        return CaptureResult::error(
            CaptureStatus::CaptureUnavailable,
            "capture:region: No monitors found",
        );
    }

    let monitor_bounds: Vec<monitor_utils::MonitorBounds> = match monitors
        .iter()
        .map(get_monitor_bounds)
        .collect::<Result<Vec<_>, _>>()
    {
        Ok(bounds) => bounds,
        Err(e) => {
            return CaptureResult::error(
                CaptureStatus::InternalError,
                format!("capture:region: Failed to get monitor bounds: {e}"),
            );
        }
    };

    let desktop_bounds = match monitor_utils::compute_virtual_desktop_bounds(&monitor_bounds) {
        Some(b) => b,
        None => {
            return CaptureResult::error(
                CaptureStatus::InternalError,
                "capture:region: Failed to compute virtual desktop bounds",
            );
        }
    };

    // Phase 3: validate region against desktop bounds (early rejection before WGC)
    if let Err(e) = monitor_utils::validate_crop_region(&region, &desktop_bounds) {
        return CaptureResult::error(CaptureStatus::InvalidBuffer, e);
    }

    // Phase 4: capture full virtual desktop
    let captured = do_capture_all_monitors();
    if captured.status != CaptureStatus::Ok {
        // Propagate capture failure as-is; no intermediate frame to free
        return captured;
    }

    // Phase 5: validate captured buffer metadata before crop
    if let Err(e) = validate_captured_buffer(&captured) {
        unsafe { captcho_free_frame(captured.frame_data) };
        return e;
    }

    // Phase 6: crop the requested region from the captured buffer
    let src_data = unsafe {
        std::slice::from_raw_parts(captured.frame_data, captured.data_len as usize)
    };

    let crop_result = match monitor_utils::crop_region(
        src_data,
        captured.width,
        captured.height,
        captured.stride,
        &region,
        &desktop_bounds,
    ) {
        Ok(c) => c,
        Err(e) => {
            unsafe { captcho_free_frame(captured.frame_data) };
            return CaptureResult::error(CaptureStatus::InvalidBuffer, e);
        }
    };

    // Phase 7: free the intermediate full-desktop frame
    // (registered by do_capture_all_monitors; we must unregister + free it)
    unsafe { captcho_free_frame(captured.frame_data) };

    // Phase 8: register and return the cropped frame with tight stride
    let crop_stride = crop_result.width * 4;
    let len = crop_result.data.len();
    let result = CaptureResult::from_frame(
        crop_result.data,
        crop_result.width,
        crop_result.height,
        crop_stride,
    );
    register_frame(result.frame_data, len, len);
    result
}

/// Capture a single frame of the foreground (active) window.
///
/// Uses `Window::foreground()` to find the currently active window,
/// validates it is capturable, then performs a one-shot WGC capture.
///
/// # Safety
/// This function is safe to call from C#. The returned struct contains
/// pointers that must be freed using the matching free functions.
#[no_mangle]
pub extern "C" fn captcho_capture_active_window() -> CaptureResult {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        do_capture_active_window()
    })) {
        Ok(result) => result,
        Err(_) => CaptureResult::error(
            CaptureStatus::InternalError,
            "Unexpected panic during active window capture",
        ),
    }
}

/// Implementation for active window capture.
fn do_capture_active_window() -> CaptureResult {
    use windows::Win32::UI::WindowsAndMessaging::GetForegroundWindow;
    use windows_capture::window::Window;

    // Get foreground window via Win32 directly so we can validate the HWND
    let hwnd = unsafe { GetForegroundWindow() };
    if let Err(e) = validate_window_for_capture(hwnd, "validate_window") {
        return CaptureResult::error(CaptureStatus::CaptureUnavailable, e);
    }

    let window = Window::from_raw_hwnd(hwnd.0);
    do_capture_window(window, "active_window")
}

/// Capture a single frame of the window under the mouse cursor.
///
/// Uses `GetCursorPos` + `WindowFromPoint` to find the window under the cursor,
/// walks up to the top-level ancestor, validates it, then performs a one-shot WGC capture.
///
/// # Safety
/// This function is safe to call from C#. The returned struct contains
/// pointers that must be freed using the matching free functions.
#[no_mangle]
pub extern "C" fn captcho_capture_window_under_cursor() -> CaptureResult {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        do_capture_window_under_cursor()
    })) {
        Ok(result) => result,
        Err(_) => CaptureResult::error(
            CaptureStatus::InternalError,
            "Unexpected panic during window-under-cursor capture",
        ),
    }
}

/// Implementation for window-under-cursor capture.
fn do_capture_window_under_cursor() -> CaptureResult {
    use windows::Win32::Foundation::POINT;
    use windows::Win32::UI::WindowsAndMessaging::{GetCursorPos, GetAncestor, WindowFromPoint, GA_ROOT};
    use windows_capture::window::Window;

    // Get cursor position
    let mut point = POINT { x: 0, y: 0 };
    if unsafe { GetCursorPos(&mut point) }.is_err() {
        return CaptureResult::error(
            CaptureStatus::CaptureUnavailable,
            "select_window: Failed to get cursor position",
        );
    }

    // Get the window at the cursor position
    let hwnd = unsafe { WindowFromPoint(point) };
    if hwnd.is_invalid() || hwnd.0.is_null() {
        return CaptureResult::error(
            CaptureStatus::CaptureUnavailable,
            "select_window: No window at cursor position",
        );
    }

    // Walk to top-level ancestor (WindowFromPoint may return a child/control)
    let top_hwnd = unsafe { GetAncestor(hwnd, GA_ROOT) };
    if top_hwnd.is_invalid() || top_hwnd.0.is_null() {
        return CaptureResult::error(
            CaptureStatus::CaptureUnavailable,
            "select_window: Failed to resolve top-level window at cursor",
        );
    }

    if let Err(e) = validate_window_for_capture(top_hwnd, "validate_window") {
        return CaptureResult::error(CaptureStatus::CaptureUnavailable, e);
    }

    let window = Window::from_raw_hwnd(top_hwnd.0);
    do_capture_window(window, "window_under_cursor")
}

/// Capture a single frame from the window identified by the given native handle.
///
/// The `hwnd` parameter should be a valid Windows `HWND` value. If the handle is
/// invalid, stale, refers to a non-capturable window (desktop, child, invisible,
/// minimized), or WGC cannot capture it, a non-OK `CaptureResult` is returned.
///
/// # Safety
/// This function is safe to call from C#. The returned struct contains
/// pointers that must be freed using the matching free functions.
#[no_mangle]
pub extern "C" fn captcho_capture_window_by_handle(hwnd: u64) -> CaptureResult {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        do_capture_window_by_handle(hwnd)
    })) {
        Ok(result) => result,
        Err(_) => CaptureResult::error(
            CaptureStatus::InternalError,
            "Unexpected panic during window-by-handle capture",
        ),
    }
}

/// Implementation for capture by explicit window handle.
fn do_capture_window_by_handle(hwnd: u64) -> CaptureResult {
    use windows::Win32::Foundation::HWND;
    use windows_capture::window::Window;

    // Reject null / zero handles
    if hwnd == 0 {
        return CaptureResult::error(
            CaptureStatus::CaptureUnavailable,
            "select_window: null window handle (hwnd=0)",
        );
    }

    let raw_hwnd = HWND(hwnd as *mut std::ffi::c_void);

    if let Err(e) = validate_window_for_capture(raw_hwnd, "validate_window") {
        return CaptureResult::error(CaptureStatus::CaptureUnavailable, e);
    }

    let window = Window::from_raw_hwnd(raw_hwnd.0);
    do_capture_window(window, "window_by_handle")
}

/// Free an error message string previously returned in a `CaptureResult`.
///
/// # Safety
/// `msg` must be a pointer previously returned in `CaptureResult.error_message`,
/// or null. Passing any other value is undefined behavior.
#[no_mangle]
pub unsafe extern "C" fn captcho_free_error_message(msg: *mut std::ffi::c_char) {
    if msg.is_null() {
        return;
    }
    // Retake ownership of the CString and let it drop.
    let _ = CString::from_raw(msg);
}

/// Free all allocations inside a `CaptureResult`.
///
/// This is a convenience function that frees both the frame data and error message
/// in a single call. After this call, the CaptureResult's pointers must not be used.
///
/// # Safety
/// The result must contain pointers previously returned by `captcho_capture_frame`,
/// or null pointers. Passing any other values is undefined behavior.
#[no_mangle]
pub unsafe extern "C" fn captcho_free_capture_result(result: CaptureResult) {
    captcho_free_frame(result.frame_data);
    captcho_free_error_message(result.error_message);
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capture_status_values_are_stable() {
        assert_eq!(CaptureStatus::Ok as i32, 0);
        assert!((CaptureStatus::NotImplemented as i32) < 0);
        assert!((CaptureStatus::CaptureUnavailable as i32) < 0);
        assert!((CaptureStatus::PermissionDenied as i32) < 0);
        assert!((CaptureStatus::InternalError as i32) < 0);
        assert!((CaptureStatus::Timeout as i32) < 0);
        assert!((CaptureStatus::InvalidBuffer as i32) < 0);
    }

    #[test]
    fn capture_result_default_is_not_implemented() {
        let result = CaptureResult::default();
        assert_eq!(result.status, CaptureStatus::NotImplemented);
        assert!(result.frame_data.is_null());
        assert_eq!(result.width, 0);
        assert_eq!(result.height, 0);
    }

    #[test]
    fn error_result_contains_message() {
        let result = CaptureResult::error(CaptureStatus::InternalError, "test error");
        assert_eq!(result.status, CaptureStatus::InternalError);
        assert!(!result.error_message.is_null());
        let msg = unsafe { std::ffi::CStr::from_ptr(result.error_message) };
        assert_eq!(msg.to_str().unwrap(), "test error");
        unsafe { captcho_free_error_message(result.error_message) };
    }

    #[test]
    fn free_null_pointers_are_safe() {
        unsafe {
            captcho_free_frame(std::ptr::null_mut());
            captcho_free_error_message(std::ptr::null_mut());
            captcho_free_capture_result(CaptureResult::default());
        }
    }

    // --- Synthetic frame tests ---

    #[test]
    fn synthetic_frame_has_correct_dimensions() {
        let result = create_synthetic_frame(64, 48);
        assert_eq!(result.status, CaptureStatus::Ok);
        assert_eq!(result.width, 64);
        assert_eq!(result.height, 48);
        assert_eq!(result.stride, 64 * 4); // 256
        assert_eq!(result.data_len, 256 * 48); // 12288
        assert!(!result.frame_data.is_null());
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn synthetic_frame_data_len_equals_stride_times_height() {
        let result = create_synthetic_frame(100, 50);
        assert_eq!(result.data_len, result.stride * result.height);
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn synthetic_frame_with_padding() {
        // stride = 420 > width*4 = 400
        let result = create_synthetic_frame_with_stride(100, 50, 420);
        assert_eq!(result.status, CaptureStatus::Ok);
        assert_eq!(result.width, 100);
        assert_eq!(result.height, 50);
        assert_eq!(result.stride, 420);
        assert_eq!(result.data_len, 420 * 50); // 21000
        assert!(!result.frame_data.is_null());
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn synthetic_frame_with_stride_less_than_minimum_is_error() {
        let result = create_synthetic_frame_with_stride(100, 50, 399);
        assert_eq!(result.status, CaptureStatus::InvalidBuffer);
        assert!(result.frame_data.is_null());
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn synthetic_frame_pixels_are_readable() {
        let result = create_synthetic_frame(2, 1);
        assert_eq!(result.status, CaptureStatus::Ok);
        // Read back pixels (don't crash)
        assert!(!result.frame_data.is_null());
        let slice = unsafe {
            std::slice::from_raw_parts(result.frame_data, result.data_len as usize)
        };
        // First pixel: x=0, y=0 → B=0, G=0, R=0x80, A=0xFF
        assert_eq!(slice[0], 0);     // B
        assert_eq!(slice[1], 0);     // G
        assert_eq!(slice[2], 0x80);  // R
        assert_eq!(slice[3], 0xFF);  // A
        // Second pixel: x=1, y=0 → B=1, G=0, R=0x80, A=0xFF
        assert_eq!(slice[4], 1);     // B
        assert_eq!(slice[5], 0);     // G
        assert_eq!(slice[6], 0x80);  // R
        assert_eq!(slice[7], 0xFF);  // A
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn double_free_is_safe() {
        let result = create_synthetic_frame(4, 4);
        let ptr = result.frame_data;
        assert!(!ptr.is_null());

        // First free via free_frame
        unsafe { captcho_free_frame(ptr) };

        // Second free should be silently ignored (pointer not in registry)
        unsafe { captcho_free_frame(ptr) };
    }

    #[test]
    fn free_via_capture_result_works() {
        let result = create_synthetic_frame(8, 8);
        assert_eq!(result.status, CaptureStatus::Ok);
        unsafe { captcho_free_capture_result(result) };
        // No crash = pass
    }

    #[test]
    fn invalid_frame_zero_dims_returns_error() {
        let result = create_invalid_frame_zero_dims();
        assert_eq!(result.status, CaptureStatus::InvalidBuffer);
        assert!(result.frame_data.is_null());
        unsafe { captcho_free_capture_result(result) };
    }

    // =======================================================================
    // Region capture tests (export-adjacent validation without live WGC)
    // =======================================================================

    /// Test helper: mirror of do_capture_region phases 5–8.
    /// Takes a captured frame (synthetic or real), validates its buffer metadata,
    /// crops the requested region, frees the intermediate frame, and returns
    /// the cropped result registered in the frame registry.
    fn do_crop_from_result(
        captured: CaptureResult,
        region: &monitor_utils::CropRegion,
        desktop: &monitor_utils::VirtualDesktopBounds,
    ) -> CaptureResult {
        // Propagate capture errors without touching frame_data
        if captured.status != CaptureStatus::Ok {
            return captured;
        }

        // Validate captured buffer metadata
        if let Err(e) = validate_captured_buffer(&captured) {
            unsafe { captcho_free_frame(captured.frame_data) };
            return e;
        }

        // Crop the requested region
        let src_data = unsafe {
            std::slice::from_raw_parts(captured.frame_data, captured.data_len as usize)
        };

        let crop_result = match monitor_utils::crop_region(
            src_data,
            captured.width,
            captured.height,
            captured.stride,
            region,
            desktop,
        ) {
            Ok(c) => c,
            Err(e) => {
                unsafe { captcho_free_frame(captured.frame_data) };
                return CaptureResult::error(CaptureStatus::InvalidBuffer, e);
            }
        };

        // Free the intermediate full-desktop frame
        unsafe { captcho_free_frame(captured.frame_data) };

        // Register and return the cropped frame with tight stride
        let crop_stride = crop_result.width * 4;
        let len = crop_result.data.len();
        let result = CaptureResult::from_frame(
            crop_result.data,
            crop_result.width,
            crop_result.height,
            crop_stride,
        );
        register_frame(result.frame_data, len, len);
        result
    }

    #[test]
    fn capture_region_crop_from_synthetic_frame() {
        let desktop = monitor_utils::VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 100,
            height: 80,
        };
        let captured = create_synthetic_frame(100, 80);
        let region = monitor_utils::CropRegion {
            x: 10,
            y: 20,
            width: 30,
            height: 40,
        };

        let result = do_crop_from_result(captured, &region, &desktop);
        assert_eq!(result.status, CaptureStatus::Ok);
        assert_eq!(result.width, 30);
        assert_eq!(result.height, 40);
        assert_eq!(result.stride, 120); // 30 * 4
        assert_eq!(result.data_len, 4800); // 30 * 40 * 4
        assert!(!result.frame_data.is_null());
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn capture_region_out_of_bounds_returns_error() {
        let desktop = monitor_utils::VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 100,
            height: 80,
        };
        let captured = create_synthetic_frame(100, 80);
        let region = monitor_utils::CropRegion {
            x: 90,
            y: 70,
            width: 20,
            height: 20,
        }; // exceeds desktop

        let result = do_crop_from_result(captured, &region, &desktop);
        assert_eq!(result.status, CaptureStatus::InvalidBuffer);
        assert!(result.frame_data.is_null());
        // Error message should be region: prefixed (from validate_crop_region)
        let msg = unsafe { std::ffi::CStr::from_ptr(result.error_message) };
        let msg_str = msg.to_str().unwrap();
        assert!(msg_str.starts_with("region:"));
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn capture_region_error_result_propagates() {
        let desktop = monitor_utils::VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 100,
            height: 80,
        };
        let error_result = CaptureResult::error(
            CaptureStatus::CaptureUnavailable,
            "test capture failure",
        );
        let region = monitor_utils::CropRegion {
            x: 0,
            y: 0,
            width: 10,
            height: 10,
        };

        let result = do_crop_from_result(error_result, &region, &desktop);
        assert_eq!(result.status, CaptureStatus::CaptureUnavailable);
        assert!(result.frame_data.is_null());
        let msg = unsafe { std::ffi::CStr::from_ptr(result.error_message) };
        assert_eq!(msg.to_str().unwrap(), "test capture failure");
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn capture_region_negative_desktop_origin_crop() {
        let desktop = monitor_utils::VirtualDesktopBounds {
            x: -1920,
            y: -1080,
            width: 3840,
            height: 2160,
        };
        let captured = create_synthetic_frame(3840, 2160);
        let region = monitor_utils::CropRegion {
            x: -1920,
            y: -1080,
            width: 100,
            height: 100,
        };

        let result = do_crop_from_result(captured, &region, &desktop);
        assert_eq!(result.status, CaptureStatus::Ok);
        assert_eq!(result.width, 100);
        assert_eq!(result.height, 100);
        assert_eq!(result.stride, 400); // 100 * 4
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn capture_region_intermediate_frame_freed() {
        // Verify that the intermediate full-desktop frame is properly freed
        // by checking the frame registry after crop
        let desktop = monitor_utils::VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 50,
            height: 50,
        };
        let captured = create_synthetic_frame(50, 50);
        let intermediate_ptr = captured.frame_data;
        assert!(!intermediate_ptr.is_null());

        let region = monitor_utils::CropRegion {
            x: 0,
            y: 0,
            width: 10,
            height: 10,
        };
        let result = do_crop_from_result(captured, &region, &desktop);
        assert_eq!(result.status, CaptureStatus::Ok);

        // The intermediate pointer should no longer be in the registry
        assert!(unregister_frame(intermediate_ptr).is_none());

        // The result pointer should be in the registry
        let result_ptr = result.frame_data;
        assert!(unregister_frame(result_ptr).is_some());

        // Free the result frame manually since we unregistered it
        unsafe {
            register_frame(result_ptr, result.data_len as usize, result.data_len as usize);
            captcho_free_capture_result(result);
        }
    }

    #[test]
    fn capture_region_tight_stride_from_padded_source() {
        let desktop = monitor_utils::VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 100,
            height: 80,
        };
        // Use a padded stride source to verify tight output stride
        let captured = create_synthetic_frame_with_stride(100, 80, 420); // stride > width*4
        let region = monitor_utils::CropRegion {
            x: 5,
            y: 5,
            width: 50,
            height: 60,
        };

        let result = do_crop_from_result(captured, &region, &desktop);
        assert_eq!(result.status, CaptureStatus::Ok);
        assert_eq!(result.stride, 50 * 4); // Tight stride, not padded
        assert_eq!(result.data_len, 50 * 60 * 4);
        unsafe { captcho_free_capture_result(result) };
    }

    #[test]
    fn validate_captured_buffer_rejects_zero_dims() {
        let captured = CaptureResult {
            status: CaptureStatus::Ok,
            width: 0,
            height: 0,
            stride: 0,
            data_len: 0,
            frame_data: std::ptr::null_mut(),
            error_message: std::ptr::null_mut(),
        };
        let err = validate_captured_buffer(&captured).unwrap_err();
        assert_eq!(err.status, CaptureStatus::InvalidBuffer);
        let msg = unsafe { std::ffi::CStr::from_ptr(err.error_message) };
        assert!(msg.to_str().unwrap().contains("zero dimensions"));
        unsafe { captcho_free_capture_result(err) };
    }

    #[test]
    fn validate_captured_buffer_rejects_short_stride() {
        let captured = CaptureResult {
            status: CaptureStatus::Ok,
            width: 100,
            height: 50,
            stride: 100, // less than width * 4 = 400
            data_len: 100 * 50,
            frame_data: std::ptr::null_mut(),
            error_message: std::ptr::null_mut(),
        };
        let err = validate_captured_buffer(&captured).unwrap_err();
        assert_eq!(err.status, CaptureStatus::InvalidBuffer);
        let msg = unsafe { std::ffi::CStr::from_ptr(err.error_message) };
        assert!(msg.to_str().unwrap().contains("stride"));
        unsafe { captcho_free_capture_result(err) };
    }

    #[test]
    fn validate_captured_buffer_rejects_data_len_mismatch() {
        let captured = CaptureResult {
            status: CaptureStatus::Ok,
            width: 100,
            height: 50,
            stride: 400,
            data_len: 19999, // should be 400 * 50 = 20000
            frame_data: std::ptr::null_mut(),
            error_message: std::ptr::null_mut(),
        };
        let err = validate_captured_buffer(&captured).unwrap_err();
        assert_eq!(err.status, CaptureStatus::InvalidBuffer);
        let msg = unsafe { std::ffi::CStr::from_ptr(err.error_message) };
        assert!(msg.to_str().unwrap().contains("mismatch"));
        unsafe { captcho_free_capture_result(err) };
    }

    #[test]
    fn validate_captured_buffer_accepts_valid() {
        let captured = CaptureResult {
            status: CaptureStatus::Ok,
            width: 100,
            height: 50,
            stride: 400,
            data_len: 20000,
            frame_data: std::ptr::null_mut(),
            error_message: std::ptr::null_mut(),
        };
        assert!(validate_captured_buffer(&captured).is_ok());
    }
}
