# OCR & QR Code Integration Research

Research for adding OCR (text recognition) and QR code detection to Captcho after a screenshot is captured.

---

## 1. OCR Options

### Comparison Table

| Library | .NET 8 Support | Windows Support | Accuracy / Performance | Ease of Integration | License |
|---|---|---|---|---|---|
| **Windows.Media.Ocr** (built-in) | Yes (`net8.0-windows10.0.19041.0`) | Windows 10 1803+ only | Good for clean text; no preprocessing; no PDF/table support | Medium — requires WinRT interop, MSIX package identity, async API; accepts `SoftwareBitmap` directly | OS-provided, free |
| **Tesseract (charlesw/tesseract)** | Yes (netstandard2.0, computed for net8.0) | Yes (ships native x86/x64 DLLs) | Good with LSTM engine (Tesseract 4/5); language packs required separately; no auto-preprocessing | Medium — requires native DLL deployment + tessdata folder; `Tesseract.Drawing` adds `System.Drawing.Bitmap` support | Apache-2.0 |
| **TesseractOCR (Sicos1977)** | Yes (netstandard2.0, net8.0-windows) | Yes (bundles native binaries) | Good — wraps Tesseract 5 LSTM; auto-downloads tessdata; includes preprocessing | Easy — single NuGet, auto tessdata management, built-in image filters | MIT |
| **IronOCR (IronTesseract)** | Yes (net6.0–net9.0) | Yes (Windows/Linux/macOS/Docker) | Very good — Tesseract 5 engine + auto preprocessing (deskew, denoise, contrast, binarization); PDF support | Very easy — single NuGet, zero native deps to deploy, rich API | Commercial (free dev trial) |
| **Tesseract.Net.SDK (Patagames)** | Yes (netstandard2.0+) | Yes | Good — Tesseract 5 LSTM, 120+ languages | Medium — NuGet available, requires tessdata management | Commercial (paid) |

### Key Findings

**KDE Spectacle integration**: Spectacle added OCR in Plasma 6.6 using Tesseract via a runtime-loaded shared library (`TesseractRuntimeLoader`). It uses `QLibrary` to find `libtesseract.so` at runtime, avoiding a hard compile-time dependency. This approach does not translate directly to .NET/WinUI — instead, NuGet packages that bundle native binaries (TesseractOCR or IronOCR) are the equivalent.

**Windows.Media.Ocr** is the most natural fit for WinUI 3 because:
- It natively accepts `SoftwareBitmap` — the same type WinUI uses for images
- No external binary deployment needed
- Built-in language support via Windows Optional Features
- However: requires MSIX package identity, no image preprocessing, no PDF support

**Tesseract-based packages** are better if:
- You need offline operation without Windows language packs
- You need preprocessing (deskew, denoise, contrast)
- You want cross-platform potential in the future
- `TesseractOCR` (Sicos1977) is the best free option — auto tessdata, .NET 8 support, MIT license
- `IronOCR` is best if budget allows — zero-config, auto preprocessing, richest API

### Image Format Compatibility

- **Windows.Media.Ocr**: Accepts `SoftwareBitmap` (from `Windows.Graphics.Imaging`). To use from a WinUI capture, convert captured frame to `SoftwareBitmap` via `SoftwareBitmap.CreateCopyFromSurfaceAsync()` or `BitmapDecoder`.
- **TesseractOCR / charlesw/tesseract**: Accepts `System.Drawing.Bitmap` via `Tesseract.Drawing` package, or raw byte arrays / Pix (Leptonica). `System.Drawing.Common` is Windows-only on .NET 8 (supported, but generates platform warnings if referenced in cross-platform code).
- **IronOCR**: Accepts `System.Drawing.Bitmap`, file paths, streams, and byte arrays natively.

### Recommendation

**Primary: Windows.Media.Ocr** — free, zero-dependency, native WinRT integration, accepts SoftwareBitmap directly. Best for a Windows-only WinUI 3 app. Limitation: requires MSIX packaging (which Captcho likely already uses) and Windows language packs for non-English OCR.

**Fallback: TesseractOCR (Sicos1977)** — if you need richer preprocessing, more language flexibility, or want to avoid MSIX requirements. MIT license, auto tessdata download.

---

## 2. QR Code Options

### Comparison Table

| Library | .NET 8 Support | Windows Support | Decodes from In-Memory Bitmap | Performance | License |
|---|---|---|---|---|---|
| **ZXing.Net** (micjah/ZXing.Net) | Yes (net8.0, with bindings) | Yes | Yes — `ZXing.Windows.Compatibility` for `System.Drawing.Bitmap`; custom `SoftwareBitmapLuminanceSource` for WinRT `SoftwareBitmap` | Fast for static images; multi-threading caution needed | Apache-2.0 |
| **QRCoder** | Yes (net8.0) | Yes (Windows renderers need `System.Drawing`) | **No** — `QRCodeDetector` requires `System.Drawing.Bitmap`; no `SoftwareBitmap` support | Good for static images | MIT |
| **ZXing.Net.MAUI** | Yes (.NET 8) | Yes (WinUI via MAUI) | Yes (via MAUI camera) | Good | Apache-2.0 |
| **ZBar (via NuGet)** | Limited (.NET wrappers exist but stale) | Yes | Requires interop | Fastest for real-time | LGPL-2.1 |

### Key Findings

**ZXing.Net** is the clear winner for WinUI 3 QR detection:
- Pure C# core (no native dependencies for the decoding logic)
- Proven WinUI 3 compatibility — a working example exists (Simon Mourier's blog) using a thin `SoftwareBitmapLuminanceSource` adapter that converts WinRT `SoftwareBitmap` to ZXing's luminance format
- Supports QR, PDF417, Aztec, Data Matrix, EAN, UPC, Code128, and more
- Can decode from `System.Drawing.Bitmap` or custom `SoftwareBitmap` adapter
- Apache-2.0 license

**QRCoder** is primarily a QR **generator**, not decoder. Its `QRCodeDetector` class exists but requires `System.Drawing.Bitmap` and is less battle-tested than ZXing for detection. Not recommended for this use case.

**ZBar** is fastest for real-time scanning but the .NET bindings are stale and harder to integrate. Overkill for post-capture static image analysis.

### ZXing.Net + SoftwareBitmap Integration Pattern

From the proven WinUI 3 example:
```csharp
// Thin adapter: SoftwareBitmap → ZXing luminance source
public class SoftwareBitmapLuminanceSource : BaseLuminanceSource
{
    public SoftwareBitmapLuminanceSource(SoftwareBitmap bmp)
        : base(bmp.PixelWidth, bmp.PixelHeight)
    {
        if (bmp.BitmapPixelFormat != BitmapPixelFormat.Gray8)
        {
            using var converted = SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Gray8);
            converted.CopyToBuffer(luminances.AsBuffer());
            return;
        }
        bmp.CopyToBuffer(luminances.AsBuffer());
    }
}

// Usage
var reader = new BarcodeReader<SoftwareBitmap>(
    s => new SoftwareBitmapLuminanceSource(s));
var result = reader.Decode(softwareBitmap);
```

### Recommendation

**ZXing.Net** (`ZXing.Net` core + `ZXing.Windows.Compatibility`) — Apache-2.0, proven WinUI 3 integration, fast, supports all common barcode formats including QR. The `SoftwareBitmapLuminanceSource` adapter is a well-documented pattern.

---

## 3. Gotchas & Compatibility Issues

### WinUI 3 / Windows App SDK

1. **`System.Drawing.Common` is Windows-only on .NET 8+**: It works, but using it in a `net8.0-windows` project is fine. Avoid referencing it in cross-platform code. Tesseract-based libraries that depend on `System.Drawing` will work in Captcho since it's Windows-only.

2. **`SoftwareBitmap` ↔ `System.Drawing.Bitmap` conversion**: There is no built-in conversion. If using TesseractOCR/IronOCR (which want `System.Drawing.Bitmap`), you'll need to either:
   - Save the `SoftwareBitmap` to a stream, then load as `Bitmap`
   - Use `SoftwareBitmap.LoadAsync()` + `BitmapDecoder` to bridge

3. **Windows.Media.Ocr requires MSIX package identity**: The API won't work without it. Since Captcho uses WinUI 3 (likely packaged), this should be fine, but it's a hard requirement.

4. **Windows.Media.Ocr language packs**: Users must install OCR language packs via Windows Optional Features. The API returns `null` from `OcrEngine.TryCreateFromLanguage()` if the pack isn't installed. You should check and display a user-friendly message.

5. **ZXing.Net multithreading**: When scanning frames from a camera (not relevant for post-capture, but worth noting), the `BarcodeReader` is not thread-safe. A `lock` is needed. For static post-capture images, this isn't an issue.

6. **Native DLL deployment for Tesseract packages**: TesseractOCR and charlesw/tesseract bundle native `tesseract.dll` + `leptonica.dll`. These must be in the app's output directory or on PATH. NuGet should handle this automatically, but verify with single-file publishing and MSIX packaging.

7. **Image orientation**: Captured screenshots may have orientation metadata. `Windows.Media.Ocr` handles this automatically; Tesseract may not. Pre-rotate if needed.

---

## 4. Summary Recommendations

| Feature | Recommended Library | Rationale |
|---|---|---|
| **OCR** | **Windows.Media.Ocr** | Zero-dependency, native WinRT integration, accepts `SoftwareBitmap`, free, built into Windows. Fallback: TesseractOCR for richer preprocessing. |
| **QR Code Detection** | **ZXing.Net** | Apache-2.0, proven WinUI 3 compatibility via `SoftwareBitmapLuminanceSource`, fast, supports all common formats. |

### Combined Architecture Sketch

```
Screenshot captured (SoftwareBitmap or WriteableBitmap)
    │
    ├──→ OCR: Windows.Media.Ocr.OcrEngine.RecognizeAsync(softwareBitmap)
    │         → OcrResult.Lines → extracted text
    │
    └──→ QR:  ZXing.Net BarcodeReader<SoftwareBitmap>.Decode(softwareBitmap)
              → Result.Text → decoded content
```

Both libraries accept `SoftwareBitmap` directly, so the capture pipeline feeds both features without format conversion.
