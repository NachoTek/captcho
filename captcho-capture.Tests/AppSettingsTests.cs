// AppSettingsTests.cs — Unit and persistence tests for the per-Global-Hotkey
// enabled states on AppSettings.
//
// Verifies the default (null = every global hotkey enabled), the per-route resolution, the
// deep-copy semantics through Normalized(), JSON round-trip through the
// ConfigurationService, and that settings files created before per-Global-Hotkey enabled
// states existed continue to load with every global hotkey enabled. Also covers migration
// of the earlier M002 wire format (keys were the Win32 stable hotkey ids 1–4) to the
// current route-name-keyed format.

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

    // All four capture routes, for exhaustive default/enabled assertions.
    private static readonly GlobalHotkeyRoute[] AllRoutes =
    {
        GlobalHotkeyRoute.CurrentMonitor,
        GlobalHotkeyRoute.ActiveWindow,
        GlobalHotkeyRoute.FullDesktop,
        GlobalHotkeyRoute.RectangularRegion,
    };

    // ── Default: null states means every global hotkey is enabled ───────────────

    [Fact]
    public void IsGlobalHotkeyEnabled_NullStates_EnabledForEveryRoute()
    {
        var settings = new AppSettings();

        Assert.All(AllRoutes, route => Assert.True(settings.IsGlobalHotkeyEnabled(route)));
    }

    [Fact]
    public void WithDefaults_GlobalHotkeyEnabledStatesIsNull_AllEnabledByDefault()
    {
        var settings = AppSettings.WithDefaults();

        Assert.Null(settings.GlobalHotkeyEnabledStates);
        Assert.True(settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.ActiveWindow));
    }

    // ── Per-route resolution ────────────────────────────────────────────────

    [Fact]
    public void IsGlobalHotkeyEnabled_MissingRouteTreatedAsEnabled()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.ActiveWindow] = false,
            },
        };

        // ActiveWindow is explicitly disabled; every other route is absent -> enabled.
        Assert.True(settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));
        Assert.False(settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.ActiveWindow));
        Assert.True(settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.FullDesktop));
        Assert.True(settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.RectangularRegion));
    }

    [Fact]
    public void IsGlobalHotkeyEnabled_ExplicitTrue_IsEnabled()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.FullDesktop] = true,
            },
        };

        Assert.True(settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.FullDesktop));
    }

    // ── Normalized() deep-copies the states dictionary ───────────────────

    [Fact]
    public void Normalized_DeepCopiesGlobalHotkeyEnabledStates()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.CurrentMonitor] = false,
                [GlobalHotkeyRoute.RectangularRegion] = false,
            },
        };

        var normalized = source.Normalized();

        // Mutating the source after Normalized must not affect the copy.
        source.GlobalHotkeyEnabledStates![GlobalHotkeyRoute.CurrentMonitor] = true;
        Assert.False(normalized.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));

        // Mutating the copy must not affect the source.
        normalized.GlobalHotkeyEnabledStates![GlobalHotkeyRoute.RectangularRegion] = true;
        Assert.False(source.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.RectangularRegion));
    }

    [Fact]
    public void Normalized_NullGlobalHotkeyStates_StaysNull()
    {
        var source = new AppSettings();

        var normalized = source.Normalized();

        Assert.Null(normalized.GlobalHotkeyEnabledStates);
    }

    // ── JSON round-trip through ConfigurationService (route-name keys) ─────

    [Fact]
    public void SaveThenLoad_PreservesGlobalHotkeyEnabledStates()
    {
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings
        {
            SaveLocation = Path.Combine(_tempDir, "Captures"),
            FilenameTemplate = "shot_<#>",
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.CurrentMonitor] = true,
                [GlobalHotkeyRoute.ActiveWindow] = false,
                [GlobalHotkeyRoute.FullDesktop] = true,
                [GlobalHotkeyRoute.RectangularRegion] = false,
            },
        };

        var saveResult = svc.Save(original);
        Assert.True(saveResult.Success);

        var loaded = svc.Load();
        Assert.True(loaded.Success);

        Assert.NotNull(loaded.Settings.GlobalHotkeyEnabledStates);
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));
        Assert.False(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.ActiveWindow));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.FullDesktop));
        Assert.False(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.RectangularRegion));
    }

    [Fact]
    public void SaveThenLoad_AllEnabledGlobalHotkeys_OmitsOrPreservesCleanly()
    {
        var svc = new ConfigurationService(_tempDir);
        var original = new AppSettings
        {
            GlobalHotkeyEnabledStates = AllRoutes.ToDictionary(r => r, _ => true),
        };

        svc.Save(original);
        var loaded = svc.Load();

        // All enabled round-trips as enabled regardless of representation.
        Assert.All(AllRoutes, route => Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(route)));
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
        Assert.All(AllRoutes, route => Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(route)));
    }

    // ── Legacy migration: M002 wire format (stable-id keys 1–4) ────────────────

    [Fact]
    public void Load_LegacyIntIdKeys_MigratesToRoutesPreservingIntent()
    {
        // Settings written by M002 keyed enabled state by the Win32 stable hotkey ids
        // (1=CurrentMonitor, 2=ActiveWindow, 3=FullDesktop, 4=RectangularRegion).
        // These must load with the user's intent preserved, not reset to defaults.
        var legacyJson = """
        {
          "saveLocation": "C:\\Captures",
          "hotkeyEnabledStates": {
            "1": false,
            "3": false
          }
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), legacyJson);

        var svc = new ConfigurationService(_tempDir);
        var loaded = svc.Load();

        Assert.True(loaded.Success);
        Assert.NotNull(loaded.Settings.GlobalHotkeyEnabledStates);
        // id 1 -> CurrentMonitor, id 3 -> FullDesktop were disabled.
        Assert.False(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.ActiveWindow));
        Assert.False(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.FullDesktop));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.RectangularRegion));
    }

    [Fact]
    public void Load_LegacyIntIdKeys_AllExplicit_RoundTripsAsRouteNames()
    {
        // Every legacy id present; after load+save the file is rewritten with route names.
        var legacyJson = """
        {
          "hotkeyEnabledStates": { "1": true, "2": false, "3": true, "4": false }
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), legacyJson);

        var svc = new ConfigurationService(_tempDir);
        var loaded = svc.Load();
        Assert.True(loaded.Success);
        svc.Save(loaded.Settings);

        string rewritten = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));
        Assert.Contains("\"currentMonitor\"", rewritten);
        Assert.Contains("\"activeWindow\"", rewritten);
        Assert.Contains("\"fullDesktop\"", rewritten);
        Assert.Contains("\"rectangularRegion\"", rewritten);
        // No legacy numeric keys remain after the rewrite.
        Assert.DoesNotContain("\"1\"", rewritten);
        Assert.DoesNotContain("\"2\"", rewritten);
    }

    [Fact]
    public void Load_RouteNameKeys_CaseInsensitive_LoadsCorrectly()
    {
        // The current wire format uses camelCase route names; reading is case-insensitive.
        var json = """
        {
          "hotkeyEnabledStates": { "FullDesktop": false }
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), json);

        var svc = new ConfigurationService(_tempDir);
        var loaded = svc.Load();

        Assert.True(loaded.Success);
        Assert.False(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.FullDesktop));
        Assert.True(loaded.Settings.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));
    }

    // ── JSON shape ───────────────────────────────────────────────────────

    [Fact]
    public void Save_WithDisabledGlobalHotkeys_WritesRouteNameKeys()
    {
        var svc = new ConfigurationService(_tempDir);
        svc.Save(new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.FullDesktop] = false,
            },
        });

        string json = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));

        Assert.Contains("\"hotkeyEnabledStates\"", json);
        // Dictionary keys are the route names (camelCase) — the UI-independent identity.
        Assert.Contains("\"fullDesktop\"", json);
    }

    [Fact]
    public void Save_AllEnabledNullStates_OmitsGlobalHotkeyField()
    {
        var svc = new ConfigurationService(_tempDir);
        svc.Save(new AppSettings { GlobalHotkeyEnabledStates = null });

        string json = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));

        Assert.DoesNotContain("hotkeyEnabledStates", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validate(): structured, field-attributed source of truth (#13) ──────────

    [Fact]
    public void Validate_EmptySettings_HasNoIssues()
    {
        var settings = new AppSettings();

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Validate_WithDefaults_HasNoIssues()
    {
        var settings = AppSettings.WithDefaults();

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Validate_RelativeSaveLocation_AttributedToSaveLocationField()
    {
        var settings = new AppSettings { SaveLocation = "relative/path" };

        var issue = Assert.Single(settings.Validate());

        Assert.Equal(SettingsField.SaveLocation, issue.Field);
        Assert.Contains("absolute", issue.Message);
    }

    [Fact]
    public void Validate_EmptySaveLocation_HasNoIssue_AllowingDefaultFallback()
    {
        // Empty/whitespace resolves to the default on save; it is not a validation error.
        var settings = new AppSettings { SaveLocation = "   " };

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Validate_WhitespaceFilenameTemplate_AttributedToFilenameTemplateField()
    {
        var settings = new AppSettings { FilenameTemplate = "   " };

        var issue = Assert.Single(settings.Validate());

        Assert.Equal(SettingsField.FilenameTemplate, issue.Field);
        Assert.Contains("empty", issue.Message);
    }

    [Fact]
    public void Validate_NullFilenameTemplate_HasNoIssue_AllowingDefaultFallback()
    {
        var settings = new AppSettings { FilenameTemplate = null };

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Validate_MultipleInvalidFields_ReportsEachWithAttribution()
    {
        var settings = new AppSettings
        {
            SaveLocation = "relative",
            FilenameTemplate = "  ",
        };

        var issues = settings.Validate();

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Field == SettingsField.SaveLocation);
        Assert.Contains(issues, i => i.Field == SettingsField.FilenameTemplate);
    }

    // ── Validate(): Global Hotkey enabled-states rule (extended for #13) ────────

    [Fact]
    public void Validate_GlobalHotkeyStates_KnownRoutes_HasNoIssue()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyEnabledStates = AllRoutes.ToDictionary(r => r, _ => true),
        };

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Validate_GlobalHotkeyStates_Null_HasNoIssue()
    {
        var settings = new AppSettings { GlobalHotkeyEnabledStates = null };

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Validate_GlobalHotkeyStates_UnknownRoute_AttributedToHotkeyField()
    {
        // Defensive boundary check: an out-of-range route key (only reachable through a
        // programming error or corruption; the typed API and JSON converter cannot
        // introduce one through normal use) is flagged so the hotkey tab is never
        // "valid by accident".
        var settings = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [(GlobalHotkeyRoute)999] = true,
            },
        };

        var issue = Assert.Single(settings.Validate());

        Assert.Equal(SettingsField.GlobalHotkeyEnabledStates, issue.Field);
        Assert.Contains("unknown route", issue.Message);
    }
}
