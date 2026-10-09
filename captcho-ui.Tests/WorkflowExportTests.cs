// WorkflowExportTests.cs — Production-side tests for the Export workflow
// actions (Save, Save As, Copy Frame, Copy Path) on the runtime session.
//
// These tests exercise the real CaptureWorkflowSession Export surface through
// fake platform adapters, and the real WorkflowExportAdapter against temp
// directories. They are the production-side specification for issue #44:
// Save writes a PNG using the configured destination and Filename Template and
// records the default saved-file identity; repeating Save (including after an
// automatic save routes through the same action) refers to that same file
// rather than creating accidental duplicates; Save As explicitly chooses
// another location/name without replacing the default identity; Copy Frame
// places image content on the clipboard without creating a file; Copy Path is
// unavailable until a valid saved file exists; and cancellation and failures
// stay retryable.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Fake Export adapters ─────────────────────────────────────────────────

/// <summary>
/// Fake Export adapter. Records calls per action and delegates each to a
/// configurable implementation. Defaults succeed against a virtual default
/// path so identity tests need no file system.
/// </summary>
internal sealed class FakeWorkflowExportAdapter : IWorkflowExportAdapter
{
    public string DefaultPath { get; set; } = @"C:\captcho\default.png";

    public Func<ContiguousBitmap, CancellationToken, ExportResult>? SaveDefaultImpl { get; set; }
    public Func<ContiguousBitmap, string, CancellationToken, ExportResult>? SaveToImpl { get; set; }
    public Func<ContiguousBitmap, ClipboardExportResult>? CopyImageImpl { get; set; }
    public Func<string, ClipboardExportResult>? CopyTextImpl { get; set; }

    public int SaveDefaultCallCount { get; private set; }
    public int SaveToCallCount { get; private set; }
    public int CopyImageCallCount { get; private set; }
    public int CopyTextCallCount { get; private set; }

    public ContiguousBitmap? LastSaveDefaultFrame { get; private set; }
    public ContiguousBitmap? LastSaveToFrame { get; private set; }
    public string? LastSaveToPath { get; private set; }
    public ContiguousBitmap? LastCopyImageFrame { get; private set; }
    public string? LastCopyText { get; private set; }

    public ExportResult SaveDefault(ContiguousBitmap frame, CancellationToken cancellationToken)
    {
        SaveDefaultCallCount++;
        LastSaveDefaultFrame = frame;
        return SaveDefaultImpl != null
            ? SaveDefaultImpl(frame, cancellationToken)
            : ExportResult.Ok(DefaultPath, frame.Width, frame.Height, 4, TimeSpan.FromMilliseconds(1));
    }

    public ExportResult SaveTo(ContiguousBitmap frame, string destinationPath, CancellationToken cancellationToken)
    {
        SaveToCallCount++;
        LastSaveToFrame = frame;
        LastSaveToPath = destinationPath;
        return SaveToImpl != null
            ? SaveToImpl(frame, destinationPath, cancellationToken)
            : ExportResult.Ok(destinationPath, frame.Width, frame.Height, 4, TimeSpan.FromMilliseconds(1));
    }

    public ClipboardExportResult CopyImage(ContiguousBitmap frame)
    {
        CopyImageCallCount++;
        LastCopyImageFrame = frame;
        return CopyImageImpl != null
            ? CopyImageImpl(frame)
            : ClipboardExportResult.Ok(frame.Width, frame.Height, 64, TimeSpan.FromMilliseconds(1));
    }

    public ClipboardExportResult CopyText(string text)
    {
        CopyTextCallCount++;
        LastCopyText = text;
        return CopyTextImpl != null
            ? CopyTextImpl(text)
            : ClipboardExportResult.TextOk(text.Length, TimeSpan.FromMilliseconds(1));
    }
}

/// <summary>
/// Fake Save As dialog adapter. Returns a configurable chosen path, or null
/// for cancellation, and records call count.
/// </summary>
internal sealed class FakeSaveAsDialogAdapter : ISaveAsDialogAdapter
{
    public int CallCount { get; private set; }
    public string? NextResult { get; set; }

    public Task<string?> ShowAsync()
    {
        CallCount++;
        return Task.FromResult(NextResult);
    }
}

// ── Shared harness ───────────────────────────────────────────────────────

internal static class WorkflowExportTestHarness
{
    /// <summary>
    /// Creates a session wired for Export tests: fake Export/Save-As adapters,
    /// annotation disabled (that gate has its own coverage), and default
    /// success for the Full Desktop Capture route. Delivery (Open With /
    /// Share, issue #46) stays unwired — covered by WorkflowDeliveryTests.
    /// </summary>
    public static CaptureWorkflowSession<object> CreateSession(
        FakeWorkflowExportAdapter export,
        FakeSaveAsDialogAdapter saveAsDialog,
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
            saveAsDialog);
    }

    /// <summary>
    /// Creates a session and performs one successful Full Desktop Capture
    /// producing the supplied Frame, so Export actions have a session-owned
    /// Frame to act on.
    /// </summary>
    public static async Task<CaptureWorkflowSession<object>> CaptureAsync(
        FakeWorkflowExportAdapter export,
        FakeSaveAsDialogAdapter saveAsDialog,
        ContiguousBitmap frame)
    {
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(frame, $"{frame.Width}×{frame.Height}", 1),
        };
        var session = CreateSession(export, saveAsDialog, capture);
        var result = await session.CaptureFullDesktopAsync();
        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        return session;
    }

    public static Task<CaptureWorkflowSession<object>> CaptureAsync(
        FakeWorkflowExportAdapter export,
        FakeSaveAsDialogAdapter saveAsDialog) =>
        CaptureAsync(export, saveAsDialog, ExportTestHelpers.CreateTestBitmap(8, 4));
}

// ── Save action tests ────────────────────────────────────────────────────

public class WorkflowExportSaveTests
{
    [Fact]
    public async Task Save_BeforeAnyCapture_ReturnsNoFrame()
    {
        var export = new FakeWorkflowExportAdapter();
        var session = WorkflowExportTestHarness.CreateSession(export, new FakeSaveAsDialogAdapter());

        var result = await session.SaveAsync();

        Assert.Equal(WorkflowExportAction.Save, result.Action);
        Assert.Equal(WorkflowExportStatus.NoFrame, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, export.SaveDefaultCallCount);
        Assert.Null(session.DefaultSavedFilePath);
    }

    [Fact]
    public async Task Save_AfterCapture_SavesThroughExportAdapterAndRecordsDefaultIdentity()
    {
        var export = new FakeWorkflowExportAdapter();
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());

        var result = await session.SaveAsync();

        Assert.Equal(WorkflowExportStatus.Succeeded, result.Status);
        Assert.Equal(1, export.SaveDefaultCallCount);
        Assert.Equal(0, export.SaveToCallCount);
        Assert.Equal(export.DefaultPath, result.FilePath);
        Assert.Equal(export.DefaultPath, session.DefaultSavedFilePath);
        Assert.Equal(export.DefaultPath, session.LastSavedFilePath);
        Assert.True(session.HasSavedFile);
    }

    [Fact]
    public async Task Save_ExportsTheSessionOwnedFrame()
    {
        var frame = ExportTestHelpers.CreateTestBitmap(8, 4);
        var export = new FakeWorkflowExportAdapter();
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), frame);

        await session.SaveAsync();

        Assert.Same(frame, export.LastSaveDefaultFrame);
        Assert.Same(frame, session.LastFrame);
    }

    [Fact]
    public async Task Save_Repeated_RefersToSameDefaultFileWithoutDuplicates()
    {
        var export = new FakeWorkflowExportAdapter();
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());

        var first = await session.SaveAsync();
        var second = await session.SaveAsync();

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        // The identity was computed once by the first Save; the repeat writes
        // to that same path instead of expanding the template again.
        Assert.Equal(1, export.SaveDefaultCallCount);
        Assert.Equal(1, export.SaveToCallCount);
        Assert.Equal(export.DefaultPath, export.LastSaveToPath);
        Assert.Equal(export.DefaultPath, second.FilePath);
        Assert.Equal(session.DefaultSavedFilePath, second.FilePath);
    }

    [Fact]
    public async Task Save_AfterSaveAs_StillCreatesAndUsesDefaultIdentity()
    {
        var export = new FakeWorkflowExportAdapter();
        var saveAs = new FakeSaveAsDialogAdapter { NextResult = @"C:\elsewhere\explicit.png" };
        var session = await WorkflowExportTestHarness.CaptureAsync(export, saveAs);

        await session.SaveAsAsync();
        var save = await session.SaveAsync();

        Assert.True(save.IsSuccess);
        Assert.Equal(export.DefaultPath, save.FilePath);
        Assert.Equal(export.DefaultPath, session.DefaultSavedFilePath);
        // Save As created its own file; Save still produced the default one.
        Assert.Equal(1, export.SaveDefaultCallCount);
        Assert.Equal(1, export.SaveToCallCount);
    }

    [Fact]
    public async Task Save_Failure_IsRetryableAndRecordsNoIdentity()
    {
        var export = new FakeWorkflowExportAdapter
        {
            SaveDefaultImpl = (_, _) => ExportResult.Fail(ExportPhase.Write, "Disk unavailable.", TimeSpan.FromMilliseconds(1)),
        };
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());

        var failed = await session.SaveAsync();

        Assert.Equal(WorkflowExportStatus.Failed, failed.Status);
        Assert.Contains("Disk unavailable.", failed.Error);
        Assert.Null(session.DefaultSavedFilePath);

        // Retry succeeds and records the identity.
        export.SaveDefaultImpl = null;
        var retried = await session.SaveAsync();
        Assert.True(retried.IsSuccess);
        Assert.Equal(export.DefaultPath, session.DefaultSavedFilePath);
    }

    [Fact]
    public async Task Save_Cancelled_MapsToCancelledStatus()
    {
        var export = new FakeWorkflowExportAdapter
        {
            SaveDefaultImpl = (_, _) => ExportResult.Cancelled(TimeSpan.FromMilliseconds(1)),
        };
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());

        var result = await session.SaveAsync();

        Assert.Equal(WorkflowExportStatus.Cancelled, result.Status);
        Assert.Null(session.DefaultSavedFilePath);
    }

    [Fact]
    public async Task Save_AfterNewCapture_ResetsSavedFileIdentity()
    {
        var export = new FakeWorkflowExportAdapter();
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
        };
        var session = WorkflowExportTestHarness.CreateSession(export, new FakeSaveAsDialogAdapter(), capture);
        await session.CaptureFullDesktopAsync();
        await session.SaveAsync();
        Assert.NotNull(session.DefaultSavedFilePath);

        capture.NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 1);
        await session.CaptureFullDesktopAsync();

        Assert.Null(session.DefaultSavedFilePath);
        Assert.Null(session.LastSavedFilePath);
        Assert.False(session.HasSavedFile);
        // The next Save computes a fresh default identity for the new Frame.
        await session.SaveAsync();
        Assert.Equal(2, export.SaveDefaultCallCount);
    }
}

// ── Save As action tests ─────────────────────────────────────────────────

public class WorkflowExportSaveAsTests
{
    [Fact]
    public async Task SaveAs_BeforeAnyCapture_ReturnsNoFrameWithoutShowingDialog()
    {
        var export = new FakeWorkflowExportAdapter();
        var saveAs = new FakeSaveAsDialogAdapter { NextResult = @"C:\elsewhere\explicit.png" };
        var session = WorkflowExportTestHarness.CreateSession(export, saveAs);

        var result = await session.SaveAsAsync();

        Assert.Equal(WorkflowExportAction.SaveAs, result.Action);
        Assert.Equal(WorkflowExportStatus.NoFrame, result.Status);
        Assert.Equal(0, saveAs.CallCount);
        Assert.Equal(0, export.SaveToCallCount);
    }

    [Fact]
    public async Task SaveAs_DialogCancelled_ReturnsCancelledWithNoSave()
    {
        var export = new FakeWorkflowExportAdapter();
        var saveAs = new FakeSaveAsDialogAdapter { NextResult = null };
        var session = await WorkflowExportTestHarness.CaptureAsync(export, saveAs);

        var result = await session.SaveAsAsync();

        Assert.Equal(WorkflowExportStatus.Cancelled, result.Status);
        Assert.Equal(1, saveAs.CallCount);
        Assert.Equal(0, export.SaveToCallCount);
        Assert.False(session.HasSavedFile);
    }

    [Fact]
    public async Task SaveAs_ChosenPath_CreatesSecondFileWithoutReplacingDefaultIdentity()
    {
        var export = new FakeWorkflowExportAdapter();
        var saveAs = new FakeSaveAsDialogAdapter { NextResult = @"C:\elsewhere\explicit.png" };
        var session = await WorkflowExportTestHarness.CaptureAsync(export, saveAs);
        await session.SaveAsync();
        Assert.Equal(export.DefaultPath, session.DefaultSavedFilePath);

        var result = await session.SaveAsAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\elsewhere\explicit.png", result.FilePath);
        Assert.Equal(@"C:\elsewhere\explicit.png", export.LastSaveToPath);
        Assert.Equal(@"C:\elsewhere\explicit.png", session.LastSavedFilePath);
        // The default identity is intentionally not replaced by Save As.
        Assert.Equal(export.DefaultPath, session.DefaultSavedFilePath);
    }

    [Fact]
    public async Task SaveAs_Failure_KeepsLastSavedFileUnchanged()
    {
        var export = new FakeWorkflowExportAdapter();
        var saveAs = new FakeSaveAsDialogAdapter { NextResult = @"C:\elsewhere\explicit.png" };
        var session = await WorkflowExportTestHarness.CaptureAsync(export, saveAs);
        await session.SaveAsync();

        export.SaveToImpl = (_, _, _) => ExportResult.Fail(ExportPhase.Write, "Access denied.", TimeSpan.FromMilliseconds(1));
        var result = await session.SaveAsAsync();

        Assert.Equal(WorkflowExportStatus.Failed, result.Status);
        Assert.Contains("Access denied.", result.Error);
        Assert.Equal(export.DefaultPath, session.LastSavedFilePath);
    }

    [Fact]
    public async Task SaveAs_ExportsTheSessionOwnedFrame()
    {
        var frame = ExportTestHelpers.CreateTestBitmap(6, 6);
        var export = new FakeWorkflowExportAdapter();
        var saveAs = new FakeSaveAsDialogAdapter { NextResult = @"C:\elsewhere\explicit.png" };
        var session = await WorkflowExportTestHarness.CaptureAsync(export, saveAs, frame);

        await session.SaveAsAsync();

        Assert.Same(frame, export.LastSaveToFrame);
    }
}

// ── Copy Frame action tests ──────────────────────────────────────────────

public class WorkflowExportCopyFrameTests
{
    [Fact]
    public async Task CopyFrame_BeforeAnyCapture_ReturnsNoFrame()
    {
        var export = new FakeWorkflowExportAdapter();
        var session = WorkflowExportTestHarness.CreateSession(export, new FakeSaveAsDialogAdapter());

        var result = await session.CopyFrameAsync();

        Assert.Equal(WorkflowExportAction.CopyFrame, result.Action);
        Assert.Equal(WorkflowExportStatus.NoFrame, result.Status);
        Assert.Equal(0, export.CopyImageCallCount);
    }

    [Fact]
    public async Task CopyFrame_PlacesImageContentOnClipboardWithoutCreatingAFile()
    {
        var export = new FakeWorkflowExportAdapter();
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());

        var result = await session.CopyFrameAsync();

        Assert.Equal(WorkflowExportStatus.Succeeded, result.Status);
        Assert.Equal(8, result.Width);
        Assert.Equal(4, result.Height);
        Assert.Equal(1, export.CopyImageCallCount);
        // Clipboard-only export: no file actions of any kind.
        Assert.Equal(0, export.SaveDefaultCallCount);
        Assert.Equal(0, export.SaveToCallCount);
        Assert.Equal(0, export.CopyTextCallCount);
        Assert.False(session.HasSavedFile);
        Assert.Null(session.LastSavedFilePath);
    }

    [Fact]
    public async Task CopyFrame_CopiesTheSessionOwnedFrame()
    {
        var frame = ExportTestHelpers.CreateTestBitmap(5, 7);
        var export = new FakeWorkflowExportAdapter();
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter(), frame);

        await session.CopyFrameAsync();

        Assert.Same(frame, export.LastCopyImageFrame);
    }

    [Fact]
    public async Task CopyFrame_ClipboardFailure_IsRetryable()
    {
        var export = new FakeWorkflowExportAdapter
        {
            CopyImageImpl = _ => ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.FromMilliseconds(1)),
        };
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());

        var failed = await session.CopyFrameAsync();

        Assert.Equal(WorkflowExportStatus.Failed, failed.Status);
        Assert.Contains("Failed to set clipboard content", failed.Error);

        export.CopyImageImpl = null;
        var retried = await session.CopyFrameAsync();
        Assert.True(retried.IsSuccess);
    }
}

// ── Copy Path action tests ───────────────────────────────────────────────

public class WorkflowExportCopyPathTests
{
    [Fact]
    public async Task CopyPath_BeforeAnySave_IsUnavailable()
    {
        var export = new FakeWorkflowExportAdapter();
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());

        var result = await session.CopyPathAsync();

        Assert.Equal(WorkflowExportAction.CopyPath, result.Action);
        Assert.Equal(WorkflowExportStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal(0, export.CopyTextCallCount);
    }

    [Fact]
    public async Task CopyPath_AfterSave_CopiesTheSavedFilePath()
    {
        var export = new FakeWorkflowExportAdapter();
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());
        await session.SaveAsync();

        var result = await session.CopyPathAsync();

        Assert.Equal(WorkflowExportStatus.Succeeded, result.Status);
        Assert.Equal(1, export.CopyTextCallCount);
        Assert.Equal(export.DefaultPath, export.LastCopyText);
    }

    [Fact]
    public async Task CopyPath_AfterSaveAsOnly_CopiesTheSaveAsPath()
    {
        var export = new FakeWorkflowExportAdapter();
        var saveAs = new FakeSaveAsDialogAdapter { NextResult = @"C:\elsewhere\explicit.png" };
        var session = await WorkflowExportTestHarness.CaptureAsync(export, saveAs);

        await session.SaveAsAsync();
        var result = await session.CopyPathAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\elsewhere\explicit.png", export.LastCopyText);
    }

    [Fact]
    public async Task CopyPath_AfterNewCapture_BecomesUnavailableAgain()
    {
        var export = new FakeWorkflowExportAdapter();
        var capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
        };
        var session = WorkflowExportTestHarness.CreateSession(export, new FakeSaveAsDialogAdapter(), capture);
        await session.CaptureFullDesktopAsync();
        await session.SaveAsync();
        Assert.True(await CopyPathSucceeds(session));

        capture.NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(4, 4), "4×4", 1);
        await session.CaptureFullDesktopAsync();

        Assert.False(session.HasSavedFile);
        var result = await session.CopyPathAsync();
        Assert.Equal(WorkflowExportStatus.Failed, result.Status);
    }

    private static async Task<bool> CopyPathSucceeds(CaptureWorkflowSession<object> session)
    {
        var result = await session.CopyPathAsync();
        return result.IsSuccess;
    }
}

// ── Operation guard tests ────────────────────────────────────────────────

public class WorkflowExportOperationGuardTests
{
    [Fact]
    public async Task Save_DuringInFlightCapture_ReturnsOperationInProgress()
    {
        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var capture = new SlowGateCaptureAdapter(
            started, release,
            new FakeCaptureAdapter
            {
                NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(2, 2), "2×2", 0),
            });
        var export = new FakeWorkflowExportAdapter();
        var session = WorkflowExportTestHarness.CreateSession(export, new FakeSaveAsDialogAdapter(), capture);

        var inFlight = session.CaptureFullDesktopAsync();
        await started.Task;

        var result = await session.SaveAsync();

        Assert.Equal(WorkflowExportStatus.OperationInProgress, result.Status);
        Assert.Equal(0, export.SaveDefaultCallCount);

        release.SetResult(true);
        await inFlight;
    }

    [Fact]
    public async Task CopyFrame_DuringInFlightSave_ReturnsOperationInProgress()
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
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());

        var inFlight = session.SaveAsync();
        await saveStarted.Task;

        var result = await session.CopyFrameAsync();
        Assert.Equal(WorkflowExportStatus.OperationInProgress, result.Status);
        Assert.Equal(0, export.CopyImageCallCount);

        saveRelease.SetResult(true);
        await inFlight;
    }

    [Fact]
    public async Task AfterExportCompletes_SessionAcceptsNextAction()
    {
        var export = new FakeWorkflowExportAdapter();
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());

        await session.SaveAsync();
        var copy = await session.CopyFrameAsync();

        Assert.True(copy.IsSuccess);
        Assert.Equal(1, export.CopyImageCallCount);
    }
}

// ── Legacy-frame adoption tests ──────────────────────────────────────────

public class WorkflowExportFrameAdoptionTests
{
    [Fact]
    public async Task AdoptFrame_MakesLegacyFrameExportableAndResetsIdentity()
    {
        var export = new FakeWorkflowExportAdapter();
        var session = await WorkflowExportTestHarness.CaptureAsync(export, new FakeSaveAsDialogAdapter());
        await session.SaveAsync();
        Assert.NotNull(session.DefaultSavedFilePath);

        var legacyFrame = ExportTestHelpers.CreateTestBitmap(3, 3);
        session.AdoptFrame(legacyFrame);

        Assert.Same(legacyFrame, session.LastFrame);
        Assert.Null(session.DefaultSavedFilePath);
        Assert.Null(session.LastSavedFilePath);

        var result = await session.SaveAsync();
        Assert.True(result.IsSuccess);
        Assert.Same(legacyFrame, export.LastSaveDefaultFrame);
    }

    [Fact]
    public void AdoptFrame_NullFrame_Throws()
    {
        var session = WorkflowExportTestHarness.CreateSession(
            new FakeWorkflowExportAdapter(), new FakeSaveAsDialogAdapter());
        Assert.Throws<ArgumentNullException>(() => session.AdoptFrame(null!));
    }
}

// ── Status formatter tests (WorkflowExportResult) ────────────────────────

public class WorkflowExportFormatterTests
{
    [Fact]
    public void FormatStatus_SaveSuccess_ShowsFilenameAndDimensions()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Save,
            Status = WorkflowExportStatus.Succeeded,
            FilePath = @"C:\users\test\Pictures\captcho\shot.png",
            Width = 1920,
            Height = 1080,
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("shot.png", status);
        Assert.Contains("1920×1080", status);
    }

    [Fact]
    public void FormatStatus_SaveAsSuccess_ShowsChosenFilename()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.SaveAs,
            Status = WorkflowExportStatus.Succeeded,
            FilePath = @"C:\elsewhere\explicit.png",
            Width = 8,
            Height = 4,
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("explicit.png", status);
    }

    [Fact]
    public void FormatStatus_CopyFrameSuccess_ShowsCopiedAndDimensions()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.CopyFrame,
            Status = WorkflowExportStatus.Succeeded,
            Width = 800,
            Height = 600,
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Copied to clipboard", status);
        Assert.Contains("800×600", status);
    }

    [Fact]
    public void FormatStatus_CopyPathSuccess_ShowsCopiedPath()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.CopyPath,
            Status = WorkflowExportStatus.Succeeded,
            FilePath = @"C:\captcho\default.png",
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Copied", status);
        Assert.Contains("path", status);
    }

    [Fact]
    public void FormatStatus_NoFrame_ShowsNoCapture()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Save,
            Status = WorkflowExportStatus.NoFrame,
        };
        Assert.Equal("No capture to export.", ExportStatusFormatter.FormatStatus(result));
    }

    [Fact]
    public void FormatStatus_SaveAsCancelled_ShowsSaveCancelled()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.SaveAs,
            Status = WorkflowExportStatus.Cancelled,
        };
        Assert.Equal("Save cancelled.", ExportStatusFormatter.FormatStatus(result));
    }

    [Fact]
    public void FormatStatus_SaveCancelled_ShowsExportCancelled()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Save,
            Status = WorkflowExportStatus.Cancelled,
        };
        Assert.Equal("Export cancelled.", ExportStatusFormatter.FormatStatus(result));
    }

    [Fact]
    public void FormatStatus_SaveFailure_ShowsSanitizedMessage()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Save,
            Status = WorkflowExportStatus.Failed,
            Error = "Access denied.",
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Export failed", status);
        Assert.Contains("Access denied.", status);
    }

    [Fact]
    public void FormatStatus_CopyFrameFailure_ShowsClipboardFailed()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.CopyFrame,
            Status = WorkflowExportStatus.Failed,
            Error = "Failed to set clipboard content",
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Clipboard failed", status);
    }

    [Fact]
    public void FormatStatus_CopyPathFailure_ShowsCopyPathFailed()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.CopyPath,
            Status = WorkflowExportStatus.Failed,
            Error = "No saved file to copy.",
        };
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Copy path failed", status);
        Assert.Contains("No saved file", status);
    }

    [Fact]
    public void FormatStatus_OperationInProgress_ShowsError()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Save,
            Status = WorkflowExportStatus.OperationInProgress,
            Error = "An operation is already in progress.",
        };
        Assert.Equal("An operation is already in progress.", ExportStatusFormatter.FormatStatus(result));
    }

    [Fact]
    public void FormatTiming_Save_ShowsExportMs()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Save,
            Status = WorkflowExportStatus.Succeeded,
            ElapsedMs = 25.3,
        };
        Assert.Contains("export 25.3ms", ExportStatusFormatter.FormatTiming(result));
    }

    [Fact]
    public void FormatTiming_CopyFrame_ShowsClipboardMs()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.CopyFrame,
            Status = WorkflowExportStatus.Succeeded,
            ElapsedMs = 12.7,
        };
        Assert.Contains("clipboard 12.7ms", ExportStatusFormatter.FormatTiming(result));
    }

    [Fact]
    public void FormatTiming_ZeroElapsed_ReturnsEmpty()
    {
        var result = new WorkflowExportResult
        {
            Action = WorkflowExportAction.Save,
            Status = WorkflowExportStatus.NoFrame,
        };
        Assert.Equal("", ExportStatusFormatter.FormatTiming(result));
    }
}

// ── Production adapter tests (real files, real encoding) ─────────────────

public class WorkflowExportAdapterTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"captcho_export_{Guid.NewGuid():N}");

    private static AppSettings SettingsFor(string dir, string template) => new()
    {
        SaveLocation = dir,
        FilenameTemplate = template,
    };

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void SaveDefault_WritesPngUsingConfiguredDestinationAndTemplate()
    {
        var dir = TempDir();
        try
        {
            var settings = SettingsFor(dir, "shot_<yyyy>");
            var adapter = new WorkflowExportAdapter(settings, new FakeClipboardAdapter());
            var frame = ExportTestHelpers.CreateTestBitmap(10, 6);

            var result = adapter.SaveDefault(frame, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(dir, Path.GetDirectoryName(result.DestinationPath));
            var name = Path.GetFileName(result.DestinationPath!);
            Assert.StartsWith("shot_", name, StringComparison.Ordinal);
            Assert.EndsWith(".png", name, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.DestinationPath));
            Assert.Equal(10, result.Width);
            Assert.Equal(6, result.Height);

            using var fs = File.OpenRead(result.DestinationPath!);
            var header = new byte[4];
            Assert.Equal(4, fs.Read(header, 0, 4));
            Assert.Equal(0x89, header[0]);
            Assert.Equal(0x50, header[1]); // P
            Assert.Equal(0x4E, header[2]); // N
            Assert.Equal(0x47, header[3]); // G
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void SaveDefault_FallsBackToDefaultLocationWhenUnconfigured()
    {
        // An empty Save Location resolves to the Pictures default directory;
        // assert only that the save succeeds and lands inside it.
        var settings = new AppSettings { SaveLocation = null, FilenameTemplate = "adapterfb" };
        var adapter = new WorkflowExportAdapter(settings, new FakeClipboardAdapter());
        var frame = ExportTestHelpers.CreateTestBitmap(2, 2);

        var result = adapter.SaveDefault(frame, CancellationToken.None);

        Assert.True(result.Success);
        Assert.StartsWith(
            ExportDefaults.DefaultSaveDirectory,
            result.DestinationPath,
            StringComparison.OrdinalIgnoreCase);
        try { if (File.Exists(result.DestinationPath)) File.Delete(result.DestinationPath!); } catch { }
    }

    [Fact]
    public void SaveDefault_ResolvesCollisionsWithoutOverwriting()
    {
        var dir = TempDir();
        try
        {
            var settings = SettingsFor(dir, "collision");
            var adapter = new WorkflowExportAdapter(settings, new FakeClipboardAdapter());
            var frame = ExportTestHelpers.CreateTestBitmap(2, 2);

            var first = adapter.SaveDefault(frame, CancellationToken.None);
            // Occupy the next collision slot so resolution must skip past it.
            var secondPath = Path.Combine(
                dir, $"collision_{2:D4}.png");
            File.WriteAllBytes(secondPath, new byte[] { 0x00 });

            var second = adapter.SaveDefault(frame, CancellationToken.None);

            Assert.NotEqual(first.DestinationPath, second.DestinationPath);
            Assert.NotEqual(secondPath, second.DestinationPath);
            Assert.True(File.Exists(second.DestinationPath));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void SaveTo_WritesPngToExplicitPath()
    {
        var dir = TempDir();
        try
        {
            var adapter = new WorkflowExportAdapter(SettingsFor(dir, "ignored"), new FakeClipboardAdapter());
            var frame = ExportTestHelpers.CreateTestBitmap(4, 4);
            var path = Path.Combine(dir, "explicit", "chosen.png");

            var result = adapter.SaveTo(frame, path, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(path, result.DestinationPath);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void SaveTo_CancelledToken_ReturnsCancelledAndWritesNoFile()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var adapter = new WorkflowExportAdapter(SettingsFor(dir, "x"), new FakeClipboardAdapter());
            var frame = ExportTestHelpers.CreateTestBitmap(4, 4);
            var path = Path.Combine(dir, "cancelled.png");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = adapter.SaveTo(frame, path, cts.Token);

            Assert.False(result.Success);
            Assert.Equal(ExportPhase.Cancelled, result.Phase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void CopyImage_PlacesPngContentOnClipboardWithoutCreatingAFile()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var clipboard = new FakeClipboardAdapter();
            var adapter = new WorkflowExportAdapter(SettingsFor(dir, "x"), clipboard);
            var frame = ExportTestHelpers.CreateTestBitmap(8, 8);

            var result = adapter.CopyImage(frame);

            Assert.True(result.Success);
            Assert.Equal(8, result.Width);
            Assert.Equal(8, result.Height);
            Assert.Equal(1, clipboard.CallCount);
            Assert.NotNull(clipboard.LastPngBytes);
            Assert.True(clipboard.LastPngBytes!.Length >= 8);
            Assert.Equal(0x89, clipboard.LastPngBytes[0]);
            Assert.Equal(0x50, clipboard.LastPngBytes[1]); // P
            Assert.Equal(0x4E, clipboard.LastPngBytes[2]); // N
            Assert.Equal(0x47, clipboard.LastPngBytes[3]); // G
            // Clipboard-only export created no file.
            Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void CopyText_PlacesTextOnClipboard()
    {
        var clipboard = new FakeClipboardAdapter();
        var adapter = new WorkflowExportAdapter(SettingsFor(TempDir(), "x"), clipboard);

        var result = adapter.CopyText(@"C:\captcho\default.png");

        Assert.True(result.Success);
        Assert.Equal(1, clipboard.TextCallCount);
        Assert.Equal(@"C:\captcho\default.png", clipboard.LastText);
    }

    [Fact]
    public void CopyText_ClipboardAdapterFails_ReturnsFailure()
    {
        var clipboard = new FakeClipboardAdapter { TextShouldSucceed = false };
        var adapter = new WorkflowExportAdapter(SettingsFor(TempDir(), "x"), clipboard);

        var result = adapter.CopyText(@"C:\captcho\default.png");

        Assert.False(result.Success);
        Assert.Contains("Failed to set clipboard content", result.Message);
    }

    [Fact]
    public void CopyImage_ClipboardAdapterFails_ReturnsFailure()
    {
        var clipboard = new FakeClipboardAdapter { ShouldSucceed = false };
        var adapter = new WorkflowExportAdapter(SettingsFor(TempDir(), "x"), clipboard);

        var result = adapter.CopyImage(ExportTestHelpers.CreateTestBitmap(2, 2));

        Assert.False(result.Success);
        Assert.Contains("Failed to set clipboard content", result.Message);
    }
}
