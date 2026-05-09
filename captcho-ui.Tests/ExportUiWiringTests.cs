// ExportUiWiringTests.cs — Tests for export UI wiring.
//
// Verifies the export flow through a testable mediator that mirrors
// MainWindow's export button handling logic without WinUI controls.
// Covers: enabled-state transitions, result-to-status formatting,
// picker-cancel/status behavior, no-capture guard, export failures,
// clipboard failures, concurrent-click protection, and cache interactions.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

/// <summary>
/// Testable mediator that mirrors MainWindow's export button handling logic.
/// Records state transitions so tests can assert on control flow.
/// Simulates Save, Save As…, and Copy to Clipboard operations.
/// </summary>
public class ExportFlowMediator
{
    // Injected dependencies (test doubles)
    private readonly Func<ExportResult> _saveToDefault;
    private readonly Func<string?, ExportResult> _saveToFile;
    private readonly Func<ClipboardExportResult> _copyToClipboard;

    // Simulated UI state
    public bool HasCapture { get; set; }
    public bool IsOperationRunning { get; private set; }
    public bool CaptureButtonsEnabled { get; private set; } = true;
    public bool ExportButtonsEnabled { get; private set; }
    public string? StatusText { get; private set; } = "Ready — choose a capture mode.";
    public string? TimingText { get; private set; } = "";

    /// <summary>
    /// Records whether export methods were called.
    /// </summary>
    public bool SaveToDefaultWasCalled { get; private set; }
    public bool SaveToFileWasCalled { get; private set; }
    public bool CopyToClipboardWasCalled { get; private set; }

    public ExportFlowMediator(
        Func<ExportResult>? saveToDefault = null,
        Func<string?, ExportResult>? saveToFile = null,
        Func<ClipboardExportResult>? copyToClipboard = null)
    {
        _saveToDefault = saveToDefault ?? (() => ExportResult.Ok("test.png", 100, 100, 1024, TimeSpan.FromMilliseconds(5)));
        _saveToFile = saveToFile ?? (path => ExportResult.Ok(path ?? "test.png", 100, 100, 1024, TimeSpan.FromMilliseconds(5)));
        _copyToClipboard = copyToClipboard ?? (() => ClipboardExportResult.Ok(100, 100, 1024, TimeSpan.FromMilliseconds(3)));
    }

    // ── Simulates capture result application ──────────────────────────

    /// <summary>
    /// Simulates a successful capture — sets HasCapture and enables export buttons.
    /// Mirrors MainWindow.ApplyCaptureResult for the success path.
    /// </summary>
    public void ApplySuccessfulCapture(string mode, string dimensions)
    {
        HasCapture = true;
        ExportButtonsEnabled = true;
        StatusText = $"{mode} — {dimensions}";
        UpdateExportButtonState();
    }

    /// <summary>
    /// Simulates a failed capture — does NOT change HasCapture.
    /// </summary>
    public void ApplyFailedCapture(string error)
    {
        StatusText = error;
        UpdateExportButtonState();
    }

    // ── Simulates export button clicks ───────────────────────────────

    /// <summary>
    /// Mirrors MainWindow.Save_Click — saves to default location.
    /// </summary>
    public async Task SaveClickAsync()
    {
        if (IsOperationRunning || !HasCapture)
        {
            StatusText = ExportStatusFormatter.FormatNoCapture();
            return;
        }

        EnterExportState();
        StatusText = "Saving…";

        try
        {
            var result = await Task.Run(() => _saveToDefault());
            StatusText = ExportStatusFormatter.FormatStatus(result);
            TimingText = ExportStatusFormatter.FormatTiming(result);
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            TimingText = "";
        }
        finally
        {
            ExitExportState();
        }
    }

    /// <summary>
    /// Mirrors MainWindow.SaveAs_Click with a simulated picker result.
    /// pickerResult: null = cancelled, string = chosen path.
    /// </summary>
    public async Task SaveAsClickAsync(string? pickerResult)
    {
        if (IsOperationRunning || !HasCapture)
        {
            StatusText = ExportStatusFormatter.FormatNoCapture();
            return;
        }

        EnterExportState();
        StatusText = "Choose save location…";

        try
        {
            if (pickerResult == null)
            {
                StatusText = ExportStatusFormatter.FormatPickerCancelled();
                TimingText = "";
                return;
            }

            StatusText = "Saving…";
            var result = await Task.Run(() => _saveToFile(pickerResult));
            SaveToFileWasCalled = true;
            StatusText = ExportStatusFormatter.FormatStatus(result);
            TimingText = ExportStatusFormatter.FormatTiming(result);
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
            TimingText = "";
        }
        finally
        {
            ExitExportState();
        }
    }

    /// <summary>
    /// Mirrors MainWindow.CopyToClipboard_Click.
    /// </summary>
    public async Task CopyClickAsync()
    {
        if (IsOperationRunning || !HasCapture)
        {
            StatusText = ExportStatusFormatter.FormatNoCapture();
            return;
        }

        EnterExportState();
        StatusText = "Copying to clipboard…";

        try
        {
            var result = await Task.Run(() => _copyToClipboard());
            CopyToClipboardWasCalled = true;
            StatusText = ExportStatusFormatter.FormatStatus(result);
            TimingText = ExportStatusFormatter.FormatTiming(result);
        }
        catch (Exception ex)
        {
            StatusText = $"Clipboard failed: {ex.Message}";
            TimingText = "";
        }
        finally
        {
            ExitExportState();
        }
    }

    /// <summary>
    /// Simulates entering capture state (buttons disabled).
    /// </summary>
    public void EnterCaptureState()
    {
        IsOperationRunning = true;
        CaptureButtonsEnabled = false;
        ExportButtonsEnabled = false;
    }

    /// <summary>
    /// Simulates exiting capture state (buttons re-enabled).
    /// </summary>
    public void ExitCaptureState()
    {
        IsOperationRunning = false;
        CaptureButtonsEnabled = true;
        UpdateExportButtonState();
    }

    // ── Private helpers ──────────────────────────────────────────────

    private void EnterExportState()
    {
        IsOperationRunning = true;
        CaptureButtonsEnabled = false;
        ExportButtonsEnabled = false;
    }

    private void ExitExportState()
    {
        IsOperationRunning = false;
        CaptureButtonsEnabled = true;
        UpdateExportButtonState();
    }

    private void UpdateExportButtonState()
    {
        ExportButtonsEnabled = HasCapture && !IsOperationRunning;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════

public class ExportUiWiringTests
{
    // ── Export buttons disabled until capture succeeds ────────────────

    [Fact]
    public void Initially_ExportButtonsDisabled()
    {
        var mediator = new ExportFlowMediator();
        Assert.False(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public void SuccessfulCapture_EnablesExportButtons()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        Assert.True(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public void FailedCapture_ExportButtonsStayDisabled()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplyFailedCapture("Capture failed");
        Assert.False(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public void FailedCapture_AfterSuccessful_ExportButtonsStayEnabled()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        // Second capture fails — but HasCapture is still true
        mediator.ApplyFailedCapture("Capture failed");
        Assert.True(mediator.ExportButtonsEnabled);
    }

    // ── Save to default location ─────────────────────────────────────

    [Fact]
    public async Task Save_Click_NoCapture_ShowsNoCapture()
    {
        var mediator = new ExportFlowMediator();
        await mediator.SaveClickAsync();
        Assert.Equal("No capture to export.", mediator.StatusText);
    }

    [Fact]
    public async Task Save_Click_WithCapture_ShowsSavedStatus()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.Contains("Saved", mediator.StatusText);
        // Status shows export dimensions from the result, not capture dimensions
        Assert.Contains("100×100", mediator.StatusText);
    }

    [Fact]
    public async Task Save_Click_WithCapture_ReEnablesButtons()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.True(mediator.CaptureButtonsEnabled);
        Assert.True(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public async Task Save_Click_WithCapture_ShowsTiming()
    {
        var mediator = new ExportFlowMediator(
            saveToDefault: () => ExportResult.Ok(
                @"C:\Users\test\Pictures\captcho\screenshot.png",
                100, 100, 1024, TimeSpan.FromMilliseconds(15.5)));
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.Contains("export 15.5ms", mediator.TimingText);
    }

    // ── Save As picker ───────────────────────────────────────────────

    [Fact]
    public async Task SaveAs_Cancelled_ShowsSaveCancelled()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveAsClickAsync(pickerResult: null);

        Assert.Equal("Save cancelled.", mediator.StatusText);
    }

    [Fact]
    public async Task SaveAs_Cancelled_ReEnablesButtons()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveAsClickAsync(pickerResult: null);

        Assert.True(mediator.CaptureButtonsEnabled);
        Assert.True(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public async Task SaveAs_WithFile_SavesAndShowsStatus()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveAsClickAsync(pickerResult: @"C:\test\custom_save.png");

        Assert.True(mediator.SaveToFileWasCalled);
        Assert.Contains("Saved", mediator.StatusText);
        Assert.Contains("custom_save.png", mediator.StatusText);
    }

    [Fact]
    public async Task SaveAs_NoCapture_ShowsNoCapture()
    {
        var mediator = new ExportFlowMediator();
        await mediator.SaveAsClickAsync(pickerResult: "test.png");
        Assert.Equal("No capture to export.", mediator.StatusText);
    }

    // ── Copy to clipboard ────────────────────────────────────────────

    [Fact]
    public async Task Copy_NoCapture_ShowsNoCapture()
    {
        var mediator = new ExportFlowMediator();
        await mediator.CopyClickAsync();
        Assert.Equal("No capture to export.", mediator.StatusText);
    }

    [Fact]
    public async Task Copy_WithCapture_ShowsCopiedStatus()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.CopyClickAsync();

        Assert.True(mediator.CopyToClipboardWasCalled);
        Assert.Contains("Copied to clipboard", mediator.StatusText);
    }

    [Fact]
    public async Task Copy_WithCapture_ReEnablesButtons()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.CopyClickAsync();

        Assert.True(mediator.CaptureButtonsEnabled);
        Assert.True(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public async Task Copy_Failure_ShowsClipboardFailed()
    {
        var mediator = new ExportFlowMediator(
            copyToClipboard: () => ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.Zero));
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.CopyClickAsync();

        Assert.Contains("Clipboard failed", mediator.StatusText);
        Assert.True(mediator.CaptureButtonsEnabled);
    }

    [Fact]
    public async Task Copy_ShowsTiming()
    {
        var mediator = new ExportFlowMediator(
            copyToClipboard: () => ClipboardExportResult.Ok(100, 100, 1024, TimeSpan.FromMilliseconds(8.3)));
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.CopyClickAsync();

        Assert.Contains("clipboard 8.3ms", mediator.TimingText);
    }

    // ── Concurrent click protection (10x breakpoint) ─────────────────

    [Fact]
    public async Task Export_DuringOperation_DisablesButtons()
    {
        var tcs = new TaskCompletionSource<ExportResult>();
        var mediator = new ExportFlowMediator(
            saveToDefault: () => tcs.Task.Result);
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        var task = mediator.SaveClickAsync();

        // During the export, buttons should be disabled
        Assert.False(mediator.ExportButtonsEnabled);
        Assert.False(mediator.CaptureButtonsEnabled);

        tcs.SetResult(ExportResult.Ok("test.png", 100, 100, 1024, TimeSpan.FromMilliseconds(5)));
        await task;

        // After completion, buttons re-enabled
        Assert.True(mediator.ExportButtonsEnabled);
        Assert.True(mediator.CaptureButtonsEnabled);
    }

    [Fact]
    public async Task Save_DuringCapture_NoCaptureGuard()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        // Simulate entering capture state
        mediator.EnterCaptureState();

        // Try to save during capture
        await mediator.SaveClickAsync();

        Assert.Equal("No capture to export.", mediator.StatusText);
    }

    // ── Export service failure (sanitized) ────────────────────────────

    [Fact]
    public async Task Save_ServiceFails_ShowsSanitizedFailure()
    {
        var mediator = new ExportFlowMediator(
            saveToDefault: () => ExportResult.Fail(ExportPhase.Write, "Access denied", TimeSpan.Zero));
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.Contains("Export failed", mediator.StatusText);
        Assert.Contains("Access denied", mediator.StatusText);
        Assert.True(mediator.CaptureButtonsEnabled);
        Assert.True(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public async Task Copy_ServiceFails_ShowsSanitizedFailure()
    {
        var mediator = new ExportFlowMediator(
            copyToClipboard: () => ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.FromMilliseconds(1)));
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.CopyClickAsync();

        Assert.Contains("Clipboard failed", mediator.StatusText);
        Assert.True(mediator.CaptureButtonsEnabled);
    }

    // ── Export service exception ──────────────────────────────────────

    [Fact]
    public async Task Save_ServiceThrows_ShowsSanitizedError()
    {
        var mediator = new ExportFlowMediator(
            saveToDefault: () => throw new InvalidOperationException("Disk full"));
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.Contains("Export failed", mediator.StatusText);
        Assert.Contains("Disk full", mediator.StatusText);
        Assert.True(mediator.CaptureButtonsEnabled);
        Assert.True(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public async Task Copy_ServiceThrows_ShowsSanitizedError()
    {
        var mediator = new ExportFlowMediator(
            copyToClipboard: () => throw new InvalidOperationException("Clipboard locked"));
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.CopyClickAsync();

        Assert.Contains("Clipboard failed", mediator.StatusText);
        Assert.Contains("Clipboard locked", mediator.StatusText);
        Assert.True(mediator.CaptureButtonsEnabled);
    }

    // ── Export cancellation ───────────────────────────────────────────

    [Fact]
    public async Task Save_Cancelled_ShowsExportCancelled()
    {
        var mediator = new ExportFlowMediator(
            saveToDefault: () => ExportResult.Cancelled(TimeSpan.FromMilliseconds(1)));
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.Contains("Export cancelled", mediator.StatusText);
    }

    // ── Capture state disables export during countdown ───────────────

    [Fact]
    public void EnterCaptureState_DisablesExportButtons()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        Assert.True(mediator.ExportButtonsEnabled);

        mediator.EnterCaptureState();
        Assert.False(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public void ExitCaptureState_ReEnablesExportButtons()
    {
        var mediator = new ExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        mediator.EnterCaptureState();
        Assert.False(mediator.ExportButtonsEnabled);

        mediator.ExitCaptureState();
        Assert.True(mediator.ExportButtonsEnabled);
    }

    // ── ExportStatusFormatter unit tests ──────────────────────────────

    [Fact]
    public void FormatStatus_Success_ShowsFilenameAndDimensions()
    {
        var result = ExportResult.Ok(
            @"C:\Users\test\Pictures\captcho\screenshot.png", 1920, 1080, 204800, TimeSpan.FromMilliseconds(50));
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("screenshot.png", status);
        Assert.Contains("1920×1080", status);
    }

    [Fact]
    public void FormatStatus_Failure_ShowsSanitizedMessage()
    {
        var result = ExportResult.Fail(ExportPhase.Write, "Access denied", TimeSpan.Zero);
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Export failed", status);
        Assert.Contains("Access denied", status);
    }

    [Fact]
    public void FormatStatus_Cancelled_ShowsExportCancelled()
    {
        var result = ExportResult.Cancelled(TimeSpan.FromMilliseconds(1));
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Export cancelled", status);
    }

    [Fact]
    public void FormatStatus_ClipboardSuccess_ShowsCopiedAndDimensions()
    {
        var result = ClipboardExportResult.Ok(800, 600, 51200, TimeSpan.FromMilliseconds(10));
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Copied to clipboard", status);
        Assert.Contains("800×600", status);
    }

    [Fact]
    public void FormatStatus_ClipboardFailure_ShowsFailure()
    {
        var result = ClipboardExportResult.Fail("Failed to set clipboard content", TimeSpan.Zero);
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("Clipboard failed", status);
    }

    [Fact]
    public void FormatTiming_ExportResult_ShowsExportMs()
    {
        var result = ExportResult.Ok("test.png", 100, 100, 1024, TimeSpan.FromMilliseconds(25.3));
        var timing = ExportStatusFormatter.FormatTiming(result);
        Assert.Contains("export 25.3ms", timing);
    }

    [Fact]
    public void FormatTiming_ClipboardResult_ShowsClipboardMs()
    {
        var result = ClipboardExportResult.Ok(100, 100, 1024, TimeSpan.FromMilliseconds(12.7));
        var timing = ExportStatusFormatter.FormatTiming(result);
        Assert.Contains("clipboard 12.7ms", timing);
    }

    [Fact]
    public void FormatTiming_ZeroElapsed_ReturnsEmpty()
    {
        var result = ExportResult.Fail(ExportPhase.Validation, "No capture", TimeSpan.Zero);
        var timing = ExportStatusFormatter.FormatTiming(result);
        Assert.Equal("", timing);
    }

    [Fact]
    public void FormatPickerCancelled_ReturnsSaveCancelled()
    {
        Assert.Equal("Save cancelled.", ExportStatusFormatter.FormatPickerCancelled());
    }

    [Fact]
    public void FormatNoCapture_ReturnsNoCapture()
    {
        Assert.Equal("No capture to export.", ExportStatusFormatter.FormatNoCapture());
    }

    // ── Button name verification ──────────────────────────────────────

    [Fact]
    public void ExportStatusFormatter_MethodsExist()
    {
        // Verify the formatter has all expected public methods
        var type = typeof(ExportStatusFormatter);
        Assert.NotNull(type.GetMethod("FormatStatus", new[] { typeof(ExportResult) }));
        Assert.NotNull(type.GetMethod("FormatStatus", new[] { typeof(ClipboardExportResult) }));
        Assert.NotNull(type.GetMethod("FormatTiming", new[] { typeof(ExportResult) }));
        Assert.NotNull(type.GetMethod("FormatTiming", new[] { typeof(ClipboardExportResult) }));
        Assert.NotNull(type.GetMethod("FormatPickerCancelled", Type.EmptyTypes));
        Assert.NotNull(type.GetMethod("FormatNoCapture", Type.EmptyTypes));
    }

    // ── WindowsClipboardAdapter exists and implements interface ───────

    [Fact]
    public void WindowsClipboardAdapter_ImplementsIClipboardAdapter()
    {
        Assert.True(typeof(IClipboardAdapter).IsAssignableFrom(typeof(WindowsClipboardAdapter)));
    }

    // ── Multiple exports from same capture ───────────────────────────

    [Fact]
    public async Task MultipleExports_SameCapture_AllSucceed()
    {
        var saveCount = 0;
        var copyCount = 0;
        var mediator = new ExportFlowMediator(
            saveToDefault: () => { saveCount++; return ExportResult.Ok("test.png", 100, 100, 1024, TimeSpan.FromMilliseconds(1)); },
            copyToClipboard: () => { copyCount++; return ClipboardExportResult.Ok(100, 100, 1024, TimeSpan.FromMilliseconds(1)); });

        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();
        Assert.Equal(1, saveCount);
        Assert.True(mediator.ExportButtonsEnabled);

        await mediator.CopyClickAsync();
        Assert.Equal(1, copyCount);
        Assert.True(mediator.ExportButtonsEnabled);

        await mediator.SaveClickAsync();
        Assert.Equal(2, saveCount);
        Assert.True(mediator.ExportButtonsEnabled);
    }
}
