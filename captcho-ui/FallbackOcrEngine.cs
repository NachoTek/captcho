// FallbackOcrEngine.cs — Windows-first OCR with the packaged Tesseract
// fallback (spec #50).
//
// Composes two IOcrEngine implementations behind the same seam the
// Workflow Session already routes Recognize Text through, so neither the
// workflow nor the UI changes: Windows.Media.Ocr runs first (no external
// dependencies, OS language packs); when it is unavailable or lacks the
// selected language — the defined fallback condition — the same in-memory
// Frame is re-recognized on the packaged Tesseract engine. Recognized-text
// and no-text are terminal answers from Windows; failures and unsupported
// languages fall through. Language enumeration unions both catalogs so
// Settings offers every language the chain can use.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Pure fallback-selection seam: which Windows OCR outcomes are terminal
/// answers and which re-run on the packaged Tesseract engine. Kept
/// separate from the engine chain so the condition is testable (and
/// tunable) without faking recognition.
/// </summary>
public static class OcrFallbackPolicy
{
    /// <summary>
    /// True when the Windows engine's outcome should fall back to the
    /// packaged Tesseract engine: engine failure (unavailable runtime, no
    /// usable language packs) or an unsupported selected language.
    /// Recognized-text and no-text are answers about the Frame, not about
    /// the engine, so they never fall back.
    /// </summary>
    public static bool ShouldFallBack(OcrResult windowsResult) =>
        windowsResult.Outcome is OcrOutcome.Failed or OcrOutcome.UnsupportedLanguage;
}

/// <summary>
/// Windows-first <see cref="IOcrEngine"/> with the packaged Tesseract
/// fallback. Preserves the never-throw contract and the OcrResult shapes
/// of both underlying engines: the surfaced result is always the last
/// engine's answer, so Recognized-text, no-text, unsupported-language, and
/// failure outcomes keep their user-facing meaning.
/// </summary>
public sealed class FallbackOcrEngine : IOcrEngine
{
    private readonly IOcrEngine _windows;
    private readonly IOcrEngine _fallback;

    /// <summary>
    /// Creates the chain. Production composes WindowsOcrEngine over
    /// TesseractOcrEngine; tests inject fakes.
    /// </summary>
    public FallbackOcrEngine(IOcrEngine windows, IOcrEngine fallback)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>
    /// Every language either engine can use: the Windows OCR language
    /// packs followed by the packaged Tesseract languages, de-duplicated
    /// by tag so the Settings combo lists each once.
    /// </summary>
    public IReadOnlyList<OcrLanguage> GetAvailableLanguages()
    {
        var languages = new List<OcrLanguage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var language in _windows.GetAvailableLanguages())
        {
            if (seen.Add(language.Tag))
                languages.Add(language);
        }

        foreach (var language in _fallback.GetAvailableLanguages())
        {
            if (seen.Add(language.Tag))
                languages.Add(language);
        }

        return languages;
    }

    /// <summary>
    /// Recognizes text with Windows OCR first; when the outcome meets the
    /// fallback condition (<see cref="OcrFallbackPolicy"/>), re-recognizes
    /// the same Frame on the packaged Tesseract engine with the same
    /// language selection. Never throws for expected failures — a
    /// throwing primary engine is treated as a failure and falls back.
    /// </summary>
    public async Task<OcrResult> RecognizeAsync(ContiguousBitmap frame, string? languageTag)
    {
        ArgumentNullException.ThrowIfNull(frame);

        OcrResult windowsResult;
        try
        {
            windowsResult = await _windows.RecognizeAsync(frame, languageTag).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The engine contract says never-throw; a defensive catch
            // routes an unexpected adapter crash into the fallback instead
            // of losing the Frame.
            windowsResult = OcrResult.Fail($"Windows text recognition failed: {ex.Message}", languageTag ?? "");
        }

        if (!OcrFallbackPolicy.ShouldFallBack(windowsResult))
            return windowsResult;

        return await _fallback.RecognizeAsync(frame, languageTag).ConfigureAwait(false);
    }

    /// <summary>
    /// Production composition: Windows OCR first, the packaged Tesseract
    /// engine behind it. A static factory (not an extension) so the chain
    /// stays explicit at both wiring sites.
    /// </summary>
    public static FallbackOcrEngine WindowsWithTesseract() =>
        new(new WindowsOcrEngine(), new TesseractOcrEngine());
}
