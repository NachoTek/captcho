// AppSettingsTests.cs — Unit and persistence tests for the per-Global-Hotkey
// enabled states on AppSettings.
//
// Verifies the default (null = every hotkey enabled), the per-id resolution, the
// deep-copy semantics through Normalized(), JSON round-trip through the
// ConfigurationService, and that settings files created before per-hotkey enabled
// states existed continue to load with every hotkey enabled.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace captcho.Capture.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _tempDir;

    public AppSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"captchoAppSettings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Default: null states means every hotkey is enabled ───────────────

    [Fact]
    public void IsHotkeyEnabled_NullStates_EnabledForEveryKnownId()
    {
        var settings = new AppSettings();

        Assert.True(settings.IsHotkeyEnabled(1));
        Assert.True(settings.IsHotkeyEnabled(2));
        Assert.True(settings.IsHotkeyEnabled(3));
        Assert.True(settings.IsHotkeyEnabled(4));
    }

    [Fact]
    public void WithDefaults_HotkeyEnabledStatesIsNull_AllEnabledByDefault()
    {
        var settings = AppSettings.WithDefaults();

        Assert.Null(settings.HotkeyEnabledStates);
        Assert.True(settings.IsHotkeyEnabled(2));
    }

    // ── Per-id resolution ────────────────────────────────────────────────

    [Fact]
    public void IsHotkeyEnabled_MissingIdTreatedAsEnabled()
    {
        var settings = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool> { [2] = false },
        };

        // Id 2 is explicitly disabled; every other id is absent -> enabled.
        Assert.True(settings.IsHotkeyEnabled(1));
        Assert.False(settings.IsHotkeyEnabled(2));
        Assert.True(settings.IsHotkeyEnabled(3));
        Assert.True(settings.IsHotkeyEnabled(4));
    }

    [Fact]
    public void IsHotkeyEnabled_ExplicitTrue_IsEnabled()
    {
        var settings = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool> { [3] = true },
        };

        Assert.True(settings.IsHotkeyEnabled(3));
    }

    // ── Normalized() deep-copies the states dictionary ───────────────────

    [Fact]
    public void Normalized_DeepCopiesHotkeyEnabledStates()
    {
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool> { [1] = false, [4] = false },
        };

        var normalized = source.Normalized();

        // Mutating the source after Normalized must not affect the copy.
        source.HotkeyEnabledStates![1] = true;
        Assert.False(normalized.IsHotkeyEnabled(1));

        // Mutating the copy must not affect the source.
        normalized.HotkeyEnabledStates![4] = true;
        Assert.False(source.IsHotkeyEnabled(4));
    }

    [Fact]
    public void Normalized_NullHotkeyStates_StaysNull()
    {
        var source = new AppSettings();

        var normalized = source.Normalized();

        Assert.Null(normalized.HotkeyEnabledStates);
    }

    // ── JSON round-trip through ConfigurationService ─────────────────────

    [Fact]
    public void SaveThenLoad_PreservesHotkeyEnabledStates()
    {
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings
        {
            SaveLocation = Path.Combine(_tempDir, "Captures"),
            FilenameTemplate = "shot_<#>",
            HotkeyEnabledStates = new Dictionary<int, bool> { [1] = true, [2] = false, [3] = true, [4] = false },
        };

        var saveResult = svc.Save(original);
        Assert.True(saveResult.Success);

        var loaded = svc.Load();
        Assert.True(loaded.Success);

        Assert.NotNull(loaded.Settings.HotkeyEnabledStates);
        Assert.True(loaded.Settings.IsHotkeyEnabled(1));
        Assert.False(loaded.Settings.IsHotkeyEnabled(2));
        Assert.True(loaded.Settings.IsHotkeyEnabled(3));
        Assert.False(loaded.Settings.IsHotkeyEnabled(4));
    }

    [Fact]
    public void SaveThenLoad_AllEnabledHotkeys_OmitsOrPreservesCleanly()
    {
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool> { [1] = true, [2] = true, [3] = true, [4] = true },
        };

        svc.Save(original);
        var loaded = svc.Load();

        // All enabled round-trips as enabled regardless of representation.
        Assert.True(loaded.Settings.IsHotkeyEnabled(1));
        Assert.True(loaded.Settings.IsHotkeyEnabled(2));
        Assert.True(loaded.Settings.IsHotkeyEnabled(3));
        Assert.True(loaded.Settings.IsHotkeyEnabled(4));
    }

    // ── Settings files from before per-hotkey toggles keep loading ───────

    [Fact]
    public void Load_OldSettingsFileWithoutHotkeyStates_LoadsAllEnabled()
    {
        // A settings file as it would have existed before this slice shipped:
        // only saveLocation and filenameTemplate, no hotkeyEnabledStates field.
        var oldJson = """
        {
          "saveLocation": "C:\\Captures",
          "filenameTemplate": "legacy_<yyyy>"
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), oldJson);

        var svc = new ConfigurationService(_tempDir);
        var loaded = svc.Load();

        Assert.True(loaded.Success);
        Assert.Null(loaded.Settings.HotkeyEnabledStates);
        Assert.True(loaded.Settings.IsHotkeyEnabled(1));
        Assert.True(loaded.Settings.IsHotkeyEnabled(2));
        Assert.True(loaded.Settings.IsHotkeyEnabled(3));
        Assert.True(loaded.Settings.IsHotkeyEnabled(4));
    }

    // ── JSON shape ───────────────────────────────────────────────────────

    [Fact]
    public void Save_WithDisabledHotkeys_WritesCamelCaseHotkeyStates()
    {
        var svc = new ConfigurationService(_tempDir);
        svc.Save(new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool> { [2] = false },
        });

        string json = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));

        Assert.Contains("\"hotkeyEnabledStates\"", json);
        // Dictionary keys are the stable hotkey ids as JSON string keys.
        Assert.Contains("\"2\"", json);
    }

    [Fact]
    public void Save_AllEnabledNullStates_OmitsHotkeyField()
    {
        var svc = new ConfigurationService(_tempDir);
        svc.Save(new AppSettings { HotkeyEnabledStates = null });

        string json = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));

        Assert.DoesNotContain("hotkeyEnabledStates", json, StringComparison.OrdinalIgnoreCase);
    }
}
