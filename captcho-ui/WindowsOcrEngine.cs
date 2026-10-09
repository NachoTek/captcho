// WindowsOcrEngine.cs — Production IOcrEngine over Windows.Media.Ocr.
//
// Implements the adapter seam the Workflow Session routes Recognize Text
// through (spec #49): converts the session-owned in-memory Frame (contiguous
// BGRA) into a WinRT SoftwareBitmap — the Frame→SoftwareBitmap conversion is
// the adapter seam the research (#15/#16) identified — and runs
// OcrEngine.RecognizeAsync with either the resolved installed language or the
// user default. Never throws for expected failures: WinRT failures, missing
// language packs, and engine-unavailable conditions surface through
// OcrResult.Fail/OcrResult.Unsupported so the Frame stays retryable. The
// Tesseract fallback planned by #50 slots in as another IOcrEngine beside
// this one without touching the workflow.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using captcho.Capture;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinOcr = Windows.Media.Ocr.OcrResult;

namespace captcho.UI;

/// <summary>
/// Production <see cref="IOcrEngine"/> backed by Windows.Media.Ocr. Uses the
/// built-in OS OCR engine — no external dependencies — and the OCR language
/// packs installed with Windows. Language selection reads
/// <see cref="OcrEngine.AvailableRecognizerLanguages"/> so the UI is populated
/// from exactly the languages the engine can use.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    /// <summary>
    /// The OCR-capable language packs installed on the machine, as reported by
    /// Windows. Empty when the OCR feature or every pack is unavailable —
    /// callers treat an empty list plus a persisted selection as
    /// unsupported, and the default selection still attempts the user
    /// language so a fresh install with one pack behaves naturally.
    /// </summary>
    public IReadOnlyList<OcrLanguage> GetAvailableLanguages()
    {
        try
        {
            var available = OcrEngine.AvailableRecognizerLanguages;
            if (available is null || available.Count == 0)
                return Array.Empty<OcrLanguage>();

            var languages = new List<OcrLanguage>(available.Count);
            foreach (Language language in available)
            {
                languages.Add(new OcrLanguage(
                    language.LanguageTag,
                    FormatDisplayName(language)));
            }

            return languages;
        }
        catch (Exception)
        {
            // WinRT projection failure (class-not-registered, packaging
            // identity missing) — report no languages; recognition will
            // surface the retryable failure.
            return Array.Empty<OcrLanguage>();
        }
    }

    /// <summary>
    /// Recognizes text in the supplied in-memory Frame. A null
    /// <paramref name="languageTag"/> uses the user's default OCR language.
    /// Never throws for expected failures — engine creation failures for a
    /// missing language pack surface as
    /// <see cref="OcrOutcome.UnsupportedLanguage"/>, everything else as
    /// <see cref="OcrOutcome.Failed"/> with a retryable message.
    /// </summary>
    public async Task<OcrResult> RecognizeAsync(ContiguousBitmap frame, string? languageTag)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var sw = Stopwatch.StartNew();
        try
        {
            var engine = CreateEngine(languageTag);
            if (engine is null)
            {
                sw.Stop();
                return string.IsNullOrWhiteSpace(languageTag)
                    ? OcrResult.Fail(
                        "Windows text recognition is unavailable. Install an OCR language pack (Settings → Time & language → Language & region) and try again.")
                    : OcrResult.Unsupported(languageTag);
            }

            using var softwareBitmap = await ToSoftwareBitmapAsync(frame);
            var winResult = await engine.RecognizeAsync(softwareBitmap).AsTask();

            sw.Stop();

            var text = ExtractText(winResult);
            var usedTag = engine.RecognizerLanguage?.LanguageTag ?? languageTag ?? "";
            return string.IsNullOrWhiteSpace(text)
                ? OcrResult.NoTextResult(usedTag, sw.Elapsed.TotalMilliseconds)
                : OcrResult.Recognized(text, usedTag, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return OcrResult.Fail($"Text recognition failed: {ex.Message}", languageTag ?? "", sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Creates the OcrEngine for the supplied tag, or null when the engine
    /// cannot be created — a missing language pack for an explicit tag, or no
    /// usable OCR language at all for the default.
    /// </summary>
    private static OcrEngine? CreateEngine(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
            return OcrEngine.TryCreateFromUserProfileLanguages();

        var language = new Language(languageTag);
        return OcrEngine.TryCreateFromLanguage(language);
    }

    /// <summary>
    /// Converts the contiguous BGRA Frame into the SoftwareBitmap the OCR
    /// engine consumes — the Frame→SoftwareBitmap adapter seam. Performs the
    /// same dimension/buffer validation as the preview converter, wraps the
    /// pixels in a WinRT buffer, and fills a Bgra8 bitmap through
    /// SoftwareBitmap.CopyFromBuffer.
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(ContiguousBitmap frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException($"Frame dimensions must be positive ({frame.Width}×{frame.Height}).", nameof(frame));

        int expectedLen = frame.Stride * frame.Height;
        if (frame.Pixels.Length < expectedLen)
            throw new ArgumentException(
                $"Pixel buffer ({frame.Pixels.Length} bytes) is shorter than stride×height ({expectedLen}).",
                nameof(frame));

        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            frame.Width,
            frame.Height,
            BitmapAlphaMode.Premultiplied);

        var buffer = new Windows.Storage.Streams.Buffer((uint)expectedLen);
        using (var stream = buffer.AsStream())
        {
            await stream.WriteAsync(frame.Pixels, 0, expectedLen);
        }

        bitmap.CopyFromBuffer(buffer);
        return bitmap;
    }

    /// <summary>
    /// Extracts the recognized text from a Windows OcrResult, joining lines
    /// with environment newlines so multi-line recognition stays readable.
    /// </summary>
    private static string ExtractText(WinOcr? result)
    {
        if (result?.Lines is null || result.Lines.Count == 0)
            return "";

        var builder = new StringBuilder();
        foreach (var line in result.Lines)
        {
            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append(line.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats a Windows <see cref="Language"/> display name, falling back to
    /// the tag when the system cannot produce one.
    /// </summary>
    private static string FormatDisplayName(Language language)
    {
        try
        {
            var name = language.DisplayName;
            return string.IsNullOrWhiteSpace(name) ? language.LanguageTag : name;
        }
        catch
        {
            return language.LanguageTag;
        }
    }
}
