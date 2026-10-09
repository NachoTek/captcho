// OcrFallbackTests.cs — Fallback selection tests for the Recognize Text
// engine chain (spec #50).
//
// The workflow selects Tesseract according to a defined fallback condition:
// when Windows OCR is unavailable (engine creation fails / no installed
// language packs) or lacks the selected language, recognition re-runs on
// the packaged Tesseract engine with the same Frame and the same language
// selection semantics — without changing the user-facing OcrResult shapes.
// These tests cover the pure selection seam (which outcomes fall through,
// which are terminal) and the composed engine through fakes.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Fallback selection policy ───────────────────────────────────────────

public class OcrFallbackPolicyTests
{
    [Fact]
    public void WindowsSuccess_RecognizedText_IsTerminal()
    {
        Assert.False(OcrFallbackPolicy.ShouldFallBack(OcrResult.Recognized("hi", "en-US")));
    }

    [Fact]
    public void WindowsSuccess_NoText_IsTerminal()
    {
        // The engine ran and read the Frame; an empty result is an answer,
        // not a fallback condition.
        Assert.False(OcrFallbackPolicy.ShouldFallBack(OcrResult.NoTextResult("en-US")));
    }

    [Fact]
    public void WindowsFailure_IsRetryableOnTesseract()
    {
        Assert.True(OcrFallbackPolicy.ShouldFallBack(OcrResult.Fail("engine exploded")));
    }

    [Fact]
    public void WindowsUnsupportedLanguage_IsRetryableOnTesseract()
    {
        Assert.True(OcrFallbackPolicy.ShouldFallBack(OcrResult.Unsupported("fr-FR")));
    }
}

// ── Composed fallback engine ────────────────────────────────────────────

public class FallbackOcrEngineTests
{
    private static readonly OcrLanguage[] WindowsInstalled =
    {
        new("en-US", "English (United States)"),
    };

    private static readonly OcrLanguage[] PackagedTesseract =
    {
        new("en-US", "English (United States)"),
        new("de-DE", "German (Germany)"),
        new("fr-FR", "French (France)"),
    };

    [Fact]
    public async Task WindowsRecognizes_ReturnsWindowsResultWithoutTouchingFallback()
    {
        var windows = new FakeOcrEngine { NextResult = OcrResult.Recognized("windows text", "en-US") };
        var tesseract = new FakeOcrEngine();
        var engine = new FallbackOcrEngine(windows, tesseract);

        var result = await engine.RecognizeAsync(ExportTestHelpers.CreateTestBitmap(4, 4), "en-US");

        Assert.Equal(OcrOutcome.RecognizedText, result.Outcome);
        Assert.Equal("windows text", result.Text);
        Assert.Equal(1, windows.CallCount);
        Assert.Equal(0, tesseract.CallCount);
    }

    [Fact]
    public async Task WindowsFails_FallsBackToTesseractWithTheSameFrame()
    {
        var frame = ExportTestHelpers.CreateTestBitmap(4, 4);
        var windows = new FakeOcrEngine { NextResult = OcrResult.Fail("Windows text recognition is unavailable.") };
        var tesseract = new FakeOcrEngine { NextResult = OcrResult.Recognized("tesseract text", "eng") };
        var engine = new FallbackOcrEngine(windows, tesseract);

        var result = await engine.RecognizeAsync(frame, "en-US");

        Assert.Equal(OcrOutcome.RecognizedText, result.Outcome);
        Assert.Equal("tesseract text", result.Text);
        Assert.Equal(1, windows.CallCount);
        Assert.Equal(1, tesseract.CallCount);
        Assert.Same(tesseract.LastFrame, frame);
    }

    [Fact]
    public async Task WindowsLacksLanguage_TesseractHasIt_FallsBackAndRecognizes()
    {
        var frame = ExportTestHelpers.CreateTestBitmap(4, 4);
        var windows = new FakeOcrEngine { NextResult = OcrResult.Unsupported("de-DE") };
        var tesseract = new FakeOcrEngine { NextResult = OcrResult.Recognized("hallo", "deu") };
        var engine = new FallbackOcrEngine(windows, tesseract);

        var result = await engine.RecognizeAsync(frame, "de-DE");

        Assert.Equal(OcrOutcome.RecognizedText, result.Outcome);
        Assert.Equal("hallo", result.Text);
        Assert.Equal(1, tesseract.CallCount);
        Assert.Equal("de-DE", tesseract.LastLanguageTag);
    }

    [Fact]
    public async Task WindowsNoText_IsTerminal_FallbackNotConsulted()
    {
        var windows = new FakeOcrEngine { NextResult = OcrResult.NoTextResult("en-US") };
        var tesseract = new FakeOcrEngine();
        var engine = new FallbackOcrEngine(windows, tesseract);

        var result = await engine.RecognizeAsync(ExportTestHelpers.CreateTestBitmap(4, 4), "en-US");

        Assert.Equal(OcrOutcome.NoText, result.Outcome);
        Assert.Equal(0, tesseract.CallCount);
    }

    [Fact]
    public async Task WindowsAndFallbackBothFail_SurfacesFallbackFailureAsRetryable()
    {
        var windows = new FakeOcrEngine { NextResult = OcrResult.Fail("windows down") };
        var tesseract = new FakeOcrEngine { NextResult = OcrResult.Fail("tesseract down") };
        var engine = new FallbackOcrEngine(windows, tesseract);

        var result = await engine.RecognizeAsync(ExportTestHelpers.CreateTestBitmap(4, 4), "en-US");

        Assert.Equal(OcrOutcome.Failed, result.Outcome);
        Assert.Contains("tesseract down", result.Error);
    }

    [Fact]
    public async Task WindowsFails_FallbackUnsupportedLanguage_SurfacesUnsupported()
    {
        var windows = new FakeOcrEngine { NextResult = OcrResult.Fail("windows down") };
        var tesseract = new FakeOcrEngine { NextResult = OcrResult.Unsupported("ja") };
        var engine = new FallbackOcrEngine(windows, tesseract);

        var result = await engine.RecognizeAsync(ExportTestHelpers.CreateTestBitmap(4, 4), "ja");

        Assert.Equal(OcrOutcome.UnsupportedLanguage, result.Outcome);
    }

    [Fact]
    public async Task NullLanguageTag_WindowsDefaultFails_FallbackRunsDefault()
    {
        var windows = new FakeOcrEngine { NextResult = OcrResult.Fail("no packs") };
        var tesseract = new FakeOcrEngine { NextResult = OcrResult.Recognized("fallback default", "eng") };
        var engine = new FallbackOcrEngine(windows, tesseract);

        var result = await engine.RecognizeAsync(ExportTestHelpers.CreateTestBitmap(4, 4), null);

        Assert.Equal(OcrOutcome.RecognizedText, result.Outcome);
        Assert.Equal("fallback default", result.Text);
        Assert.Null(tesseract.LastLanguageTag);
    }

    [Fact]
    public void GetAvailableLanguages_UnionsWindowsAndFallbackTags()
    {
        var windows = new FakeOcrEngine { Languages = WindowsInstalled };
        var tesseract = new FakeOcrEngine { Languages = PackagedTesseract };
        var engine = new FallbackOcrEngine(windows, tesseract);

        var languages = engine.GetAvailableLanguages();

        Assert.Contains(languages, l => l.Tag == "en-US");
        Assert.Contains(languages, l => l.Tag == "de-DE");
        Assert.Contains(languages, l => l.Tag == "fr-FR");
        // The shared en-US entry appears once, not twice.
        Assert.Single(languages, l => l.Tag == "en-US");
    }

    [Fact]
    public async Task ThrowingPrimary_SurfacesFailureWithoutCrashing()
    {
        var throwing = new ThrowingOcrEngine();
        var tesseract = new FakeOcrEngine { NextResult = OcrResult.Recognized("rescued", "eng") };
        var engine = new FallbackOcrEngine(throwing, tesseract);

        var result = await engine.RecognizeAsync(ExportTestHelpers.CreateTestBitmap(4, 4), "en-US");

        Assert.Equal(OcrOutcome.RecognizedText, result.Outcome);
        Assert.Equal("rescued", result.Text);
    }

    private sealed class ThrowingOcrEngine : IOcrEngine
    {
        public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => WindowsInstalled;

        public Task<OcrResult> RecognizeAsync(ContiguousBitmap frame, string? languageTag) =>
            throw new InvalidOperationException("boom");
    }
}
