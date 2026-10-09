// QrWorkflowSessionTests.cs — Production-side workflow tests for the Scan QR
// Trigger routed through CaptureWorkflowSession.
//
// These tests exercise the real session through fake adapters (mirroring the
// OcrWorkflowSessionTests shape): QR scanning operates on the in-memory Frame
// the session already owns without a second Capture and without creating or
// requiring a saved file, found and no-code outcomes are distinct, scanner
// failures produce retryable errors without losing the Frame (which stays
// available for delivery), and the operation guard is shared with Capture
// Triggers.

using System;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class QrWorkflowSessionTests
{
    private static FakeCaptureAdapter CapturedSessionFrame() => new()
    {
        NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 1),
    };

    private static CaptureWorkflowSession<object> Session(
        IWorkflowCaptureAdapter capture,
        IQrScanner scanner) =>
        new(
            capture,
            new FakePreviewAdapter(),
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            new DisabledAnnotationForQrTests(),
            static () => false,
            static () => AnnotationToolState.WithDefaults(),
            NullOcrEngineForQrTests.Instance,
            static () => null,
            scanner,
            new UnconfiguredWorkflowExportAdapter(),
            new UnavailableSaveAsDialogAdapter(),
            static () => AutomaticExportSettings.WithDefaults());

    // ── In-memory Frame reuse ───────────────────────────────────────────

    [Fact]
    public async Task ScanQr_UsesTheSessionOwnedFrameWithoutAnotherCapture()
    {
        var capture = CapturedSessionFrame();
        var scanner = new FakeQrScanner { NextResult = QrScanResult.Found(new[] { "hi" }) };
        var session = Session(capture, scanner);

        var captureResult = await session.CaptureFullDesktopAsync();
        Assert.True(captureResult.IsSuccess);

        var qrResult = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.Found, qrResult.Outcome);
        Assert.Same(session.LastFrame, scanner.LastFrame);
        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, scanner.CallCount);
    }

    [Fact]
    public async Task ScanQr_AdoptedLegacyFrame_IsScannedWithoutASavedFile()
    {
        // The legacy route adopts its Frame into the session; Scan QR reads
        // it in-memory. No export adapter call is ever made — scanning does
        // not create or require a saved file.
        var scanner = new FakeQrScanner { NextResult = QrScanResult.Found(new[] { "adopted" }) };
        var session = Session(CapturedSessionFrame(), scanner);
        var frame = ExportTestHelpers.CreateTestBitmap(4, 4);
        session.AdoptFrame(frame);

        var result = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.Found, result.Outcome);
        Assert.Same(frame, scanner.LastFrame);
        Assert.False(session.HasSavedFile);
    }

    [Fact]
    public async Task ScanQr_BeforeAnyCapture_ReturnsNoFrameWithoutTouchingTheScanner()
    {
        var scanner = new FakeQrScanner();
        var session = Session(CapturedSessionFrame(), scanner);

        var result = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.NoFrame, result.Outcome);
        Assert.Equal(0, scanner.CallCount);
        Assert.NotNull(result.Error);
    }

    // ── Multiple results ───────────────────────────────────────────────

    [Fact]
    public async Task ScanQr_MultipleCodes_AllValuesAreReturnedWithoutDropping()
    {
        var scanner = new FakeQrScanner
        {
            NextResult = QrScanResult.Found(new[] { "alpha", "beta", "gamma" }),
        };
        var session = Session(CapturedSessionFrame(), scanner);
        await session.CaptureFullDesktopAsync();

        var result = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.Found, result.Outcome);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, result.Values);
    }

    // ── Distinct outcomes ──────────────────────────────────────────────

    [Fact]
    public async Task ScanQr_NoCodeOutcome_IsDistinctFromFoundAndCarriesNoError()
    {
        var scanner = new FakeQrScanner { NextResult = QrScanResult.NoCode() };
        var session = Session(CapturedSessionFrame(), scanner);
        await session.CaptureFullDesktopAsync();

        var result = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.NotFound, result.Outcome);
        Assert.Empty(result.Values);
        Assert.Null(result.Error);
        Assert.NotEqual(QrOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ScanQr_ScannerFailure_ReturnsFailedWithRetryableErrorAndPreservesFrame()
    {
        var frame = ExportTestHelpers.CreateTestBitmap(4, 4);
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(frame, "4×4", 1),
        };
        var scanner = new FakeQrScanner { NextResult = QrScanResult.Fail("decoder exploded") };
        var session = Session(capture, scanner);
        await session.CaptureFullDesktopAsync();

        var result = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.Failed, result.Outcome);
        Assert.Contains("decoder exploded", result.Error);
        // Frame preserved for retry or delivery.
        Assert.Same(frame, session.LastFrame);
    }

    [Fact]
    public async Task ScanQr_AfterFailure_SameFrameIsScannedOnRetry()
    {
        var scanner = new FakeQrScanner();
        var session = Session(CapturedSessionFrame(), scanner);
        await session.CaptureFullDesktopAsync();

        scanner.NextResult = QrScanResult.Fail("decoder exploded");
        await session.ScanQrAsync();

        scanner.NextResult = QrScanResult.Found(new[] { "retry works" });
        var retry = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.Found, retry.Outcome);
        Assert.Equal(new[] { "retry works" }, retry.Values);
        Assert.Same(session.LastFrame, scanner.LastFrame);
        Assert.Equal(2, scanner.CallCount);
    }

    // ── Operation guard ────────────────────────────────────────────────

    [Fact]
    public async Task ScanQr_DuringInFlightCapture_IsRejectedWithOperationInProgress()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var capture = new SlowGateCaptureAdapter(started, release, CapturedSessionFrame());
        var scanner = new FakeQrScanner { NextResult = QrScanResult.Found(new[] { "hi" }) };
        var session = Session(capture, scanner);

        var captureTask = session.CaptureFullDesktopAsync();
        await started.Task;

        var qrResult = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.OperationInProgress, qrResult.Outcome);
        Assert.Equal(0, scanner.CallCount);

        release.SetResult(true);
        await captureTask;
    }

    [Fact]
    public async Task Capture_DuringInFlightScan_IsRejectedWithOperationInProgress()
    {
        var scanStarted = new TaskCompletionSource<bool>();
        var scanRelease = new TaskCompletionSource<bool>();
        var scanner = new FakeQrScanner
        {
            Started = scanStarted,
            Release = scanRelease,
            NextResult = QrScanResult.Found(new[] { "hi" }),
        };
        var session = Session(CapturedSessionFrame(), scanner);
        await session.CaptureFullDesktopAsync();

        var scanTask = session.ScanQrAsync();
        await scanStarted.Task;

        var captureResult = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.OperationInProgress, captureResult.Status);

        scanRelease.SetResult(true);
        await scanTask;
    }

    [Fact]
    public async Task ScanQr_Completes_GuardReleasesForNextTrigger()
    {
        var scanner = new FakeQrScanner { NextResult = QrScanResult.Found(new[] { "hi" }) };
        var session = Session(CapturedSessionFrame(), scanner);
        await session.CaptureFullDesktopAsync();

        await session.ScanQrAsync();
        var second = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.Found, second.Outcome);
        Assert.Equal(2, scanner.CallCount);
    }

    // ── Defensive contract ─────────────────────────────────────────────

    [Fact]
    public async Task ScanQr_ThrowingScannerSurfacesFailureInsteadOfCrashing()
    {
        var throwing = new ThrowingQrScanner();
        var session = Session(CapturedSessionFrame(), throwing);
        await session.CaptureFullDesktopAsync();

        var result = await session.ScanQrAsync();

        Assert.Equal(QrOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.NotNull(session.LastFrame);
    }

    private sealed class ThrowingQrScanner : IQrScanner
    {
        public Task<QrScanResult> ScanAsync(ContiguousBitmap frame) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class DisabledAnnotationForQrTests : IAnnotationOverlayAdapter
    {
        public Task<AnnotationPresentResult> ShowAsync(ContiguousBitmap sourceFrame) =>
            Task.FromResult(AnnotationPresentResult.Fail("Annotation is disabled."));
    }

    private sealed class NullOcrEngineForQrTests : IOcrEngine
    {
        public static NullOcrEngineForQrTests Instance { get; } = new();

        public IReadOnlyList<OcrLanguage> GetAvailableLanguages() => Array.Empty<OcrLanguage>();

        public Task<OcrResult> RecognizeAsync(ContiguousBitmap frame, string? languageTag) =>
            Task.FromResult(OcrResult.Fail("Text recognition is not available."));
    }
}
