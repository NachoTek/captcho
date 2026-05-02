# Respectacle — Screen Capture Preview

A Windows screen capture application built with Rust (native capture) + C#/.NET 8 (managed interop) + WinUI 3 (preview UI).

## Architecture

| Layer | Technology | Description |
|-------|-----------|-------------|
| **Native capture** | Rust DLL (`rust-dll/`) | Windows Graphics Capture API via `windows-capture` crate. Supports monitor-by-index, full-desktop (stitched), active window, window-under-cursor, and window-by-HWND capture modes. |
| **Managed interop** | .NET 8 library (`respectacle-capture/`) | P/Invoke bindings, `SafeCaptureResult` handle, `BitmapBufferConverter` for stride stripping, `CaptureDiagnostics` for timing, `WindowResolver` for cursor-to-HWND resolution. |
| **Console tester** | .NET 8 console (`cs-tester/`) | CLI runner for capture commands and structured verification output, including `--verify-s03` for window capture diagnostics. |
| **Preview UI** | WinUI 3 (`respectacle-ui/`) | Desktop app with Full Desktop, Current Monitor, Active Window, and Window Under Cursor capture buttons, WriteableBitmap preview, and timing diagnostics. |

## Prerequisites

- **Rust toolchain** — `cargo` on PATH (stable, targeting MSVC)
- **.NET 8 SDK** — `dotnet` on PATH
- **Windows 10 1903+** or **Windows 11** — required for Windows Graphics Capture API
- **Interactive desktop session** — WGC requires a visible desktop; RDP/frozen sessions may fail

## Build

```powershell
# Build everything
cargo build --manifest-path rust-dll/Cargo.toml --release
dotnet build Respectacle.sln
```

## Verification Scripts

### S01 — Core Capture Spike

```powershell
# Build + compile checks only (no desktop required)
powershell -ExecutionPolicy Bypass -File scripts/verify-s01-build.ps1

# Real desktop capture verification
powershell -ExecutionPolicy Bypass -File scripts/verify-s01-e2e.ps1
```

### S02 — Capture Library + WinUI Preview

```powershell
# Build + compile + all test projects
powershell -ExecutionPolicy Bypass -File scripts/verify-s02-build.ps1

# Real desktop capture verification (full desktop + monitor[0])
# Also verifies WinUI project builds successfully
powershell -ExecutionPolicy Bypass -File scripts/verify-s02-e2e.ps1
```

### S03 — Window Capture Modes

```powershell
# Build + compile + all test projects (Rust + .NET, including S03 window tests)
powershell -ExecutionPolicy Bypass -File scripts/verify-s03-build.ps1

# Real window capture verification (active window + window under cursor + negative tests)
# Gracefully reports CaptureUnavailable in non-interactive sessions
powershell -ExecutionPolicy Bypass -File scripts/verify-s03-e2e.ps1
```

### S04 — Rectangular Region Selector Spike

```powershell
# Build + all S04 deterministic tests (RegionSelection, CoordinateHelper, RegionSelectionStatus)
powershell -ExecutionPolicy Bypass -File scripts/verify-s04-build.ps1
```

Manual UAT for the region overlay window (requires interactive desktop):
See [docs/s04-region-selector-spike.md](docs/s04-region-selector-spike.md) for the full UAT checklist covering overlay coverage, drag-to-draw, keyboard move/resize, confirm, cancel, and multi-monitor behavior.

### S05 — Rectangular Region Capture

```powershell
# Build + Rust crop/FFI tests + managed interop + UI service/wiring tests + S04 regressions
powershell -ExecutionPolicy Bypass -File scripts/verify-s05-build.ps1
```

Manual UAT for the region capture flow (draw/move/resize → confirm → crop → preview):
See [docs/s05-region-selector.md](docs/s05-region-selector.md) for the full UAT checklist covering capture-region button, drag-to-draw, keyboard move/resize, confirm with preview, cancel, negative-origin monitors, spanning regions, invalid tiny regions, sanitized error paths, and existing-button regressions.

### S06 — Delayed Capture

```powershell
# Build + DelayedCapture countdown + UI wiring tests + CapturePreviewService regression + S05 regressions
powershell -ExecutionPolicy Bypass -File scripts/verify-s06-build.ps1
```

Manual UAT for delayed capture (requires interactive desktop):

### S07 — Export Workflows (Save, Save As, Copy)

```powershell
# Build + capture-library export tests + UI export wiring + clipboard tests + CapturePreviewService regression + S06/S05 regressions
powershell -ExecutionPolicy Bypass -File scripts/verify-s07-build.ps1
```

**Export architecture:**
- **Filename template expander** (`ExportFilenameTemplate`) — expands `{date}`, `{time}`, `{title}`, `{seq}` placeholders with filesystem-safe sanitization. Default template: `{date}_{time}`.
- **PNG export** (`PngExportService`) — encodes `ContiguousBitmap` BGRA data via `System.Drawing.Common`, creates directories, reports timing and phase-labeled failures.
- **Default save location** — `Pictures\Respectacle\` under the user profile, with auto-incrementing sequence collision resolution.
- **Clipboard copy** (`ClipboardExportService`) — copies PNG bytes via `IClipboardAdapter` abstraction; production uses `DataPackage`, tests use fakes.
- **Status feedback** (`ExportStatusFormatter`) — pure-logic formatting of `ExportResult` status/timing into user-visible strings; fully testable without WinUI.
- **Export buttons** (Save, Save As, Copy) are disabled until a capture succeeds and during export operations, preventing double-export races.

**Headless limitations:** The automated tests cover all logic paths using mediator doubles. The following require interactive desktop validation:
- **FileSavePicker** (Save As) — launches a native file picker dialog that cannot be tested headless.
- **Clipboard paste verification** — the clipboard adapter is faked in tests; real `DataPackage` interop requires a COM-enabled desktop session.

Manual UAT for export workflows (requires interactive desktop):

### S08 — Global Hotkeys

```powershell
# Build + HotkeyRoute/HotkeyManager/HotkeyUiWiring tests + CapturePreviewService + Window/Region/Delayed/Export regression
powershell -ExecutionPolicy Bypass -File scripts/verify-s08-hotkeys.ps1
```

**Hotkey architecture:**
- **HotkeyRoute** — pure enum and spec mapping (Print Screen → Current Monitor, Win+Print → Active Window, Shift+Print → Full Desktop, Win+Shift+Print → Rectangular Region). No WinUI/Win32 dependencies — fully headless-testable.
- **HotkeyManager** — registration manager with `IHotkeyRegistrar` abstraction; production uses `WindowsHotkeyRegistrar` (P/Invoke), tests use fakes. Tracks successful registrations, unregisters idempotently.
- **MainWindow wiring** — registers all four hotkeys on startup, subclasses WndProc for `WM_HOTKEY`, dispatches to existing capture workflows via `RunDelayedCaptureAsync`/`RunDelayedRegionCaptureAsync`. Reports conflicts in status text. Unregisters on window close.
- **Overlap guard** — hotkey triggers during active capture/export are silently ignored, preventing the 10x breakpoint from repeated Print Screen presses.

**Headless tests:** 76 tests (20 HotkeyRoute + 31 HotkeyManager + 25 HotkeyUiWiring) verify mapping, registration, cleanup, dispatch routing, operation guard, and conflict reporting.

**Manual UAT for global hotkeys (requires interactive desktop):**

### S09 — Production CLI

```powershell
# Build + CLI unit tests + help/invalid smoke + documentation consistency + optional capture
powershell -ExecutionPolicy Bypass -File scripts/verify-s09-cli.ps1
```

### S10 — Configuration Persistence

```powershell
# Build + config persistence tests + UI wiring tests + CapturePreviewService regression + ExportUiWiring regression
powershell -ExecutionPolicy Bypass -File scripts/verify-s10-configuration.ps1
```

**Configuration architecture:**
- **Settings file** — `%LOCALAPPDATA%\Respectacle\settings.json`. Created automatically on first save. Uses camelCase JSON with unknown-property preservation for forward compatibility.
- **AppSettings model** — Two nullable properties: `saveLocation` (default save directory) and `filenameTemplate` (filename pattern). Computed `EffectiveSaveLocation`/`EffectiveFilenameTemplate` properties fall back to defaults when unset.
- **ConfigurationService** — Loads/saves settings with constructor-injected config path. Production default: `%LOCALAPPDATA%\Respectacle\`. Tests inject temp directories. Returns structured `ConfigurationLoadResult`/`ConfigurationSaveResult` with phase, sanitized error message, and metadata — never throws for expected failures.
- **Atomic save** — Writes to a temp file first, then moves into place. Corrupted JSON files are renamed to `.backup` (with incremental suffixes) before falling back to defaults.
- **Startup wiring** — `App.OnLaunched` loads settings and passes them to `MainWindow`. Missing file → defaults (no error). Corrupted file → defaults + status warning + backup file created.
- **Save/SaveAs integration** — Save uses `settings.EffectiveSaveLocation` and `settings.EffectiveFilenameTemplate`. SaveAs persists the chosen directory only after successful save. Cancel/failure does not modify settings.
- **Defaults** — Save location: `Pictures\Respectacle\` under user profile. Filename template: `Respectacle_<yyyy>-<MM>-<dd>_<hh><mm><ss>`.

**Settings file example:**
```json
{
  "saveLocation": "C:\\Users\\example\\Pictures\\Screenshots",
  "filenameTemplate": "Capture_<yyyy>-<MM>-<dd>_<hh><mm><ss>"
}
```

An empty JSON object `{}` is valid — it means "use all defaults". Omitting either property also falls back to the default.

**Corrupted config behavior:**
1. App detects malformed JSON in `settings.json`.
2. The corrupted file is renamed to `settings.json.backup` (or `.backup.1`, `.backup.2`, etc. if backups already exist).
3. App falls back to default settings and shows a configuration warning in the status bar.
4. On next successful save, a new `settings.json` is written with valid JSON.

**Headless tests:** 56 tests (32 capture-library config + 24 UI wiring) cover round-trip persistence, corruption backup, defaults on missing file, empty JSON, unknown properties, validation, save to unwritable directory, Save/SaveAs settings awareness, persistence failure non-fatality, and cancel/failure not persisting. Plus CapturePreviewService and ExportUiWiring regressions.

**Manual UAT for configuration persistence (requires interactive desktop):**

1. **Restart persistence**: Launch the app → capture a screenshot → click **Save As** → choose a different directory and save → close the app → relaunch → capture again → click **Save** → verify it saves to the previously chosen directory (not the default `Pictures\Respectacle\`).
2. **Missing config**: Delete `%LOCALAPPDATA%\Respectacle\settings.json` if it exists → launch the app → verify it starts normally with no error. Capture and Save → verify file goes to default `Pictures\Respectacle\`.
3. **Corrupted config recovery**: Edit `%LOCALAPPDATA%\Respectacle\settings.json` to contain invalid JSON (e.g., `{bad`) → launch the app → verify it starts with defaults and shows a config warning → check that `settings.json.backup` was created → the original corrupted content is preserved in the backup file.
4. **Save As cancel does not persist**: Launch the app → capture → click **Save As** → cancel the picker → verify status shows "Save cancelled" → close and relaunch → capture → click **Save** → verify it still uses the previous directory (not any cancelled location).

## Production CLI

The `respectacle-cli` project provides headless screen capture from the command line — no WinUI preview window is shown. It supports all five capture modes, writes PNG files, and returns deterministic exit codes plus structured diagnostics for automation.

### Usage

```
respectacle-cli <mode> [options]
```

### Capture Modes (exactly one required)

| Flag | Description |
|------|-------------|
| `--full` | Capture all monitors (full virtual desktop) |
| `--monitor [index]` | Capture a monitor by 0-based index. Without index, captures the current monitor |
| `--window-active` | Capture the currently active (foreground) window |
| `--window-cursor` | Capture the top-level window under the cursor |
| `--region <x,y,width,height>` | Capture a rectangular region of the virtual desktop using coordinates |

**Note:** Scripted `--region` uses virtual-desktop coordinates and does not show the interactive overlay selector.

### Output Options

| Flag | Description |
|------|-------------|
| `--output <path>` | Output file path or directory. If path ends in `.png`, treated as full file path. Otherwise treated as a directory. Default: `%USERPROFILE%\Pictures\Respectacle\` |
| `--filename <template>` | Filename template with placeholders: `<yyyy>` `<MM>` `<dd>` `<hh>` `<mm>` `<ss>` `<#>`. Default: `Respectacle_<yyyy>-<MM>-<dd>_<hh><mm><ss>.png` |

### General Options

| Flag | Description |
|------|-------------|
| `--verbose`, `-v` | Print detailed diagnostics (timings, dimensions) |
| `--help`, `-h` | Show help message |

### Examples

```powershell
# Capture all monitors, save to default directory
respectacle-cli --full

# Capture second monitor (index 1), save to custom directory
respectacle-cli --monitor 1 --output C:\captures

# Capture current monitor with custom filename
respectacle-cli --monitor --filename my_<yyyy><MM><dd>.png

# Capture active window, save as specific file
respectacle-cli --window-active --output screenshot.png

# Capture 800x600 region at (100,200) with verbose diagnostics
respectacle-cli --region 100,200,800,600 --output C:\out --verbose

# Capture window under cursor
respectacle-cli --window-cursor --output C:\captures
```

### Exit Codes

| Code | Name | Description |
|------|------|-------------|
| 0 | Success | Capture completed and PNG written |
| 1 | InvalidArguments | Bad mode, malformed values, missing required options |
| 2 | CaptureFailed | Native capture returned non-OK status (graphics pipeline error, WGC unavailable) |
| 3 | ExportFailed | Filesystem write or PNG encoding error |
| 4 | InternalError | Unexpected exception |

### Diagnostics

All output is printed as `key=value` pairs to stdout. Use `--verbose` for a capture/bitmap/export/total timing breakdown. Errors are printed to stderr with the offending option identified.

Example success output:
```
CaptureMode=full Status=success OutputPath=C:\Users\...\Respectacle_2026-05-01_224800.png Dimensions=3840x1080 ExitCode=0
```

Example verbose output:
```
CaptureMode=region Status=success OutputPath=C:\out\capture.png Dimensions=800x600 CaptureMs=12.3 BitmapMs=0.4 ExportMs=8.1 TotalMs=21.5 ExitCode=0
```

Example failure output (stderr):
```
Error: Phase=capture Message=Status=CaptureUnavailable
```

### Headless and Non-Interactive Sessions

The CLI is designed for automation. In non-interactive environments (CI, RDP without desktop, headless), Windows Graphics Capture may return `CaptureUnavailable`. The CLI reports this as exit code 2 with `Status=failed` diagnostics — it does not crash or hang. The `verify-s09-cli.ps1` script treats `CaptureUnavailable` as an environment limitation rather than a test failure.

1. **Delay 0 — immediate capture**: Set the Delay (sec) spinner to 0, click any capture button → preview appears immediately with no countdown. Behavior matches the pre-S06 capture experience.
2. **Delay 5 — countdown then capture**: Set Delay (sec) to 5, click any capture button → window title shows "Capturing in X…", status text counts down each second, progress bar fills deterministically. At zero, capture triggers and preview/timing appear as normal.
3. **Cancel before zero**: Start a delayed capture with any non-zero delay, click Cancel during countdown → countdown stops, progress resets, window title/status return to idle. No capture occurs.
4. **Max delay sanity**: Set Delay (sec) to 60 (the maximum) → verify the spinner clamps at 60. Start capture → countdown runs 60 seconds, then captures. Verify the progress bar reaches 100% at the end.

1. **Save (default location)**: Capture any screenshot → click **Save** → status text shows "Saved — <filename> (<dimensions>)" and timing shows export milliseconds. Verify the PNG file exists in `Pictures\Respectacle\` under your user profile with a timestamped filename.
2. **Save As (custom location)**: Capture any screenshot → click **Save As** → FileSavePicker opens → navigate to a different folder, enter a filename, click Save → status shows "Saved — <filename> (<dimensions>)". Verify the file is at the chosen location.
3. **Save As cancelled**: Capture any screenshot → click **Save As** → dismiss the picker without saving → status shows "Save cancelled". Export buttons should remain enabled.
4. **Copy to clipboard**: Capture any screenshot → click **Copy** → status shows "Copied to clipboard (<dimensions>)" with timing. Paste into Paint or another application → the screenshot image should appear.
5. **Copy failure**: If clipboard is unavailable (unlikely on interactive desktop), status shows "Clipboard failed" with sanitized message.
6. **No capture guard**: Before any capture, all three export buttons (Save, Save As, Copy) are disabled. Clicking them does nothing. Status shows "No capture available".
7. **Button state during export**: While an export is in progress, all three export buttons are disabled. They re-enable after the export completes (success, failure, or cancellation).
8. **Service failure handling**: If the export service fails (e.g., directory not writable), status shows a sanitized failure message without exposing internal paths or stack traces.

## Manual UAT

### WinUI Preview App

The e2e script verifies the WinUI project compiles. Runtime UI testing is manual:

1. Stage the Rust DLL:
   ```powershell
   Copy-Item rust-dll/target/release/respectacle_capture.dll respectacle-ui/bin/Debug/net8.0-windows10.0.19041.0/win-x64/ -Force
   ```
   (Adjust output path if needed — the DLL must be beside the app executable.)

2. Launch the app:
   ```powershell
   dotnet run --project respectacle-ui
   ```

3. Expected behavior:
   - Window opens at ~1000×700 with title "Respectacle — Screen Capture Preview"
   - Click **Full Desktop** → captures all monitors stitched, displays preview
   - Click **Current Monitor** → captures the monitor under cursor, displays preview
   - Click **Active Window** → captures the foreground window, displays preview
   - Click **Window Under Cursor** → captures the top-level window under the cursor, displays preview
   - Status text shows mode + dimensions (e.g., "Full Desktop — 3840×1080")
   - Timing text shows capture/display/total milliseconds

## CLI Tester Commands

```powershell
# Stage DLL first:
Copy-Item rust-dll/target/release/respectacle_capture.dll cs-tester/bin/Debug/net8.0-windows/ -Force

# Run specific captures:
dotnet run --project cs-tester -- --capture
dotnet run --project cs-tester -- --capture-full
dotnet run --project cs-tester -- --capture-monitor 0

# Window capture (S03):
dotnet run --project cs-tester -- --capture-active-window
dotnet run --project cs-tester -- --capture-window-under-cursor
dotnet run --project cs-tester -- --capture-window-handle 0x12345   # decimal or 0x hex

# Run verification:
dotnet run --project cs-tester -- --verify      # S01
dotnet run --project cs-tester -- --verify-s02  # S02
dotnet run --project cs-tester -- --verify-s03  # S03 (active window + cursor + zero-HWND negative test)
```

## Manual UAT — S03 Window Capture

### Automated (Headless) Verification

The `verify-s03-e2e.ps1` script handles non-interactive sessions gracefully. When WGC is unavailable (CI, RDP without desktop, headless), both window capture modes report `CaptureUnavailable` and the script exits cleanly with a warning. The zero-HWND negative test passes regardless of session type.

### Interactive UAT — Two Windows

1. Open two visible windows side-by-side (e.g., Notepad and a browser).
2. Launch the WinUI app:
   ```powershell
   dotnet run --project respectacle-ui
   ```
3. **Active Window capture**: Click "Active Window" button → preview shows the Respectacle window itself (it was the foreground window when clicked).
4. **Window Under Cursor**: Move cursor over a different window (e.g., Notepad), press the "Window Under Cursor" button → preview shows Notepad.
5. Verify status text shows mode + dimensions (e.g., "Active Window — 800×600").
6. Verify timing text shows capture/display/total milliseconds.

### Cursor Target Switching

1. Position the cursor over Window A, trigger "Window Under Cursor" → Window A captured.
2. Move cursor to Window B (different size/position), trigger again → Window B captured.
3. Verify dimensions change between captures if windows differ in size.

### Window Decorations

Window captures include title bars and window borders (decorations) as rendered by the Windows Desktop Window Manager. This matches the WGC default behavior and user expectations for "capture this window". No separate decoration toggle is provided in M001.

### Minimized / Closed Window Behavior

- **Minimized window**: WGC may return `CaptureUnavailable` or capture a blank/minimal frame. This is expected — minimized windows are not composited.
- **Closed/invalid HWND**: `--capture-window-handle` with an invalid handle returns a phase-labeled error, not a crash.

### Multi-Monitor / Spanning Windows

- A window spanning two monitors is captured as a single frame covering the full window extent.
- Active Window and Window Under Cursor capture the logical window regardless of which monitor(s) it spans.
- Test by dragging a window across monitor boundaries and capturing.
