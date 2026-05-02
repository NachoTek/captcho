# S05 Region Capture — Manual UAT

## Purpose

This document provides manual UAT steps for the S05 rectangular region capture
feature. S05 turns the S04 overlay spike into the real capture mode: the user
draws a rectangular region on the virtual-desktop overlay, confirms it, and the
selected area is cropped from a full-desktop Windows Graphics Capture frame and
displayed in the preview pane — with timing diagnostics and sanitized status text.

## Scope

- **In scope**: Region draw/move/resize/confirm flow, native crop, preview
  display, timing/status text, cancel, negative-origin monitors,
  monitor-spanning regions, invalid/tiny selections, error/status behavior.
- **Out of scope**: Region persistence, clipboard copy, file save, per-monitor
  DPI scaling beyond virtual-pixel coordinates.

## ⚠ Diagnostics Policy

> **Diagnostics text (status bar, timing labels, error messages) must never
> include pixel data or screen content.** Mode labels contain coordinates and
> dimensions only (e.g., `"Region X=100, Y=200, W=300, H=150"`). This is
> enforced by tests (`S05_DiagnosticOutput_NeverContainsRawPixels`) and must
> be maintained in all future changes.

## Prerequisites

- .NET 8 SDK on PATH
- Rust toolchain on PATH (`cargo build` succeeds for `rust-dll`)
- Windows 10 1903+ or Windows 11
- **Interactive desktop session** (the overlay is a real WinUI 3 window and WGC
  requires a composited desktop)
- Multi-monitor setup recommended but not required (single-monitor tests are valid)

## Automated Verification

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-s05-build.ps1
```

This runs Rust crop/validate/capture_region tests, managed interop tests,
UI service tests, UI wiring tests, and S04 overlay regression tests. All must
pass before manual UAT.

## Launch

Stage the Rust DLL (if not already staged):

```powershell
cargo build --manifest-path rust-dll/Cargo.toml --release
Copy-Item rust-dll/target/release/respectacle_capture.dll respectacle-ui/bin/Debug/net8.0-windows10.0.19041.0/win-x64/ -Force
```

Then launch:

```powershell
dotnet run --project respectacle-ui
```

The app opens at approximately 1000×700 with the title "Respectacle — Screen Capture Preview".

## UAT Checklist

### 1. Capture Region Button (R001 — basic capture, R003 — region mode)

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 1.1 | Click **Capture Region** button | All buttons disable, progress ring appears, status shows "Select a region on screen…" | ☐ |
| 1.2 | Observe overlay window appears | Semi-transparent dark scrim covers entire virtual desktop (all monitors) | ☐ |
| 1.3 | Observe instruction text | White text at top-center reads "Drag to select region · Enter=Confirm · Esc=Cancel · Arrows=Move · Alt+Arrows=Resize" | ☐ |

### 2. Pointer Drag-to-Draw

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 2.1 | Press and drag on overlay | Cyan dashed-border rectangle appears, following pointer direction | ☐ |
| 2.2 | Drag right and down | Rectangle grows from top-left to bottom-right | ☐ |
| 2.3 | Drag left or up (reversed) | Rectangle normalizes — drawn correctly regardless of drag direction | ☐ |
| 2.4 | Release pointer | Rectangle stays in place, status text shows dimensions (e.g., "300 × 200") | ☐ |
| 2.5 | Very small drag (< 4px) | No rectangle drawn (minimum size threshold not met) | ☐ |

### 3. Keyboard Move

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 3.1 | Draw a rectangle, then press Arrow keys | Rectangle moves 10px per keypress in the arrow direction | ☐ |
| 3.2 | Press Shift+Arrow | Rectangle moves 1px (fine mode) | ☐ |
| 3.3 | Move to screen edge | Rectangle clamps at virtual-desktop boundary, does not disappear | ☐ |

### 4. Keyboard Resize

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 4.1 | Draw a rectangle, then press Alt+Arrow | Rectangle resizes from top-left anchor (bottom-right corner moves) | ☐ |
| 4.2 | Alt+Arrow to shrink below minimum | Rectangle snaps to minimum size (4×4), does not disappear | ☐ |
| 4.3 | Alt+Shift+Arrow | Resizes 1px (fine mode) | ☐ |

### 5. Confirm → Capture → Preview (R001 acceptance)

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 5.1 | Draw a rectangle (≥ 100×100 recommended) and press Enter | Overlay closes, progress ring continues while capture runs | ☐ |
| 5.2 | Wait for capture to complete | Preview pane displays the captured region as an image | ☐ |
| 5.3 | Verify preview dimensions match the selected region | Image in preview pane has the same width and height as the drawn rectangle (within ±1px rounding) | ☐ |
| 5.4 | Verify status text shows mode label with coordinates | Status reads like `"Region X=100, Y=200, W=300, H=150"` (values match the selected rectangle) | ☐ |
| 5.5 | Verify timing text shows capture/display/total | Three timing values appear (e.g., "Capture: 45ms · Display: 12ms · Total: 57ms") | ☐ |
| 5.6 | Verify all buttons re-enable after capture | All toolbar buttons are clickable again | ☐ |
| 5.7 | Double-click inside the rectangle | Same result as Enter — overlay closes, capture runs, preview updates | ☐ |

### 6. Cancel Selection

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 6.1 | Draw a rectangle and press Escape | Overlay closes, status text shows "Region selection cancelled" | ☐ |
| 6.2 | Press Escape without drawing | Overlay closes cleanly with cancellation status | ☐ |
| 6.3 | Verify all buttons re-enable after cancel | All toolbar buttons are clickable again | ☐ |

### 7. Multi-Monitor and Negative Coordinates (R003 boundary)

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 7.1 | With monitors extending left of primary, drag on left monitor | Overlay covers all monitors, rectangle draws correctly on left monitor | ☐ |
| 7.2 | Confirm selection on non-primary monitor | Capture succeeds, status shows negative X coordinates (e.g., `"Region X=-1920, Y=0, W=800, H=600"`) | ☐ |
| 7.3 | Verify preview shows correct content from the non-primary monitor area | Preview image matches what was on screen in the selected area | ☐ |
| 7.4 | Draw region spanning two monitors | Capture succeeds, preview shows content from both monitors in the selected area | ☐ |

### 8. Invalid / Tiny Regions (negative tests)

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 8.1 | Draw a very small region (< 4×4) and try to confirm | Region is rejected as too small, overlay stays open OR status shows an error | ☐ |
| 8.2 | Draw zero-size rectangle and confirm | Status shows an error (e.g., "Invalid region" or "Region too small"), no capture attempted | ☐ |
| 8.3 | Verify no crash or hung state after invalid region | App remains responsive, buttons re-enable | ☐ |

### 9. Error Paths (sanitized status)

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 9.1 | If WGC is unavailable (e.g., RDP session), attempt capture region | Status shows a sanitized error message, no stack trace or raw COM error visible | ☐ |
| 9.2 | Verify error message does NOT contain pixel data or screen content | Error text is human-readable and diagnostic-safe | ☐ |

### 10. Existing Button Regressions

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 10.1 | Click **Full Desktop** | Full-desktop capture still works, preview updates | ☐ |
| 10.2 | Click **Current Monitor** | Current monitor capture still works | ☐ |
| 10.3 | Click **Active Window** | Active window capture still works | ☐ |
| 10.4 | Click **Window Under Cursor** | Window-under-cursor capture still works | ☐ |

## Test Coverage Map

| Requirement | UAT Sections | Automated Tests |
|-------------|-------------|-----------------|
| R001 — basic screen capture | 5 (confirm→capture→preview), 10 (regression) | Rust crop, FFI, interop, service, wiring tests |
| R003 — rectangular region mode | 1–8 (full draw/move/resize/confirm flow) | RegionCaptureInteropTests, RegionCapturePreviewServiceTests, RegionCaptureUiWiringTests |

## Known Limitations

1. **DPI awareness**: Coordinates are in virtual pixels. Per-monitor DPI scaling is not
   fully tested; minor rounding may occur at non-100% scales.
2. **Overlay always-on-top**: Uses `OverlappedPresenter` with `IsAlwaysOnTop`. Some
   full-screen apps may still appear above it.
3. **Cold WGC capture**: First capture may take longer due to WGC frame-pool
   initialization. Timing diagnostics surface this via `capture_ms`.
4. **No persistence**: Captured region is displayed in the preview only — not saved
   to file or clipboard in M001.

## Evidence Template

Record the following for each UAT run:

| Field | Value |
|-------|-------|
| Date | |
| OS version | |
| Monitor configuration | |
| Tester | |
| Items passed | /10 sections |
| Items failed | |
| Notes | |
