// WorkflowDeliveryTests.cs — Tests for the Open With and Share Export
// workflow actions (issue #46) on the runtime session, plus headless-safe
// tests for the production Windows delivery adapter.
//
// These tests exercise the real CaptureWorkflowSession delivery surface
// through fake platform adapters: Open With and Share are available only
// while a valid saved file exists as workflow state for the current Capture
// (the same gating as Copy Path), platform failures and cancellations
// preserve the composed Frame, Annotation state, and saved-file identity
// for retry, and concurrent operations are rejected with
// OperationInProgress. The production adapter tests cover the
// never-throw contract for missing files, extension-less paths, missing
// window handles, and pre-cancelled tokens — the paths reachable
// headlessly without launching Windows UI.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Fake delivery adapter ─────────────────────────────────────────────────

/// <summary>
/// Delivery-aware harness extensions. Mirror the Export harness but wire
/// the fake delivery adapter through the session's full constructor.
/// </summary>
internal static class WorkflowDeliveryTestHarness
{
    public static CaptureWorkflowSession<object> CreateSession(
        FakeWorkflowExportAdapter export,
        FakeSaveAsDialogAdapter saveAsDialog,
        FakeWorkflowDeliveryAdapter delivery,
        IWorkflowCaptureAdapter? capture = null)
    {
        return new CaptureWorkflowSession<object>(
            capture ?? new FakeCaptureAdapter(),
            new FakePreviewAdapter(),
            new FakeSelectionOverlayAdapter(),
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            new FakeAnnotationOverlayAdapter(),
            static () => false,
            static () => AnnotationToolState.WithDefaults(),
            export,
            saveAsDialog,
            delivery);
    }

    public static async Task<CaptureWorkflowSession<object>> CaptureAsync(
        FakeWorkflowExportAdapter export,
        FakeSaveAsDialogAdapter saveAsDialog,
        FakeWorkflowDeliveryAdapter delivery,
        ContiguousBitmap? frame = null,
        IWorkflowCaptureAdapter? capture = null)
    {
        frame ??= ExportTestHelpers.CreateTestBitmap(8, 4);
        var captureAdapter = capture ?? new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(frame, $"{frame.Width}×{frame.Height}", 1),
        };
        var session = CreateSession(export, saveAsDialog, delivery, captureAdapter);
        var result = await session.CaptureFullDesktopAsync();
        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        return session;
    }
}

/// <summary>
/// Fake Windows delivery adapter. Records the paths it was asked to open
/// or share and returns configurable results. Defaults succeed so happy-path
/// tests need no platform.
/// </summary>
internal sealed class FakeWorkflowDeliveryAdapter : IWorkflowDeliveryAdapter
{
    public Func<string, CancellationToken, Task<DeliveryResult>>? OpenWithImpl { get; set; }
    public Func<string, CancellationToken, Task<DeliveryResult>>? ShareImpl { get; set; }

    public int OpenWithCallCount { get; private set; }
    public int ShareCallCount { get; private set; }
    public string? LastOpenWithPath { get; private set; }
    public string? LastSharePath { get; private set; }

    public Task<DeliveryResult> OpenWithAsync(string filePath, CancellationToken cancellationToken)
    {
        OpenWithCallCount++;
        LastOpenWithPath = filePath;
        return OpenWithImpl != null
            ? OpenWithImpl(filePath, cancellationToken)
            : Task.FromResult(DeliveryResult.Ok(1, TimeSpan.FromMilliseconds(1).TotalMilliseconds));
    }

    public Task<DeliveryResult> ShareAsync(string filePath, CancellationToken cancellationToken)
    {
        ShareCallCount++;
        LastSharePath = filePath;
        return ShareImpl != null
            ? ShareImpl(filePath, cancellationToken)
            : Task.FromResult(DeliveryResult.Ok(null, TimeSpan.FromMilliseconds(1).TotalMilliseconds));
    }
}

// ── Open With action tests ────────────────────────────────────────────────

public class WorkflowOpenWithTests
{
    [Fact]
    public async Task OpenWith_BeforeAnySave_IsUnavailable()
    {
        var delivery = new FakeWorkflowDeliveryAdapter();
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(
            new FakeWorkflowExportAdapter(), new FakeSaveAsDialogAdapter(), delivery);

        var result = await session.OpenWithAsync();

        Assert.Equal(WorkflowExportAction.OpenWith, result.Action);
        Assert.Equal(WorkflowExportStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(0, delivery.OpenWithCallCount);
    }

    [Fact]
    public async Task OpenWith_AfterSave_OpensTheSavedFileThroughTheAdapter()
    {
        var export = new FakeWorkflowExportAdapter();
        var delivery = new FakeWorkflowDeliveryAdapter();
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), delivery);
        await session.SaveAsync();

        var result = await session.OpenWithAsync();

        Assert.Equal(WorkflowExportStatus.Succeeded, result.Status);
        Assert.Equal(1, delivery.OpenWithCallCount);
        Assert.Equal(export.DefaultPath, delivery.LastOpenWithPath);
        Assert.Equal(export.DefaultPath, result.FilePath);
    }

    [Fact]
    public async Task OpenWith_AfterSaveAsOnly_UsesTheSaveAsPath()
    {
        var delivery = new FakeWorkflowDeliveryAdapter();
        var saveAs = new FakeSaveAsDialogAdapter { NextResult = @"C:\elsewhere\explicit.png" };
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(
            new FakeWorkflowExportAdapter(), saveAs, delivery);

        await session.SaveAsAsync();
        var result = await session.OpenWithAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\elsewhere\explicit.png", delivery.LastOpenWithPath);
    }

    [Fact]
    public async Task OpenWith_AfterNewCapture_BecomesUnavailableAgain()
    {
        var delivery = new FakeWorkflowDeliveryAdapter();
        var export = new FakeWorkflowExportAdapter();
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
        };
        var session = WorkflowDeliveryTestHarness.CreateSession(export, new FakeSaveAsDialogAdapter(), delivery, capture);
        await session.CaptureFullDesktopAsync();
        await session.SaveAsync();

        capture.NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 1);
        await session.CaptureFullDesktopAsync();

        Assert.False(session.HasSavedFile);
        var result = await session.OpenWithAsync();
        Assert.Equal(WorkflowExportStatus.Failed, result.Status);
        Assert.Equal(0, delivery.OpenWithCallCount);
    }

    [Fact]
    public async Task OpenWith_AdapterFailure_IsRetryableAndPreservesFrameAndIdentity()
    {
        var export = new FakeWorkflowExportAdapter();
        var frame = ExportTestHelpers.CreateTestBitmap(7, 3);
        var delivery = new FakeWorkflowDeliveryAdapter
        {
            OpenWithImpl = (_, _) => Task.FromResult(DeliveryResult.Fail(
                "No Windows application is registered to open .png files.",
                TimeSpan.FromMilliseconds(1).TotalMilliseconds)),
        };
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), delivery, frame);
        await session.SaveAsync();
        var savedPath = session.LastSavedFilePath;

        var failed = await session.OpenWithAsync();

        Assert.Equal(WorkflowExportStatus.Failed, failed.Status);
        Assert.Contains("registered", failed.Error);
        // The composed Frame and the saved-file identity survive for retry.
        Assert.Same(frame, session.LastFrame);
        Assert.Equal(savedPath, session.LastSavedFilePath);

        delivery.OpenWithImpl = null;
        var retried = await session.OpenWithAsync();
        Assert.True(retried.IsSuccess);
    }

    [Fact]
    public async Task OpenWith_AdapterCancelled_PreservesFrameAndIdentityForRetry()
    {
        var export = new FakeWorkflowExportAdapter();
        var delivery = new FakeWorkflowDeliveryAdapter
        {
            OpenWithImpl = (_, _) => Task.FromResult(DeliveryResult.Cancelled(
                TimeSpan.FromMilliseconds(1).TotalMilliseconds)),
        };
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), delivery);
        await session.SaveAsync();

        var result = await session.OpenWithAsync();

        Assert.Equal(WorkflowExportStatus.Cancelled, result.Status);
        Assert.NotNull(session.LastFrame);
        Assert.True(session.HasSavedFile);

        delivery.OpenWithImpl = null;
        var retried = await session.OpenWithAsync();
        Assert.True(retried.IsSuccess);
    }

    [Fact]
    public async Task OpenWith_DuringInFlightSave_ReturnsOperationInProgress()
    {
        var saveStarted = new TaskCompletionSource<bool>();
        var saveRelease = new TaskCompletionSource<bool>();
        var export = new FakeWorkflowExportAdapter
        {
            SaveDefaultImpl = (_, _) =>
            {
                saveStarted.TrySetResult(true);
                saveRelease.Task.Wait();
                return ExportResult.Ok(@"C:\captcho\default.png", 8, 4, 4, TimeSpan.FromMilliseconds(1));
            },
        };
        var delivery = new FakeWorkflowDeliveryAdapter();
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), delivery);

        var inFlight = session.SaveAsync();
        await saveStarted.Task;

        var result = await session.OpenWithAsync();

        Assert.Equal(WorkflowExportStatus.OperationInProgress, result.Status);
        Assert.Equal(0, delivery.OpenWithCallCount);

        saveRelease.SetResult(true);
        await inFlight;
    }

    [Fact]
    public async Task OpenWith_ExportsNothingThroughTheExportAdapter()
    {
        var export = new FakeWorkflowExportAdapter();
        var delivery = new FakeWorkflowDeliveryAdapter();
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), delivery);
        await session.SaveAsync();

        await session.OpenWithAsync();

        // Delivery is read-only with respect to files: no writes, no clipboard.
        Assert.Equal(1, export.SaveDefaultCallCount);
        Assert.Equal(0, export.SaveToCallCount);
        Assert.Equal(0, export.CopyImageCallCount);
        Assert.Equal(0, export.CopyTextCallCount);
    }
}

// ── Share action tests ────────────────────────────────────────────────────

public class WorkflowShareTests
{
    [Fact]
    public async Task Share_BeforeAnySave_IsUnavailable()
    {
        var delivery = new FakeWorkflowDeliveryAdapter();
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(
            new FakeWorkflowExportAdapter(), new FakeSaveAsDialogAdapter(), delivery);

        var result = await session.ShareAsync();

        Assert.Equal(WorkflowExportAction.Share, result.Action);
        Assert.Equal(WorkflowExportStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(0, delivery.ShareCallCount);
    }

    [Fact]
    public async Task Share_AfterSave_SharesTheSavedFileThroughTheAdapter()
    {
        var export = new FakeWorkflowExportAdapter();
        var delivery = new FakeWorkflowDeliveryAdapter();
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), delivery);
        await session.SaveAsync();

        var result = await session.ShareAsync();

        Assert.Equal(WorkflowExportStatus.Succeeded, result.Status);
        Assert.Equal(1, delivery.ShareCallCount);
        Assert.Equal(export.DefaultPath, delivery.LastSharePath);
        Assert.Equal(export.DefaultPath, result.FilePath);
    }

    [Fact]
    public async Task Share_AfterSaveAsOnly_UsesTheSaveAsPath()
    {
        var delivery = new FakeWorkflowDeliveryAdapter();
        var saveAs = new FakeSaveAsDialogAdapter { NextResult = @"C:\elsewhere\explicit.png" };
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(
            new FakeWorkflowExportAdapter(), saveAs, delivery);

        await session.SaveAsAsync();
        var result = await session.ShareAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\elsewhere\explicit.png", delivery.LastSharePath);
    }

    [Fact]
    public async Task Share_AfterNewCapture_BecomesUnavailableAgain()
    {
        var delivery = new FakeWorkflowDeliveryAdapter();
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
        };
        var session = WorkflowDeliveryTestHarness.CreateSession(
            new FakeWorkflowExportAdapter(), new FakeSaveAsDialogAdapter(), delivery, capture);
        await session.CaptureFullDesktopAsync();
        await session.SaveAsync();

        capture.NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 1);
        await session.CaptureFullDesktopAsync();

        var result = await session.ShareAsync();
        Assert.Equal(WorkflowExportStatus.Failed, result.Status);
        Assert.Equal(0, delivery.ShareCallCount);
    }

    [Fact]
    public async Task Share_AdapterFailure_IsRetryableAndPreservesFrameAndIdentity()
    {
        var export = new FakeWorkflowExportAdapter();
        var frame = ExportTestHelpers.CreateTestBitmap(6, 2);
        var delivery = new FakeWorkflowDeliveryAdapter
        {
            ShareImpl = (_, _) => Task.FromResult(DeliveryResult.Fail(
                "Windows sharing is not supported on this system.",
                TimeSpan.FromMilliseconds(1).TotalMilliseconds)),
        };
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), delivery, frame);
        await session.SaveAsync();
        var savedPath = session.LastSavedFilePath;

        var failed = await session.ShareAsync();

        Assert.Equal(WorkflowExportStatus.Failed, failed.Status);
        Assert.Contains("not supported", failed.Error);
        Assert.Same(frame, session.LastFrame);
        Assert.Equal(savedPath, session.LastSavedFilePath);

        delivery.ShareImpl = null;
        var retried = await session.ShareAsync();
        Assert.True(retried.IsSuccess);
    }

    [Fact]
    public async Task Share_AdapterCancelled_PreservesFrameAndIdentityForRetry()
    {
        var delivery = new FakeWorkflowDeliveryAdapter
        {
            ShareImpl = (_, _) => Task.FromResult(DeliveryResult.Cancelled(
                TimeSpan.FromMilliseconds(1).TotalMilliseconds)),
        };
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(
            new FakeWorkflowExportAdapter(), new FakeSaveAsDialogAdapter(), delivery);
        await session.SaveAsync();

        var result = await session.ShareAsync();

        Assert.Equal(WorkflowExportStatus.Cancelled, result.Status);
        Assert.True(session.HasSavedFile);

        delivery.ShareImpl = null;
        var retried = await session.ShareAsync();
        Assert.True(retried.IsSuccess);
    }

    [Fact]
    public async Task Share_DuringInFlightSave_ReturnsOperationInProgress()
    {
        var saveStarted = new TaskCompletionSource<bool>();
        var saveRelease = new TaskCompletionSource<bool>();
        var export = new FakeWorkflowExportAdapter
        {
            SaveDefaultImpl = (_, _) =>
            {
                saveStarted.TrySetResult(true);
                saveRelease.Task.Wait();
                return ExportResult.Ok(@"C:\captcho\default.png", 8, 4, 4, TimeSpan.FromMilliseconds(1));
            },
        };
        var delivery = new FakeWorkflowDeliveryAdapter();
        var session = await WorkflowDeliveryTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), delivery);

        var inFlight = session.SaveAsync();
        await saveStarted.Task;

        var result = await session.ShareAsync();

        Assert.Equal(WorkflowExportStatus.OperationInProgress, result.Status);
        Assert.Equal(0, delivery.ShareCallCount);

        saveRelease.SetResult(true);
        await inFlight;
    }
}

// ── Unconfigured (null-object) delivery wiring ────────────────────────────

public class WorkflowDeliveryUnconfiguredTests
{
    [Fact]
    public async Task Session_WithoutDeliveryWiring_OpenWithFailsCleanly()
    {
        var session = WorkflowExportTestHarness.CreateSession(
            new FakeWorkflowExportAdapter(), new FakeSaveAsDialogAdapter());

        var result = await session.OpenWithAsync();

        Assert.Equal(WorkflowExportStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Session_WithoutDeliveryWiring_ShareFailsCleanly()
    {
        var session = WorkflowExportTestHarness.CreateSession(
            new FakeWorkflowExportAdapter(), new FakeSaveAsDialogAdapter());

        var result = await session.ShareAsync();

        Assert.Equal(WorkflowExportStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }
}

// ── Status formatter tests (delivery actions) ─────────────────────────────

public class WorkflowDeliveryFormatterTests
{
    [Fact]
    public void FormatStatus_OpenWithSuccess_ShowsOpenedFile()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.OpenWith,
            Status = WorkflowExportStatus.Succeeded,
            FilePath = @"C:\users\test\Pictures\captcho\shot.png",
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("shot.png", status);
        Assert.Contains("Opened", status);
    }

    [Fact]
    public void FormatStatus_ShareSuccess_ShowsSharedFile()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Share,
            Status = WorkflowExportStatus.Succeeded,
            FilePath = @"C:\users\test\Pictures\captcho\shot.png",
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("shot.png", status);
        Assert.Contains("Shar", status);
    }

    [Fact]
    public void FormatStatus_OpenWithFailure_ShowsOpenWithFailed()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.OpenWith,
            Status = WorkflowExportStatus.Failed,
            Error = "No Windows application is registered to open .png files.",
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Open With failed", status);
        Assert.Contains("registered", status);
    }

    [Fact]
    public void FormatStatus_ShareFailure_ShowsShareFailed()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Share,
            Status = WorkflowExportStatus.Failed,
            Error = "Windows sharing is not supported on this system.",
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Share failed", status);
    }

    [Fact]
    public void FormatStatus_OpenWithCancelled_ShowsCancelled()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.OpenWith,
            Status = WorkflowExportStatus.Cancelled,
        };
        Assert.Equal("Open With cancelled.", ExportStatusFormatter.FormatStatus(result));
    }

    [Fact]
    public void FormatStatus_ShareCancelled_ShowsCancelled()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Share,
            Status = WorkflowExportStatus.Cancelled,
        };
        Assert.Equal("Share cancelled.", ExportStatusFormatter.FormatStatus(result));
    }

    [Fact]
    public void FormatTiming_DeliveryActions_ShowsElapsedMs()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Share,
            Status = WorkflowExportStatus.Succeeded,
            ElapsedMs = 30.2,
        };
        Assert.Contains("30.2ms", ExportStatusFormatter.FormatTiming(result));
    }
}

// ── Production adapter tests (headless-safe paths only) ───────────────────

public class WindowsDeliveryAdapterTests
{
    [Fact]
    public async Task OpenWith_MissingFile_FailsWithoutThrowing()
    {
        var adapter = new WindowsDeliveryAdapter(() => IntPtr.Zero);
        var missing = Path.Combine(Path.GetTempPath(), $"captcho_missing_{Guid.NewGuid():N}.png");

        var result = await adapter.OpenWithAsync(missing, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task OpenWith_ExtensionlessPath_FailsValidation()
    {
        var adapter = new WindowsDeliveryAdapter(() => IntPtr.Zero);
        var missing = Path.Combine(Path.GetTempPath(), $"captcho_noext_{Guid.NewGuid():N}");

        var result = await adapter.OpenWithAsync(missing, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task OpenWith_CancelledToken_ReturnsCancelledWithoutPlatformCalls()
    {
        var adapter = new WindowsDeliveryAdapter(() => IntPtr.Zero);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await adapter.OpenWithAsync(@"C:\captcho\default.png", cts.Token);

        Assert.Equal(DeliveryStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Share_CancelledToken_ReturnsCancelledWithoutPlatformCalls()
    {
        var adapter = new WindowsDeliveryAdapter(() => IntPtr.Zero);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await adapter.ShareAsync(@"C:\captcho\default.png", cts.Token);

        Assert.Equal(DeliveryStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Share_MissingWindowHandle_FailsWithoutShowingShareUI()
    {
        var adapter = new WindowsDeliveryAdapter(() => IntPtr.Zero);

        var result = await adapter.ShareAsync(@"C:\captcho\default.png", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }
}
