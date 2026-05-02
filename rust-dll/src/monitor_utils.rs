//! Monitor geometry and buffer stitching helpers for multi-monitor capture.
//!
//! This module provides testable pure functions for:
//! - Monitor bounds (position + size in virtual-desktop coordinates)
//! - Virtual desktop extent calculation from a set of monitor bounds
//! - Row-copy stitching of per-monitor frames into a virtual-desktop buffer
//! - Buffer invariant validation
//!
//! These are pure functions with no WGC or Win32 dependencies, so they can be
//! unit-tested with synthetic data.

/// Bounds of a single monitor in virtual-desktop coordinates.
///
/// Origin (0,0) is the top-left of the primary monitor. Secondary monitors may
/// have negative x/y coordinates.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MonitorBounds {
    /// Left edge in pixels (may be negative).
    pub x: i32,
    /// Top edge in pixels (may be negative).
    pub y: i32,
    /// Width in pixels.
    pub width: u32,
    /// Height in pixels.
    pub height: u32,
}

impl MonitorBounds {
    /// Right edge (exclusive) in virtual-desktop coordinates.
    pub fn right(&self) -> i32 {
        self.x + self.width as i32
    }

    /// Bottom edge (exclusive) in virtual-desktop coordinates.
    pub fn bottom(&self) -> i32 {
        self.y + self.height as i32
    }
}

/// Computed virtual-desktop extents encompassing all monitors.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VirtualDesktopBounds {
    /// Left edge of the virtual desktop.
    pub x: i32,
    /// Top edge of the virtual desktop.
    pub y: i32,
    /// Total width in pixels.
    pub width: u32,
    /// Total height in pixels.
    pub height: u32,
}

impl VirtualDesktopBounds {
    /// Right edge (exclusive) in virtual-desktop coordinates.
    pub fn right(&self) -> i32 {
        self.x + self.width as i32
    }

    /// Bottom edge (exclusive) in virtual-desktop coordinates.
    pub fn bottom(&self) -> i32 {
        self.y + self.height as i32
    }
}

/// Compute the bounding rectangle that encompasses all given monitor bounds.
///
/// Returns `None` if the monitor list is empty.
/// Returns `None` if the computed width or height would overflow u32.
pub fn compute_virtual_desktop_bounds(monitors: &[MonitorBounds]) -> Option<VirtualDesktopBounds> {
    if monitors.is_empty() {
        return None;
    }

    let min_x = monitors.iter().map(|m| m.x).min()?;
    let min_y = monitors.iter().map(|m| m.y).min()?;
    let max_x = monitors.iter().map(|m| m.right()).max()?;
    let max_y = monitors.iter().map(|m| m.bottom()).max()?;

    let width = (max_x - min_x).try_into().ok()?;
    let height = (max_y - min_y).try_into().ok()?;

    Some(VirtualDesktopBounds {
        x: min_x,
        y: min_y,
        width,
        height,
    })
}

/// Copy a single row of BGRA pixel data from source to destination with stride handling.
///
/// `src` is a slice of the source row data (exactly `src_stride` bytes).
/// `dst` is the full destination buffer.
/// `dst_row_offset` is the byte offset in `dst` where this row starts.
/// `dst_stride` is the byte stride of the destination buffer.
/// `copy_width_bytes` is how many bytes of actual pixel data to copy (typically `monitor_width * 4`).
///
/// # Panics
///
/// Panics if offsets/lengths would cause out-of-bounds access.
pub fn copy_row(
    src: &[u8],
    src_stride: u32,
    dst: &mut [u8],
    dst_row_offset: usize,
    dst_stride: u32,
    copy_width_bytes: u32,
) {
    let copy_len = copy_width_bytes as usize;

    let src_end = src_stride as usize;
    assert!(
        src.len() >= src_end,
        "src buffer too short: len={}, need src_stride={}",
        src.len(),
        src_end
    );

    let dst_end = dst_row_offset + copy_len;
    assert!(
        dst.len() >= dst_end,
        "dst buffer too short: len={}, need offset+copy_len={}",
        dst.len(),
        dst_end
    );

    assert!(
        copy_len <= src_stride as usize,
        "copy_width_bytes {} exceeds src_stride {}",
        copy_width_bytes,
        src_stride
    );
    assert!(
        copy_len <= dst_stride as usize,
        "copy_width_bytes {} exceeds dst_stride {}",
        copy_width_bytes,
        dst_stride
    );

    dst[dst_row_offset..dst_row_offset + copy_len]
        .copy_from_slice(&src[..copy_len]);
}

/// Stitch multiple per-monitor frames into a single virtual-desktop BGRA buffer.
///
/// # Arguments
///
/// * `frames` — iterator of `(MonitorBounds, frame_data: &[u8], frame_stride: u32)`
/// * `desktop` — the computed virtual desktop bounds
/// * `dst` — destination buffer (must be `desktop.stride * desktop.height` bytes, where stride = desktop.width * 4)
///
/// # Returns
///
/// `Ok(())` on success.
/// `Err(String)` with a descriptive message if invariant checks fail.
///
/// # Notes
///
/// - The destination stride is `desktop.width * 4` (tight, no padding).
/// - Each source frame is expected to have stride >= `monitor.width * 4`.
/// - Regions of `dst` not covered by any monitor frame are left as zeros (black).
pub fn stitch_frames<'a>(
    frames: impl ExactSizeIterator<Item = (MonitorBounds, &'a [u8], u32)>,
    desktop: &VirtualDesktopBounds,
    dst: &mut [u8],
) -> Result<(), String> {
    let dst_stride = desktop.width * 4;
    let expected_dst_len = dst_stride as usize * desktop.height as usize;

    if dst.len() != expected_dst_len {
        return Err(format!(
            "stitch: destination buffer size mismatch: got {}, expected {} (stride={} height={})",
            dst.len(),
            expected_dst_len,
            dst_stride,
            desktop.height
        ));
    }

    // Zero the destination so uncovered regions are black
    dst.fill(0);

    for (idx, (bounds, frame_data, frame_stride)) in frames.enumerate() {
        let frame_width_bytes = bounds.width * 4;
        let frame_expected_len = frame_stride as usize * bounds.height as usize;

        if frame_data.len() != frame_expected_len {
            return Err(format!(
                "stitch: frame {} size mismatch: got {}, expected {} (stride={} height={})",
                idx,
                frame_data.len(),
                frame_expected_len,
                frame_stride,
                bounds.height
            ));
        }

        if frame_stride < frame_width_bytes {
            return Err(format!(
                "stitch: frame {} stride {} < minimum width_bytes {}",
                idx, frame_stride, frame_width_bytes
            ));
        }

        // Calculate the offset in the destination buffer where this monitor's pixels go.
        // Both desktop and monitor coordinates use the same virtual-desktop origin.
        let offset_x_bytes = (bounds.x - desktop.x) * 4;
        let offset_y = bounds.y - desktop.y;

        // Bounds check: the monitor must fit within the virtual desktop
        if offset_x_bytes < 0 || offset_y < 0 {
            return Err(format!(
                "stitch: frame {} has negative offset in desktop: x_offset_bytes={}, y_offset={}",
                idx, offset_x_bytes, offset_y
            ));
        }

        let offset_x_bytes = offset_x_bytes as usize;
        let offset_y = offset_y as usize;

        // Copy each row
        for row in 0..bounds.height as usize {
            let src_row_start = row * frame_stride as usize;
            let src_row = &frame_data[src_row_start..src_row_start + frame_stride as usize];

            let dst_row = offset_y + row;
            if dst_row >= desktop.height as usize {
                return Err(format!(
                    "stitch: frame {} row {} exceeds desktop height {}",
                    idx, dst_row, desktop.height
                ));
            }

            let dst_row_offset = dst_row * dst_stride as usize + offset_x_bytes;

            copy_row(
                src_row,
                frame_stride,
                dst,
                dst_row_offset,
                dst_stride,
                frame_width_bytes,
            );
        }
    }

    Ok(())
}

/// Validate buffer invariants for a captured/stitched frame.
///
/// Returns `Ok(())` if all invariants hold, or `Err(description)` otherwise.
pub fn validate_buffer_invariants(
    width: u32,
    height: u32,
    stride: u32,
    data_len: u32,
) -> Result<(), String> {
    if width == 0 || height == 0 {
        return Err(format!(
            "validate: zero dimensions: width={} height={}",
            width, height
        ));
    }

    let min_stride = width * 4;
    if stride < min_stride {
        return Err(format!(
            "validate: stride {} < minimum {} (width={})",
            stride, min_stride, width
        ));
    }

    let expected_len = stride as u64 * height as u64;
    if data_len as u64 != expected_len {
        return Err(format!(
            "validate: data_len {} != stride*height {} (stride={} height={})",
            data_len, expected_len, stride, height
        ));
    }

    Ok(())
}

/// A rectangular region in virtual-desktop coordinates.
///
/// Used to specify the sub-rectangle to crop from a captured/stitched
/// virtual-desktop buffer.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CropRegion {
    /// Left edge in virtual-desktop pixels.
    pub x: i32,
    /// Top edge in virtual-desktop pixels.
    pub y: i32,
    /// Width in pixels.
    pub width: u32,
    /// Height in pixels.
    pub height: u32,
}

impl CropRegion {
    /// Right edge (exclusive) in virtual-desktop coordinates.
    pub fn right(&self) -> i32 {
        self.x + self.width as i32
    }

    /// Bottom edge (exclusive) in virtual-desktop coordinates.
    pub fn bottom(&self) -> i32 {
        self.y + self.height as i32
    }
}

/// Validate that a crop region is within virtual-desktop bounds and has
/// non-zero dimensions.
///
/// Returns `Ok(())` if the region is valid, or `Err(String)` with a
/// phase-prefixed `"region:"` message describing the problem.
///
/// Checks:
/// - Non-zero width and height
/// - Region right/bottom do not overflow (checked arithmetic)
/// - Region is fully contained within the desktop bounds
pub fn validate_crop_region(
    region: &CropRegion,
    desktop: &VirtualDesktopBounds,
) -> Result<(), String> {
    // Zero dimensions
    if region.width == 0 {
        return Err(format!(
            "region: zero width (x={}, y={}, width=0, height={})",
            region.x, region.y, region.height
        ));
    }
    if region.height == 0 {
        return Err(format!(
            "region: zero height (x={}, y={}, width={}, height=0)",
            region.x, region.y, region.width
        ));
    }

    // Overflow-safe right/bottom
    let right = region.right();
    let bottom = region.bottom();

    // Detect overflow: if width is so large that x + width wraps around
    let desktop_right = desktop.right();
    let desktop_bottom = desktop.bottom();

    // Check containment: region must be fully inside desktop
    if region.x < desktop.x {
        return Err(format!(
            "region: left edge {} is left of desktop origin {}",
            region.x, desktop.x
        ));
    }
    if region.y < desktop.y {
        return Err(format!(
            "region: top edge {} is above desktop origin {}",
            region.y, desktop.y
        ));
    }
    if right > desktop_right {
        return Err(format!(
            "region: right edge {} exceeds desktop right {} (x={}, width={})",
            right, desktop_right, region.x, region.width
        ));
    }
    if bottom > desktop_bottom {
        return Err(format!(
            "region: bottom edge {} exceeds desktop bottom {} (y={}, height={})",
            bottom, desktop_bottom, region.y, region.height
        ));
    }

    Ok(())
}

/// Crop result containing tight-stride BGRA pixel data.
#[derive(Debug)]
pub struct CropResult {
    /// Cropped pixel data with tight stride (`width * 4`).
    pub data: Vec<u8>,
    /// Width of the cropped region in pixels.
    pub width: u32,
    /// Height of the cropped region in pixels.
    pub height: u32,
}

/// Crop a sub-rectangle from a virtual-desktop BGRA buffer.
///
/// # Arguments
///
/// * `src_data` — source BGRA buffer (stitched virtual desktop)
/// * `src_width` — source width in pixels
/// * `src_height` — source height in pixels
/// * `src_stride` — source byte stride per row (may have padding)
/// * `region` — the sub-rectangle to extract, in virtual-desktop coordinates
/// * `desktop` — the virtual-desktop bounds (used to translate coordinates)
///
/// # Returns
///
/// `Ok(CropResult)` with tight-stride BGRA data on success.
/// `Err(String)` with a phase-prefixed `"crop:"` message on failure.
///
/// # Notes
///
/// - The result always has a tight stride of `width * 4`.
/// - Only the requested BGRA pixels are copied; no padding is preserved.
/// - Region is validated before any buffer reads.
/// - No global state is touched; safe for concurrent use.
pub fn crop_region(
    src_data: &[u8],
    src_width: u32,
    src_height: u32,
    src_stride: u32,
    region: &CropRegion,
    desktop: &VirtualDesktopBounds,
) -> Result<CropResult, String> {
    // Phase 1: validate region geometry
    validate_crop_region(region, desktop)?;

    // Phase 2: validate source buffer
    let min_src_stride = src_width
        .checked_mul(4)
        .ok_or_else(|| "crop: source width overflow when computing minimum stride".to_string())?;
    if src_stride < min_src_stride {
        return Err(format!(
            "crop: source stride {} < minimum {} (width={})",
            src_stride, min_src_stride, src_width
        ));
    }

    let expected_src_len = (src_stride as u64)
        .checked_mul(src_height as u64)
        .ok_or_else(|| "crop: source buffer length overflow".to_string())?;
    if src_data.len() < expected_src_len as usize {
        return Err(format!(
            "crop: source buffer too short: {} bytes, need {} (stride={} height={})",
            src_data.len(),
            expected_src_len,
            src_stride,
            src_height
        ));
    }

    // Phase 3: compute source buffer offsets
    let offset_x = region.x - desktop.x;
    let offset_y = region.y - desktop.y;

    // These should be non-negative after validate_crop_region, but defend
    // against arithmetic edge cases
    if offset_x < 0 || offset_y < 0 {
        return Err(format!(
            "crop: negative offset after coordinate translation (offset_x={}, offset_y={})",
            offset_x, offset_y
        ));
    }

    let offset_x = offset_x as u32;
    let offset_y = offset_y as u32;

    // Byte offset of the first pixel in each source row
    let src_row_byte_offset = offset_x
        .checked_mul(4)
        .ok_or_else(|| "crop: x offset overflow when computing byte offset".to_string())?;

    let copy_bytes = region
        .width
        .checked_mul(4)
        .ok_or_else(|| "crop: region width overflow when computing copy bytes".to_string())?;

    // Verify the crop doesn't exceed source dimensions
    if offset_x + region.width > src_width {
        return Err(format!(
            "crop: region x range [{}, {}) exceeds source width {}",
            offset_x,
            offset_x + region.width,
            src_width
        ));
    }
    if offset_y + region.height > src_height {
        return Err(format!(
            "crop: region y range [{}, {}) exceeds source height {}",
            offset_y,
            offset_y + region.height,
            src_height
        ));
    }

    // Phase 4: allocate and copy rows with tight stride
    let dst_stride = copy_bytes as usize;
    let dst_len = dst_stride
        .checked_mul(region.height as usize)
        .ok_or_else(|| "crop: destination buffer size overflow".to_string())?;
    let mut dst = vec![0u8; dst_len];

    for row in 0..region.height as usize {
        let src_row_start = (offset_y as usize + row) * src_stride as usize + src_row_byte_offset as usize;
        let src_row_end = src_row_start + copy_bytes as usize;
        let dst_row_start = row * dst_stride;

        // Bounds check (should be guaranteed by above validation, but be safe)
        if src_row_end > src_data.len() {
            return Err(format!(
                "crop: source row {} would read past buffer end ({} > {})",
                row, src_row_end, src_data.len()
            ));
        }

        dst[dst_row_start..dst_row_start + copy_bytes as usize]
            .copy_from_slice(&src_data[src_row_start..src_row_end]);
    }

    Ok(CropResult {
        data: dst,
        width: region.width,
        height: region.height,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    // --- MonitorBounds ---

    #[test]
    fn monitor_bounds_right_and_bottom() {
        let b = MonitorBounds {
            x: -1920,
            y: 0,
            width: 1920,
            height: 1080,
        };
        assert_eq!(b.right(), 0);
        assert_eq!(b.bottom(), 1080);
    }

    #[test]
    fn monitor_bounds_origin() {
        let b = MonitorBounds {
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
        };
        assert_eq!(b.right(), 1920);
        assert_eq!(b.bottom(), 1080);
    }

    #[test]
    fn monitor_bounds_negative_both() {
        let b = MonitorBounds {
            x: -3840,
            y: -2160,
            width: 1920,
            height: 1080,
        };
        assert_eq!(b.right(), -1920);
        assert_eq!(b.bottom(), -1080);
    }

    // --- compute_virtual_desktop_bounds ---

    #[test]
    fn compute_bounds_empty_is_none() {
        assert!(compute_virtual_desktop_bounds(&[]).is_none());
    }

    #[test]
    fn compute_bounds_single_primary() {
        let monitors = [MonitorBounds {
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
        }];
        let bounds = compute_virtual_desktop_bounds(&monitors).unwrap();
        assert_eq!(bounds.x, 0);
        assert_eq!(bounds.y, 0);
        assert_eq!(bounds.width, 1920);
        assert_eq!(bounds.height, 1080);
    }

    #[test]
    fn compute_bounds_dual_side_by_side() {
        let monitors = [
            MonitorBounds {
                x: 0,
                y: 0,
                width: 1920,
                height: 1080,
            },
            MonitorBounds {
                x: 1920,
                y: 0,
                width: 1920,
                height: 1080,
            },
        ];
        let bounds = compute_virtual_desktop_bounds(&monitors).unwrap();
        assert_eq!(bounds.x, 0);
        assert_eq!(bounds.y, 0);
        assert_eq!(bounds.width, 3840);
        assert_eq!(bounds.height, 1080);
    }

    #[test]
    fn compute_bounds_left_monitor_negative_x() {
        let monitors = [
            MonitorBounds {
                x: -1920,
                y: 0,
                width: 1920,
                height: 1080,
            },
            MonitorBounds {
                x: 0,
                y: 0,
                width: 1920,
                height: 1080,
            },
        ];
        let bounds = compute_virtual_desktop_bounds(&monitors).unwrap();
        assert_eq!(bounds.x, -1920);
        assert_eq!(bounds.y, 0);
        assert_eq!(bounds.width, 3840);
        assert_eq!(bounds.height, 1080);
    }

    #[test]
    fn compute_bounds_stacked_monitors() {
        let monitors = [
            MonitorBounds {
                x: 0,
                y: -1080,
                width: 1920,
                height: 1080,
            },
            MonitorBounds {
                x: 0,
                y: 0,
                width: 1920,
                height: 1080,
            },
        ];
        let bounds = compute_virtual_desktop_bounds(&monitors).unwrap();
        assert_eq!(bounds.x, 0);
        assert_eq!(bounds.y, -1080);
        assert_eq!(bounds.width, 1920);
        assert_eq!(bounds.height, 2160);
    }

    #[test]
    fn compute_bounds_misaligned_monitors() {
        let monitors = [
            MonitorBounds {
                x: -1920,
                y: -200,
                width: 1920,
                height: 1080,
            },
            MonitorBounds {
                x: 0,
                y: 0,
                width: 2560,
                height: 1440,
            },
        ];
        let bounds = compute_virtual_desktop_bounds(&monitors).unwrap();
        assert_eq!(bounds.x, -1920);
        assert_eq!(bounds.y, -200);
        assert_eq!(bounds.width, 4480); // 2560 - (-1920) = 4480
        assert_eq!(bounds.height, 1640); // 1440 - (-200) = 1640
    }

    // --- validate_buffer_invariants ---

    #[test]
    fn validate_ok() {
        assert!(validate_buffer_invariants(1920, 1080, 7680, 8294400).is_ok());
    }

    #[test]
    fn validate_zero_width() {
        assert!(validate_buffer_invariants(0, 1080, 0, 0).is_err());
    }

    #[test]
    fn validate_zero_height() {
        assert!(validate_buffer_invariants(1920, 0, 7680, 0).is_err());
    }

    #[test]
    fn validate_stride_less_than_minimum() {
        let result = validate_buffer_invariants(100, 50, 399, 399 * 50);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("stride"));
    }

    #[test]
    fn validate_data_len_mismatch() {
        let result = validate_buffer_invariants(100, 50, 400, 19999);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("data_len"));
    }

    // --- copy_row ---

    #[test]
    fn copy_row_basic() {
        let src = [1u8, 2, 3, 4, 5, 6, 7, 8]; // 2 pixels, stride 8
        let mut dst = [0u8; 8];
        copy_row(&src, 8, &mut dst, 0, 8, 8);
        assert_eq!(dst, src);
    }

    #[test]
    fn copy_row_with_src_padding() {
        // 1 pixel (4 bytes) + 4 bytes padding = stride 8
        let src = [0xAA, 0xBB, 0xCC, 0xDD, 0x00, 0x00, 0x00, 0x00];
        let mut dst = [0u8; 8];
        // Only copy the 4 real bytes
        copy_row(&src, 8, &mut dst, 0, 8, 4);
        assert_eq!(&dst[..4], &[0xAA, 0xBB, 0xCC, 0xDD]);
        assert_eq!(&dst[4..], &[0, 0, 0, 0]); // padding untouched
    }

    #[test]
    fn copy_row_to_offset() {
        let src = [0xFF; 4];
        let mut dst = [0u8; 12];
        copy_row(&src, 4, &mut dst, 4, 8, 4);
        assert_eq!(&dst[..4], &[0, 0, 0, 0]);
        assert_eq!(&dst[4..8], &[0xFF; 4]);
        assert_eq!(&dst[8..], &[0, 0, 0, 0]);
    }

    // --- stitch_frames ---

    fn make_frame(width: u32, height: u32, value: u8) -> (Vec<u8>, u32) {
        let stride = width * 4;
        let data = vec![value; stride as usize * height as usize];
        (data, stride)
    }

    #[test]
    fn stitch_single_monitor_fills_dst() {
        let monitor = MonitorBounds {
            x: 0,
            y: 0,
            width: 4,
            height: 2,
        };
        let desktop = VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 4,
            height: 2,
        };
        let (frame_data, frame_stride) = make_frame(4, 2, 0xAB);
        let mut dst = vec![0u8; 4 * 4 * 2]; // 32 bytes

        stitch_frames(
            std::iter::once((monitor, frame_data.as_slice(), frame_stride)),
            &desktop,
            &mut dst,
        )
        .unwrap();

        // All bytes should be 0xAB
        assert!(dst.iter().all(|&b| b == 0xAB));
    }

    #[test]
    fn stitch_two_monitors_side_by_side() {
        let m1 = MonitorBounds {
            x: 0,
            y: 0,
            width: 2,
            height: 2,
        };
        let m2 = MonitorBounds {
            x: 2,
            y: 0,
            width: 2,
            height: 2,
        };
        let desktop = VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 4,
            height: 2,
        };
        let (f1, s1) = make_frame(2, 2, 0x11);
        let (f2, s2) = make_frame(2, 2, 0x22);
        let mut dst = vec![0u8; 4 * 4 * 2]; // 32 bytes

        stitch_frames(
            vec![
                (m1, f1.as_slice(), s1),
                (m2, f2.as_slice(), s2),
            ]
            .into_iter(),
            &desktop,
            &mut dst,
        )
        .unwrap();

        // Row 0: m1 pixels (2px * 4 = 8 bytes of 0x11), m2 pixels (8 bytes of 0x22)
        let dst_stride = 16;
        for row in 0..2 {
            let base = row * dst_stride;
            assert!(dst[base..base + 8].iter().all(|&b| b == 0x11), "m1 row {}", row);
            assert!(dst[base + 8..base + 16].iter().all(|&b| b == 0x22), "m2 row {}", row);
        }
    }

    #[test]
    fn stitch_with_negative_x_offset() {
        // Monitor at x=-2, desktop starts at x=-2
        let m = MonitorBounds {
            x: -2,
            y: 0,
            width: 2,
            height: 1,
        };
        let desktop = VirtualDesktopBounds {
            x: -2,
            y: 0,
            width: 2,
            height: 1,
        };
        let (frame, stride) = make_frame(2, 1, 0xCC);
        let mut dst = vec![0u8; 2 * 4 * 1]; // 8 bytes

        stitch_frames(
            std::iter::once((m, frame.as_slice(), stride)),
            &desktop,
            &mut dst,
        )
        .unwrap();

        assert!(dst.iter().all(|&b| b == 0xCC));
    }

    #[test]
    fn stitch_dst_size_mismatch_returns_error() {
        let m = MonitorBounds {
            x: 0,
            y: 0,
            width: 2,
            height: 2,
        };
        let desktop = VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 2,
            height: 2,
        };
        let (frame, stride) = make_frame(2, 2, 0xFF);
        let mut dst = vec![0u8; 7]; // Wrong size

        let result = stitch_frames(
            std::iter::once((m, frame.as_slice(), stride)),
            &desktop,
            &mut dst,
        );
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("size mismatch"));
    }

    #[test]
    fn stitch_frame_size_mismatch_returns_error() {
        let m = MonitorBounds {
            x: 0,
            y: 0,
            width: 2,
            height: 2,
        };
        let desktop = VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 2,
            height: 2,
        };
        let bad_frame = vec![0xFF; 5]; // Too short
        let mut dst = vec![0u8; 16];

        let result = stitch_frames(
            std::iter::once((m, bad_frame.as_slice(), 8u32)),
            &desktop,
            &mut dst,
        );
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("size mismatch"));
    }

    #[test]
    fn stitch_uncovered_region_is_black() {
        // Desktop is 4x1 but only first 2 pixels covered by a monitor
        let m = MonitorBounds {
            x: 0,
            y: 0,
            width: 2,
            height: 1,
        };
        let desktop = VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 4,
            height: 1,
        };
        let (frame, stride) = make_frame(2, 1, 0xFF);
        let mut dst = vec![0u8; 4 * 4 * 1]; // 16 bytes

        stitch_frames(
            std::iter::once((m, frame.as_slice(), stride)),
            &desktop,
            &mut dst,
        )
        .unwrap();

        // First 8 bytes are 0xFF, last 8 are 0x00
        assert!(dst[..8].iter().all(|&b| b == 0xFF));
        assert!(dst[8..].iter().all(|&b| b == 0x00));
    }

    #[test]
    fn stitch_with_padded_src_stride() {
        // Monitor is 2px wide, stride is 16 (8 bytes pixel + 8 bytes padding)
        let m = MonitorBounds {
            x: 0,
            y: 0,
            width: 2,
            height: 1,
        };
        let desktop = VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 2,
            height: 1,
        };
        let mut frame = vec![0u8; 16]; // stride=16, but only 8 bytes of real data
        frame[..8].fill(0xAB);
        // padding stays 0x00
        let mut dst = vec![0u8; 8]; // tight stride (2*4=8)

        stitch_frames(
            std::iter::once((m, frame.as_slice(), 16u32)),
            &desktop,
            &mut dst,
        )
        .unwrap();

        // Should only copy the real pixel bytes (8), padding ignored
        assert!(dst.iter().all(|&b| b == 0xAB));
    }

    // =======================================================================
    // Crop region validation tests
    // =======================================================================

    fn desktop_3840x2160() -> VirtualDesktopBounds {
        VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 3840,
            height: 2160,
        }
    }

    fn desktop_with_negative_origin() -> VirtualDesktopBounds {
        VirtualDesktopBounds {
            x: -1920,
            y: -1080,
            width: 3840,
            height: 2160,
        }
    }

    // --- validate_crop_region: happy paths ---

    #[test]
    fn validate_region_full_desktop() {
        let region = CropRegion {
            x: 0,
            y: 0,
            width: 3840,
            height: 2160,
        };
        assert!(validate_crop_region(&region, &desktop_3840x2160()).is_ok());
    }

    #[test]
    fn validate_region_small_interior() {
        let region = CropRegion {
            x: 100,
            y: 200,
            width: 800,
            height: 600,
        };
        assert!(validate_crop_region(&region, &desktop_3840x2160()).is_ok());
    }

    #[test]
    fn validate_region_1x1_top_left() {
        let region = CropRegion {
            x: 0,
            y: 0,
            width: 1,
            height: 1,
        };
        assert!(validate_crop_region(&region, &desktop_3840x2160()).is_ok());
    }

    #[test]
    fn validate_region_1x1_bottom_right_edge() {
        let region = CropRegion {
            x: 3839,
            y: 2159,
            width: 1,
            height: 1,
        };
        assert!(validate_crop_region(&region, &desktop_3840x2160()).is_ok());
    }

    #[test]
    fn validate_region_with_negative_desktop_origin() {
        // Region starts at desktop origin (-1920, -1080)
        let region = CropRegion {
            x: -1920,
            y: -1080,
            width: 3840,
            height: 2160,
        };
        assert!(validate_crop_region(&region, &desktop_with_negative_origin()).is_ok());
    }

    // --- validate_crop_region: rejection paths ---

    #[test]
    fn validate_region_zero_width_rejected() {
        let region = CropRegion {
            x: 100,
            y: 100,
            width: 0,
            height: 100,
        };
        let err = validate_crop_region(&region, &desktop_3840x2160()).unwrap_err();
        assert!(err.starts_with("region:"));
        assert!(err.contains("zero width"));
    }

    #[test]
    fn validate_region_zero_height_rejected() {
        let region = CropRegion {
            x: 100,
            y: 100,
            width: 100,
            height: 0,
        };
        let err = validate_crop_region(&region, &desktop_3840x2160()).unwrap_err();
        assert!(err.starts_with("region:"));
        assert!(err.contains("zero height"));
    }

    #[test]
    fn validate_region_left_of_desktop_rejected() {
        let region = CropRegion {
            x: -1,
            y: 0,
            width: 100,
            height: 100,
        };
        let err = validate_crop_region(&region, &desktop_3840x2160()).unwrap_err();
        assert!(err.starts_with("region:"));
        assert!(err.contains("left of desktop"));
    }

    #[test]
    fn validate_region_above_desktop_rejected() {
        let region = CropRegion {
            x: 0,
            y: -1,
            width: 100,
            height: 100,
        };
        let err = validate_crop_region(&region, &desktop_3840x2160()).unwrap_err();
        assert!(err.starts_with("region:"));
        assert!(err.contains("above desktop"));
    }

    #[test]
    fn validate_region_right_exceeds_desktop_rejected() {
        let region = CropRegion {
            x: 3830,
            y: 0,
            width: 20, // right = 3850 > 3840
            height: 100,
        };
        let err = validate_crop_region(&region, &desktop_3840x2160()).unwrap_err();
        assert!(err.starts_with("region:"));
        assert!(err.contains("exceeds desktop right"));
    }

    #[test]
    fn validate_region_bottom_exceeds_desktop_rejected() {
        let region = CropRegion {
            x: 0,
            y: 2150,
            width: 100,
            height: 20, // bottom = 2170 > 2160
        };
        let err = validate_crop_region(&region, &desktop_3840x2160()).unwrap_err();
        assert!(err.starts_with("region:"));
        assert!(err.contains("exceeds desktop bottom"));
    }

    #[test]
    fn validate_region_negative_origin_left_of_desktop() {
        // Desktop at (-1920, -1080, 3840x2160); region starts at -1921
        let region = CropRegion {
            x: -1921,
            y: -1080,
            width: 100,
            height: 100,
        };
        let err = validate_crop_region(&region, &desktop_with_negative_origin()).unwrap_err();
        assert!(err.starts_with("region:"));
    }

    // =======================================================================
    // crop_region tests
    // =======================================================================

    /// Helper: create a synthetic BGRA source buffer filled with a pattern.
    /// Each pixel at (x, y) gets value ((x + y * width) % 256) repeated 4 times.
    fn make_pattern_bgra(width: u32, height: u32, stride: u32) -> Vec<u8> {
        let mut buf = vec![0u8; stride as usize * height as usize];
        for y in 0..height {
            for x in 0..width {
                let val = ((x + y * width) % 256) as u8;
                let offset = y as usize * stride as usize + x as usize * 4;
                buf[offset..offset + 4].fill(val);
            }
        }
        buf
    }

    // --- crop_region: happy paths ---

    #[test]
    fn crop_tight_stride_full_region() {
        let desktop = desktop_3840x2160();
        let (w, h) = (desktop.width, desktop.height);
        let stride = w * 4;
        let src = make_pattern_bgra(w, h, stride);
        let region = CropRegion {
            x: 0,
            y: 0,
            width: w,
            height: h,
        };

        let result = crop_region(&src, w, h, stride, &region, &desktop).unwrap();
        assert_eq!(result.width, w);
        assert_eq!(result.height, h);
        assert_eq!(result.data.len(), w as usize * h as usize * 4);
        // Data should match source exactly
        assert_eq!(result.data, src);
    }

    #[test]
    fn crop_small_interior_region() {
        let desktop = desktop_3840x2160();
        let (w, h) = (desktop.width, desktop.height);
        let stride = w * 4;
        let src = make_pattern_bgra(w, h, stride);
        let region = CropRegion {
            x: 100,
            y: 200,
            width: 800,
            height: 600,
        };

        let result = crop_region(&src, w, h, stride, &region, &desktop).unwrap();
        assert_eq!(result.width, 800);
        assert_eq!(result.height, 600);
        assert_eq!(result.data.len(), 800 * 600 * 4);

        // Verify a few sample pixels
        for (dy, dx) in [(0, 0), (300, 400), (599, 799)] {
            let src_x = region.x as u32 + dx;
            let src_y = region.y as u32 + dy;
            let expected_val = ((src_x + src_y * w) % 256) as u8;
            let dst_offset = dy as usize * 800 * 4 + dx as usize * 4;
            assert_eq!(
                result.data[dst_offset..dst_offset + 4],
                [expected_val; 4],
                "pixel mismatch at ({}, {})",
                dx, dy
            );
        }
    }

    #[test]
    fn crop_1x1_top_left() {
        let desktop = desktop_3840x2160();
        let (w, h) = (desktop.width, desktop.height);
        let stride = w * 4;
        let src = make_pattern_bgra(w, h, stride);
        let region = CropRegion {
            x: 0,
            y: 0,
            width: 1,
            height: 1,
        };

        let result = crop_region(&src, w, h, stride, &region, &desktop).unwrap();
        assert_eq!(result.width, 1);
        assert_eq!(result.height, 1);
        assert_eq!(result.data.len(), 4);
        let expected = ((0u32 + 0u32 * w) % 256) as u8;
        assert_eq!(result.data[..], [expected; 4]);
    }

    #[test]
    fn crop_1x1_bottom_right_edge() {
        let desktop = desktop_3840x2160();
        let (w, h) = (desktop.width, desktop.height);
        let stride = w * 4;
        let src = make_pattern_bgra(w, h, stride);
        let region = CropRegion {
            x: 3839,
            y: 2159,
            width: 1,
            height: 1,
        };

        let result = crop_region(&src, w, h, stride, &region, &desktop).unwrap();
        assert_eq!(result.width, 1);
        assert_eq!(result.height, 1);
        assert_eq!(result.data.len(), 4);
        let expected = ((3839u32 + 2159u32 * w) % 256) as u8;
        assert_eq!(result.data[..], [expected; 4]);
    }

    #[test]
    fn crop_with_negative_desktop_origin() {
        let desktop = desktop_with_negative_origin();
        let (w, h) = (desktop.width, desktop.height);
        let stride = w * 4;
        let src = make_pattern_bgra(w, h, stride);

        // Crop a 10x10 region starting at the desktop origin
        let region = CropRegion {
            x: -1920,
            y: -1080,
            width: 10,
            height: 10,
        };

        let result = crop_region(&src, w, h, stride, &region, &desktop).unwrap();
        assert_eq!(result.width, 10);
        assert_eq!(result.height, 10);

        // Verify first pixel (virtual coords -1920,-1080 map to offset 0,0)
        let expected = ((0u32 + 0u32 * w) % 256) as u8;
        assert_eq!(result.data[..4], [expected; 4]);
    }

    #[test]
    fn crop_with_padded_source_stride() {
        let desktop = desktop_3840x2160();
        let (w, h) = (desktop.width, desktop.height);
        let padded_stride = w * 4 + 128; // 128 bytes of padding per row
        let src = make_pattern_bgra(w, h, padded_stride);
        let region = CropRegion {
            x: 100,
            y: 50,
            width: 200,
            height: 100,
        };

        let result = crop_region(&src, w, h, padded_stride, &region, &desktop).unwrap();
        assert_eq!(result.width, 200);
        assert_eq!(result.height, 100);

        // Verify pixel at (0, 0) of the crop maps to src pixel (100, 50)
        let src_x = 100u32;
        let src_y = 50u32;
        let expected = ((src_x + src_y * w) % 256) as u8;
        assert_eq!(result.data[..4], [expected; 4]);
    }

    #[test]
    fn crop_tight_stride_output() {
        // Verify result stride is exactly width * 4 (no padding)
        let desktop = desktop_3840x2160();
        let (w, h) = (desktop.width, desktop.height);
        let stride = w * 4;
        let src = make_pattern_bgra(w, h, stride);
        let region = CropRegion {
            x: 500,
            y: 300,
            width: 123,
            height: 87,
        };

        let result = crop_region(&src, w, h, stride, &region, &desktop).unwrap();
        assert_eq!(result.data.len(), 123 * 87 * 4);
    }

    // --- crop_region: rejection paths ---

    #[test]
    fn crop_zero_width_rejected() {
        let desktop = desktop_3840x2160();
        let src = make_pattern_bgra(desktop.width, desktop.height, desktop.width * 4);
        let region = CropRegion {
            x: 100,
            y: 100,
            width: 0,
            height: 100,
        };

        let err = crop_region(&src, desktop.width, desktop.height, desktop.width * 4, &region, &desktop).unwrap_err();
        assert!(err.starts_with("region:"));
        assert!(err.contains("zero width"));
    }

    #[test]
    fn crop_out_of_bounds_right_rejected() {
        let desktop = desktop_3840x2160();
        let src = make_pattern_bgra(desktop.width, desktop.height, desktop.width * 4);
        let region = CropRegion {
            x: 3800,
            y: 0,
            width: 100, // right = 3900 > 3840
            height: 10,
        };

        let err = crop_region(&src, desktop.width, desktop.height, desktop.width * 4, &region, &desktop).unwrap_err();
        assert!(err.starts_with("region:"));
    }

    #[test]
    fn crop_source_buffer_too_short_rejected() {
        let desktop = desktop_3840x2160();
        let tiny_src = vec![0u8; 16]; // Way too small
        let region = CropRegion {
            x: 0,
            y: 0,
            width: 1,
            height: 1,
        };

        let err = crop_region(&tiny_src, desktop.width, desktop.height, desktop.width * 4, &region, &desktop).unwrap_err();
        assert!(err.starts_with("crop:"));
        assert!(err.contains("too short"));
    }

    #[test]
    fn crop_stride_less_than_minimum_rejected() {
        let desktop = desktop_3840x2160();
        let src = vec![0u8; 999999];
        let region = CropRegion {
            x: 0,
            y: 0,
            width: 1,
            height: 1,
        };

        // Pass a stride smaller than width * 4
        let err = crop_region(&src, desktop.width, desktop.height, 100, &region, &desktop).unwrap_err();
        assert!(err.starts_with("crop:"));
        assert!(err.contains("source stride"));
    }

    #[test]
    fn crop_region_left_of_desktop_rejected() {
        let desktop = desktop_3840x2160();
        let src = make_pattern_bgra(desktop.width, desktop.height, desktop.width * 4);
        let region = CropRegion {
            x: -10,
            y: 0,
            width: 100,
            height: 100,
        };

        let err = crop_region(&src, desktop.width, desktop.height, desktop.width * 4, &region, &desktop).unwrap_err();
        assert!(err.starts_with("region:"));
    }

    #[test]
    fn crop_region_above_desktop_rejected() {
        let desktop = desktop_3840x2160();
        let src = make_pattern_bgra(desktop.width, desktop.height, desktop.width * 4);
        let region = CropRegion {
            x: 0,
            y: -5,
            width: 100,
            height: 100,
        };

        let err = crop_region(&src, desktop.width, desktop.height, desktop.width * 4, &region, &desktop).unwrap_err();
        assert!(err.starts_with("region:"));
    }

    // --- VirtualDesktopBounds right/bottom ---

    #[test]
    fn desktop_bounds_right_and_bottom() {
        let b = VirtualDesktopBounds {
            x: -1920,
            y: -1080,
            width: 3840,
            height: 2160,
        };
        assert_eq!(b.right(), 1920);
        assert_eq!(b.bottom(), 1080);
    }

    #[test]
    fn desktop_bounds_origin() {
        let b = VirtualDesktopBounds {
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
        };
        assert_eq!(b.right(), 1920);
        assert_eq!(b.bottom(), 1080);
    }
}
