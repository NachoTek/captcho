// ConfigurationUiWiringTests.cs — Tests for configuration-wired startup and export flows.
//
// Uses a pure mediator (ConfiguredExportFlowMediator) that mirrors MainWindow's
// settings-aware Save/Save As logic without WinUI dependencies.
// Covers: configured save location/template, Save As persistence on success,
// Save As cancel does not persist, configuration load fallback to defaults,
// persistence failure does not crash, and existing export behavior preservation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

/// <summary>
/// Testable mediator that mirrors MainWindow's settings-aware export logic.
/// Simulates Save and Save As with configuration persistence.
/// All state is tracked for assertions without WinUI controls.
/// </summary>
public class ConfiguredExportFlowMediator
{
    // Injected dependencies
    private readonly Func<AppSettings, ExportResult> _saveWithSettings;
    private readonly Func<string, ExportResult> _saveToFile;
    private readonly Func<AppSettings, ConfigurationSaveResult>? _persistSettings;

    // Current settings state
    public AppSettings CurrentSettings { get; private set; }

    // Simulated UI state
    public bool HasCapture { get; set; }
    public bool IsOperationRunning { get; private set; }
    public bool CaptureButtonsEnabled { get; private set; } = true;
    public bool ExportButtonsEnabled { get; private set; }
    public string? StatusText { get; private set; } = "Ready — choose a capture mode.";
    public string? TimingText { get; private set; } = "";

    /// <summary>Records whether save methods and persistence were called.</summary>
    public bool SaveWithSettingsWasCalled { get; private set; }
    public bool SaveToFileWasCalled { get; private set; }
    public bool PersistSettingsWasCalled { get; private set; }
    public AppSettings? LastPersistedSettings { get; private set; }

    public ConfiguredExportFlowMediator(
        AppSettings? settings = null,
        Func<AppSettings, ExportResult>? saveWithSettings = null,
        Func<string, ExportResult>? saveToFile = null,
        Func<AppSettings, ConfigurationSaveResult>? persistSettings = null)
    {
        CurrentSettings = settings ?? AppSettings.WithDefaults();
        _saveWithSettings = saveWithSettings ??
            (s => ExportResult.Ok(
                ExportFilenameTemplate.GetExportPath(s, DateTime.Now),
                100, 100, 1024, TimeSpan.FromMilliseconds(5)));
        _saveToFile = saveToFile ??
            (path => ExportResult.Ok(path, 100, 100, 1024, TimeSpan.FromMilliseconds(5)));
        _persistSettings = persistSettings;
    }

    // ── Capture simulation ────────────────────────────────────────────

    public void ApplySuccessfulCapture(string mode, string dimensions)
    {
        HasCapture = true;
        ExportButtonsEnabled = true;
        StatusText = $"{mode} — {dimensions}";
        UpdateExportButtonState();
    }

    public void ApplyFailedCapture(string error)
    {
        StatusText = error;
        UpdateExportButtonState();
    }

    // ── Save with configured settings ────────────────────────────────

    /// <summary>
    /// Mirrors MainWindow.Save_Click using configured settings.
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
            var result = await Task.Run(() => _saveWithSettings(CurrentSettings));
            SaveWithSettingsWasCalled = true;
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

    // ── Save As with persistence ──────────────────────────────────────

    /// <summary>
    /// Mirrors MainWindow.SaveAs_Click with a simulated picker result.
    /// pickerResult: null = cancelled, string = chosen path.
    /// Persists the directory after successful save only.
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
                // User cancelled — do NOT persist settings
                StatusText = ExportStatusFormatter.FormatPickerCancelled();
                TimingText = "";
                return;
            }

            StatusText = "Saving…";
            var result = await Task.Run(() => _saveToFile(pickerResult));
            SaveToFileWasCalled = true;
            StatusText = ExportStatusFormatter.FormatStatus(result);
            TimingText = ExportStatusFormatter.FormatTiming(result);

            // Persist the selected directory after successful save only
            if (result.Success)
            {
                PersistSaveAsDirectory(Path.GetDirectoryName(pickerResult));
            }
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

    // ── Suggested filename from settings ──────────────────────────────

    /// <summary>
    /// Returns the filename that would be suggested in the Save As picker
    /// based on current settings and the given timestamp.
    /// </summary>
    public string GetSuggestedFilename(DateTime timestamp)
    {
        return ExportFilenameTemplate.Expand(
            CurrentSettings.EffectiveFilenameTemplate, timestamp);
    }

    // ── UI state helpers ──────────────────────────────────────────────

    public void EnterCaptureState()
    {
        IsOperationRunning = true;
        CaptureButtonsEnabled = false;
        ExportButtonsEnabled = false;
    }

    public void ExitCaptureState()
    {
        IsOperationRunning = false;
        CaptureButtonsEnabled = true;
        UpdateExportButtonState();
    }

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

    /// <summary>
    /// Mirrors MainWindow.PersistSaveAsDirectory — persists the directory
    /// after successful Save As, updates in-memory settings.
    /// </summary>
    private void PersistSaveAsDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        var updatedSettings = new AppSettings
        {
            SaveLocation = directory,
            FilenameTemplate = CurrentSettings.FilenameTemplate,
        };

        if (_persistSettings != null)
        {
            var saveResult = _persistSettings(updatedSettings);
            if (saveResult.Success)
            {
                CurrentSettings = updatedSettings;
            }
        }
        else
        {
            // No persistence service — just update in-memory
            CurrentSettings = updatedSettings;
        }

        PersistSettingsWasCalled = _persistSettings != null;
        LastPersistedSettings = updatedSettings;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Tests
// ═══════════════════════════════════════════════════════════════════════

public class ConfigurationUiWiringTests
{
    // ── Configured Save uses settings-aware path ──────────────────────

    [Fact]
    public async Task Save_Click_WithCapture_UsesConfiguredSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho_test_{Guid.NewGuid():N}");
        var settings = new AppSettings
        {
            SaveLocation = tempDir,
            FilenameTemplate = "CustomCapture_<yyyy><MM><dd>"
        };

        AppSettings? usedSettings = null;
        var mediator = new ConfiguredExportFlowMediator(
            settings: settings,
            saveWithSettings: s =>
            {
                usedSettings = s;
                var path = ExportFilenameTemplate.GetExportPath(s, DateTime.Now);
                Directory.CreateDirectory(tempDir);
                return ExportResult.Ok(path, 100, 100, 1024, TimeSpan.FromMilliseconds(5));
            });

        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        await mediator.SaveClickAsync();

        Assert.True(mediator.SaveWithSettingsWasCalled);
        Assert.NotNull(usedSettings);
        Assert.Equal(tempDir, usedSettings!.SaveLocation);
        Assert.Equal("CustomCapture_<yyyy><MM><dd>", usedSettings.FilenameTemplate);
    }

    [Fact]
    public async Task Save_Click_WithCapture_SavesToConfiguredDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho_save_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var settings = new AppSettings { SaveLocation = tempDir };
            string? actualPath = null;

            var mediator = new ConfiguredExportFlowMediator(
                settings: settings,
                saveWithSettings: s =>
                {
                    var path = ExportFilenameTemplate.GetExportPath(s, DateTime.Now);
                    actualPath = path;
                    return ExportResult.Ok(path, 100, 100, 1024, TimeSpan.FromMilliseconds(5));
                });

            mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
            await mediator.SaveClickAsync();

            Assert.Contains("Saved", mediator.StatusText);
            Assert.NotNull(actualPath);
            Assert.StartsWith(tempDir, actualPath);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Save_Click_DefaultSettings_SavesToDefaultLocation()
    {
        var mediator = new ConfiguredExportFlowMediator();
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.Contains("Saved", mediator.StatusText);
    }

    // ── Save As uses configured template for suggested filename ───────

    [Fact]
    public void GetSuggestedFilename_UsesConfiguredTemplate()
    {
        var settings = new AppSettings
        {
            FilenameTemplate = "MyTemplate_<yyyy>-<MM>"
        };
        var mediator = new ConfiguredExportFlowMediator(settings: settings);
        var timestamp = new DateTime(2026, 5, 1, 14, 30, 0);

        var filename = mediator.GetSuggestedFilename(timestamp);

        Assert.Contains("MyTemplate_2026-05", filename);
    }

    [Fact]
    public void GetSuggestedFilename_DefaultSettings_UsesDefaultTemplate()
    {
        var mediator = new ConfiguredExportFlowMediator();
        var timestamp = new DateTime(2026, 5, 1, 14, 30, 0);

        var filename = mediator.GetSuggestedFilename(timestamp);

        Assert.Contains("captcho_2026-05-01", filename);
    }

    // ── Save As persists directory after successful save ──────────────

    [Fact]
    public async Task SaveAs_Success_PersistsDirectory()
    {
        var settings = AppSettings.WithDefaults();
        ConfigurationSaveResult? persistResult = null;

        var mediator = new ConfiguredExportFlowMediator(
            settings: settings,
            persistSettings: s =>
            {
                persistResult = new ConfigurationSaveResult { Success = true };
                return persistResult;
            });

        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        await mediator.SaveAsClickAsync(@"C:\Users\test\CustomDir\export.png");

        Assert.True(mediator.PersistSettingsWasCalled);
        Assert.NotNull(mediator.LastPersistedSettings);
        Assert.Equal(@"C:\Users\test\CustomDir", mediator.LastPersistedSettings!.SaveLocation);
        // Filename template is preserved
        Assert.Equal(settings.FilenameTemplate, mediator.LastPersistedSettings!.FilenameTemplate);
    }

    [Fact]
    public async Task SaveAs_Success_UpdatesInMemorySettings()
    {
        var mediator = new ConfiguredExportFlowMediator(
            persistSettings: s => new ConfigurationSaveResult { Success = true });

        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        await mediator.SaveAsClickAsync(@"D:\Screenshots\test.png");

        Assert.Equal(@"D:\Screenshots", mediator.CurrentSettings.SaveLocation);
    }

    // ── Save As cancel does not persist ───────────────────────────────

    [Fact]
    public async Task SaveAs_Cancel_DoesNotPersist()
    {
        var settings = AppSettings.WithDefaults();
        var mediator = new ConfiguredExportFlowMediator(
            settings: settings,
            persistSettings: s => new ConfigurationSaveResult { Success = true });

        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        await mediator.SaveAsClickAsync(pickerResult: null);

        Assert.False(mediator.PersistSettingsWasCalled);
        Assert.Equal(settings.SaveLocation, mediator.CurrentSettings.SaveLocation);
    }

    // ── Save As failure does not persist ──────────────────────────────

    [Fact]
    public async Task SaveAs_ExportFailure_DoesNotPersist()
    {
        var mediator = new ConfiguredExportFlowMediator(
            saveToFile: path => ExportResult.Fail(ExportPhase.Write, "Disk full", TimeSpan.Zero),
            persistSettings: s => new ConfigurationSaveResult { Success = true });

        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        await mediator.SaveAsClickAsync(@"C:\test\fail.png");

        Assert.False(mediator.PersistSettingsWasCalled);
        Assert.Contains("Export failed", mediator.StatusText);
    }

    // ── Configuration load fallback to defaults ───────────────────────

    [Fact]
    public async Task Save_Click_NullSettings_FallsBackToDefaults()
    {
        var mediator = new ConfiguredExportFlowMediator(settings: new AppSettings());
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.True(mediator.SaveWithSettingsWasCalled);
        // EffectiveSaveLocation should fall back to default
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, mediator.CurrentSettings.EffectiveSaveLocation);
    }

    [Fact]
    public async Task Save_Click_EmptyTemplate_FallsBackToDefaults()
    {
        var settings = new AppSettings { FilenameTemplate = "" };
        var mediator = new ConfiguredExportFlowMediator(settings: settings);
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.True(mediator.SaveWithSettingsWasCalled);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, mediator.CurrentSettings.EffectiveFilenameTemplate);
    }

    // ── Persistence failure does not crash or lose export result ──────

    [Fact]
    public async Task SaveAs_PersistenceFailure_StillShowsSuccess()
    {
        var mediator = new ConfiguredExportFlowMediator(
            persistSettings: s => new ConfigurationSaveResult
            {
                Success = false,
                Phase = "WriteTemp",
                ErrorMessage = "Access denied"
            });

        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        await mediator.SaveAsClickAsync(@"C:\test\persist_fail.png");

        // Export result is still visible
        Assert.Contains("Saved", mediator.StatusText);
        Assert.True(mediator.PersistSettingsWasCalled);
        // In-memory settings NOT updated because persist failed
        Assert.NotEqual(@"C:\test", mediator.CurrentSettings.SaveLocation);
    }

    [Fact]
    public async Task SaveAs_PersistenceException_DoesNotCrash()
    {
        var mediator = new ConfiguredExportFlowMediator(
            persistSettings: s => throw new InvalidOperationException("Disk corrupted"));

        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        // Should not throw — persistence failure is caught
        var ex = await Record.ExceptionAsync(() =>
            mediator.SaveAsClickAsync(@"C:\test\crash_test.png"));

        // The mediator catches persistence exceptions internally
        Assert.Null(ex);
    }

    // ── No-capture guard still works with settings ────────────────────

    [Fact]
    public async Task Save_Click_NoCapture_ShowsNoCapture()
    {
        var mediator = new ConfiguredExportFlowMediator(
            settings: new AppSettings { SaveLocation = @"C:\Custom" });
        await mediator.SaveClickAsync();
        Assert.Equal("No capture to export.", mediator.StatusText);
    }

    [Fact]
    public async Task SaveAs_Click_NoCapture_ShowsNoCapture()
    {
        var mediator = new ConfiguredExportFlowMediator(
            settings: new AppSettings { SaveLocation = @"C:\Custom" });
        await mediator.SaveAsClickAsync("test.png");
        Assert.Equal("No capture to export.", mediator.StatusText);
    }

    // ── Export buttons re-enable after settings-aware export ───────────

    [Fact]
    public async Task Save_Click_ReEnablesButtons()
    {
        var mediator = new ConfiguredExportFlowMediator(
            settings: new AppSettings { SaveLocation = @"C:\Custom" });
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveClickAsync();

        Assert.True(mediator.CaptureButtonsEnabled);
        Assert.True(mediator.ExportButtonsEnabled);
    }

    [Fact]
    public async Task SaveAs_Success_ReEnablesButtons()
    {
        var mediator = new ConfiguredExportFlowMediator(
            persistSettings: s => new ConfigurationSaveResult { Success = true });
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        await mediator.SaveAsClickAsync(@"C:\test\re_enable.png");

        Assert.True(mediator.CaptureButtonsEnabled);
        Assert.True(mediator.ExportButtonsEnabled);
    }

    // ── Concurrent click protection with settings ─────────────────────

    [Fact]
    public async Task Save_DuringCapture_NoCaptureGuard()
    {
        var mediator = new ConfiguredExportFlowMediator(
            settings: new AppSettings { SaveLocation = @"C:\Custom" });
        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");
        mediator.EnterCaptureState();

        await mediator.SaveClickAsync();

        Assert.Equal("No capture to export.", mediator.StatusText);
    }

    // ── Multiple exports preserve settings across operations ──────────

    [Fact]
    public async Task MultipleSaves_SettingsPreserved()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho_multi_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var settings = new AppSettings { SaveLocation = tempDir };
            var mediator = new ConfiguredExportFlowMediator(
                settings: settings,
                saveWithSettings: s =>
                {
                    var path = ExportFilenameTemplate.GetExportPath(s, DateTime.Now);
                    return ExportResult.Ok(path, 100, 100, 1024, TimeSpan.FromMilliseconds(1));
                });

            mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

            await mediator.SaveClickAsync();
            Assert.Equal(tempDir, mediator.CurrentSettings.EffectiveSaveLocation);

            await mediator.SaveClickAsync();
            Assert.Equal(tempDir, mediator.CurrentSettings.EffectiveSaveLocation);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ── Save As updates settings that subsequent Save uses ────────────

    [Fact]
    public async Task SaveAs_ThenSave_UsesUpdatedDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho_seq_{Guid.NewGuid():N}");
        try
        {
            var mediator = new ConfiguredExportFlowMediator(
                persistSettings: s =>
                {
                    if (!string.IsNullOrWhiteSpace(s.SaveLocation))
                        Directory.CreateDirectory(s.SaveLocation);
                    return new ConfigurationSaveResult { Success = true };
                });

            mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

            // Save As to custom directory
            var saveAsPath = Path.Combine(tempDir, "test.png");
            await mediator.SaveAsClickAsync(saveAsPath);
            Assert.Equal(tempDir, mediator.CurrentSettings.SaveLocation);

            // Subsequent Save should use the updated directory
            string? savePath = null;
            var mediator2 = new ConfiguredExportFlowMediator(
                settings: mediator.CurrentSettings,
                saveWithSettings: s =>
                {
                    savePath = ExportFilenameTemplate.GetExportPath(s, DateTime.Now);
                    return ExportResult.Ok(savePath, 100, 100, 1024, TimeSpan.FromMilliseconds(1));
                });
            mediator2.ApplySuccessfulCapture("Full Desktop", "1920×1080");

            await mediator2.SaveClickAsync();

            Assert.NotNull(savePath);
            Assert.StartsWith(tempDir, savePath);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ── ConfigurationService integration: real load → mediator ────────

    [Fact]
    public void ConfigurationService_Load_MissingFile_ReturnsDefaultsForMediator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho_cfg_{Guid.NewGuid():N}");
        try
        {
            var configService = new ConfigurationService(tempDir);
            var loadResult = configService.Load();

            Assert.True(loadResult.Success);
            Assert.True(loadResult.UsedDefaults);

            var mediator = new ConfiguredExportFlowMediator(settings: loadResult.Settings);
            Assert.Equal(ExportDefaults.DefaultSaveDirectory, mediator.CurrentSettings.EffectiveSaveLocation);
            Assert.Equal(ExportDefaults.DefaultFilenameTemplate, mediator.CurrentSettings.EffectiveFilenameTemplate);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ConfigurationService_Load_WithSettings_ReturnsSettingsForMediator()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho_cfg2_{Guid.NewGuid():N}");
        try
        {
            // Write settings first
            var configService = new ConfigurationService(tempDir);
            var expectedDir = Path.Combine(tempDir, "MyExports");
            var saveResult = configService.Save(new AppSettings
            {
                SaveLocation = expectedDir,
                FilenameTemplate = "Test_<yyyy>"
            });
            Assert.True(saveResult.Success);

            // Load and use in mediator
            var loadResult = configService.Load();
            var mediator = new ConfiguredExportFlowMediator(settings: loadResult.Settings);

            Assert.Equal(expectedDir, mediator.CurrentSettings.SaveLocation);
            Assert.Equal("Test_<yyyy>", mediator.CurrentSettings.FilenameTemplate);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ── SaveAs cancel doesn't persist even after prior successful SaveAs ──

    [Fact]
    public async Task SaveAs_CancelAfterSuccessfulSaveAs_KeepsFirstDirectory()
    {
        var mediator = new ConfiguredExportFlowMediator(
            persistSettings: s => new ConfigurationSaveResult { Success = true });

        mediator.ApplySuccessfulCapture("Full Desktop", "1920×1080");

        // First Save As to directory A
        await mediator.SaveAsClickAsync(@"D:\DirA\test.png");
        Assert.Equal(@"D:\DirA", mediator.CurrentSettings.SaveLocation);

        // Second Save As cancelled
        await mediator.SaveAsClickAsync(pickerResult: null);

        // Should still have DirA
        Assert.Equal(@"D:\DirA", mediator.CurrentSettings.SaveLocation);
    }

    // ── ExportStatusFormatter works with configured-path results ───────

    [Fact]
    public void FormatStatus_ConfiguredPath_ShowsFilename()
    {
        var result = ExportResult.Ok(
            @"D:\CustomExports\my_screenshot.png", 1920, 1080, 204800, TimeSpan.FromMilliseconds(50));
        var status = ExportStatusFormatter.FormatStatus(result);
        Assert.Contains("my_screenshot.png", status);
        Assert.Contains("1920×1080", status);
    }
}
