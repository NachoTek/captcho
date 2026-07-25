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
Pre-capture markup on a live screen overlay. The user draws, marks, or annotates the screen before the capture is taken. Available when enabled in settings.
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

### Virtual Desktop

**Virtual Desktop**:
The combined bounding rectangle of all monitors, including monitors at negative coordinates or non-trivial layouts.
_Avoid_: Desktop bounds, screen area, combined display

### User Interface

**Settings**:
User-facing preferences configurable through the UI — hotkeys, export behavior, annotation toggles, theme.
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
