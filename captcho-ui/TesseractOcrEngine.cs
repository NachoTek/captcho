// TesseractOcrEngine.cs — Production IOcrEngine fallback over the packaged
// Tesseract runtime (spec #50).
//
// Implements the second adapter beside WindowsOcrEngine on the same
// IOcrEngine seam the Workflow Session routes Recognize Text through: the
// TesseractOCR NuGet package supplies the native runtime (tesseract52 +
// leptonica, copied to x64/ by its build targets), and the tessdata_fast
// language data ships as Content beside the app. The session-owned
// in-memory Frame (contiguous BGRA) is converted to a BMP in memory and
// loaded through leptonica's pixReadMem — no file is written. BCP-47
// selections map to the packaged Tesseract language codes (en-US → eng);
// unmapped or unpackaged languages surface as
// OcrResult.Unsupported/Fail so the Frame stays retryable, mirroring the
// Windows path's never-throw contract.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using captcho.Capture;
using TesseractEngine = TesseractOCR.Engine;
using TessImage = TesseractOCR.Pix.Image;

namespace captcho.UI;

/// <summary>
/// Fallback <see cref="IOcrEngine"/> backed by the packaged Tesseract 5
/// runtime (TesseractOCR NuGet, Apache-2.0) and the tessdata_fast language
/// data packaged with the app. Selected supported languages map from
/// BCP-47 tags to the packaged Tesseract codes; unavailable language data
/// surfaces a clear unsupported error instead of a silent fallback.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    /// <summary>
    /// Packaged Tesseract code → the canonical BCP-47 tag advertised to the
    /// language selection UI. Region-qualified forms match the tags Windows
    /// OCR reports (en-US, de-DE), so the fallback chain deduplicates to one
    /// Settings entry per language and a removed Windows pack still resolves
    /// to the packaged Tesseract data.
    /// </summary>
    private static readonly Dictionary<string, string> TesseractCodeToBcp47 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en-US",
        ["deu"] = "de-DE",
        ["fra"] = "fr-FR",
        ["spa"] = "es-ES",
        ["ita"] = "it-IT",
    };

    /// <summary>
    /// Packaged Tesseract code → user-facing display name, matching the
    /// OcrLanguage contract the Settings combo consumes.
    /// </summary>
    private static readonly Dictionary<string, string> TesseractDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "English (United States)",
        ["deu"] = "German (Germany)",
        ["fra"] = "French (France)",
        ["spa"] = "Spanish (Spain)",
        ["ita"] = "Italian (Italy)",
    };

    private readonly string? _tessdataPath;

    /// <summary>
    /// Creates the engine over the packaged tessdata, located beside the
    /// entry assembly. Lazily constructs the native engine per
    /// recognition (mirroring WindowsOcrEngine, which recreates the WinRT
    /// engine per call) so a changed tessdata directory is picked up
    /// without restarting the app.
    /// </summary>
    public TesseractOcrEngine()
        : this(LocateTessdata(AppContext.BaseDirectory))
    {
    }

    /// <summary>
    /// Creates the engine over an explicit tessdata directory. A null or
    /// missing directory keeps the engine constructible — recognition
    /// surfaces the retryable missing-assets failure instead of throwing,
    /// and language enumeration reports nothing.
    /// </summary>
    public TesseractOcrEngine(string? tessdataPath)
    {
        _tessdataPath = tessdataPath;
    }

    /// <summary>
    /// The packaged OCR-capable languages, derived from the tessdata that
    /// ships with the app. Empty when the packaged assets are missing —
    /// the fallback chain then reports only the Windows languages.
    /// </summary>
    public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => GetPackagedLanguages();

    /// <summary>
    /// Catalog of the packaged tessdata languages: every packaged
    /// *.traineddata file advertised through its canonical BCP-47 tag and
    /// display name. Unknown codes (a user dropped an extra .traineddata in)
    /// surface with the bare code as their tag.
    /// </summary>
    public static IReadOnlyList<OcrLanguage> GetPackagedLanguages()
    {
        var tessdata = LocateTessdata(AppContext.BaseDirectory);
        if (tessdata is null)
            return Array.Empty<OcrLanguage>();

        try
        {
            return Directory.EnumerateFiles(tessdata, "*.traineddata")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => new OcrLanguage(
                    TesseractCodeToBcp47.TryGetValue(code!, out var tag) ? tag : code!,
                    TesseractDisplayNames.TryGetValue(code!, out var name) ? name : code!))
                .OrderBy(language => language.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<OcrLanguage>();
        }
    }

    /// <summary>
    /// Maps a language selection to the packaged Tesseract language code:
    /// an exact Tesseract code matches directly (eng), an exact BCP-47 tag
    /// matches (en-US), otherwise the primary subtag maps through the
    /// packaged set (de → deu, en-GB → eng). Returns null for
    /// null/whitespace input and for languages with no packaged data.
    /// </summary>
    public static string? MapToPackagedTag(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
            return null;

        var tag = languageTag.Trim();

        // Exact Tesseract code (eng) matches directly, canonicalized to the
        // packaged file name's casing.
        if (TesseractDisplayNames.TryGetValue(tag, out _))
            return TesseractDisplayNames.Keys.FirstOrDefault(
                key => string.Equals(key, tag, StringComparison.OrdinalIgnoreCase))!;

        // Advertised BCP-47 tag (en-US) maps to its packaged code.
        var exact = CodeForBcp47(tag);
        if (exact is not null)
            return exact;

        // Primary subtag: "de" → "deu"; "en-GB" → "eng".
        return CodeForPrimary(tag.Split('-')[0]);
    }

    /// <summary>
    /// The packaged Tesseract code whose advertised BCP-47 tag matches, or
    /// null when the tag is not one of the packaged canonical forms.
    /// </summary>
    private static string? CodeForBcp47(string tag) =>
        TesseractCodeToBcp47.FirstOrDefault(pair => string.Equals(pair.Value, tag, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>
    /// The packaged Tesseract code for an ISO 639-1 primary subtag, or
    /// null when no packaged language matches (ISO 639-2/3 codes like
    /// "deu" also match their Tesseract code directly here).
    /// </summary>
    private static string? CodeForPrimary(string primary)
    {
        foreach (var (code, tag) in TesseractCodeToBcp47)
        {
            if (string.Equals(tag.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase))
                return code;
        }

        // Bare Tesseract code (deu) or 639-2/B variants (ger, fre) map
        // through the display-name keys.
        return TesseractDisplayNames.ContainsKey(primary) ? primary : null;
    }

    /// <summary>
    /// Locates a usable tessdata directory under <paramref name="baseDir"/>:
    /// the "tessdata" subdirectory, valid only when it exists and contains
    /// at least one traineddata file. Returns null otherwise.
    /// </summary>
    public static string? LocateTessdata(string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir))
            return null;

        try
        {
            var tessdata = Path.Combine(baseDir, "tessdata");
            if (!Directory.Exists(tessdata))
                return null;

            return Directory.EnumerateFiles(tessdata, "*.traineddata").Any() ? tessdata : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Recognizes text in the supplied in-memory Frame using the packaged
    /// Tesseract runtime. A null <paramref name="languageTag"/> uses the
    /// packaged default (eng). Never throws for expected failures —
    /// missing native runtime/tessdata, unmapped languages, and engine
    /// failures surface as OcrResult.Fail/Unsupported so the Frame stays
    /// retryable. The synchronous CPU-bound recognition runs on the thread
    /// pool so the workflow caller's (UI) thread stays free.
    /// </summary>
    public Task<OcrResult> RecognizeAsync(ContiguousBitmap frame, string? languageTag)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Language mapping mirrors the Windows path's resolution:
                // the default selection uses the packaged default (eng); a
                // selected tag must map to packaged data or recognition is
                // blocked with the retryable unsupported outcome.
                var code = MapToPackagedTag(languageTag);
                if (!string.IsNullOrWhiteSpace(languageTag) && code is null)
                {
                    sw.Stop();
                    return OcrResult.Unsupported(
                        languageTag,
                        $"The selected OCR language ({languageTag}) has no packaged Tesseract language data. Pick a packaged language in Settings and retry.");
                }

                code ??= "eng";
                if (_tessdataPath is null)
                {
                    sw.Stop();
                    return OcrResult.Fail(
                        "The packaged text recognition assets are missing. Reinstall the application and try again.",
                        languageTag ?? "",
                        sw.Elapsed.TotalMilliseconds);
                }

                // The 5.2.16 wrapper resolves {dataPath}\{code}.traineddata
                // directly (see Engine.Initialize), so the datapath is the
                // tessdata directory itself — not its parent, as later
                // wrapper versions document.
                using var engine = new TesseractEngine(
                    _tessdataPath,
                    code,
                    TesseractOCR.Enums.EngineMode.Default);

                using var image = TessImage.LoadFromMemory(ToBmpBytes(frame));
                using var page = engine.Process(image);
                sw.Stop();

                var text = (page.Text ?? "").Trim();
                return text.Length == 0
                    ? OcrResult.NoTextResult(code, sw.Elapsed.TotalMilliseconds)
                    : OcrResult.Recognized(text, code, sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return OcrResult.Fail($"Text recognition failed: {ex.Message}", languageTag ?? "", sw.Elapsed.TotalMilliseconds);
            }
        });
    }

    /// <summary>
    /// Converts the contiguous BGRA Frame into an in-memory BMP — the
    /// Frame→pix adapter seam through leptonica's pixReadMem (no file is
    /// written). Performs the same dimension/buffer validation as the
    /// Windows engine's SoftwareBitmap conversion.
    /// </summary>
    private static byte[] ToBmpBytes(ContiguousBitmap frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException($"Frame dimensions must be positive ({frame.Width}×{frame.Height}).", nameof(frame));

        int expectedLen = frame.Stride * frame.Height;
        if (frame.Pixels.Length < expectedLen)
            throw new ArgumentException(
                $"Pixel buffer ({frame.Pixels.Length} bytes) is shorter than stride×height ({expectedLen}).",
                nameof(frame));

        using var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            // Both layouts are BGRA: copy row by row (bitmap stride may
            // exceed width*4; the Frame's stride is exactly width*4).
            for (int y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Pixels, y * frame.Stride, data.Scan0 + y * data.Stride, frame.Width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        return stream.ToArray();
    }
}
