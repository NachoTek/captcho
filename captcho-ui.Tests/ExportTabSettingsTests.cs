// ExportTabSettingsTests.cs — Headless tests for the read-only Export tab seam.
//
// Verifies the pure C# ExportTabSettings coordinator surfaces the current export
// format as PNG with the .png extension, provides a readable description of why
// PNG is the default, and never presents JPEG controls or implies that JPEG
// export is available. The tab is read-only, so there is no editing, validation,
// or save session to exercise — only the displayed content.

using System;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class ExportTabSettingsTests
{
    // ── The current export format is PNG with the .png extension ────────

    [Fact]
    public void Heading_IsNonEmptyAndReadable()
    {
        var tab = new ExportTabSettings();

        Assert.False(string.IsNullOrWhiteSpace(tab.Heading));
        Assert.Contains("Export", tab.Heading, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatName_IsPng_DerivedFromExportDefaults()
    {
        var tab = new ExportTabSettings();

        Assert.Equal("PNG", tab.FormatName);
        Assert.Equal(ExportDefaults.DefaultExtension.ToUpperInvariant(), tab.FormatName);
    }

    [Fact]
    public void FileExtension_IsDotPng_DerivedFromExportDefaults()
    {
        var tab = new ExportTabSettings();

        Assert.Equal(".png", tab.FileExtension);
        Assert.Equal("." + ExportDefaults.DefaultExtension, tab.FileExtension);
    }

    // ── The description explains PNG as the current export format ───────

    [Fact]
    public void FormatDescription_IsNonEmptyAndMentionsPng()
    {
        var tab = new ExportTabSettings();

        Assert.False(string.IsNullOrWhiteSpace(tab.FormatDescription));
        Assert.Contains("PNG", tab.FormatDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatDescription_IsReadableProseNotADevPlaceholder()
    {
        var tab = new ExportTabSettings();

        // Readable explanatory text, not a "not implemented" development placeholder.
        Assert.DoesNotContain("not implemented", tab.FormatDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("placeholder", tab.FormatDescription, StringComparison.OrdinalIgnoreCase);
    }

    // ── A planned-formats note exists but never implies JPEG is available ──

    [Fact]
    public void PlannedFormatsNote_IsNonEmptyAndMentionsFutureFormats()
    {
        var tab = new ExportTabSettings();

        Assert.False(string.IsNullOrWhiteSpace(tab.PlannedFormatsNote));
        Assert.Contains("planned", tab.PlannedFormatsNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SurfacedText_DoesNotImplyJpegExportIsAvailable()
    {
        var tab = new ExportTabSettings();

        // None of the surfaced read-only text may present JPEG controls or imply
        // that JPEG export is available (acceptance criterion).
        var surfaced = new[]
        {
            tab.Heading,
            tab.FormatName,
            tab.FileExtension,
            tab.FormatDescription,
            tab.PlannedFormatsNote,
        };

        foreach (var text in surfaced)
        {
            Assert.DoesNotContain("JPEG", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("JPG", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Every surfaced string is non-empty readable prose ──────────────

    [Fact]
    public void AllSurfacedText_IsNonEmptyReadableProse()
    {
        var tab = new ExportTabSettings();

        Assert.False(string.IsNullOrWhiteSpace(tab.Heading));
        Assert.False(string.IsNullOrWhiteSpace(tab.FormatName));
        Assert.False(string.IsNullOrWhiteSpace(tab.FileExtension));
        Assert.False(string.IsNullOrWhiteSpace(tab.FormatDescription));
        Assert.False(string.IsNullOrWhiteSpace(tab.PlannedFormatsNote));
    }
}
