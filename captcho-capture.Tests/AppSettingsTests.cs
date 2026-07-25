// AppSettingsTests.cs — Unit and persistence tests for the per-Global-Hotkey
// enabled states on AppSettings.
//
// Verifies the default (null = every global hotkey enabled), the per-id resolution, the
// deep-copy semantics through Normalized(), JSON round-trip through the
// ConfigurationService, and that settings files created before per-Global-Hotkey enabled
// states existed continue to load with every global hotkey enabled.

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

    // ── Default: null states means every global hotkey is enabled ───────────────

    [Fact]
    public void IsGlobalHotkeyEnabled_NullStates_EnabledForEveryKnownId()
    {
        var settings = new AppSettings();

        Assert.True(settings.IsGlobalHotkeyEnabled(1));
        Assert.True(settings.IsGlobalHotkeyEnabled(2));
        Assert.True(settings.IsGlobalHotkeyEnabled(3));
        Assert.True(settings.IsGlobalHotkeyEnabled(4));
    }

    [Fact]
    public void WithDefaults_GlobalHotkeyEnabledStatesIsNull_AllEnabledByDefault()
    {
        var settings = AppSettings.WithDefaults();

        Assert.Null(settings.GlobalHotkeyEnabledStates);
        Assert.True(settings.IsGlobalHotkeyEnabled(2));
    }

    // ── Per-id resolution ────────────────────────────────────────────────

    [Fact]
    public void IsGlobalHotkeyEnabled_MissingIdTreatedAsEnabled()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<int, bool> { [2] = false },
        };

        // Id 2 is explicitly disabled; every other id is absent -> enabled.
        Assert.True(settings.IsGlobalHotkeyEnabled(1));
        Assert.False(settings.IsGlobalHotkeyEnabled(2));
        Assert.True(settings.IsGlobalHotkeyEnabled(3));
        Assert.True(settings.IsGlobalHotkeyEnabled(4));
    }

    [Fact]
    public void IsGlobalHotkeyEnabled_ExplicitTrue_IsEnabled()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<int, bool> { [3] = true },
        };

        Assert.True(settings.IsGlobalHotkeyEnabled(3));
    }

    // ── Normalized() deep-copies the states dictionary ───────────────────

    [Fact]
    public void Normalized_DeepCopiesGlobalHotkeyEnabledStates()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<int, bool> { [1] = false, [4] = false },
        };

        var normalized = source.Normalized();

        // Mutating the source after Normalized must not affect the copy.
        source.GlobalHotkeyEnabledStates![1] = true;
        Assert.False(normalized.IsGlobalHotkeyEnabled(1));

        // Mutating the copy must not affect the source.
        normalized.GlobalHotkeyEnabledStates![4] = true;
        Assert.False(source.IsGlobalHotkeyEnabled(4));
    }

    [Fact]
    public void Normalized_NullGlobalHotkeyStates_StaysNull()
    {
        var source = new AppSettings();

        var normalized = source.Normalized();

        Assert.Null(normalized.GlobalHotkeyEnabledStates);
    }

    // ── JSON round-trip through ConfigurationService ─────────────────────

    [Fact]
    public void SaveThenLoad_PreservesGlobalHotkeyEnabledStates()
    {
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings
        {
            SaveLocation = Path.Combine(_tempDir, "Captures"),
            FilenameTemplate = "shot_<#>",
            GlobalHotkeyEnabledStates = new Dictionary<int, bool> { [1] = true, [2] = false, [3] = true, [4] = false },
        };

        var saveResult = svc.Save(original);
        Assert.True(saveResult.Success);

        var loaded = svc.Load();
        Assert.True(loaded.Success);

        Assert.NotNull(loaded.Settings.GlobalHotkeyEnabledStates);
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(1));
        Assert.False(loaded.Settings.IsGlobalHotkeyEnabled(2));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(3));
        Assert.False(loaded.Settings.IsGlobalHotkeyEnabled(4));
    }

    [Fact]
    public void SaveThenLoad_AllEnabledGlobalHotkeys_OmitsOrPreservesCleanly()
    {
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<int, bool> { [1] = true, [2] = true, [3] = true, [4] = true },
        };

        svc.Save(original);
        var loaded = svc.Load();

        // All enabled round-trips as enabled regardless of representation.
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(1));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(2));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(3));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(4));
    }

    // ── Settings files from before per-Global-Hotkey toggles keep loading ───────

    [Fact]
    public void Load_OldSettingsFileWithoutGlobalHotkeyStates_LoadsAllEnabled()
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
        Assert.Null(loaded.Settings.GlobalHotkeyEnabledStates);
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(1));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(2));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(3));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(4));
    }

    // ── JSON shape ───────────────────────────────────────────────────────

    [Fact]
    public void Save_WithDisabledGlobalHotkeys_WritesCamelCaseGlobalHotkeyStates()
    {
        var svc = new ConfigurationService(_tempDir);
        svc.Save(new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<int, bool> { [2] = false },
        });

        string json = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));

        Assert.Contains("\"hotkeyEnabledStates\"", json);
        // Dictionary keys are the stable Global Hotkey ids as JSON string keys.
        Assert.Contains("\"2\"", json);
    }

    [Fact]
    public void Save_AllEnabledNullStates_OmitsGlobalHotkeyField()
    {
        var svc = new ConfigurationService(_tempDir);
        svc.Save(new AppSettings { GlobalHotkeyEnabledStates = null });

        string json = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));

        Assert.DoesNotContain("hotkeyEnabledStates", json, StringComparison.OrdinalIgnoreCase);
    }
}
