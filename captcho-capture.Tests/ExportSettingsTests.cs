// ExportSettingsTests — Validates the persisted export format settings slice:
// PNG/JPEG selection, JPEG quality boundaries (inclusive 0–100), validation,
// normalization, Configuration round-trip, and safe upgrade of legacy files
// that predate the export-format fields (issue #45).

using System;
using System.IO;
using System.Text;
using Xunit;

namespace captcho.Capture.Tests;

public class ExportSettingsTests
{
    // ── Defaults ─────────────────────────────────────────────────────────

    [Fact]
    public void WithDefaults_IsPng()
    {
        var settings = ExportSettings.WithDefaults();

        Assert.Equal(ExportImageFormat.Png, settings.Format);
    }

    [Fact]
    public void WithDefaults_JpegQualityIsInRange()
    {
        var settings = ExportSettings.WithDefaults();

        Assert.InRange(settings.JpegQuality,
            ExportSettings.MinimumJpegQuality, ExportSettings.MaximumJpegQuality);
    }

    [Fact]
    public void Constants_QualityBoundsAreInclusiveZeroThroughOneHundred()
    {
        Assert.Equal(0, ExportSettings.MinimumJpegQuality);
        Assert.Equal(100, ExportSettings.MaximumJpegQuality);
    }

    // ── Extension derivation ─────────────────────────────────────────────

    [Theory]
    [InlineData(ExportImageFormat.Png, "png")]
    [InlineData(ExportImageFormat.Jpeg, "jpg")]
    public void Extension_DerivesFromFormat(ExportImageFormat format, string expected)
    {
        Assert.Equal(expected, format.Extension());
    }

    // ── Normalization ────────────────────────────────────────────────────

    [Fact]
    public void Normalized_UndefinedFormat_FallsBackToPng()
    {
        var settings = new ExportSettings((ExportImageFormat)99, 80);

        var normalized = settings.Normalized();

        Assert.Equal(ExportImageFormat.Png, normalized.Format);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Normalized_QualityOutOfRange_FallsBackToDefault(int quality)
    {
        var settings = new ExportSettings(ExportImageFormat.Jpeg, quality);

        var normalized = settings.Normalized();

        Assert.Equal(ExportSettings.DefaultJpegQuality, normalized.JpegQuality);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void Normalized_QualityBoundaries_ArePreserved(int quality)
    {
        var settings = new ExportSettings(ExportImageFormat.Jpeg, quality);

        var normalized = settings.Normalized();

        Assert.Equal(quality, normalized.JpegQuality);
    }

    // ── Validation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void Validate_QualityBoundaries_ProduceNoIssues(int quality)
    {
        var settings = new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, quality),
        };

        Assert.Empty(settings.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_QualityOutOfRange_ReportsExportSettingsIssue(int quality)
    {
        var settings = new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, quality),
        };

        var issues = settings.Validate();
        var issue = Assert.Single(issues);
        Assert.Equal(SettingsField.ExportSettings, issue.Field);
    }

    [Fact]
    public void Validate_NullExportSettings_ReportsExportSettingsIssue()
    {
        var settings = new AppSettings { ExportSettings = null! };

        var issues = settings.Validate();
        var issue = Assert.Single(issues);
        Assert.Equal(SettingsField.ExportSettings, issue.Field);
    }

    // ── AppSettings integration ──────────────────────────────────────────

    [Fact]
    public void AppSettings_NullExportSettings_EffectiveFallsBackToDefaults()
    {
        var settings = new AppSettings { ExportSettings = null! };

        Assert.Equal(ExportSettings.WithDefaults(), settings.EffectiveExportSettings);
    }

    [Fact]
    public void Normalized_CarriesExportSettingsForward()
    {
        var settings = new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 73),
        };

        var normalized = settings.Normalized();

        Assert.Equal(ExportImageFormat.Jpeg, normalized.ExportSettings!.Format);
        Assert.Equal(73, normalized.ExportSettings.JpegQuality);
    }

    // ── Configuration persistence ────────────────────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTripsFormatAndQuality()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"captchoTests_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var svc = new ConfigurationService(dir);
            var original = new AppSettings
            {
                ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 73),
            };

            Assert.True(svc.Save(original).Success);

            var loaded = svc.Load();
            Assert.True(loaded.Success);
            Assert.Equal(ExportImageFormat.Jpeg, loaded.Settings.EffectiveExportSettings.Format);
            Assert.Equal(73, loaded.Settings.EffectiveExportSettings.JpegQuality);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Load_LegacyFileWithoutExportSettings_UpgradesSafelyToPngDefaults()
    {
        // A settings file written before the export-format fields shipped
        // (issue #45) must load with PNG defaults, not fail.
        var dir = Path.Combine(Path.GetTempPath(), $"captchoTests_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            string legacyJson = """{"saveLocation":"C:\\Test","filenameTemplate":"shot_<#>"}""";
            File.WriteAllText(Path.Combine(dir, "settings.json"), legacyJson, Encoding.UTF8);

            var result = new ConfigurationService(dir).Load();

            Assert.True(result.Success);
            Assert.False(result.UsedDefaults);
            Assert.Equal(ExportImageFormat.Png, result.Settings.EffectiveExportSettings.Format);
            Assert.Equal(ExportSettings.DefaultJpegQuality,
                result.Settings.EffectiveExportSettings.JpegQuality);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Load_EmptyJsonObject_UsesPngDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"captchoTests_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{}", Encoding.UTF8);

            var result = new ConfigurationService(dir).Load();

            Assert.True(result.Success);
            Assert.Equal(ExportImageFormat.Png, result.Settings.EffectiveExportSettings.Format);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
