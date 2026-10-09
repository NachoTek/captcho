// LaunchBehaviorSettingsTests.cs — Unit and persistence tests for the
// configured launch behavior (issue #53).
//
// Verifies the Do-nothing safe default, JSON round-trip through the
// ConfigurationService, migration of settings files written before the
// launch behavior shipped, and the startup-mode resolution fallbacks for
// absent or invalid persisted values.

using System;
using System.IO;
using Xunit;

namespace captcho.Capture.Tests;

public class LaunchBehaviorSettingsTests : IDisposable
{
    private readonly string _tempDir;

    public LaunchBehaviorSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"captchoLaunch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Defaults ────────────────────────────────────────────────────────

    [Fact]
    public void WithDefaults_IsDoNothing()
    {
        var settings = LaunchBehaviorSettings.WithDefaults();

        Assert.Equal(LaunchAction.DoNothing, settings.Action);
        Assert.Null(settings.ConfiguredMode);
    }

    [Fact]
    public void AppSettingsDefaults_CarryDoNothing()
    {
        Assert.Equal(LaunchBehaviorSettings.WithDefaults(), AppSettings.WithDefaults().LaunchBehavior);
        Assert.Null(AppSettings.WithDefaults().LastCaptureMode);
    }

    // ── Effective resolution and startup-mode fallbacks ─────────────────

    [Fact]
    public void ResolveStartupMode_DoNothing_ReturnsNull()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.DoNothing, null),
            LastCaptureMode = CaptureMode.Selection,
        };

        Assert.Null(settings.ResolveStartupMode());
    }

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void ResolveStartupMode_ConfiguredMode_ReturnsIt(CaptureMode configured)
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, configured),
        };

        Assert.Equal(configured, settings.ResolveStartupMode());
    }

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void ResolveStartupMode_LastCaptureMode_ReturnsRememberedMode(CaptureMode last)
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
            LastCaptureMode = last,
        };

        Assert.Equal(last, settings.ResolveStartupMode());
    }

    [Fact]
    public void ResolveStartupMode_LastCaptureMode_Absent_ReturnsNull()
    {
        // Nothing recorded yet — startup stays idle rather than guessing.
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
            LastCaptureMode = null,
        };

        Assert.Null(settings.ResolveStartupMode());
    }

    [Fact]
    public void ResolveStartupMode_ConfiguredMode_InvalidEnum_FallsBackToDoNothing()
    {
        // A hand-edited file could persist an out-of-range enum; startup must
        // stay idle rather than capture something unintended.
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings((LaunchAction)99, CaptureMode.Selection),
        };

        Assert.Null(settings.ResolveStartupMode());
    }

    [Fact]
    public void ResolveStartupMode_LastCaptureMode_InvalidEnum_ReturnsNull()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
            LastCaptureMode = (CaptureMode)42,
        };

        Assert.Null(settings.ResolveStartupMode());
    }

    [Fact]
    public void ResolveStartupMode_ConfiguredMode_SelectedWhileActionIsLast_IgnoresConfigured()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, CaptureMode.FullDesktop),
            LastCaptureMode = CaptureMode.ActiveWindow,
        };

        Assert.Equal(CaptureMode.ActiveWindow, settings.ResolveStartupMode());
    }

    [Fact]
    public void EffectiveLaunchBehavior_NullBacking_FallsBackToDefaults()
    {
        // Defensive: a hand-edited settings file with an explicit null value
        // resolves to the Do-nothing defaults rather than null.
        var settings = new AppSettings { LaunchBehavior = null! };

        Assert.Equal(LaunchBehaviorSettings.WithDefaults(), settings.EffectiveLaunchBehavior);
    }

    [Fact]
    public void EffectiveLastCaptureMode_InvalidEnum_FallsBackToNull()
    {
        var settings = new AppSettings { LastCaptureMode = (CaptureMode)(-1) };

        Assert.Null(settings.EffectiveLastCaptureMode);
    }

    // ── Validation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(LaunchAction.DoNothing, null)]
    [InlineData(LaunchAction.LastCaptureMode, null)]
    [InlineData(LaunchAction.LastCaptureMode, CaptureMode.Selection)]
    public void Validate_AllValidCombinations_ReportNoIssues(LaunchAction action, CaptureMode? configured)
    {
        var settings = AppSettings.WithDefaults();
        settings.LaunchBehavior = new LaunchBehaviorSettings(action, configured);

        Assert.DoesNotContain(settings.Validate(), i => i.Field == SettingsField.LaunchBehavior);
    }

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void Validate_ConfiguredMode_ValidMode_HasNoIssue(CaptureMode configured)
    {
        var settings = AppSettings.WithDefaults();
        settings.LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, configured);

        Assert.DoesNotContain(settings.Validate(), i => i.Field == SettingsField.LaunchBehavior);
    }

    [Fact]
    public void Validate_ConfiguredMode_WithoutMode_IsAnIssue()
    {
        // The Settings tab edits this through the typed API (always supplies a
        // mode), so this is a defensive persistence-boundary rule: a
        // hand-edited file cannot configure "capture nothing".
        var settings = AppSettings.WithDefaults();
        settings.LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, null);

        var issue = Assert.Single(settings.Validate(), i => i.Field == SettingsField.LaunchBehavior);
        Assert.NotNull(issue.Message);
    }

    [Fact]
    public void Validate_ConfiguredMode_InvalidEnum_IsAnIssue()
    {
        var settings = AppSettings.WithDefaults();
        settings.LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, (CaptureMode)7);

        Assert.Contains(settings.Validate(), i => i.Field == SettingsField.LaunchBehavior);
    }

    [Fact]
    public void Validate_InvalidActionEnum_IsAnIssue()
    {
        var settings = AppSettings.WithDefaults();
        settings.LaunchBehavior = new LaunchBehaviorSettings((LaunchAction)5, null);

        Assert.Contains(settings.Validate(), i => i.Field == SettingsField.LaunchBehavior);
    }

    // ── Normalized ──────────────────────────────────────────────────────

    [Fact]
    public void Normalized_PreservesConfiguredLaunchBehavior()
    {
        var source = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.SelectedMonitor),
            LastCaptureMode = CaptureMode.Selection,
        };

        var normalized = source.Normalized();

        Assert.Equal(new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.SelectedMonitor),
            normalized.LaunchBehavior);
        Assert.Equal(CaptureMode.Selection, normalized.LastCaptureMode);
    }

    // ── Persistence round-trip ──────────────────────────────────────────

    [Fact]
    public void ConfigurationService_RoundTripsLaunchBehavior()
    {
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.SelectedWindow),
            LastCaptureMode = CaptureMode.Selection,
        };

        Assert.True(svc.Save(original).Success);

        var loaded = svc.Load();
        Assert.True(loaded.Success);
        Assert.Equal(LaunchAction.ConfiguredCaptureMode, loaded.Settings.LaunchBehavior.Action);
        Assert.Equal(CaptureMode.SelectedWindow, loaded.Settings.LaunchBehavior.ConfiguredMode);
        Assert.Equal(CaptureMode.Selection, loaded.Settings.LastCaptureMode);
    }

    [Fact]
    public void ConfigurationService_RoundTripsDoNothingWithoutLastMode()
    {
        // Do nothing needs no configured mode; a settings file that never
        // captured must not grow a lastCaptureMode on save.
        var svc = new ConfigurationService(_tempDir);
        var original = AppSettings.WithDefaults();

        Assert.True(svc.Save(original).Success);

        var loaded = svc.Load();
        Assert.True(loaded.Success);
        Assert.Equal(LaunchAction.DoNothing, loaded.Settings.LaunchBehavior.Action);
        Assert.Null(loaded.Settings.LaunchBehavior.ConfiguredMode);
        Assert.Null(loaded.Settings.LastCaptureMode);
    }

    // ── Migration: settings files predating launch behavior ─────────────

    [Fact]
    public void Load_SettingsFilePredatingLaunchBehavior_LoadsAsDoNothing()
    {
        // A settings file written before this field shipped has no
        // launchBehavior property; it must load with the safe Do-nothing
        // default so startup never captures unexpectedly.
        File.WriteAllText(
            Path.Combine(_tempDir, "settings.json"),
            /*lang=json,strict*/ "{\"saveLocation\": \"D:\\\\Captures\", \"filenameTemplate\": \"shot\"}");
        var svc = new ConfigurationService(_tempDir);

        var loadResult = svc.Load();

        Assert.True(loadResult.Success);
        Assert.Equal(LaunchBehaviorSettings.WithDefaults(), loadResult.Settings.LaunchBehavior);
        Assert.Null(loadResult.Settings.LastCaptureMode);
        Assert.Null(loadResult.Settings.ResolveStartupMode());
    }

    [Fact]
    public void Load_LastCaptureModeWithoutLaunchBehavior_StillReadsLastMode()
    {
        // Forward-written or partially-migrated files keep their recorded
        // last Capture Mode; only the behavior's action decides whether it is
        // used at startup. Enums serialize as numbers (the same wire format
        // as RememberSelectLifetime); 4 = Selection.
        File.WriteAllText(
            Path.Combine(_tempDir, "settings.json"),
            /*lang=json,strict*/ "{\"lastCaptureMode\": 4}");
        var svc = new ConfigurationService(_tempDir);

        var loadResult = svc.Load();

        Assert.True(loadResult.Success);
        Assert.Equal(LaunchAction.DoNothing, loadResult.Settings.LaunchBehavior.Action);
        Assert.Equal(CaptureMode.Selection, loadResult.Settings.LastCaptureMode);
    }
}
