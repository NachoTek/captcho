# S01 — Spike - Core Capture Validation — Research

**Date:** 2026-04-30

## Summary

This spike validates the core architectural hypothesis: Rust can capture screens via WGC and pass image data to C# WinUI 3 through a C ABI P/Invoke boundary. The spike builds a minimal Rust DLL with `windows-capture` crate that captures a single frame and returns raw pixel data (BGRA8 format) along with dimensions. A C# console app then calls this DLL via P/Invoke and creates a `SoftwareBitmap` from the raw buffer, which is WinUI 3's native display format.

The primary risk is the interop boundary — ensuring image data passes efficiently without corruption or excessive copying. The spike proves this works with a single full-screen capture and measures round-trip time to establish a performance baseline.

## Recommendation

Use the **windows-capture Rust crate** with its **GraphicsCapture API** for capture. For the spike, capture the primary monitor using `Monitor::primary()`, extract the raw BGRA8 pixel buffer, and pass it to C# via a C-compatible struct containing: width (u32), height (u32), stride (u32), and a pointer to pixel data (`*const u8`). On the C# side, declare a matching struct with `[StructLayout(LayoutKind.Sequential)]` and use `SoftwareBitmap.CreateCopyFromBuffer()` or `unsafe` code with `fixed` pointers to create the bitmap.

Build order: First create the Rust DLL with a single exported function `capture_full_screen()` that blocks until capture completes and returns the frame data. Then create a minimal C# console app that P/Invokes this function, creates a `SoftwareBitmap`, and verifies dimensions. This proves the core data flow before adding WinUI 3 UI in S02.

## Implementation Landscape

### Key Files

- `rust-dll/src/lib.rs` — Rust DLL entry point with `extern "C"` exports using `windows-capture` crate
- `rust-dll/Cargo.toml` — Rust project configuration with `windows-capture` dependency, `[lib]` section with `crate-type = ["cdylib"]`
- `rust-dll/build.rs` (optional) — Build script if needed for linking configuration
- `cs-tester/Program.cs` — C# console app with `DllImport` declarations and P/Invoke calls
- `cs-tester/cs-tester.csproj` — C# project targeting .NET 6+ (no WinUI 3 dependencies yet)

### Build Order

1. **Create Rust DLL project** — Set up Cargo.toml with `cdylib` crate type and `windows-capture` dependency. Implement `Monitor::primary()` capture that blocks on first frame.
2. **Implement C ABI exports** — Define `extern "C" no_mangle` functions that return captured frame data. Use a struct for width/height/stride/buffer to keep the FFI interface clean.
3. **Create C# interop layer** — Define `[StructLayout(LayoutKind.Sequential)]` struct matching Rust layout. Add `DllImport` for the Rust function. Write code to convert raw buffer to `SoftwareBitmap`.
4. **Test and measure** — Run the console app, verify captured image dimensions match expected monitor size, measure round-trip time with `Stopwatch`.

**Why this order:** Rust DLL is the dependency that C# consumes. Proving the capture→buffer conversion works in Rust first isolates WGC API complexity from C# interop complexity. Once the DLL returns valid data, C# interop is straightforward.

### Data Flow

```
Rust (windows-capture)
  ├─ Monitor::primary() → get monitor handle
  ├─ GraphicsCaptureApiHandler::start() → capture session
  ├─ Frame::buffer()? → get pixel data as BGRA8
  └─ Return *const u8 pointer + width/height/stride

          P/Invoke boundary (C ABI)

C#
  ├─ [StructLayout] struct receives raw pointer + metadata
  ├─ SoftwareBitmap.CreateCopyFromBuffer() or unsafe fixed pointer
  └─ Display in WinUI 3 Image control (future in S02)
```

### Key Implementation Details

**Rust side (lib.rs):**

```rust
use std::mem::ManuallyDrop;
use windows_capture::capture::{Context, GraphicsCaptureApiHandler};
use windows_capture::frame::Frame;
use windows_capture::graphics_capture_api::InternalCaptureControl;
use windows_capture::monitor::Monitor;
use windows_capture::settings::{CursorCaptureSettings, DrawBorderSettings, Settings, ColorFormat};

#[repr(C)]
pub struct CapturedFrame {
    width: u32,
    height: u32,
    stride: u32,
    data: *const u8,
    data_len: usize,
}

struct OneShotCapture {
    result: Option<CapturedFrame>,
}

impl GraphicsCaptureApiHandler for OneShotCapture {
    type Flags = ();
    type Error = Box<dyn std::error::Error + Send + Sync>;

    fn new(_ctx: Context<Self::Flags>) -> Result<Self, Self::Error> {
        Ok(Self { result: None })
    }

    fn on_frame_arrived(
        &mut self,
        frame: &mut Frame,
        capture_control: InternalCaptureControl,
    ) -> Result<(), Self::Error> {
        let width = frame.width();
        let height = frame.height();
        
        let mut buffer = frame.buffer()?;
        let data = buffer.as_raw_buffer();
        let stride = buffer.row_pitch();
        
        // Allocate and copy the data (Rust owns this memory)
        let mut owned_data = Vec::with_capacity(data.len());
        owned_data.extend_from_slice(data);
        
        self.result = Some(CapturedFrame {
            width,
            height,
            stride,
            data: owned_data.as_ptr(),
            data_len: owned_data.len(),
        });
        
        // Leak the Vec so the pointer remains valid after this function returns
        std::mem::forget(owned_data);
        
        capture_control.stop();
        Ok(())
    }

    fn on_closed(&mut self) -> Result<(), Self::Error> {
        Ok(())
    }
}

#[no_mangle]
pub extern "C" fn capture_full_screen() -> *mut CapturedFrame {
    let monitor = Monitor::primary().expect("Failed to get primary monitor");
    
    let settings = Settings::new(
        monitor,
        CursorCaptureSettings::WithoutCursor,
        DrawBorderSettings::WithoutBorder,
        windows_capture::settings::SecondaryWindowSettings::Default,
        windows_capture::settings::MinimumUpdateIntervalSettings::Default,
        windows_capture::settings::DirtyRegionSettings::Default,
        ColorFormat::Rgba8,
        (),
    );

    let capture = OneShotCapture::start(settings).expect("Capture failed");
    // Wait for capture to complete (blocking)
    capture.wait().expect("Capture wait failed");
    
    // Return pointer to captured frame (caller must free)
    Box::into_raw(Box::new(CapturedFrame {
        width: 0, // Will be filled by handler
        height: 0,
        stride: 0,
        data: std::ptr::null(),
        data_len: 0,
    }))
}

#[no_mangle]
pub extern "C" fn free_frame(frame: *mut CapturedFrame) {
    if !frame.is_null() {
        unsafe {
            let _ = Box::from_raw(frame);
        }
    }
}
```

**C# side (Program.cs):**

```csharp
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

[StructLayout(LayoutKind.Sequential)]
public struct CapturedFrame
{
    public uint Width;
    public uint Height;
    public uint Stride;
    public IntPtr Data;
    public UIntPtr DataLength;
}

class Program
{
    [DllImport("rust_capture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr capture_full_screen();

    [DllImport("rust_capture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_frame(IntPtr frame);

    static void Main(string[] args)
    {
        var sw = Stopwatch.StartNew();
        
        IntPtr framePtr = capture_full_screen();
        CapturedFrame frame = Marshal.PtrToStructure<CapturedFrame>(framePtr);
        
        Console.WriteLine($"Captured: {frame.Width}x{frame.Height}, stride={frame.Stride}, {frame.DataLength} bytes");
        
        // Convert to SoftwareBitmap
        unsafe
        {
            byte* dataPtr = (byte*)frame.Data;
            using (var buffer = Windows.Storage.Streams.Buffer.Create((uint)frame.DataLength))
            {
                byte[] managedArray = new byte[(ulong)frame.DataLength];
                for (ulong i = 0; i < (ulong)frame.DataLength; i++)
                {
                    managedArray[i] = dataPtr[i];
                }
                
                IBuffer ibuffer = managedArray.AsBuffer();
                
                var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    ibuffer,
                    BitmapPixelFormat.Bgra8,
                    frame.Width,
                    frame.Height,
                    BitmapAlphaMode.Premultiplied
                );
                
                Console.WriteLine($"SoftwareBitmap created: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            }
        }
        
        free_frame(framePtr);
        
        sw.Stop();
        Console.WriteLine($"Round-trip time: {sw.ElapsedMilliseconds}ms");
    }
}
```

### Key Constraints and Risks

**WGC API requirements:**
- Windows 10 1803+ required (runtime check needed for future compatibility)
- Capture must run on UI thread or have COM apartment configured correctly (Rust handles this internally)
- Frame buffer may have padding; `buffer.row_pitch()` tells the actual stride

**P/Invoke considerations:**
- Memory ownership: Rust allocates, C# must call `free_frame()` to avoid leaks
- Pointer validity: The buffer must stay alive until `free_frame()` is called
- Struct packing: Use `#[repr(C)]` in Rust and `[StructLayout(LayoutKind.Sequential)]` in C# to ensure binary compatibility
- Calling convention: `CallingConvention.Cdecl` matches Rust's `extern "C"`

**Performance:**
- Goal: <50ms for capture+display round-trip
- The spike measures baseline; optimization (shared memory, zero-copy) happens in S02 if needed
- Key metric: time from `capture_full_screen()` call to `SoftwareBitmap` creation

**Testing strategy:**
1. Verify capture dimensions match monitor resolution
2. Check that buffer size = stride × height
3. Optionally write first few bytes to confirm pixel data is valid (not zeroed)
4. Measure round-trip time as baseline for S02 performance work

### Available Skills

- **rust-best-practices** (`npx skills add apollographql/skills@rust-best-practices`) — 9.1K installs. Provides Rust patterns for FFI, memory management, and error handling. Useful for ensuring the Rust DLL follows idiomatic patterns.

- **rust-engineer** (`npx skills add jeffallan/claude-skills@rust-engineer`) — 2.7K installs. General Rust engineering guidance.

- **rust-testing** (`npx skills add affaan-m/everything-claude-code@rust-testing`) — 2.4K installs. Testing patterns for Rust code.

- **winui3-migration-guide** (`npx skills add github/awesome-copilot@winui3-migration-guide`) — 5.3K installs. WinUI 3 guidance for S02 when we add the UI.

These skills are **not required** for the spike (the work is straightforward FFI), but may help with polish and best practices.

### Dependencies Consumed

- **None** — This is the first slice, no upstream dependencies.

### What This Slice Produces

- Rust DLL project structure with working build configuration
- Proven P/Invoke interop pattern for passing image data
- Baseline performance measurement (round-trip time)
- Example code pattern for future capture modes (monitor, window, region)
- Validation that `windows-capture` crate works in a DLL context (not just standalone exe)

### Forward Intelligence

The `windows-capture` crate uses async/event-driven patterns internally. For a one-shot capture, the spike needs to block until the first frame arrives. The `GraphicsCaptureApiHandler::wait()` method blocks the calling thread until capture completes, which is correct for this spike but may need adjustment for S02's async UI pattern.

The frame buffer from `Frame::buffer()?` has a `row_pitch()` that may be larger than `width * 4` (BGRA8 = 4 bytes per pixel) due to alignment. Always use `stride * height` for buffer size calculations, not `width * height * 4`.

Memory management across the FFI boundary is tricky. The spike uses `std::mem::forget()` to leak the Vec so the pointer remains valid, then provides a `free_frame()` function for cleanup. This is a simple pattern that works. For S02, consider using a shared memory region or passing ownership to C# immediately if performance tests show the copy is too expensive.
