# S04 Region Selector Spike — Manual UAT

## Purpose

This document provides manual UAT steps for the S04 rectangular region selector spike.
The spike proves that a WinUI 3 overlay window can cover the virtual desktop, accept
pointer drag and keyboard input for rectangle selection, and return sanitized region
coordinates — **without invoking Windows Graphics Capture or any native capture APIs**.

## Scope

- **In scope**: Overlay window rendering, pointer drag-to-draw, keyboard move/resize,
  confirm (Enter/double-click), cancel (Escape), coordinate sanitization, status text.
- **Out of scope**: Actual screen capture of the selected region (deferred to later
  milestones). The spike returns coordinates only.

## Prerequisites

- .NET 8 SDK on PATH
- Windows 10 1903+ or Windows 11
- Interactive desktop session (the overlay is a real WinUI 3 window)
- Multi-monitor setup recommended but not required (single-monitor tests are valid)

## Launch

```powershell
dotnet run --project respectacle-ui
```

The app opens at approximately 1000×700 with the title "Respectacle — Screen Capture Preview".

## UAT Checklist

### 1. Region Spike Button

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 1.1 | Click **Region Spike** button | All buttons disable, progress ring appears, status shows "Select a region on screen…" | ☐ |
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

### 5. Confirm Selection

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 5.1 | Draw a rectangle and press Enter | Overlay closes, status text shows confirmed coordinates (e.g., "Region: X=100, Y=200, W=300, H=150") | ☐ |
| 5.2 | Double-click inside the rectangle | Same result as Enter — overlay closes with confirmed coordinates | ☐ |
| 5.3 | Verify all buttons re-enable after confirm | All 5 toolbar buttons are clickable again | ☐ |

### 6. Cancel Selection

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 6.1 | Draw a rectangle and press Escape | Overlay closes, status text shows "Region selection cancelled" | ☐ |
| 6.2 | Press Escape without drawing | Overlay closes cleanly with cancellation status | ☐ |
| 6.3 | Verify all buttons re-enable after cancel | All 5 toolbar buttons are clickable again | ☐ |

### 7. Multi-Monitor and Negative Coordinates

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 7.1 | With monitors extending left of primary, drag on left monitor | Overlay covers all monitors, rectangle draws correctly on left monitor | ☐ |
| 7.2 | Confirm selection on non-primary monitor | Status text shows negative X coordinates (e.g., "X=-1920, Y=0, W=800, H=600") | ☐ |
| 7.3 | Verify overlay covers full virtual desktop extent | No gaps or uncovered areas on any monitor | ☐ |

### 8. Edge Cases

| Step | Action | Expected Result | Pass/Fail |
|------|--------|----------------|-----------|
| 8.1 | Confirm with zero-size rectangle | Status shows "Invalid region selected" (null returned from overlay) | ☐ |
| 8.2 | Rapidly click Region Spike twice in succession | Only one overlay appears; second click is blocked while buttons are disabled | ☐ |

## Known Limitations

1. **No capture**: The spike returns coordinates only — it does not capture the selected region.
2. **No persistence**: Coordinates are displayed as status text but not saved to file or clipboard.
3. **Overlay is always-on-top**: Uses `OverlappedPresenter` with `IsAlwaysOnTop`. Some full-screen apps may still appear above it.
4. **DPI awareness**: Coordinate rounding uses `RoundToPixel` but per-monitor DPI scaling is not fully tested. Coordinates are in virtual pixels.

## Evidence Template

Record the following for each UAT run:

| Field | Value |
|-------|-------|
| Date | |
| OS version | |
| Monitor configuration | |
| Tester | |
| Items passed | /8 sections |
| Items failed | |
| Notes | |
