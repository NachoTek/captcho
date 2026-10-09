# Captcho

A fast, modern screen capture application for Windows. Captures screenshots of the full desktop, individual monitors, windows, or custom regions. Users can annotate the live screen before capturing, then save to file or copy to clipboard. Supports global hotkeys and a CLI for automation.

## Language

### Capture Modes

**Full Desktop**:
Captures the entire virtual desktop across all monitors immediately, without target selection.
_Avoid_: Entire screen, whole screen

**Active Window**:
Captures the foreground window immediately, without target selection.
_Avoid_: Focused window, current window

**Selected Window**:
The user clicks on a target window to capture. Opens a window picker overlay.
_Avoid_: Window under cursor, hovered window, window selection

**Selected Monitor**:
The user clicks on a target monitor to capture. Opens a monitor picker overlay.
_Avoid_: Current monitor, single monitor, monitor selection

**Selection**:
The user draws a rectangular region on the screen. Target selection and annotation happen in a single combined overlay pass.
_Avoid_: Rectangular region, region capture, area selection

### Capture Pipeline

**Trigger**:
The user action that starts a capture — a global hotkey press or a CLI invocation.
_Avoid_: Invoke, initiate, launch

**Target Selection**:
An optional interactive step where the user picks what to capture. Skipped for Full Desktop and Active Window modes.
_Avoid_: Target picking, source selection

**Annotation**:
Post-capture markup on a full-screen overlay that displays the captured Frame. The user draws, marks, or annotates the frozen Frame before Export. V1 keeps one global Annotation tool state and one source-plus-strokes composite; the pen and the shape tools (rectangle, ellipse, line, arrow) render both in-progress and committed strokes without mutating the source Frame, sharing one color and stroke width, with fill available to the closed shapes. The text tool places an editable entry whose committed content, position, color, and size stay reversible document entries; empty or cancelled input never becomes one. The marker tool places sequential numbered entries whose committed number, position, and style stay reversible document entries; the next number follows one past the highest still-committed marker, so undo never renumbers the survivors. The blur tool defines rectangular regions whose committed geometry stays a reversible document entry; composition samples the source Frame pixels in the region and rasterizes an obscured version clipped to the region, without mutating the source Frame or adopting the shared color and width. Committed annotations form a document history: Undo removes the latest committed annotation, Redo restores the most recently undone one, and committing a new annotation after Undo clears the redo branch. Available when enabled in Settings; if disabled, the existing preview workflow is retained.
_Avoid_: Markup, drawing, annotation overlay

**Capture**:
The operation that produces a frame. Encompasses the trigger through to pixel acquisition.
_Avoid_: Screenshot operation, screen grab

**Frame**:
The raw pixel data produced by a capture — width, height, stride, and BGRA pixel buffer.
_Avoid_: Screenshot, image, bitmap, capture data

**Export**:
Post-capture processing — saving a frame to file or copying it to the clipboard.
_Avoid_: Output, save, deliver

### Workflow

**Workflow Session**:
The WinUI-free runtime component that owns Capture Mode routing, operation state, the captured Frame, the post-capture Annotation gate, Recognize Text — OCR over the in-memory Frame through an engine adapter, with distinct Recognized-text, no-text, and retryable failure outcomes — Scan QR — multi-value QR decoding over the in-memory Frame through a scanner adapter, with distinct found, no-code, and retryable failure outcomes — and the Export actions (Save, Save As, Copy Frame, Copy Path) with the one default saved-file identity per Capture. The Full Desktop, Active Window, Selection, Selected Monitor, and Selected Window routes are wired through it today; Annotation tools, OCR language fallback engines, and the exit decision will move behind it as their tickets land. WinUI windows and Win32 layered windows are thin event/rendering adapters over the Workflow Session and must not duplicate workflow rules.
_Avoid_: Mediator, controller, view model

**Capture Mode Routing**:
The Workflow Session's responsibility for dispatching a Trigger to the right Capture path based on the active Capture Mode. Full Desktop and Active Window skip Target Selection; Selected Window, Selected Monitor, and Selection use interactive overlays before Capture. For Selection, the overlay returns confirmed geometry (or cancellation) to the session, which then owns Capture of that geometry, the resulting Frame, and the preview transition — the overlay itself never performs Capture.
_Avoid_: Mode switch, dispatch table

**Operation State**:
The Workflow Session's guard against concurrent operations. A Trigger that arrives while another operation is in flight is rejected with an OperationInProgress outcome rather than queuing or interrupting the in-flight operation.
_Avoid_: Lock, mutex, busy flag

### Virtual Desktop

**Virtual Desktop**:
The combined bounding rectangle of all monitors, including monitors at negative coordinates or non-trivial layouts.
_Avoid_: Desktop bounds, screen area, combined display

### User Interface

**Settings**:
User-facing preferences configurable through the UI — hotkeys, export behavior, Annotation defaults and toggles, theme, and launch behavior (what captcho does on startup: Do nothing, Last Capture Mode, or a configured Capture Mode).
_Avoid_: Preferences, options, config

**Configuration**:
The code-level mechanism for persisting and loading settings (ConfigurationService, settings file).
_Avoid_: Config file, persistence layer

**Global Hotkey**:
A system-wide keyboard shortcut that triggers a capture regardless of whether Captcho has focus.
_Avoid_: Keyboard shortcut, hotkey, shortcut

**Filename Template**:
A configurable naming pattern for saved capture files, with placeholders for date, time, sequence number, and window title.
_Avoid_: Naming pattern, save pattern, file mask

**CLI**:
The command-line interface for headless capture and automation.
_Avoid_: Command line, console app
