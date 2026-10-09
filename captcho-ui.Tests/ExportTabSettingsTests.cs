// ExportTabSettingsTests.cs — Headless tests for the editable Export tab seam.
//
// Verifies the ExportTabSettings editing session (issue #45): format selection
// between PNG and JPEG, JPEG quality editing with inclusive 0–100 validation,
// the derived extension following the working format, and the quality control's
// availability being derived from the format (disabled for PNG).

using System;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class ExportTabSettingsTests
{
    // ── Format selection ─────────────────────────────────────────────────

    [Fact]
    public void Format_DefaultsToPng()
    {
        var tab = new ExportTabSettings(new AppSettings());

        Assert.Equal(ExportImageFormat.Png, tab.Format);
    }

    [Fact]
    public void EditFormat_Jpeg_UpdatesWorkingFormatAndDerivedExtension()
    {
        var tab = new ExportTabSettings(AppSettings.WithDefaults());

        tab.EditFormat(ExportImageFormat.Jpeg);

        Assert.Equal(ExportImageFormat.Jpeg, tab.Format);
        Assert.Equal(".jpg", tab.FileExtension);
    }

    [Fact]
    public void EditFormat_Png_RestoresPngExtension()
    {
        var tab = new ExportTabSettings(new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 80),
        });

        tab.EditFormat(ExportImageFormat.Png);

        Assert.Equal(ExportImageFormat.Png, tab.Format);
        Assert.Equal(".png", tab.FileExtension);
    }

    [Fact]
    public void FileExtension_FollowsPersistedFormatOnConstruction()
    {
        var tab = new ExportTabSettings(new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 80),
        });

        Assert.Equal(ExportImageFormat.Jpeg, tab.Format);
        Assert.Equal(".jpg", tab.FileExtension);
    }

    // ── Quality editing ──────────────────────────────────────────────────

    [Fact]
    public void JpegQuality_ReflectsPersistedValue()
    {
        var tab = new ExportTabSettings(new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 73),
        });

        Assert.Equal(73, tab.JpegQuality);
    }

    [Fact]
    public void EditJpegQuality_UpdatesWorkingValue()
    {
        var tab = new ExportTabSettings(AppSettings.WithDefaults());

        tab.EditJpegQuality(42);

        Assert.Equal(42, tab.JpegQuality);
    }

    // ── Quality availability is derived from the format ──────────────────

    [Fact]
    public void JpegQualityAvailable_IsFalseForPng()
    {
        var tab = new ExportTabSettings(AppSettings.WithDefaults());

        Assert.False(tab.JpegQualityAvailable);
    }

    [Fact]
    public void JpegQualityAvailable_IsTrueForJpeg()
    {
        var tab = new ExportTabSettings(new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 80),
        });

        Assert.True(tab.JpegQualityAvailable);
    }

    [Fact]
    public void EditFormat_Png_DisablesQualityWithoutDiscardingTheValue()
    {
        // Switching to PNG disables the quality control but keeps the working
        // value, so a format round-trip never silently discards the user's
        // quality choice.
        var tab = new ExportTabSettings(new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 65),
        });

        tab.EditFormat(ExportImageFormat.Png);
        Assert.False(tab.JpegQualityAvailable);

        tab.EditFormat(ExportImageFormat.Jpeg);
        Assert.True(tab.JpegQualityAvailable);
        Assert.Equal(65, tab.JpegQuality);
    }

    // ── Validation: quality is inclusive 0–100 ───────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void IsValid_QualityBoundaries_AreValid(int quality)
    {
        var tab = new ExportTabSettings(new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 80),
        });

        tab.EditJpegQuality(quality);

        Assert.True(tab.IsValid);
        Assert.Null(tab.JpegQualityError);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void IsValid_QualityOutOfRange_IsInvalidWithInlineError(int quality)
    {
        var tab = new ExportTabSettings(new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 80),
        });

        tab.EditJpegQuality(quality);

        Assert.False(tab.IsValid);
        Assert.NotNull(tab.JpegQualityError);
        Assert.NotNull(tab.FirstError);
    }

    [Fact]
    public void IsValid_QualityOutOfRangeWhilePng_IsStillInvalid()
    {
        // Quality is validated even when PNG is selected, so an invalid
        // persisted value cannot slip through behind a disabled control.
        var tab = new ExportTabSettings(AppSettings.WithDefaults());

        tab.EditJpegQuality(101);
        tab.EditFormat(ExportImageFormat.Png);

        Assert.False(tab.IsValid);
    }

    // ── Dirty tracking, merge, defaults ──────────────────────────────────

    [Fact]
    public void IsDirty_FollowsFormatAndQualityEdits()
    {
        var tab = new ExportTabSettings(AppSettings.WithDefaults());

        Assert.False(tab.IsDirty);

        tab.EditFormat(ExportImageFormat.Jpeg);
        Assert.True(tab.IsDirty);

        tab.EditFormat(ExportImageFormat.Png);
        Assert.False(tab.IsDirty);

        tab.EditJpegQuality(50);
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void WriteInto_WritesOnlyTheExportSlice()
    {
        var tab = new ExportTabSettings(AppSettings.WithDefaults());
        tab.EditFormat(ExportImageFormat.Jpeg);
        tab.EditJpegQuality(55);

        var target = new AppSettings
        {
            SaveLocation = @"D:\Keep",
            FilenameTemplate = "keep_<#>",
        };
        tab.WriteInto(target);

        Assert.Equal(ExportImageFormat.Jpeg, target.ExportSettings!.Format);
        Assert.Equal(55, target.ExportSettings.JpegQuality);
        Assert.Equal(@"D:\Keep", target.SaveLocation);
        Assert.Equal("keep_<#>", target.FilenameTemplate);
    }

    [Fact]
    public void ApplyDefaults_RestoresPngDefaultsInWorkingStateOnly()
    {
        var persisted = new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 73),
        };
        var tab = new ExportTabSettings(persisted);

        tab.Reset();

        Assert.Equal(ExportImageFormat.Png, tab.Format);
        Assert.Equal(ExportSettings.DefaultJpegQuality, tab.JpegQuality);
        // The persisted source is untouched by Reset alone.
        Assert.Equal(ExportImageFormat.Jpeg, persisted.ExportSettings.Format);
    }

    [Fact]
    public void Cancel_RevertsWorkingEditsToBaseline()
    {
        var persisted = new AppSettings
        {
            ExportSettings = new ExportSettings(ExportImageFormat.Jpeg, 73),
        };
        var tab = new ExportTabSettings(persisted);

        tab.EditFormat(ExportImageFormat.Png);
        tab.EditJpegQuality(10);
        tab.Cancel();

        Assert.Equal(ExportImageFormat.Jpeg, tab.Format);
        Assert.Equal(73, tab.JpegQuality);
    }
}
