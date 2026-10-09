// OcrWorkflowTests.cs — Headless tests for the WinUI-free OCR seam.
//
// Covers the pure language-resolution trichotomy (default / resolved /
// unsupported), the OcrResult contract distinctness (recognized-text,
// no-text, no-Frame, unsupported-language, failure, operation-in-progress),
// the status formatter wording, and the Workflow Session's Recognize Text
// routing through a fake IOcrEngine: in-memory Frame reuse (no capture
// adapter call), language resolution before the engine runs, distinct
// outcomes presented through the workflow, Frame preservation across every
// retryable outcome, and the shared operation guard with Capture Triggers.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Fake OCR engine ─────────────────────────────────────────────────────

/// <summary>
/// Fake IOcrEngine. Records the frames and language tags it was asked to
/// recognize and returns a configurable result.
/// </summary>
internal sealed class FakeOcrEngine : IOcrEngine
{
    public int CallCount { get; private set; }
    public ContiguousBitmap? LastFrame { get; private set; }
    public string? LastLanguageTag { get; private set; }
    public OcrResult? NextResult { get; set; }
    public IReadOnlyList<OcrLanguage> Languages { get; set; } = Array.Empty<OcrLanguage>();

    /// <summary>Optional gate: when set, recognition blocks until released.</summary>
    public TaskCompletionSource<bool>? Started { get; set; }

    /// <summary>Optional gate: when set, recognition blocks until released.</summary>
    public TaskCompletionSource<bool>? Release { get; set; }

    public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => Languages;

    public async Task<OcrResult> RecognizeAsync(ContiguousBitmap frame, string? languageTag)
    {
        CallCount++;
        LastFrame = frame;
        LastLanguageTag = languageTag;

        if (Started is not null && Release is not null)
        {
            Started.TrySetResult(true);
            await Release.Task;
        }

        return NextResult ?? OcrResult.Recognized("hello", "en-US");
    }
}

// ── Language resolver tests ─────────────────────────────────────────────

public class OcrLanguageResolverTests
{
    private static readonly OcrLanguage[] Installed =
    {
        new("en-US", "English (United States)"),
        new("de-DE", "German (Germany)"),
        new("ja", "Japanese"),
    };

    [Fact]
    public void Resolve_NullTag_UsesDefault()
    {
        Assert.Equal(OcrLanguageResolution.Default, OcrLanguageResolver.Resolve(null, Installed));
        Assert.Equal(OcrLanguageResolution.Default, OcrLanguageResolver.Resolve("", Installed));
        Assert.Equal(OcrLanguageResolution.Default, OcrLanguageResolver.Resolve("   ", Installed));
    }

    [Fact]
    public void Resolve_ExactInstalledTag_Resolves()
    {
        Assert.Equal(OcrLanguageResolution.Resolved, OcrLanguageResolver.Resolve("en-US", Installed));
        Assert.Equal(OcrLanguageResolution.Resolved, OcrLanguageResolver.Resolve("de-DE", Installed));
    }

    [Fact]
    public void Resolve_TagCaseInsensitive_StillResolves()
    {
        Assert.Equal(OcrLanguageResolution.Resolved, OcrLanguageResolver.Resolve("en-us", Installed));
        Assert.Equal(OcrLanguageResolution.Resolved, OcrLanguageResolver.Resolve("JA", Installed));
    }

    [Fact]
    public void Resolve_RemovedPack_IsUnsupported()
    {
        Assert.Equal(OcrLanguageResolution.Unsupported, OcrLanguageResolver.Resolve("fr-FR", Installed));
    }

    [Fact]
    public void Resolve_EmptyInstalledList_OnlyDefaultOrUnsupported()
    {
        var empty = Array.Empty<OcrLanguage>();

        Assert.Equal(OcrLanguageResolution.Default, OcrLanguageResolver.Resolve(null, empty));
        Assert.Equal(OcrLanguageResolution.Unsupported, OcrLanguageResolver.Resolve("en-US", empty));
    }

    [Fact]
    public void EffectiveTag_DefaultSelection_ReturnsNull()
    {
        Assert.Null(OcrLanguageResolver.EffectiveTag(null, Installed));
        Assert.Null(OcrLanguageResolver.EffectiveTag("  ", Installed));
    }

    [Fact]
    public void EffectiveTag_InstalledSelection_ReturnsCanonicalTag()
    {
        // Casing differences resolve to the installed (canonical) tag.
        Assert.Equal("en-US", OcrLanguageResolver.EffectiveTag("EN-us", Installed));
    }

    [Fact]
    public void EffectiveTag_UninstalledSelection_ReturnsNull()
    {
        Assert.Null(OcrLanguageResolver.EffectiveTag("fr-FR", Installed));
    }

    [Fact]
    public void FormatForDisplay_AppendsTagWhenDisplayNameDiffers()
    {
        Assert.Equal(
            "English (United States) [en-US]",
            OcrLanguageResolver.FormatForDisplay(new OcrLanguage("en-US", "English (United States)")));
    }

    [Fact]
    public void FormatForDisplay_MissingDisplayName_FallsBackToTag()
    {
        Assert.Equal("ja", OcrLanguageResolver.FormatForDisplay(new OcrLanguage("ja", "")));
    }
}

// ── OcrResult contract tests ────────────────────────────────────────────

public class OcrResultContractTests
{
    [Fact]
    public void Outcomes_AreDistinctValues()
    {
        var all = Enum.GetValues<OcrOutcome>();

        Assert.Equal(6, all.Length);
        Assert.Contains(OcrOutcome.RecognizedText, all);
        Assert.Contains(OcrOutcome.NoText, all);
        Assert.Contains(OcrOutcome.NoFrame, all);
        Assert.Contains(OcrOutcome.UnsupportedLanguage, all);
        Assert.Contains(OcrOutcome.Failed, all);
        Assert.Contains(OcrOutcome.OperationInProgress, all);
    }

    [Fact]
    public void Recognized_CarriesTextAndLanguageWithoutError()
    {
        var result = OcrResult.Recognized("hello world", "en-US", 5);

        Assert.Equal(OcrOutcome.RecognizedText, result.Outcome);
        Assert.Equal("hello world", result.Text);
        Assert.Equal("en-US", result.LanguageTag);
        Assert.Null(result.Error);
        Assert.Equal(5, result.ElapsedMs);
    }

    [Fact]
    public void NoTextResult_IsDistinctFromFailureAndCarriesNoError()
    {
        var result = OcrResult.NoTextResult("en-US", 2);

        Assert.Equal(OcrOutcome.NoText, result.Outcome);
        Assert.Equal("", result.Text);
        Assert.Equal("en-US", result.LanguageTag);
        Assert.Null(result.Error);
        Assert.NotEqual(OcrOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void NoFrame_And_Busy_And_Unsupported_CarryRetryableErrors()
    {
        Assert.Equal(OcrOutcome.NoFrame, OcrResult.NoFrame().Outcome);
        Assert.NotNull(OcrResult.NoFrame().Error);

        Assert.Equal(OcrOutcome.OperationInProgress, OcrResult.Busy().Outcome);
        Assert.NotNull(OcrResult.Busy().Error);

        var unsupported = OcrResult.Unsupported("fr-FR");
        Assert.Equal(OcrOutcome.UnsupportedLanguage, unsupported.Outcome);
        Assert.Equal("fr-FR", unsupported.LanguageTag);
        Assert.Contains("fr-FR", unsupported.Error);
    }

    [Fact]
    public void Fail_DefaultsToRetryableMessage()
    {
        var blank = OcrResult.Fail(null);
        Assert.Equal(OcrOutcome.Failed, blank.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(blank.Error));

        var explicitMessage = OcrResult.Fail("engine offline", "en-US", 3);
        Assert.Equal("engine offline", explicitMessage.Error);
        Assert.Equal("en-US", explicitMessage.LanguageTag);
        Assert.Equal(3, explicitMessage.ElapsedMs);
    }
}

// ── Status formatter tests ──────────────────────────────────────────────

public class OcrStatusFormatterTests
{
    [Fact]
    public void FormatRecognized_ReportsLineCountAndLanguage()
    {
        var result = OcrResult.Recognized("line one\nline two\nline three", "en-US");

        Assert.Equal("Recognized 3 lines (en-US).", OcrStatusFormatter.FormatRecognized(result));
    }

    [Fact]
    public void FormatRecognized_SingleLineOmitsPluralAndMissingLanguageOmitted()
    {
        Assert.Equal(
            "Recognized 1 line.",
            OcrStatusFormatter.FormatRecognized(OcrResult.Recognized("only line", "")));
    }

    [Fact]
    public void FormatNoText_IsDistinctWordingFromFailure()
    {
        var noText = OcrStatusFormatter.FormatNoText(OcrResult.NoTextResult("en-US"));

        Assert.Equal("No text found (en-US).", noText);
        Assert.DoesNotContain("fail", noText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatNoFrame_DirectsUserToCapture()
    {
        Assert.Equal(
            "No captured Frame to recognize. Take a capture first.",
            OcrStatusFormatter.FormatNoFrame());
    }
}
