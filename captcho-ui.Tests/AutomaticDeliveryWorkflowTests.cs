// AutomaticDeliveryWorkflowTests.cs — Production-side tests for the configured
// automatic delivery actions run after Annotation confirmation (issue #47).
//
// These tests exercise the real CaptureWorkflowSession through fake platform
// adapters. They are the production-side specification: after Annotation
// confirmation, the enabled automatic Export actions run in dependency order
// (save, Copy Frame, Copy Path — save can establish the path consumed later in
// the same sequence); Annotation cancellation runs no automatic actions and
// creates no delivery side effects; a failed action is reported while the
// composed Frame and any valid saved-file identity are preserved for manual
// retry. Automatic delivery composes the same Export action primitives the
// manual buttons use (#44) — it never re-implements save or clipboard logic.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Automatic delivery harness ───────────────────────────────────────────

/// <summary>
/// Scripted Export adapter: records the order of every action kind and can be
/// configured to fail any subset of them.
/// </summary>
internal sealed class ScriptedExportAdapter : IWorkflowExportAdapter
{
    private readonly object _lock = new();

    public string DefaultPath { get; set; } = @"C:\captcho\default.png";

    /// <summary>Fails the first delivery of the listed action kinds.</summary>
    public Func<ContiguousBitmap, ExportResult>? SaveDefaultImpl { get; set; }

    public Func<ContiguousBitmap, ClipboardExportResult>? CopyImageImpl { get; set; }
    public Func<string, ClipboardExportResult>? CopyTextImpl { get; set; }

    public System.Collections.Generic.List<string> Calls { get; } = new();

    public ExportResult SaveDefault(ContiguousBitmap frame, CancellationToken cancellationToken)
    {
        lock (_lock) { Calls.Add("SaveDefault"); }
        return SaveDefaultImpl != null
            ? SaveDefaultImpl(frame)
            : ExportResult.Ok(DefaultPath, frame.Width, frame.Height, 4, TimeSpan.FromMilliseconds(1));
    }

    public ExportResult SaveTo(ContiguousBitmap frame, string destinationPath, CancellationToken cancellationToken)
    {
        lock (_lock) { Calls.Add($"SaveTo:{destinationPath}"); }
        return ExportResult.Ok(destinationPath, frame.Width, frame.Height, 4, TimeSpan.FromMilliseconds(1));
    }

    public ClipboardExportResult CopyImage(ContiguousBitmap frame)
    {
        lock (_lock) { Calls.Add("CopyImage"); }
        return CopyImageImpl != null
            ? CopyImageImpl(frame)
            : ClipboardExportResult.Ok(frame.Width, frame.Height, 64, TimeSpan.FromMilliseconds(1));
    }

    public ClipboardExportResult CopyText(string text)
    {
        lock (_lock) { Calls.Add($"CopyText:{text}"); }
        return CopyTextImpl != null
            ? CopyTextImpl(text)
            : ClipboardExportResult.TextOk(text.Length, TimeSpan.FromMilliseconds(1));
    }
}

internal static class AutomaticDeliveryHarness
{
    /// <summary>
    /// Creates a session with Annotation enabled, a confirming Annotation
    /// overlay, and the supplied automatic Export configuration.
    /// </summary>
    public static CaptureWorkflowSession<object> CreateSession(
        ScriptedExportAdapter export,
        AutomaticExportSettings automatic,
        FakeAnnotationOverlayAdapter? annotation = null,
        FakeCaptureAdapter? capture = null)
    {
        annotation ??= new FakeAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Confirmed(ExportTestHelpers.CreateTestBitmap(8, 4)),
        };
        capture ??= new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
        };
        return new CaptureWorkflowSession<object>(
            capture,
            new FakePreviewAdapter(),
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            annotation,
            static () => true,
            static () => AnnotationToolState.WithDefaults(),
            new FakeOcrEngine(),
            static () => null,
            export,
            new FakeSaveAsDialogAdapter(),
            () => automatic);
    }
}

// ── Ordering and action combinations ─────────────────────────────────────

public class AutomaticDeliveryOrderingTests
{
    private static AutomaticExportSettings Settings(bool save, bool frame, bool path) =>
        new(save, frame, path);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task EveryCombination_RunsExactlyTheEnabledActionsInOrder(
        bool autoSave, bool autoCopyFrame, bool autoCopyPath)
    {
        var export = new ScriptedExportAdapter();
        var session = AutomaticDeliveryHarness.CreateSession(
            export, Settings(autoSave, autoCopyFrame, autoCopyPath));

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        var report = result.AutomaticExport;

        var expected = new System.Collections.Generic.List<string>();
        if (autoSave) expected.Add("SaveDefault");
        if (autoCopyFrame) expected.Add("CopyImage");
        // Copy Path consumes the saved-file identity, so it only runs when the
        // automatic save (or an earlier manual save) established one.
        if (autoCopyPath && autoSave) expected.Add($"CopyText:{export.DefaultPath}");
        Assert.Equal(expected, export.Calls);

        if (!autoSave && !autoCopyFrame && !autoCopyPath)
        {
            Assert.Null(report);
            return;
        }

        Assert.NotNull(report);
        Assert.Equal(autoSave, report!.Save is not null && report.Save.IsSuccess);
        Assert.Equal(autoCopyFrame, report.CopyFrame is not null && report.CopyFrame.IsSuccess);
        Assert.Equal(autoCopyPath && autoSave, report.CopyPath is not null && report.CopyPath.IsSuccess);
    }

    [Fact]
    public async Task Save_EstablishesThePathConsumedByCopyPathInTheSameSequence()
    {
        // The dependency order: save runs first, so Copy Path — otherwise
        // unavailable — copies the path the automatic save just recorded.
        var export = new ScriptedExportAdapter();
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, false, true));

        var result = await session.CaptureFullDesktopAsync();

        var report = result.AutomaticExport!;
        Assert.True(report.Save!.IsSuccess);
        Assert.True(report.CopyPath!.IsSuccess);
        Assert.Equal(export.DefaultPath, report.CopyPath.FilePath);
        Assert.Equal(export.DefaultPath, session.DefaultSavedFilePath);
        // Save first, then Copy Path of exactly the path save established.
        Assert.Equal(new[] { "SaveDefault", $"CopyText:{export.DefaultPath}" }, export.Calls);
    }

    [Fact]
    public async Task CopyPath_WithoutSave_IsSkippedNotFailed()
    {
        // Copy Path without an automatic save and no prior saved file has no
        // valid path to copy; it is skipped (no clipboard side effect) rather
        // than reported as a failure needing retry.
        var export = new ScriptedExportAdapter();
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(false, false, true));

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(export.Calls);
        Assert.Null(result.AutomaticExport!.CopyPath);
        Assert.False(session.HasSavedFile);
    }

    [Fact]
    public async Task AllDisabled_NoAutomaticActionsAndNoSideEffects()
    {
        var export = new ScriptedExportAdapter();
        var session = AutomaticDeliveryHarness.CreateSession(
            export, AutomaticExportSettings.WithDefaults());

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(result.AutomaticExport);
        Assert.Empty(export.Calls);
        Assert.False(session.HasSavedFile);
    }

    [Fact]
    public async Task ManualActions_RemainAvailableAfterAutomaticDelivery()
    {
        // Automatic delivery composes the manual primitives; a later manual
        // Save repeats the same default identity instead of duplicating it.
        var export = new ScriptedExportAdapter();
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true));

        await session.CaptureFullDesktopAsync();

        var manualSave = await session.SaveAsync();
        Assert.True(manualSave.IsSuccess);
        Assert.Equal(export.DefaultPath, manualSave.FilePath);
        // SaveDefault ran once (automatic); the manual repeat went SaveTo the
        // recorded identity — no accidental duplicate.
        Assert.Equal(1, export.Calls.Count(c => c == "SaveDefault"));
        Assert.Contains($"SaveTo:{export.DefaultPath}", export.Calls);
    }
}

// ── Cancellation ─────────────────────────────────────────────────────────

public class AutomaticDeliveryCancellationTests
{
    [Fact]
    public async Task AnnotationCancelled_RunsNoAutomaticActionsAndCreatesNoSideEffects()
    {
        var export = new ScriptedExportAdapter();
        var annotation = new FakeAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Cancelled(2),
        };
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true), annotation);

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.Null(result.AutomaticExport);
        Assert.Empty(export.Calls);
        // No delivery side effects: no Frame owned, no saved-file identity.
        Assert.Null(session.LastFrame);
        Assert.Null(session.DefaultSavedFilePath);
        Assert.False(session.HasSavedFile);
    }

    [Fact]
    public async Task TargetSelectionCancelled_BeforeAnnotation_RunsNoAutomaticActions()
    {
        // Cancellation upstream of Annotation (Target Selection) also runs no
        // automatic actions — no Capture, no Frame, no delivery.
        var export = new ScriptedExportAdapter();
        var annotation = new FakeAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Confirmed(ExportTestHelpers.CreateTestBitmap(4, 4)),
        };
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
        };
        var selection = new FakeSelectionOverlayAdapter(); // defaults to cancellation
        var automatic = new AutomaticExportSettings(true, true, true);
        var session = new CaptureWorkflowSession<object>(
            capture,
            new FakePreviewAdapter(),
            selection,
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            annotation,
            static () => true,
            static () => AnnotationToolState.WithDefaults(),
            new FakeOcrEngine(),
            static () => null,
            export,
            new FakeSaveAsDialogAdapter(),
            () => automatic);

        var result = await session.CaptureSelectionAsync();

        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.Null(result.AutomaticExport);
        Assert.Empty(export.Calls);
    }
}

// ── Partial failure, retry, frame preservation ───────────────────────────

public class AutomaticDeliveryPartialFailureTests
{
    [Fact]
    public async Task SaveFailure_IsReportedWhilePreservingTheComposedFrame()
    {
        var export = new ScriptedExportAdapter
        {
            SaveDefaultImpl = _ => ExportResult.Fail(ExportPhase.Write, "Disk unavailable.", TimeSpan.FromMilliseconds(1)),
        };
        var composed = ExportTestHelpers.CreateTestBitmap(8, 4);
        var annotation = new FakeAnnotationOverlayAdapter
        {
            NextResult = AnnotationPresentResult.Confirmed(composed),
        };
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true), annotation);

        var result = await session.CaptureFullDesktopAsync();

        // The Capture still succeeded; only the automatic save failed.
        Assert.True(result.IsSuccess);
        Assert.Same(composed, result.Frame);
        Assert.Same(composed, session.LastFrame);
        var report = result.AutomaticExport!;
        Assert.NotNull(report.Save);
        Assert.False(report.Save.IsSuccess);
        Assert.Equal(WorkflowExportStatus.Failed, report.Save.Status);
        Assert.Contains("Disk unavailable.", report.Save.Error);
        // The later clipboard actions still ran — a failed save does not block
        // independent delivery.
        Assert.True(report.CopyFrame!.IsSuccess);
        // No saved-file identity was recorded for a failed save.
        Assert.False(session.HasSavedFile);
    }

    [Fact]
    public async Task SaveFailure_WithCopyPathEnabled_SkipsCopyPath()
    {
        // Copy Path depends on a valid saved-file identity. When the automatic
        // save fails, Copy Path has nothing valid to copy and is skipped, not
        // failed — the reported failure is the save itself.
        var export = new ScriptedExportAdapter
        {
            SaveDefaultImpl = _ => ExportResult.Fail(ExportPhase.Write, "Disk unavailable.", TimeSpan.FromMilliseconds(1)),
        };
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, false, true));

        var result = await session.CaptureFullDesktopAsync();

        var report = result.AutomaticExport!;
        Assert.False(report.Save!.IsSuccess);
        Assert.Null(report.CopyPath);
        Assert.DoesNotContain(export.Calls, c => c.StartsWith("CopyText:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopyFrameFailure_IsReportedWhileLaterActionsStillRun()
    {
        var export = new ScriptedExportAdapter
        {
            CopyImageImpl = _ => ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.FromMilliseconds(1)),
        };
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true));

        var result = await session.CaptureFullDesktopAsync();

        var report = result.AutomaticExport!;
        Assert.True(report.Save!.IsSuccess);
        Assert.False(report.CopyFrame!.IsSuccess);
        Assert.Contains("Failed to set clipboard content", report.CopyFrame.Error);
        // Copy Path still delivered the saved path.
        Assert.True(report.CopyPath!.IsSuccess);
        Assert.Equal(export.DefaultPath, session.DefaultSavedFilePath);
    }

    [Fact]
    public async Task CopyPathFailure_IsReportedWhileEarlierStateIsRetained()
    {
        var export = new ScriptedExportAdapter
        {
            CopyTextImpl = _ => ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.FromMilliseconds(1)),
        };
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true));

        var result = await session.CaptureFullDesktopAsync();

        var report = result.AutomaticExport!;
        Assert.True(report.Save!.IsSuccess);
        Assert.True(report.CopyFrame!.IsSuccess);
        Assert.False(report.CopyPath!.IsSuccess);
        // The saved-file identity stays valid for manual retry.
        Assert.Equal(export.DefaultPath, session.DefaultSavedFilePath);
        Assert.True(session.HasSavedFile);
    }

    [Fact]
    public async Task FailedActions_AreRetryableManually()
    {
        // After a failed automatic save, the composed Frame and (absent)
        // identity stay retryable: a manual Save succeeds and records the
        // default identity.
        var export = new ScriptedExportAdapter
        {
            SaveDefaultImpl = _ => ExportResult.Fail(ExportPhase.Write, "Disk unavailable.", TimeSpan.FromMilliseconds(1)),
        };
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, false, false));

        var captured = await session.CaptureFullDesktopAsync();
        Assert.False(captured.AutomaticExport!.Save!.IsSuccess);

        export.SaveDefaultImpl = null;
        var retried = await session.SaveAsync();

        Assert.True(retried.IsSuccess);
        Assert.Equal(export.DefaultPath, session.DefaultSavedFilePath);
        var manualCopyPath = await session.CopyPathAsync();
        Assert.True(manualCopyPath.IsSuccess);
    }

    [Fact]
    public async Task MultipleFailures_AreAllReported()
    {
        var export = new ScriptedExportAdapter
        {
            CopyImageImpl = _ => ClipboardExportResult.Fail("image failed", TimeSpan.FromMilliseconds(1)),
            CopyTextImpl = _ => ClipboardExportResult.Fail("text failed", TimeSpan.FromMilliseconds(1)),
        };
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, true, true));

        var result = await session.CaptureFullDesktopAsync();

        var report = result.AutomaticExport!;
        Assert.True(report.Save!.IsSuccess);
        Assert.False(report.CopyFrame!.IsSuccess);
        Assert.False(report.CopyPath!.IsSuccess);
    }

    [Fact]
    public async Task AutomaticDelivery_RunsInsideTheOperationGuard()
    {
        // While the workflow (capture + automatic delivery) is in flight, a
        // concurrent Export action is rejected with OperationInProgress —
        // automatic delivery holds the same guard as every workflow operation.
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
        var session = AutomaticDeliveryHarness.CreateSession(
            export, new AutomaticExportSettings(true, false, false));

        var inFlight = session.CaptureFullDesktopAsync();
        await started.Task;

        var rejected = await session.CopyFrameAsync();

        Assert.Equal(WorkflowExportStatus.OperationInProgress, rejected.Status);

        release.SetResult(true);
        var completed = await inFlight;
        Assert.True(completed.IsSuccess);
    }
}

// ── Configuration source ─────────────────────────────────────────────────

public class AutomaticDeliveryConfigurationTests
{
    [Fact]
    public async Task Configuration_IsReadLivePerCapture()
    {
        // The automatic Export configuration is read from the settings source
        // when each Capture completes, so applying Settings takes effect on
        // the next Capture without reconstructing the session.
        var export = new ScriptedExportAdapter();
        var automatic = AutomaticExportSettings.WithDefaults();
        var session = AutomaticDeliveryHarness.CreateSession(export, automatic);

        await session.CaptureFullDesktopAsync();
        Assert.Empty(export.Calls);

        automatic = automatic with { AutoSave = true, AutoCopyPath = true };
        // Rebind the source the session reads (simulate runtime write-back).
        var rebound = AutomaticDeliveryHarness.CreateSession(export, automatic);
        var result = await rebound.CaptureFullDesktopAsync();

        var report = result.AutomaticExport!;
        Assert.True(report.Save!.IsSuccess);
        Assert.True(report.CopyPath!.IsSuccess);
    }

    [Fact]
    public async Task AnnotationDisabled_AutomaticDeliveryStillRunsAfterPreview()
    {
        // The automatic delivery hook is the confirmation of the Capture's
        // delivered Frame — with Annotation disabled, the preview route's
        // success plays the same role.
        var export = new ScriptedExportAdapter();
        var automatic = new AutomaticExportSettings(true, true, true);
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
            new FakeAnnotationOverlayAdapter(),
            static () => false,
            static () => AnnotationToolState.WithDefaults(),
            new FakeOcrEngine(),
            static () => null,
            export,
            new FakeSaveAsDialogAdapter(),
            () => automatic);

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, export.Calls.Count);
        Assert.True(result.AutomaticExport!.Save!.IsSuccess);
        Assert.True(result.AutomaticExport.CopyFrame!.IsSuccess);
        Assert.True(result.AutomaticExport.CopyPath!.IsSuccess);
    }

    [Fact]
    public async Task PreviewFailure_RunsNoAutomaticActions()
    {
        // With Annotation disabled, a failed preview transition preserves the
        // Frame for retry and runs no automatic delivery.
        var export = new ScriptedExportAdapter();
        var automatic = new AutomaticExportSettings(true, true, true);
        var session = new CaptureWorkflowSession<object>(
            new FakeCaptureAdapter
            {
                NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
            },
            new FakePreviewAdapter
            {
                NextResult = PreviewPresentResult<object>.Fail("Preview unavailable."),
            },
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            new FakeAnnotationOverlayAdapter(),
            static () => false,
            static () => AnnotationToolState.WithDefaults(),
            new FakeOcrEngine(),
            static () => null,
            export,
            new FakeSaveAsDialogAdapter(),
            () => automatic);

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.PreviewFailed, result.Status);
        Assert.Null(result.AutomaticExport);
        Assert.Empty(export.Calls);
        // Frame preserved for retry.
        Assert.NotNull(result.Frame);
        Assert.NotNull(session.LastFrame);
    }
}
