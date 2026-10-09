// OcrFallbackWorkflowTests.cs — Workflow tests for Recognize Text with the
// Tesseract fallback chain (spec #50).
//
// The production session is composed with the fallback engine: when Windows
// OCR is unavailable or lacks the language, the same Trigger re-runs on the
// packaged Tesseract engine with the same session-owned Frame — no second
// Capture, no changed OcrResult shapes, and the Frame stays preserved for
// retry when both engines fail.

using System;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;
namespace captcho.UI.Tests;

public class OcrFallbackWorkflowTests
{
    private static FakeCaptureAdapter CapturedSessionFrame() => new()
    {
        NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 1),
    };

    private static CaptureWorkflowSession<object> Session(
        IWorkflowCaptureAdapter capture,
        IOcrEngine ocr,
        Func<string?> tagSource) =>
        new(
            capture,
            new FakePreviewAdapter(),
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            new DisabledAnnotationForFallbackTests(),
            static () => false,
            static () => AnnotationToolState.WithDefaults(),
            ocr,
            tagSource);

    [Fact]
    public async Task RecognizeText_WindowsUnavailable_FallsBackOnTheSameFrameWithoutAnotherCapture()
    {
        var capture = CapturedSessionFrame();
        var windows = new FakeOcrEngine { NextResult = OcrResult.Fail("Windows text recognition is unavailable.") };
        var tesseract = new FakeOcrEngine { NextResult = OcrResult.Recognized("fallback text", "eng") };
        var session = Session(capture, new FallbackOcrEngine(windows, tesseract), static () => null);
        var captureResult = await session.CaptureFullDesktopAsync();
        Assert.True(captureResult.IsSuccess);

        var result = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.RecognizedText, result.Outcome);
        Assert.Equal("fallback text", result.Text);
        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, windows.CallCount);
        Assert.Equal(1, tesseract.CallCount);
        Assert.Same(session.LastFrame, tesseract.LastFrame);
    }

    [Fact]
    public async Task RecognizeText_WindowsLacksSelectedLanguage_FallsBackAndRecognizes()
    {
        var windows = new FakeOcrEngine
        {
            Languages = new[] { new OcrLanguage("en-US", "English (United States)") },
            NextResult = OcrResult.Unsupported("de-DE"),
        };
        var tesseract = new FakeOcrEngine
        {
            Languages = new[] { new OcrLanguage("de-DE", "German (Germany)"), new OcrLanguage("en-US", "English (United States)") },
            NextResult = OcrResult.Recognized("hallo", "deu"),
        };
        var session = Session(CapturedSessionFrame(), new FallbackOcrEngine(windows, tesseract), static () => "de-DE");
        await session.CaptureFullDesktopAsync();

        var result = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.RecognizedText, result.Outcome);
        Assert.Equal("hallo", result.Text);
        Assert.Equal("de-DE", tesseract.LastLanguageTag);
    }

    [Fact]
    public async Task RecognizeText_BothEnginesFail_PreservesFrameForRetry()
    {
        var windows = new FakeOcrEngine { NextResult = OcrResult.Fail("windows down") };
        var tesseract = new FakeOcrEngine();
        var session = Session(CapturedSessionFrame(), new FallbackOcrEngine(windows, tesseract), static () => null);
        await session.CaptureFullDesktopAsync();

        tesseract.NextResult = OcrResult.Fail("tesseract down");
        var failed = await session.RecognizeTextAsync();
        Assert.Equal(OcrOutcome.Failed, failed.Outcome);

        tesseract.NextResult = OcrResult.Recognized("retry works", "eng");
        var retry = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.RecognizedText, retry.Outcome);
        Assert.Equal("retry works", retry.Text);
        Assert.Same(session.LastFrame, tesseract.LastFrame);
    }

    [Fact]
    public async Task RecognizeText_UninstalledSelection_DoesNotConsultEitherEngine()
    {
        // The union catalog reports neither en-US style tags nor de — the
        // selection "fr-FR" matches nothing, so the session blocks before
        // the chain runs, exactly as without the fallback.
        var windows = new FakeOcrEngine { Languages = Array.Empty<OcrLanguage>() };
        var tesseract = new FakeOcrEngine
        {
            Languages = new[] { new OcrLanguage("de-DE", "German (Germany)"), new OcrLanguage("en-US", "English (United States)") },
        };
        var session = Session(CapturedSessionFrame(), new FallbackOcrEngine(windows, tesseract), static () => "fr-FR");
        await session.CaptureFullDesktopAsync();

        var result = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.UnsupportedLanguage, result.Outcome);
        Assert.Equal(0, windows.CallCount);
        Assert.Equal(0, tesseract.CallCount);
        Assert.NotNull(session.LastFrame);
    }

    private sealed class DisabledAnnotationForFallbackTests : IAnnotationOverlayAdapter
    {
        public Task<AnnotationPresentResult> ShowAsync(ContiguousBitmap sourceFrame) =>
            Task.FromResult(AnnotationPresentResult.Fail("Annotation is disabled."));
    }
}
