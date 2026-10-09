// GlobalHotkeyBindingConfigurationTests.cs — Configuration persistence tests for
// per-Global-Hotkey key bindings (issue #51).
//
// Verifies the Configuration slice of remapping one Global Hotkey end to end:
// persisted bindings round-trip through settings.json, absent bindings resolve to
// the legacy hardcoded combinations (migration for pre-remap Configuration),
// enabled states keep working alongside bindings, malformed bindings are flagged
// by AppSettings.Validate, and Normalized deep-copies the map.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace captcho.Capture.Tests;

public class GlobalHotkeyBindingConfigurationTests : IDisposable
{
    // Legacy Win32 values for the hardcoded pre-remap combinations. Duplicated
    // here on purpose: these tests pin the migration contract to exact values,
    // independent of the production tables.
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;
    private const int VK_SNAPSHOT = 0x2C;

    private readonly string _tempDir;

    public GlobalHotkeyBindingConfigurationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"captchoTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private ConfigurationService CreateService() => new(_tempDir);

    // ── Defaults / migration to the legacy combinations ─────────────────

    [Fact]
    public void WithDefaults_HasNoExplicitBindings()
    {
        var settings = AppSettings.WithDefaults();

        Assert.Null(settings.GlobalHotkeyBindings);
    }

    [Fact]
    public void EffectiveBinding_NoBindingsMap_ResolvesLegacyFullDesktopCombination()
    {
        var settings = AppSettings.WithDefaults();

        var binding = settings.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop);

        Assert.Equal(new HotkeyBinding(MOD_SHIFT, VK_SNAPSHOT), binding);
    }

    [Fact]
    public void EffectiveBinding_NoBindingsMap_ResolvesEveryLegacyCombination()
    {
        var settings = AppSettings.WithDefaults();
        var expected = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
        {
            [GlobalHotkeyRoute.CurrentMonitor] = new(0, VK_SNAPSHOT),
            [GlobalHotkeyRoute.ActiveWindow] = new(MOD_WIN, VK_SNAPSHOT),
            [GlobalHotkeyRoute.FullDesktop] = new(MOD_SHIFT, VK_SNAPSHOT),
            [GlobalHotkeyRoute.RectangularRegion] = new(MOD_WIN | MOD_SHIFT, VK_SNAPSHOT),
        };

        foreach (var (route, legacy) in expected)
            Assert.Equal(legacy, settings.EffectiveGlobalHotkeyBinding(route));
    }

    [Fact]
    public void EffectiveBinding_MissingEntry_ResolvesThatRouteDefault()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = new(0x0002, 0x74), // Ctrl + F5
            },
        };

        Assert.Equal(new HotkeyBinding(0x0002, 0x74),
            settings.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
        Assert.Equal(new HotkeyBinding(MOD_WIN, VK_SNAPSHOT),
            settings.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.ActiveWindow));
    }

    [Fact]
    public void Load_PreRemapConfiguration_MigratesToLegacyCombinationWithoutLosingBehavior()
    {
        // A settings file written by a pre-remap build: enabled states only, no
        // hotkeyBindings property. Loading it must keep the enabled behavior and
        // resolve the Full Desktop Global Hotkey to the legacy combination.
        File.WriteAllText(
            Path.Combine(_tempDir, "settings.json"),
            """
            {
              "saveLocation": "D:\\Captures",
              "hotkeyEnabledStates": { "fullDesktop": false, "activeWindow": true }
            }
            """);

        var result = CreateService().Load();

        Assert.True(result.Success);
        Assert.False(result.UsedDefaults);
        var settings = result.Settings;
        Assert.False(settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.FullDesktop));
        Assert.True(settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.ActiveWindow));
        Assert.Equal(new HotkeyBinding(MOD_SHIFT, VK_SNAPSHOT),
            settings.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
    }

    // ── Round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTripsRecordedBinding()
    {
        var svc = CreateService();
        var remapped = new HotkeyBinding(0x0002, 0x74); // Ctrl + F5
        var original = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = remapped,
            },
        };

        var save = svc.Save(original);
        Assert.True(save.Success);
        var loaded = svc.Load().Settings;

        Assert.NotNull(loaded.GlobalHotkeyBindings);
        Assert.Equal(remapped, loaded.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
    }

    [Fact]
    public void SavedJson_UsesPinnedPropertyNameAndRouteKeys()
    {
        var svc = CreateService();
        var original = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = new(0x0002, 0x74),
            },
        };

        svc.Save(original);
        string json = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));

        Assert.Contains("\"hotkeyBindings\"", json);
        Assert.Contains("\"fullDesktop\"", json);
        Assert.Contains("\"modifiers\"", json);
        Assert.Contains("\"virtualKey\"", json);
    }

    [Fact]
    public void Load_UnknownRouteKeyInBindings_BackupsAndUsesDefaults()
    {
        // Hand-edited or corrupt files with an unknown route key follow the same
        // corruption path as the enabled-states map: backup + defaults.
        File.WriteAllText(
            Path.Combine(_tempDir, "settings.json"),
            """
            { "hotkeyBindings": { "notARoute": { "modifiers": 2, "virtualKey": 116 } } }
            """);

        var result = CreateService().Load();

        Assert.True(result.Success);
        Assert.True(result.UsedDefaults);
        Assert.NotNull(result.BackupPath);
    }

    // ── Normalized / Validate ───────────────────────────────────────────

    [Fact]
    public void Normalized_DeepCopiesBindingsMap()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = new(0x0002, 0x74),
            },
        };

        var normalized = settings.Normalized();
        settings.GlobalHotkeyBindings[GlobalHotkeyRoute.FullDesktop] = new(0, 0x75);

        Assert.Equal(new HotkeyBinding(0x0002, 0x74),
            normalized.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
    }

    [Fact]
    public void Validate_ZeroVirtualKey_IsFlaggedForTheBindingsField()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = new(0x0002, 0),
            },
        };

        var issues = settings.Validate();

        Assert.Contains(issues, i => i.Field == SettingsField.GlobalHotkeyBindings);
    }

    [Fact]
    public void Validate_RecordedBinding_IsValid()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = new(0x0002, 0x74),
            },
        };

        Assert.DoesNotContain(settings.Validate(),
            i => i.Field == SettingsField.GlobalHotkeyBindings);
    }
}
