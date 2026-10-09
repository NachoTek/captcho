// ExitAfterDeliverySettingsTests.cs — Persisted exit-after-delivery Setting
// tests (issue #54).
//
// Verifies the safe default (never exit), JSON round-trip through the
// ConfigurationService, migration of settings files written before the
// setting shipped, and Normalized preservation — the Configuration slice of
// the exit behavior, mirroring the LaunchBehaviorSettings test shape.

using System;
using System.IO;
using Xunit;

namespace captcho.Capture.Tests;

public class ExitAfterDeliverySettingsTests : IDisposable
{
    private readonly string _tempDir;

    public ExitAfterDeliverySettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"captchoExit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Defaults ────────────────────────────────────────────────────────

    [Fact]
    public void WithDefaults_NeverExits()
    {
        // The safe default: captcho keeps running after delivery until the
        // user opts in, so existing Configuration never starts exiting.
        Assert.False(AppSettings.WithDefaults().ExitAfterDelivery);
    }

    // ── Persistence round-trip ──────────────────────────────────────────

    [Fact]
    public void ConfigurationService_RoundTripsEnabledExit()
    {
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings { ExitAfterDelivery = true };

        Assert.True(svc.Save(original).Success);

        var loaded = svc.Load();
        Assert.True(loaded.Success);
        Assert.True(loaded.Settings.ExitAfterDelivery);
    }

    [Fact]
    public void ConfigurationService_RoundTripsDisabledExit()
    {
        // The value is real state, not presence-encoded: an explicit false
        // round-trips as false.
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings
        {
            ExitAfterDelivery = true,
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
        };
        Assert.True(svc.Save(original).Success);

        original.ExitAfterDelivery = false;
        Assert.True(svc.Save(original).Success);

        var loaded = svc.Load();
        Assert.True(loaded.Success);
        Assert.False(loaded.Settings.ExitAfterDelivery);
        // Neighboring Behavior slices survive the re-save.
        Assert.Equal(LaunchAction.LastCaptureMode, loaded.Settings.LaunchBehavior.Action);
    }

    // ── Migration: settings files predating exit after delivery ────────

    [Fact]
    public void Load_SettingsFilePredatingExitAfterDelivery_LoadsAsDisabled()
    {
        // A settings file written before this field shipped has no
        // exitAfterDelivery property; it must load with the safe
        // never-exit default so upgrading never changes exit behavior.
        File.WriteAllText(
            Path.Combine(_tempDir, "settings.json"),
            /*lang=json,strict*/ "{\"saveLocation\": \"D:\\\\Captures\", \"automaticExport\": {\"autoSave\": true, \"autoCopyFrame\": false, \"autoCopyPath\": false}}");
        var svc = new ConfigurationService(_tempDir);

        var loadResult = svc.Load();

        Assert.True(loadResult.Success);
        Assert.False(loadResult.Settings.ExitAfterDelivery);
        // Slices the file did carry survive the migration.
        Assert.True(loadResult.Settings.AutomaticExport.AutoSave);
    }

    // ── Normalized ──────────────────────────────────────────────────────

    [Fact]
    public void Normalized_PreservesExitAfterDelivery()
    {
        Assert.True(new AppSettings { ExitAfterDelivery = true }.Normalized().ExitAfterDelivery);
        Assert.False(new AppSettings { ExitAfterDelivery = false }.Normalized().ExitAfterDelivery);
    }
}
