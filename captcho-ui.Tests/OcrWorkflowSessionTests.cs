// OcrWorkflowSessionTests.cs — Production-side workflow tests for the
// Recognize Text Trigger routed through CaptureWorkflowSession.
//
// These tests exercise the real session through fake adapters (mirroring the
// AnnotationGateTests shape): OCR operates on the in-memory Frame the session
// already owns without a second Capture, the persisted language selection is
// resolved against the installed packs before the engine runs, Recognized-
// text and no-text outcomes are distinct, unsupported languages and engine
// failures produce retryable errors without losing the Frame, and the
// operation guard is shared with Capture Triggers.

using System;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class OcrWorkflowSessionTests
{
    private static readonly OcrLanguage[] Installed =
    {
        new("en-US", "English (United States)"),
        new("de-DE", "German (Germany)"),
    };

    private static FakeCaptureAdapter CapturedSessionFrame() => new()
    {
        NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 1),
    };

    private static CaptureWorkflowSession<object> Session(
        IWorkflowCaptureAdapter capture,
        IOcrEngine ocr,
        string? persistedTag = null) =>
        Session(capture, ocr, () => persistedTag);

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
            new DisabledAnnotationForOcrTests(),
            static () => false,
            static () => AnnotationToolState.WithDefaults(),
            ocr,
            tagSource);

    // ── In-memory Frame reuse ───────────────────────────────────────────

    [Fact]
    public async Task RecognizeText_UsesTheSessionOwnedFrameWithoutAnotherCapture()
    {
        var capture = CapturedSessionFrame();
        var ocr = new FakeOcrEngine { NextResult = OcrResult.Recognized("hi", "en-US") };
        var session = Session(capture, ocr);

        var captureResult = await session.CaptureFullDesktopAsync();
        Assert.True(captureResult.IsSuccess);

        var ocrResult = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.RecognizedText, ocrResult.Outcome);
        Assert.Same(session.LastFrame, ocr.LastFrame);
        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, ocr.CallCount);
    }

    [Fact]
    public async Task RecognizeText_BeforeAnyCapture_ReturnsNoFrameWithoutTouchingTheEngine()
    {
        var ocr = new FakeOcrEngine();
        var session = Session(CapturedSessionFrame(), ocr);

        var result = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.NoFrame, result.Outcome);
        Assert.Equal(0, ocr.CallCount);
        Assert.NotNull(result.Error);
    }

    // ── Language selection ─────────────────────────────────────────────

    [Fact]
    public async Task RecognizeText_NullSelection_PassesNullTagForEngineDefault()
    {
        var ocr = new FakeOcrEngine { NextResult = OcrResult.Recognized("hi", "en-US") };
        var session = Session(CapturedSessionFrame(), ocr, persistedTag: null);
        await session.CaptureFullDesktopAsync();

        await session.RecognizeTextAsync();

        Assert.Null(ocr.LastLanguageTag);
    }

    [Fact]
    public async Task RecognizeText_InstalledSelection_PassesTheResolvedTag()
    {
        var ocr = new FakeOcrEngine
        {
            Languages = Installed,
            NextResult = OcrResult.Recognized("hallo", "de-DE"),
        };
        var session = Session(CapturedSessionFrame(), ocr, persistedTag: "de-DE");
        await session.CaptureFullDesktopAsync();

        await session.RecognizeTextAsync();

        Assert.Equal("de-DE", ocr.LastLanguageTag);
    }

    [Fact]
    public async Task RecognizeText_SelectionCasingResolvesToCanonicalInstalledTag()
    {
        var ocr = new FakeOcrEngine
        {
            Languages = Installed,
            NextResult = OcrResult.Recognized("hallo", "de-DE"),
        };
        var session = Session(CapturedSessionFrame(), ocr, persistedTag: "de-de");
        await session.CaptureFullDesktopAsync();

        await session.RecognizeTextAsync();

        Assert.Equal("de-DE", ocr.LastLanguageTag);
    }

    [Fact]
    public async Task RecognizeText_RemovedLanguagePack_ReturnsUnsupportedWithoutRunningTheEngine()
    {
        var ocr = new FakeOcrEngine { Languages = Installed };
        var session = Session(CapturedSessionFrame(), ocr, persistedTag: "fr-FR");
        await session.CaptureFullDesktopAsync();

        var result = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.UnsupportedLanguage, result.Outcome);
        Assert.Equal(0, ocr.CallCount);
        Assert.Contains("fr-FR", result.Error);
        // Frame preserved for retry.
        Assert.NotNull(session.LastFrame);
    }

    // ── Distinct outcomes ──────────────────────────────────────────────

    [Fact]
    public async Task RecognizeText_NoTextOutcome_IsDistinctFromRecognizedAndCarriesNoError()
    {
        var ocr = new FakeOcrEngine { NextResult = OcrResult.NoTextResult("en-US") };
        var session = Session(CapturedSessionFrame(), ocr);
        await session.CaptureFullDesktopAsync();

        var result = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.NoText, result.Outcome);
        Assert.Equal("", result.Text);
        Assert.Null(result.Error);
        Assert.NotEqual(OcrOutcome.RecognizedText, result.Outcome);
        Assert.NotEqual(OcrOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task RecognizeText_EngineFailure_ReturnsFailedWithRetryableErrorAndPreservesFrame()
    {
        var frame = ExportTestHelpers.CreateTestBitmap(4, 4);
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(frame, "4×4", 1),
        };
        var ocr = new FakeOcrEngine { NextResult = OcrResult.Fail("engine exploded") };
        var session = Session(capture, ocr);
        await session.CaptureFullDesktopAsync();

        var result = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.Failed, result.Outcome);
        Assert.Contains("engine exploded", result.Error);
        Assert.Same(frame, session.LastFrame);
    }

    [Fact]
    public async Task RecognizeText_AfterFailure_SameFrameIsRecognizedOnRetry()
    {
        var ocr = new FakeOcrEngine();
        var session = Session(CapturedSessionFrame(), ocr);
        await session.CaptureFullDesktopAsync();

        ocr.NextResult = OcrResult.Fail("engine exploded");
        await session.RecognizeTextAsync();

        ocr.NextResult = OcrResult.Recognized("retry works", "en-US");
        var retry = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.RecognizedText, retry.Outcome);
        Assert.Equal("retry works", retry.Text);
        Assert.Same(session.LastFrame, ocr.LastFrame);
        Assert.Equal(2, ocr.CallCount);
    }

    [Fact]
    public async Task RecognizeText_UnsupportedThenInstalledSelection_RetrySucceeds()
    {
        var ocr = new FakeOcrEngine
        {
            Languages = Installed,
            NextResult = OcrResult.Recognized("hello", "en-US"),
        };
        string[] selection = { "fr-FR" };
        var session = Session(CapturedSessionFrame(), ocr, () => selection[0]);
        await session.CaptureFullDesktopAsync();

        var unsupported = await session.RecognizeTextAsync();
        Assert.Equal(OcrOutcome.UnsupportedLanguage, unsupported.Outcome);

        selection[0] = "en-US";
        var retry = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.RecognizedText, retry.Outcome);
        Assert.Equal("en-US", ocr.LastLanguageTag);
    }

    // ── Operation guard ────────────────────────────────────────────────

    [Fact]
    public async Task RecognizeText_DuringInFlightCapture_IsRejectedWithOperationInProgress()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var capture = new SlowGateCaptureAdapter(started, release, CapturedSessionFrame());
        var ocr = new FakeOcrEngine { NextResult = OcrResult.Recognized("hi", "en-US") };
        var session = Session(capture, ocr);

        var captureTask = session.CaptureFullDesktopAsync();
        await started.Task;

        var ocrResult = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.OperationInProgress, ocrResult.Outcome);
        Assert.Equal(0, ocr.CallCount);

        release.SetResult(true);
        await captureTask;
    }

    [Fact]
    public async Task Capture_DuringInFlightRecognition_IsRejectedWithOperationInProgress()
    {
        var ocrStarted = new TaskCompletionSource<bool>();
        var ocrRelease = new TaskCompletionSource<bool>();
        var ocr = new FakeOcrEngine
        {
            Started = ocrStarted,
            Release = ocrRelease,
            NextResult = OcrResult.Recognized("hi", "en-US"),
        };
        var session = Session(CapturedSessionFrame(), ocr);
        await session.CaptureFullDesktopAsync();

        var ocrTask = session.RecognizeTextAsync();
        await ocrStarted.Task;

        var captureResult = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.OperationInProgress, captureResult.Status);

        ocrRelease.SetResult(true);
        await ocrTask;
    }

    [Fact]
    public async Task RecognizeText_Completes_GuardReleasesForNextTrigger()
    {
        var ocr = new FakeOcrEngine { NextResult = OcrResult.Recognized("hi", "en-US") };
        var session = Session(CapturedSessionFrame(), ocr);
        await session.CaptureFullDesktopAsync();

        await session.RecognizeTextAsync();
        var second = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.RecognizedText, second.Outcome);
        Assert.Equal(2, ocr.CallCount);
    }

    // ── Defensive contract ─────────────────────────────────────────────

    [Fact]
    public async Task RecognizeText_ThrowingEngineSurfacesFailureInsteadOfCrashing()
    {
        var throwing = new ThrowingOcrEngine();
        var session = Session(CapturedSessionFrame(), throwing);
        await session.CaptureFullDesktopAsync();

        var result = await session.RecognizeTextAsync();

        Assert.Equal(OcrOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.NotNull(session.LastFrame);
    }

    private sealed class ThrowingOcrEngine : IOcrEngine
    {
        public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => Installed;

        public Task<OcrResult> RecognizeAsync(ContiguousBitmap frame, string? languageTag) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class DisabledAnnotationForOcrTests : IAnnotationOverlayAdapter
    {
        public Task<AnnotationPresentResult> ShowAsync(ContiguousBitmap sourceFrame) =>
            Task.FromResult(AnnotationPresentResult.Fail("Annotation is disabled."));
    }
}
