//! Integration tests for monitor geometry and stitching helpers.
//!
//! These tests exercise the pure functions in `monitor_utils` using synthetic
//! data, covering virtual desktop bounds computation, row-copy stitching,
//! stride padding, negative coordinates, and buffer invariant validation.

use respectacle_capture::monitor_utils::*;

// ---------------------------------------------------------------------------
// Virtual desktop bounds computation
// ---------------------------------------------------------------------------

#[test]
fn single_monitor_desktop_bounds_match_monitor() {
    let monitors = [MonitorBounds {
        x: 0,
        y: 0,
        width: 1920,
        height: 1080,
    }];
    let desktop = compute_virtual_desktop_bounds(&monitors).unwrap();
    assert_eq!(desktop.x, 0);
    assert_eq!(desktop.y, 0);
    assert_eq!(desktop.width, 1920);
    assert_eq!(desktop.height, 1080);
}

#[test]
fn dual_monitor_side_by_side_positive_x() {
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
            width: 2560,
            height: 1440,
        },
    ];
    let desktop = compute_virtual_desktop_bounds(&monitors).unwrap();
    assert_eq!(desktop.x, 0);
    assert_eq!(desktop.y, 0);
    assert_eq!(desktop.width, 4480);
    assert_eq!(desktop.height, 1440);
}

#[test]
fn dual_monitor_left_negative_x() {
    // Left monitor at x=-1920, right at x=0
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
    let desktop = compute_virtual_desktop_bounds(&monitors).unwrap();
    assert_eq!(desktop.x, -1920);
    assert_eq!(desktop.width, 3840);
}

#[test]
fn stacked_monitors_negative_y() {
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
    let desktop = compute_virtual_desktop_bounds(&monitors).unwrap();
    assert_eq!(desktop.y, -1080);
    assert_eq!(desktop.height, 2160);
}

#[test]
fn triple_monitor_v_shape() {
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
            width: 1920,
            height: 1080,
        },
        MonitorBounds {
            x: 1920,
            y: -200,
            width: 1920,
            height: 1080,
        },
    ];
    let desktop = compute_virtual_desktop_bounds(&monitors).unwrap();
    assert_eq!(desktop.x, -1920);
    assert_eq!(desktop.y, -200);
    assert_eq!(desktop.width, 5760);
    assert_eq!(desktop.height, 1280);
}

#[test]
fn empty_monitors_returns_none() {
    assert!(compute_virtual_desktop_bounds(&[]).is_none());
}

// ---------------------------------------------------------------------------
// Stitching edge cases
// ---------------------------------------------------------------------------

fn fill_frame(width: u32, height: u32, value: u8) -> (Vec<u8>, u32) {
    let stride = width * 4;
    let len = stride as usize * height as usize;
    (vec![value; len], stride)
}

#[test]
fn stitch_two_equal_monitors_negative_left_x() {
    let m_left = MonitorBounds {
        x: -2,
        y: 0,
        width: 2,
        height: 2,
    };
    let m_right = MonitorBounds {
        x: 0,
        y: 0,
        width: 2,
        height: 2,
    };
    let desktop = VirtualDesktopBounds {
        x: -2,
        y: 0,
        width: 4,
        height: 2,
    };

    let (f_left, s_left) = fill_frame(2, 2, 0x11);
    let (f_right, s_right) = fill_frame(2, 2, 0x22);
    let mut dst = vec![0u8; 4 * 4 * 2]; // 32 bytes, stride 16

    stitch_frames(
        vec![
            (m_left, f_left.as_slice(), s_left),
            (m_right, f_right.as_slice(), s_right),
        ]
        .into_iter(),
        &desktop,
        &mut dst,
    )
    .unwrap();

    // Each row: left 8 bytes = 0x11, right 8 bytes = 0x22
    let stride = 16;
    for row in 0..2 {
        let base = row * stride;
        assert!(dst[base..base + 8].iter().all(|&b| b == 0x11));
        assert!(dst[base + 8..base + 16].iter().all(|&b| b == 0x22));
    }
}

#[test]
fn stitch_three_monitors_v_shape() {
    let monitors = [
        MonitorBounds {
            x: -2,
            y: -1,
            width: 2,
            height: 1,
        },
        MonitorBounds {
            x: 0,
            y: 0,
            width: 2,
            height: 1,
        },
        MonitorBounds {
            x: 2,
            y: -1,
            width: 2,
            height: 1,
        },
    ];
    let desktop = VirtualDesktopBounds {
        x: -2,
        y: -1,
        width: 6,
        height: 2,
    };

    let (f0, s0) = fill_frame(2, 1, 0xAA);
    let (f1, s1) = fill_frame(2, 1, 0xBB);
    let (f2, s2) = fill_frame(2, 1, 0xCC);
    let mut dst = vec![0u8; 6 * 4 * 2]; // 48 bytes, stride 24

    stitch_frames(
        vec![
            (monitors[0], f0.as_slice(), s0),
            (monitors[1], f1.as_slice(), s1),
            (monitors[2], f2.as_slice(), s2),
        ]
        .into_iter(),
        &desktop,
        &mut dst,
    )
    .unwrap();

    let stride = 24;
    // Row 0 (desktop y=-1): monitors 0 and 2 are at y=-1
    // m0 at x=-2 → offset 0, 8 bytes of 0xAA
    assert!(dst[0..8].iter().all(|&b| b == 0xAA));
    // m0 gap: x=0 to x=0 is gap (8 bytes of 0x00)
    assert!(dst[8..16].iter().all(|&b| b == 0x00));
    // m2 at x=2 → offset 16, 8 bytes of 0xCC
    assert!(dst[16..24].iter().all(|&b| b == 0xCC));

    // Row 1 (desktop y=0): monitor 1 is at y=0
    let row1_base = stride;
    // gap before m1 (x=-2 to x=0 = 8 bytes of 0)
    assert!(dst[row1_base..row1_base + 8].iter().all(|&b| b == 0x00));
    // m1 at x=0 → offset 8, 8 bytes of 0xBB
    assert!(dst[row1_base + 8..row1_base + 16].iter().all(|&b| b == 0xBB));
    // gap after m1 (x=2 to x=4 = 8 bytes of 0)
    assert!(dst[row1_base + 16..row1_base + 24].iter().all(|&b| b == 0x00));
}

#[test]
fn stitch_single_monitor_with_padded_stride() {
    let m = MonitorBounds {
        x: 0,
        y: 0,
        width: 3,
        height: 2,
    };
    let desktop = VirtualDesktopBounds {
        x: 0,
        y: 0,
        width: 3,
        height: 2,
    };

    // Create frame with stride=20 (3*4=12 real bytes + 8 padding per row)
    let stride: u32 = 20;
    let mut frame = vec![0u8; stride as usize * 2];
    // Fill real pixel bytes with 0xDD, leave padding as 0x00
    for row in 0..2 {
        let base = row * stride as usize;
        frame[base..base + 12].fill(0xDD);
    }
    let mut dst = vec![0u8; 3 * 4 * 2]; // tight stride = 12

    stitch_frames(
        std::iter::once((m, frame.as_slice(), stride)),
        &desktop,
        &mut dst,
    )
    .unwrap();

    assert!(dst.iter().all(|&b| b == 0xDD));
}

// ---------------------------------------------------------------------------
// Buffer invariant validation
// ---------------------------------------------------------------------------

#[test]
fn valid_buffer_4k() {
    assert!(validate_buffer_invariants(3840, 2160, 15360, 33177600).is_ok());
}

#[test]
fn invalid_zero_width() {
    let err = validate_buffer_invariants(0, 100, 0, 0).unwrap_err();
    assert!(err.contains("zero dimensions"));
}

#[test]
fn invalid_stride_below_minimum() {
    let err = validate_buffer_invariants(100, 50, 300, 15000).unwrap_err();
    assert!(err.contains("stride"));
}

#[test]
fn invalid_data_len_off_by_one() {
    let err = validate_buffer_invariants(100, 50, 400, 19999).unwrap_err();
    assert!(err.contains("data_len"));
}
