// ExitAfterDeliveryWorkflowTests.cs — Production-side tests for the exit
// decision on the Workflow Session (issue #54).
//
// These tests exercise the real CaptureWorkflowSession through fake platform
// adapters. They are the production-side specification of the exit gate:
// with the Behavior Setting enabled, a confirmed Capture exits only after
// the Annotation overlay is dismissed (its result returned) and every
// configured automatic delivery action finished successfully; a manual save
// or clipboard-only delivery satisfies the exit condition when no automatic
// delivery is pending; Annotation cancellation, delivery cancellation or
// failure, no delivery at all, and in-progress actions never exit. The exit
// surfaces as exactly one exit request — an event the WinUI layer turns into
// application shutdown, never an in-session abort.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Exit-after-delivery harness ──────────────────────────────────────────

/// <summary>
/// Records the exit requests the session raised, standing in for the WinUI
/// shutdown subscriber.
/// </summary>
internal sealed class ExitRequestRecorder
{
    public List<WorkflowExitReason> Requests { get; } = new();

    public int Count => Requests.Count;

    public void OnExitRequested(WorkflowExitReason reason) => Requests.Add(reason);
}

internal static class ExitAfterDeliveryHarness
{
    /// <summary>
    /// Creates a session with Annotation enabled, a confirming Annotation
    /// overlay, the supplied automatic Export configuration, and the
    /// exit-after-delivery Setting wired with a request recorder.
    /// </summary>
    public static (CaptureWorkflowSession<object> Session, ExitRequestRecorder Recorder) CreateSession(
        ScriptedExportAdapter export,
        AutomaticExportSettings automatic,
        bool exitAfterDelivery,
        FakeAnnotationOverlayAdapter? annotation = null,
        FakeCaptureAdapter? capture = null,
        Func<bool>? annotationEnabled = null)
    {
        annotation ??= new FakeAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Confirmed(ExportTestHelpers.CreateTestBitmap(8, 4)),
        };
        capture ??= new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
        };
        var recorder = new ExitRequestRecorder();
        var session = new CaptureWorkflowSession<object>(
            capture,
            new FakePreviewAdapter(),
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            annotation,
            annotationEnabled ?? (static () => true),
            static () => AnnotationToolState.WithDefaults(),
            new FakeOcrEngine(),
            static () => null,
            export,
            new FakeSaveAsDialogAdapter(),
            () => automatic,
            exitRecorder: recorder.OnExitRequested,
            exitAfterDelivery: () => exitAfterDelivery);
        return (session, recorder);
    }
}

// ── Automatic delivery exit gating ───────────────────────────────────────

public class ExitAfterDeliveryAutomaticTests
{
    [Fact]
    public async Task ConfirmedCapture_AutomaticDeliveryAllSucceeded_ExitsOnce()
    {
        // The exit path: Annotation confirmed (overlay dismissed), every
        // configured automatic action succeeded — exactly one exit request,
        // raised after the automatic delivery completed.
        var export = new ScriptedExportAdapter();
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true), exitAfterDelivery: true);

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(WorkflowExitReason.DeliveryCompleted, recorder.Requests[0]);
    }

    [Fact]
    public async Task ExitRequest_IsRaisedAfterEveryAutomaticActionFinished()
    {
        // Event ordering: the exit request must observe the finished state
        // of the delivery it gates on — the saved-file identity the
        // automatic save recorded is already committed when the request
        // fires.
        var export = new ScriptedExportAdapter();
        string? pathAtExit = null;
        var recorder = new ExitRequestRecorder();
        CaptureWorkflowSession<object>? session = null;
        session = new CaptureWorkflowSession<object>(
            new FakeCaptureAdapter
            {
                NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
            },
            new FakePreviewAdapter(),
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            new FakeAnnotationOverlayAdapter
            {
                NextResult = AnnotationPresentResult.Confirmed(ExportTestHelpers.CreateTestBitmap(8, 4)),
            },
            static () => true,
            static () => AnnotationToolState.WithDefaults(),
            new FakeOcrEngine(),
            static () => null,
            export,
            new FakeSaveAsDialogAdapter(),
            () => new AutomaticExportSettings(true, false, false),
            exitRecorder: reason =>
            {
                pathAtExit = session!.DefaultSavedFilePath;
                recorder.OnExitRequested(reason);
            },
            exitAfterDelivery: () => true);

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(export.DefaultPath, pathAtExit);
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task AutomaticDeliveryPartialFailure_NeverExits()
    {
        // One failed configured action keeps the application running for
        // manual retry — the Frame and any valid identity are preserved.
        var export = new ScriptedExportAdapter
        {
            CopyImageImpl = _ => ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.FromMilliseconds(1)),
        };
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, false), exitAfterDelivery: true);

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.AutomaticExport!.HasFailures);
        Assert.Equal(0, recorder.Count);
        Assert.NotNull(session.LastFrame);
        Assert.True(session.HasSavedFile);
    }

    [Fact]
    public async Task AutomaticSaveFailure_NeverExits()
    {
        var export = new ScriptedExportAdapter
        {
            SaveDefaultImpl = _ => ExportResult.Fail(ExportPhase.Write, "Disk unavailable.", TimeSpan.FromMilliseconds(1)),
        };
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, false, false), exitAfterDelivery: true);

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.AutomaticExport!.Save!.IsSuccess);
        Assert.Equal(0, recorder.Count);
        // Preserved for manual retry.
        Assert.NotNull(session.LastFrame);
        Assert.False(session.HasSavedFile);
    }

    [Fact]
    public async Task AnnotationCancelled_NeverExits()
    {
        var export = new ScriptedExportAdapter();
        var annotation = new FakeAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Cancelled(2),
        };
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true), exitAfterDelivery: true, annotation);

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task NoAutomaticActionsEnabled_NoManualDelivery_NeverExits()
    {
        // "No delivery" is a terminal non-exit: a confirmed Capture whose
        // configured automatic delivery is empty and that no manual Export
        // satisfied keeps the application running.
        var export = new ScriptedExportAdapter();
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, AutomaticExportSettings.WithDefaults(), exitAfterDelivery: true);

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(result.AutomaticExport);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task SettingDisabled_AutomaticDeliverySuccess_NeverExits()
    {
        // The gate is the Behavior Setting: without it nothing exits,
        // however successful the delivery.
        var export = new ScriptedExportAdapter();
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true), exitAfterDelivery: false);

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task SettingDisabled_ByDefault_NeverExits()
    {
        // A session constructed without exit wiring (every pre-#54
        // construction) never exits.
        var export = new ScriptedExportAdapter();
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true));

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.True(result.AutomaticExport!.Save!.IsSuccess);
    }
}

// ── Manual delivery exit gating ──────────────────────────────────────────

public class ExitAfterDeliveryManualTests
{
    [Fact]
    public async Task ManualSave_WhenNoAutomaticDeliveryIsPending_ExitsOnce()
    {
        // Manual save satisfies the exit condition when nothing automatic
        // is configured: the confirmed Capture is delivered the moment the
        // save succeeds.
        var export = new ScriptedExportAdapter();
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, AutomaticExportSettings.WithDefaults(), exitAfterDelivery: true);

        await session.CaptureFullDesktopAsync();
        Assert.Equal(0, recorder.Count);

        var save = await session.SaveAsync();

        Assert.True(save.IsSuccess);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(WorkflowExitReason.DeliveryCompleted, recorder.Requests[0]);
    }

    [Fact]
    public async Task ManualCopyFrame_WhenNoAutomaticDeliveryIsPending_ExitsOnce()
    {
        // Clipboard-only delivery: Copy Frame satisfies the exit condition
        // without any file.
        var export = new ScriptedExportAdapter();
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, AutomaticExportSettings.WithDefaults(), exitAfterDelivery: true);

        await session.CaptureFullDesktopAsync();

        var copy = await session.CopyFrameAsync();

        Assert.True(copy.IsSuccess);
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task ManualSaveFailure_NeverExits()
    {
        var export = new ScriptedExportAdapter
        {
            SaveDefaultImpl = _ => ExportResult.Fail(ExportPhase.Write, "Disk unavailable.", TimeSpan.FromMilliseconds(1)),
        };
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, AutomaticExportSettings.WithDefaults(), exitAfterDelivery: true);

        await session.CaptureFullDesktopAsync();
        var save = await session.SaveAsync();

        Assert.False(save.IsSuccess);
        Assert.Equal(0, recorder.Count);
        // Retryable: a later successful save still exits.
        export.SaveDefaultImpl = null;
        var retried = await session.SaveAsync();
        Assert.True(retried.IsSuccess);
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task ManualSaveAsCancel_NeverExits()
    {
        // Save As cancelled (dialog dismissed) is a delivery cancellation.
        var dialog = new FakeSaveAsDialogAdapter { NextResult = null };
        var export = new ScriptedExportAdapter();
        var recorder = new ExitRequestRecorder();
        var session = new CaptureWorkflowSession<object>(
            new FakeCaptureAdapter
            {
                NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
            },
            new FakePreviewAdapter(),
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            new FakeAnnotationOverlayAdapter
            {
                NextResult = AnnotationPresentResult.Confirmed(ExportTestHelpers.CreateTestBitmap(8, 4)),
            },
            static () => false,
            static () => AnnotationToolState.WithDefaults(),
            new FakeOcrEngine(),
            static () => null,
            export,
            dialog,
            static () => AutomaticExportSettings.WithDefaults(),
            exitRecorder: recorder.OnExitRequested,
            exitAfterDelivery: () => true);

        await session.CaptureFullDesktopAsync();
        var saveAs = await session.SaveAsAsync();

        Assert.Equal(WorkflowExportStatus.Cancelled, saveAs.Status);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task ManualSaveAsSuccess_ExitsOnce()
    {
        // Save As is a manual file delivery: a confirmed choice that writes
        // successfully satisfies the exit condition.
        var dialog = new FakeSaveAsDialogAdapter { NextResult = @"C:\captcho\chosen.png" };
        var export = new ScriptedExportAdapter();
        var recorder = new ExitRequestRecorder();
        var session = new CaptureWorkflowSession<object>(
            new FakeCaptureAdapter
            {
                NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
            },
            new FakePreviewAdapter(),
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            new FakeAnnotationOverlayAdapter
            {
                NextResult = AnnotationPresentResult.Confirmed(ExportTestHelpers.CreateTestBitmap(8, 4)),
            },
            static () => false,
            static () => AnnotationToolState.WithDefaults(),
            new FakeOcrEngine(),
            static () => null,
            export,
            dialog,
            static () => AutomaticExportSettings.WithDefaults(),
            exitRecorder: recorder.OnExitRequested,
            exitAfterDelivery: () => true);

        await session.CaptureFullDesktopAsync();
        var saveAs = await session.SaveAsAsync();

        Assert.True(saveAs.IsSuccess);
        Assert.Equal(1, recorder.Count);
        Assert.Equal(WorkflowExitReason.DeliveryCompleted, recorder.Requests[0]);
    }

    [Fact]
    public async Task ManualSave_FailedAutomaticDeliveryStillPending_DoesNotExit()
    {
        // A failed configured automatic action leaves the exit condition
        // unsatisfied; the user's manual save must observe the configured
        // delivery (all enabled actions, including the failed one) — a bare
        // manual save cannot quietly exit past a failed configured action.
        var export = new ScriptedExportAdapter
        {
            CopyImageImpl = _ => ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.FromMilliseconds(1)),
        };
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(false, true, false), exitAfterDelivery: true);

        await session.CaptureFullDesktopAsync();
        Assert.Equal(0, recorder.Count);

        var save = await session.SaveAsync();

        Assert.True(save.IsSuccess);
        // The configured Copy Frame never succeeded — no exit.
        Assert.Equal(0, recorder.Count);

        // A later successful manual Copy Frame completes the configured
        // delivery and exits.
        export.CopyImageImpl = null;
        var copy = await session.CopyFrameAsync();
        Assert.True(copy.IsSuccess);
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task SettingDisabled_ManualDelivery_NeverExits()
    {
        var export = new ScriptedExportAdapter();
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, AutomaticExportSettings.WithDefaults(), exitAfterDelivery: false);

        await session.CaptureFullDesktopAsync();
        var save = await session.SaveAsync();

        Assert.True(save.IsSuccess);
        Assert.Equal(0, recorder.Count);
    }
}

// ── In-progress actions never exit ───────────────────────────────────────

public class ExitAfterDeliveryInProgressTests
{
    [Fact]
    public async Task OperationInProgress_IsNeverRaisedAsExit()
    {
        // A second Trigger that arrives while delivery is in flight is
        // rejected as OperationInProgress — an unfinished action, never an
        // exit.
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var export = new ScriptedExportAdapter
        {
            SaveDefaultImpl = _ =>
            {
                started.TrySetResult(true);
                release.Task.Wait();
                return ExportResult.Ok(@"C:\captcho\default.png", 8, 4, 4, TimeSpan.FromMilliseconds(1));
            },
        };
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, false, false), exitAfterDelivery: true);

        var inFlight = session.CaptureFullDesktopAsync();
        await started.Task;

        var rejected = await session.SaveAsync();

        Assert.Equal(WorkflowExportStatus.OperationInProgress, rejected.Status);
        Assert.Equal(0, recorder.Count);

        release.SetResult(true);
        var completed = await inFlight;
        Assert.True(completed.IsSuccess);
        // The exit fired exactly once, after the in-flight delivery finished.
        Assert.Equal(1, recorder.Count);
    }
}

// ── Repeated exit requests ───────────────────────────────────────────────

public class ExitAfterDeliveryOnceTests
{
    [Fact]
    public async Task ManualRepeat_AfterAutomaticExitAlreadySatisfied_DoesNotExitAgain()
    {
        // Exactly one exit request per satisfied delivery: once the
        // configured automatic delivery exited, a later manual repeat of an
        // Export action must not raise a second exit.
        var export = new ScriptedExportAdapter();
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, false, false), exitAfterDelivery: true);

        await session.CaptureFullDesktopAsync();
        Assert.Equal(1, recorder.Count);

        var repeat = await session.SaveAsync();

        Assert.True(repeat.IsSuccess);
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task NewCapture_AfterExitWasSatisfied_RequiresItsOwnDelivery()
    {
        // A new Capture clears the satisfied exit state: it must be
        // delivered again before another exit.
        var export = new ScriptedExportAdapter();
        var (session, recorder) = ExitAfterDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, false, false), exitAfterDelivery: true);

        await session.CaptureFullDesktopAsync();
        Assert.Equal(1, recorder.Count);

        var second = await session.CaptureFullDesktopAsync();

        Assert.True(second.IsSuccess);
        Assert.Equal(2, recorder.Count);
    }
}
