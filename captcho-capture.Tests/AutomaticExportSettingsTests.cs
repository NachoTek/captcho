// AutomaticExportSettingsTests.cs — Unit and persistence tests for the
// automatic Export settings (automatic save, Copy Frame, Copy Path).
//
// Verifies the disabled-by-default model, independence of the three toggles
// through Normalized(), JSON round-trip through the ConfigurationService, and
// that settings files created before the Behavior tab existed load with every
// automatic Export action disabled.

using System;
using System.IO;
using Xunit;

namespace captcho.Capture.Tests;

public class AutomaticExportSettingsTests : IDisposable
{
    private readonly string _tempDir;

    public AutomaticExportSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"captchoAutoExport_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Defaults ────────────────────────────────────────────────────────

    [Fact]
    public void WithDefaults_EveryActionIsDisabled()
    {
        var settings = AutomaticExportSettings.WithDefaults();

        Assert.False(settings.AutoSave);
        Assert.False(settings.AutoCopyFrame);
        Assert.False(settings.AutoCopyPath);
        Assert.False(settings.AnyEnabled);
    }

    [Fact]
    public void AppSettingsDefaults_CarryDisabledAutomaticExport()
    {
        Assert.Equal(AutomaticExportSettings.WithDefaults(), AppSettings.WithDefaults().AutomaticExport);
    }

    [Fact]
    public void EffectiveAutomaticExport_NullBacking_FallsBackToDefaults()
    {
        // Defensive: a hand-edited settings file with an explicit null value
        // resolves to the disabled defaults rather than null.
        var settings = new AppSettings { AutomaticExport = null! };

        Assert.Equal(AutomaticExportSettings.WithDefaults(), settings.EffectiveAutomaticExport);
    }

    // ── Independence of the three toggles ───────────────────────────────

    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(2, false, true, false)]
    [InlineData(3, true, true, false)]
    [InlineData(4, false, false, true)]
    [InlineData(5, true, false, true)]
    [InlineData(6, false, true, true)]
    [InlineData(7, true, true, true)]
    public void EveryToggleCombination_SurvivesNormalizationIndependently(
        int mask, bool autoSave, bool autoCopyFrame, bool autoCopyPath)
    {
        var source = new AutomaticExportSettings(autoSave, autoCopyFrame, autoCopyPath);

        var normalized = source.Normalized();

        Assert.Equal(autoSave, normalized.AutoSave);
        Assert.Equal(autoCopyFrame, normalized.AutoCopyFrame);
        Assert.Equal(autoCopyPath, normalized.AutoCopyPath);
        Assert.Equal(mask != 0, normalized.AnyEnabled);
    }

    [Fact]
    public void Normalized_OnAppSettings_PreservesAutomaticExport()
    {
        var source = new AppSettings
        {
            AutomaticExport = new AutomaticExportSettings(true, false, true),
        };

        var normalized = source.Normalized();

        Assert.Equal(new AutomaticExportSettings(true, false, true), normalized.AutomaticExport);
    }

    // ── Persistence round-trip ──────────────────────────────────────────

    [Fact]
    public void ConfigurationService_RoundTripsAutomaticExport()
    {
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings
        {
            AutomaticExport = new AutomaticExportSettings(true, true, false),
        };

        var saveResult = svc.Save(original);
        Assert.True(saveResult.Success);

        var loadResult = svc.Load();
        Assert.True(loadResult.Success);
        Assert.Equal(new AutomaticExportSettings(true, true, false), loadResult.Settings.AutomaticExport);
    }

    [Fact]
    public void Load_SettingsFilePredatingAutomaticExport_KeepsEveryActionDisabled()
    {
        // A settings file written before the Behavior tab shipped has no
        // automaticExport property; it must load with the disabled defaults.
        File.WriteAllText(
            Path.Combine(_tempDir, "settings.json"),
            /*lang=json,strict*/ "{\"saveLocation\": \"D:\\\\Captures\", \"filenameTemplate\": \"shot\"}");
        var svc = new ConfigurationService(_tempDir);

        var loadResult = svc.Load();

        Assert.True(loadResult.Success);
        Assert.Equal(AutomaticExportSettings.WithDefaults(), loadResult.Settings.AutomaticExport);
    }
}
