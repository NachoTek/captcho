// OcrWorkflow.cs — WinUI-free OCR seam for post-capture text recognition.
//
// The Workflow Session owns Recognized-text as a post-capture workflow feature
// (not an Export): the session routes the in-memory Frame it already owns to
// an OCR engine adapter and surfaces distinct Recognized-text, no-text,
// unsupported-language, and failure outcomes so the WinUI layer can present
// each without a second Capture. The engine seam is adapter-shaped on
// purpose — Windows.Media.Ocr is the production implementation here, and the
// planned Tesseract fallback (#50) slots in as another IOcrEngine without
// touching the workflow or the UI.
//
// Language selection is resolved before the engine is invoked: the persisted
// tag is matched against the OCR-capable language packs Windows reports, so
// "use default", "use this installed language", and "selected pack was
// removed" are distinct, testable states. OcrLanguageResolver is the pure
// seam for that matching; WindowsOcrEngine feeds it the installed pack list.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Outcome of an OCR attempt. Recognized-text and no-text are distinct
/// successes; the remaining values are retryable conditions that preserve the
/// Frame.
/// </summary>
public enum OcrOutcome
{
    /// <summary>
    /// The engine returned text for the Frame. <see cref="OcrResult.Text"/>
    /// carries the recognized content.
    /// </summary>
    RecognizedText,

    /// <summary>
    /// The engine ran to completion but recognized no text in the Frame. Not
    /// an error — the Frame is preserved and the user can retry or pick
    /// another language.
    /// </summary>
    NoText,

    /// <summary>
    /// No captured Frame is available to recognize. The user must Capture
    /// first.
    /// </summary>
    NoFrame,

    /// <summary>
    /// The selected OCR language is not installed (e.g., its language pack
    /// was removed). The Frame is preserved; the user can pick an installed
    /// language and retry.
    /// </summary>
    UnsupportedLanguage,

    /// <summary>
    /// The OCR engine failed. The Frame is preserved so the same Trigger can
    /// be retried.
    /// </summary>
    Failed,

    /// <summary>
    /// Another workflow operation was already in progress. No work was
    /// performed and the Frame is untouched.
    /// </summary>
    OperationInProgress,
}

/// <summary>
/// WinUI-free result of an OCR attempt. Mirrors the never-throw contract of
/// <see cref="WorkflowResult{TImage}"/>: expected failures surface through
/// <see cref="OcrOutcome"/> + <see cref="Error"/>, never exceptions.
/// </summary>
public sealed class OcrResult
{
    /// <summary>OCR outcome.</summary>
    public OcrOutcome Outcome { get; init; }

    /// <summary>
    /// Recognized text. Non-empty only when
    /// <see cref="OcrOutcome.RecognizedText"/>; empty for every other
    /// outcome.
    /// </summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// The BCP-47 tag of the language actually used — the resolved selection
    /// when the user picked one, or the engine's default when they did not.
    /// Carried so the UI can report which language read the Frame.
    /// </summary>
    public string LanguageTag { get; init; } = "";

    /// <summary>
    /// User-visible error message. Null on RecognizedText and NoText; set on
    /// every retryable condition.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Recognition duration in milliseconds.</summary>
    public double ElapsedMs { get; init; }

    private OcrResult(OcrOutcome outcome, string text, string languageTag, string? error, double elapsedMs)
    {
        Outcome = outcome;
        Text = text;
        LanguageTag = languageTag;
        Error = error;
        ElapsedMs = elapsedMs;
    }

    /// <summary>Builds a Recognized-text result.</summary>
    public static OcrResult Recognized(string text, string languageTag, double elapsedMs = 0) =>
        new(OcrOutcome.RecognizedText, text ?? "", languageTag ?? "", null, elapsedMs);

    /// <summary>Builds a no-text result. Distinct from every failure.</summary>
    public static OcrResult NoTextResult(string languageTag, double elapsedMs = 0) =>
        new(OcrOutcome.NoText, "", languageTag ?? "", null, elapsedMs);

    /// <summary>Builds the no-Frame result.</summary>
    public static OcrResult NoFrame() =>
        new(OcrOutcome.NoFrame, "", "", "No captured Frame to recognize. Take a capture first.", 0);

    /// <summary>Builds the unsupported-language result.</summary>
    public static OcrResult Unsupported(string languageTag, string? error = null) =>
        new(OcrOutcome.UnsupportedLanguage, "", languageTag ?? "",
            error ?? $"The selected OCR language ({languageTag}) is not installed. Pick an installed language in Settings and retry.",
            0);

    /// <summary>Builds the operation-in-progress result.</summary>
    public static OcrResult Busy() =>
        new(OcrOutcome.OperationInProgress, "", "", "A capture is already in progress.", 0);

    /// <summary>Builds an engine-failure result.</summary>
    public static OcrResult Fail(string? error, string languageTag = "", double elapsedMs = 0) =>
        new(OcrOutcome.Failed, "", languageTag ?? "",
            string.IsNullOrWhiteSpace(error) ? "Text recognition failed. Try again." : error,
            elapsedMs);
}

/// <summary>
/// One OCR-capable language reported by the engine, reduced to the
/// WinUI-free contract that crosses the workflow seam: the BCP-47 tag the
/// engine is configured with and a human-readable display name.
/// </summary>
public sealed record OcrLanguage
{
    /// <summary>BCP-47 language tag (e.g., "en-US").</summary>
    public string Tag { get; init; }

    /// <summary>Human-readable display name (e.g., "English (United States)").</summary>
    public string DisplayName { get; init; }

    public OcrLanguage(string tag, string displayName)
    {
        Tag = tag ?? "";
        DisplayName = displayName ?? "";
    }
}

/// <summary>
/// The result of matching a persisted OCR language selection against the
/// installed OCR-capable language packs.
/// </summary>
public enum OcrLanguageResolution
{
    /// <summary>
    /// No selection is persisted (null/empty tag) — the engine's default
    /// language is used.
    /// </summary>
    Default,

    /// <summary>
    /// The persisted tag matches an installed OCR language pack. That
    /// language is used.
    /// </summary>
    Resolved,

    /// <summary>
    /// A selection is persisted but no installed pack matches it (e.g., the
    /// pack was removed). Recognition is blocked with a retryable error.
    /// </summary>
    Unsupported,
}

/// <summary>
/// Narrow adapter over a text-recognition engine. Production implements this
/// with Windows.Media.Ocr (WindowsOcrEngine); the planned Tesseract fallback
/// (#50) implements the same seam. Implementations must not throw for
/// expected failures — surface them through <see cref="OcrResult.Fail"/> so
/// the Frame stays retryable.
/// </summary>
public interface IOcrEngine
{
    /// <summary>
    /// The OCR-capable languages this engine can be configured with — the
    /// Windows OCR language packs installed on the machine. Used to populate
    /// language selection.
    /// </summary>
    IReadOnlyList<OcrLanguage> GetAvailableLanguages();

    /// <summary>
    /// Recognizes text in the supplied in-memory Frame. A null
    /// <paramref name="languageTag"/> selects the engine's default language.
    /// Implementations must not throw for expected failures.
    /// </summary>
    Task<OcrResult> RecognizeAsync(ContiguousBitmap frame, string? languageTag);
}

/// <summary>
/// Pure matching seam between a persisted OCR language selection and the
/// installed OCR-capable language packs. Case-insensitive on the BCP-47 tag
/// (Windows tag casing varies); WinUI-free so selection behavior is covered
/// by headless tests. The default/resolved/unsupported trichotomy is the
/// single source every caller (workflow, Settings) reads.
/// </summary>
public static class OcrLanguageResolver
{
    /// <summary>
    /// Resolves the selected tag against the installed languages. Null or
    /// whitespace resolves to <see cref="OcrLanguageResolution.Default"/>.
    /// Matching is ordinal-ignore-case so tag casing differences between
    /// Windows releases cannot split an exact tag match.
    /// </summary>
    public static OcrLanguageResolution Resolve(string? selectedTag, IReadOnlyList<OcrLanguage> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);

        if (string.IsNullOrWhiteSpace(selectedTag))
            return OcrLanguageResolution.Default;

        foreach (var language in installed)
        {
            if (string.Equals(language.Tag, selectedTag, StringComparison.OrdinalIgnoreCase))
                return OcrLanguageResolution.Resolved;
        }

        return OcrLanguageResolution.Unsupported;
    }

    /// <summary>
    /// Resolves the effective language for recognition: the matching installed
    /// tag, or null (engine default) when nothing is selected. Returns null
    /// for an unsupported selection — the caller reports the retryable
    /// unsupported-language outcome instead of silently falling back, so a
    /// removed pack is never mistaken for the default.
    /// </summary>
    public static string? EffectiveTag(string? selectedTag, IReadOnlyList<OcrLanguage> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);

        if (string.IsNullOrWhiteSpace(selectedTag))
            return null;

        foreach (var language in installed)
        {
            if (string.Equals(language.Tag, selectedTag, StringComparison.OrdinalIgnoreCase))
                return language.Tag;
        }

        return null;
    }

    /// <summary>
    /// Formats a language for the selection UI: the display name with the tag
    /// appended when the two differ (e.g., "English (United States)
    /// [en-US]"), or the bare tag when no display name is available.
    /// </summary>
    public static string FormatForDisplay(OcrLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);

        if (string.IsNullOrWhiteSpace(language.DisplayName))
            return language.Tag;
        if (string.Equals(language.DisplayName, language.Tag, StringComparison.OrdinalIgnoreCase))
            return language.Tag;
        return $"{language.DisplayName} [{language.Tag}]";
    }
}
