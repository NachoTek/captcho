# captcho

<p align="center">
  <strong>A fast, modern screen capture application for Windows</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%2B-blue?logo=windows" alt="Windows 10+">
  <img src="https://img.shields.io/badge/Rust-stable-orange?logo=rust" alt="Rust">
  <img src="https://img.shields.io/badge/.NET-8-purple?logo=dotnet" alt=".NET 8">
  <img src="https://img.shields.io/badge/WinUI-3-blueviolet?logo=windows" alt="WinUI 3">
</p>

captcho is a high-performance screenshot tool for Windows built with a native Rust capture engine and a polished WinUI 3 interface. Capture your screen, windows, or custom regions with speed and precision.

## ✨ Features

- **5 Capture Modes** — Full desktop, current monitor, active window, window under cursor, and custom rectangular region
- **Global Hotkeys** — Quick capture with Print Screen, Win+Print, Shift+Print, and Win+Shift+Print
- **Delayed Capture** — Set a 0-60 second countdown for timed screenshots
- **Flexible Export** — Save to file with customizable filename templates, or copy directly to clipboard
- **Multi-Monitor Support** — Works seamlessly across multiple displays
- **Command-Line Interface** — Headless capture for automation and scripts
- **Persistent Configuration** — Save your preferences between sessions

## 🚀 Quick Start

### Prerequisites

- **Windows 10 1903+** or Windows 11
- **Interactive desktop session** (required by Windows Graphics Capture API)

### Installing from Release

1. Download the latest release from the [Releases](../../releases) page
2. Extract the archive
3. Run `captcho-ui.exe` for the GUI, or `captcho-cli.exe` for command-line use

### Building from Source

See the [Building from Source](#building-from-source) section below.

## 📷 Using the GUI

Launch `captcho-ui.exe` to open the capture window.

### Capture Modes

Click any button to capture:

| Button | Capture |
|--------|---------|
| **Full Desktop** | All monitors stitched together |
| **Current Monitor** | The monitor under your cursor |
| **Active Window** | The currently focused window |
| **Window Under Cursor** | The window beneath your mouse pointer |
| **Rectangular Region** | Draw a custom selection |

### Using Rectangular Region

1. Click **Rectangular Region**
2. Click and drag to draw a selection
3. Adjust with arrow keys (hold Shift for 10px increments)
4. Press Enter to confirm, or Escape to cancel

### Delayed Capture

Set the **Delay (sec)** spinner to 0-60 seconds, then click any capture button. A countdown appears in the window title and progress bar. Click **Cancel** to abort.

### Saving Screenshots

- **Save** — Saves to your configured location with the current filename template
- **Save As** — Opens a file picker to choose location and filename
- **Copy** — Copies the screenshot to your clipboard for pasting elsewhere

Default save location: `Pictures\captcho\` in your user profile.

### Global Hotkeys

These hotkeys work even when the app is minimized:

| Hotkey | Action |
|--------|--------|
| `Print Screen` | Capture current monitor |
| `Win + Print Screen` | Capture active window |
| `Shift + Print Screen` | Capture full desktop |
| `Win + Shift + Print Screen` | Capture rectangular region |

## 💻 Using the CLI

The `captcho-cli.exe` tool provides headless capture for automation.

### Basic Usage

```powershell
# Capture all monitors
captcho-cli --full

# Capture current monitor
captcho-cli --monitor

# Capture active window
captcho-cli --window-active

# Capture window under cursor
captcho-cli --window-cursor

# Capture custom region (x,y,width,height)
captcho-cli --region 100,200,800,600
```

### Output Options

```powershell
# Save to specific directory
captcho-cli --full --output C:\Screenshots

# Save to specific file
captcho-cli --window-active --output screenshot.png

# Custom filename template
captcho-cli --full --filename "screenshot_<yyyy>-<MM>-<dd>_<hh><mm><ss>.png"
```

### Filename Template Placeholders

| Placeholder | Description | Example |
|-------------|-------------|---------|
| `<yyyy>` | 4-digit year | 2026 |
| `<yy>` | 2-digit year | 26 |
| `<MM>` | Month (01-12) | 05 |
| `<dd>` | Day (01-31) | 10 |
| `<hh>` | Hour (00-23) | 14 |
| `<mm>` | Minute (00-59) | 30 |
| `<ss>` | Second (00-59) | 45 |
| `<title>` | Window title | Notepad |
| `<#>` | Sequence number | 001 |

Default template: `captcho_<yyyy>-<MM>-<dd>_<hh><mm><ss>.png`

### Verbose Mode

```powershell
captcho-cli --full --verbose
```

Output:
```
CaptureMode=full Status=success OutputPath=C:\Users\...\captcho_2026-05-10_143045.png Dimensions=3840x1080 CaptureMs=12.3 BitmapMs=0.4 ExportMs=8.1 TotalMs=21.5 ExitCode=0
```

### Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Invalid arguments |
| 2 | Capture failed |
| 3 | Export failed |
| 4 | Internal error |

### Examples

```powershell
# Capture all monitors to default location
captcho-cli --full

# Capture second monitor to custom directory
captcho-cli --monitor 1 --output D:\Screenshots

# Capture active window with custom filename
captcho-cli --window-active --filename "snap_<yyyy><MM><dd>_<hh><mm><ss>.png" --output .

# Capture region with verbose diagnostics
captcho-cli --region 100,100,1920,1080 --output capture.png --verbose

# Capture window under cursor for automation
captcho-cli --window-cursor --output %TEMP%\screenshot.png
if ($LASTEXITCODE -eq 0) {
    # Success - file saved
    Write-Host "Screenshot saved successfully"
}
```

## ⚙️ Configuration

Configuration is stored in `%LOCALAPPDATA%\captcho\settings.json`.

### Default Settings

```json
{
  "saveLocation": null,
  "filenameTemplate": null
}
```

Both `null` values mean "use defaults":
- Default save location: `Pictures\captcho\`
- Default filename template: `captcho_<yyyy>-<MM>-<dd>_<hh><mm><ss>.png`

### Custom Configuration Example

```json
{
  "saveLocation": "C:\\Users\\YourName\\Pictures\\Screenshots",
  "filenameTemplate": "Screenshot_<yyyy>-<MM>-<dd>_<hh><mm><ss>.png"
}
```

### Configuration Recovery

If the settings file becomes corrupted, captcho automatically:
1. Renames the corrupted file to `settings.json.backup`
2. Falls back to default settings
3. Shows a warning in the status bar
4. Creates a fresh `settings.json` on next save

## 🏗️ Building from Source

### Prerequisites

- **Rust toolchain** — `cargo` on PATH (stable, MSVC target)
- **.NET 8 SDK** — `dotnet` on PATH
- **Windows 10 1903+** or Windows 11

### Build Steps

```powershell
# Clone the repository
git clone https://github.com/yourusername/captcho.git
cd captcho

# Build Rust capture engine
cargo build --manifest-path rust-dll/Cargo.toml --release

# Build .NET solution
dotnet build captcho.sln --configuration Release

# Run the GUI
dotnet run --project captcho-ui --configuration Release

# Run the CLI
dotnet run --project captcho-cli --configuration Release -- --full
```

### Running Tests

```powershell
# Run all tests
dotnet test captcho.sln

# Run Rust tests
cargo test --manifest-path rust-dll/Cargo.toml

# Run verification scripts
powershell -ExecutionPolicy Bypass -File scripts\verify-s02-e2e.ps1
```

## 📁 Project Structure

```
captcho/
├── rust-dll/           # Native capture engine (Rust)
├── captcho-capture/    # Managed interop library (C#)
├── captcho-ui/         # WinUI 3 desktop application
├── captcho-cli/        # Command-line interface
├── captcho-ui.Tests/   # UI tests
├── captcho-cli.Tests/  # CLI tests
├── scripts/            # Verification and build scripts
└── docs/               # Additional documentation
```

## 🏛️ Architecture

captcho uses a hybrid architecture for optimal performance:

| Layer | Technology | Purpose |
|-------|-----------|---------|
| **Capture Engine** | Rust + windows-crate | High-performance Windows Graphics Capture API calls |
| **Interop Layer** | .NET 8 | P/Invoke bindings and managed data structures |
| **UI Layer** | WinUI 3 | Modern, native Windows desktop interface |
| **CLI Layer** | .NET 8 Console | Headless automation and scripting support |

## 🐛 Known Limitations

- **Interactive session required** — Windows Graphics Capture API requires a visible desktop session. RDP sessions without a desktop may fail.
- **Minimized windows** — Capturing minimized windows may return empty frames (WGC limitation).
- **WinAppDriver tests** — Automated UI tests are not yet implemented (planned for future release).

## 📄 License

[Your License Here] — See LICENSE file for details.

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 🙏 Acknowledgments

Built with:
- [windows-capture](https://github.com/robberphex/windows-capture) — Fast Windows Graphics Capture API bindings
- [WinUI 3](https://github.com/microsoft/microsoft-ui-xaml) — Modern Windows UI framework
- [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) — Developer platform

---

**captcho** — Fast screen capture for Windows
