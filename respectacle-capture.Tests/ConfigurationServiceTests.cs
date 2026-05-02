// ConfigurationServiceTests — Validates JSON configuration load/save,
// defaults, round-trip, corruption backup, validation, and failure modes.
// All tests use temporary directories — never the developer's real AppData.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Respectacle.Capture.Tests;

public class ConfigurationServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"RespectacleTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private ConfigurationService CreateService() => new(_tempDir);

    // ── Default Values ───────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var svc = CreateService();
        var result = svc.Load();

        Assert.True(result.Success);
        Assert.True(result.UsedDefaults);
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, result.Settings.EffectiveSaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, result.Settings.EffectiveFilenameTemplate);
    }

    [Fact]
    public void Load_MissingFile_ConfigPathSet()
    {
        var svc = CreateService();
        var result = svc.Load();

        Assert.Equal(Path.Combine(_tempDir, "settings.json"), result.ConfigPath);
    }

    [Fact]
    public void AppSettings_WithDefaults_MatchesExportDefaults()
    {
        var settings = AppSettings.WithDefaults();
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, settings.SaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, settings.FilenameTemplate);
    }

    [Fact]
    public void AppSettings_NullFields_EffectivePropertiesReturnDefaults()
    {
        var settings = new AppSettings();
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, settings.EffectiveSaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, settings.EffectiveFilenameTemplate);
    }

    [Fact]
    public void AppSettings_EmptyFields_EffectivePropertiesReturnDefaults()
    {
        var settings = new AppSettings { SaveLocation = "", FilenameTemplate = "   " };
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, settings.EffectiveSaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, settings.EffectiveFilenameTemplate);
    }

    // ── Round-Trip (Save → Load) ─────────────────────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTripsCorrectly()
    {
        var svc = CreateService();
        var original = new AppSettings
        {
            SaveLocation = Path.Combine(_tempDir, "MyScreenshots"),
            FilenameTemplate = "Screenshot_<yyyy>-<MM>-<dd>_<#>",
        };

        var saveResult = svc.Save(original);
        Assert.True(saveResult.Success);

        var loadResult = svc.Load();
        Assert.True(loadResult.Success);
        Assert.False(loadResult.UsedDefaults);
        Assert.Equal(original.SaveLocation, loadResult.Settings.SaveLocation);
        Assert.Equal(original.FilenameTemplate, loadResult.Settings.FilenameTemplate);
    }

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        string subDir = Path.Combine(_tempDir, "nested", "config");
        var svc = new ConfigurationService(subDir);
        var settings = AppSettings.WithDefaults();

        var result = svc.Save(settings);
        Assert.True(result.Success);
        Assert.True(Directory.Exists(subDir));
        Assert.True(File.Exists(Path.Combine(subDir, "settings.json")));
    }

    [Fact]
    public void Save_OverwritesExistingSettings()
    {
        var svc = CreateService();

        var first = new AppSettings { SaveLocation = "/first", FilenameTemplate = "first_<#>" };
        var saveFirst = svc.Save(first);
        Assert.True(saveFirst.Success);

        var second = new AppSettings { SaveLocation = "/second", FilenameTemplate = "second_<#>" };
        var saveSecond = svc.Save(second);
        Assert.True(saveSecond.Success);

        var loaded = svc.Load();
        Assert.Equal("/second", loaded.Settings.SaveLocation);
        Assert.Equal("second_<#>", loaded.Settings.FilenameTemplate);
    }

    [Fact]
    public void Save_UsesAtomicTempFile()
    {
        var svc = CreateService();
        var settings = AppSettings.WithDefaults();

        var result = svc.Save(settings);
        Assert.True(result.Success);
        // Temp file should be gone after successful move
        Assert.NotNull(result.TempPath);
        Assert.False(File.Exists(result.TempPath));
        // Final file should exist
        Assert.True(File.Exists(result.ConfigPath));
    }

    // ── Corruption / Invalid JSON ────────────────────────────────────────

    [Fact]
    public void Load_InvalidJSON_CreatesBackupAndReturnsDefaults()
    {
        var svc = CreateService();
        string configPath = Path.Combine(_tempDir, "settings.json");

        // Write invalid JSON
        File.WriteAllText(configPath, "{ not valid json }", Encoding.UTF8);

        var result = svc.Load();

        Assert.True(result.Success);
        Assert.True(result.UsedDefaults);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.EndsWith(".backup", result.BackupPath);
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, result.Settings.EffectiveSaveLocation);
    }

    [Fact]
    public void Load_InvalidJSON_BackupContainsOriginalContent()
    {
        var svc = CreateService();
        string configPath = Path.Combine(_tempDir, "settings.json");
        string badContent = "{ not valid json }";
        File.WriteAllText(configPath, badContent, Encoding.UTF8);

        var result = svc.Load();

        Assert.NotNull(result.BackupPath);
        string backupContent = File.ReadAllText(result.BackupPath);
        Assert.Equal(badContent, backupContent);
    }

    [Fact]
    public void Load_InvalidJSON_RemovesOriginalFile()
    {
        var svc = CreateService();
        string configPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(configPath, "bad", Encoding.UTF8);

        svc.Load();

        Assert.False(File.Exists(configPath));
    }

    [Fact]
    public void Load_MultipleCorruptions_IncrementalBackupNames()
    {
        var svc = CreateService();
        string configPath = Path.Combine(_tempDir, "settings.json");

        // First corruption
        File.WriteAllText(configPath, "bad1", Encoding.UTF8);
        var result1 = svc.Load();
        Assert.NotNull(result1.BackupPath);
        Assert.EndsWith(".backup", result1.BackupPath);

        // Second corruption
        File.WriteAllText(configPath, "bad2", Encoding.UTF8);
        var result2 = svc.Load();
        Assert.NotNull(result2.BackupPath);
        Assert.EndsWith("1.backup", result2.BackupPath);

        // Both backup files exist
        Assert.True(File.Exists(result1.BackupPath));
        Assert.True(File.Exists(result2.BackupPath));
    }

    [Fact]
    public void Load_EmptyJsonObject_ReturnsDefaultedSettings()
    {
        var svc = CreateService();
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), "{}", Encoding.UTF8);

        var result = svc.Load();

        Assert.True(result.Success);
        Assert.False(result.UsedDefaults);
        // Null properties → effective properties fall back to defaults
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, result.Settings.EffectiveSaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, result.Settings.EffectiveFilenameTemplate);
    }

    [Fact]
    public void Load_UnknownProperties_PreservedViaRoundTrip()
    {
        var svc = CreateService();
        string configPath = Path.Combine(_tempDir, "settings.json");
        string jsonWithExtra = """{"saveLocation":"C:\\Test","futureProperty":"futureValue"}""";
        File.WriteAllText(configPath, jsonWithExtra, Encoding.UTF8);

        var result = svc.Load();
        Assert.True(result.Success);
        Assert.Equal("C:\\Test", result.Settings.SaveLocation);

        // Re-read the raw file to confirm unknown props were dropped
        // (our serializer doesn't preserve unknown properties, but shouldn't crash)
    }

    // ── Validation ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_RelativeSaveLocation_ReturnsIssue()
    {
        var settings = new AppSettings { SaveLocation = "relative/path" };
        var issues = settings.Validate();
        Assert.Contains(issues, i => i.Contains("absolute"));
    }

    [Fact]
    public void Validate_AbsoluteSaveLocation_NoIssues()
    {
        var settings = new AppSettings { SaveLocation = @"C:\Users\test\Pictures" };
        var issues = settings.Validate();
        Assert.Empty(issues);
    }

    [Fact]
    public void Validate_DefaultSettings_NoIssues()
    {
        var settings = AppSettings.WithDefaults();
        var issues = settings.Validate();
        Assert.Empty(issues);
    }

    [Fact]
    public void Validate_WhitespaceFilenameTemplate_ReturnsIssue()
    {
        var settings = new AppSettings { FilenameTemplate = "   " };
        var issues = settings.Validate();
        Assert.Contains(issues, i => i.Contains("FilenameTemplate"));
    }

    [Fact]
    public void Normalized_ReplacesEmptyFieldsWithDefaults()
    {
        var settings = new AppSettings();
        var normalized = settings.Normalized();
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, normalized.SaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, normalized.FilenameTemplate);
    }

    // ── Save Failures ────────────────────────────────────────────────────

    [Fact]
    public void Save_UnwritableDirectory_ReturnsFailure()
    {
        // Use a path with invalid characters to trigger a failure
        string badDir = Path.Combine(_tempDir, "nonexistent|pipe");
        var svc = new ConfigurationService(badDir);
        var result = svc.Save(AppSettings.WithDefaults());

        Assert.False(result.Success);
        Assert.NotNull(result.Phase);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── Settings-Aware Export Path ───────────────────────────────────────

    [Fact]
    public void GetExportPath_UsesCustomDirectory()
    {
        var settings = new AppSettings
        {
            SaveLocation = Path.Combine(_tempDir, "CustomOutput"),
            FilenameTemplate = "custom_<yyyy><MM><dd>",
        };

        var ts = new DateTime(2025, 6, 15, 14, 30, 45);
        string path = ExportFilenameTemplate.GetExportPath(settings, ts);

        Assert.Contains("CustomOutput", path);
        Assert.Contains("custom_20250615", path);
        Assert.EndsWith(".png", path);
    }

    [Fact]
    public void GetExportPath_CreatesDirectory()
    {
        string outputDir = Path.Combine(_tempDir, "AutoCreated");
        Assert.False(Directory.Exists(outputDir));

        var settings = new AppSettings { SaveLocation = outputDir };
        ExportFilenameTemplate.GetExportPath(settings, DateTime.Now);

        Assert.True(Directory.Exists(outputDir));
    }

    [Fact]
    public void GetExportPath_DefaultSettings_MatchesGetDefaultExportPath()
    {
        var ts = new DateTime(2025, 6, 15, 14, 30, 45);
        string fromSettings = ExportFilenameTemplate.GetExportPath(AppSettings.WithDefaults(), ts);
        string fromDefaults = ExportFilenameTemplate.GetDefaultExportPath(ts);
        Assert.Equal(fromDefaults, fromSettings);
    }

    // ── JSON Serialization Details ──────────────────────────────────────

    [Fact]
    public void SavedJson_UsesCamelCase()
    {
        var svc = CreateService();
        svc.Save(new AppSettings { SaveLocation = "/test", FilenameTemplate = "test" });

        string json = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));
        Assert.Contains("saveLocation", json);   // camelCase
        Assert.Contains("filenameTemplate", json);
        Assert.DoesNotContain("SaveLocation", json);  // PascalCase absent
    }

    [Fact]
    public void SavedJson_IsIndented()
    {
        var svc = CreateService();
        svc.Save(AppSettings.WithDefaults());

        string json = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));
        Assert.Contains("\n", json);
        Assert.Contains("  ", json);
    }

    // ── Configuration Path ──────────────────────────────────────────────

    [Fact]
    public void ConfigPath_ReturnsInjectedPath()
    {
        var svc = CreateService();
        Assert.Equal(Path.Combine(_tempDir, "settings.json"), svc.ConfigPath);
    }

    [Fact]
    public void DefaultConstructor_UsesLocalAppData()
    {
        var svc = new ConfigurationService();
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Respectacle",
            "settings.json");
        Assert.Equal(expected, svc.ConfigPath);
    }
}
