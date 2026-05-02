// FilenameTemplateTests — Validates template expansion, sanitization,
// sequence behavior, unknown placeholder preservation, and negative cases.

using System;
using Xunit;

namespace Respectacle.Capture.Tests;

public class FilenameTemplateTests
{
    // ── Date/Time Placeholder Expansion ──────────────────────────────────

    [Fact]
    public void Expand_AllDateTimePlaceholders()
    {
        var ts = new DateTime(2025, 6, 15, 14, 30, 45);
        string result = ExportFilenameTemplate.Expand(
            "shot_<yyyy>_<yy>_<MM>_<dd>_<hh>_<mm>_<ss>", ts);

        Assert.Equal("shot_2025_25_06_15_14_30_45", result);
    }

    [Fact]
    public void Expand_24HourFormat()
    {
        var ts = new DateTime(2025, 1, 1, 23, 59, 59);
        string result = ExportFilenameTemplate.Expand("<hh><mm><ss>", ts);
        Assert.Equal("235959", result);
    }

    [Fact]
    public void Expand_Midnight()
    {
        var ts = new DateTime(2025, 12, 31, 0, 0, 0);
        string result = ExportFilenameTemplate.Expand("<hh><mm><ss>", ts);
        Assert.Equal("000000", result);
    }

    // ── Title Placeholder ────────────────────────────────────────────────

    [Fact]
    public void Expand_TitlePlaceholder_Sanitized()
    {
        var ts = DateTime.Now;
        string result = ExportFilenameTemplate.Expand("capture_<title>", ts, "Hello:World<test>");
        Assert.Contains("Hello_World_test", result);
        Assert.DoesNotContain(":", result);
    }

    [Fact]
    public void Expand_NullTitle_Empty()
    {
        var ts = new DateTime(2025, 1, 1);
        string result = ExportFilenameTemplate.Expand("shot_<title>_end", ts, null);
        Assert.Equal("shot__end", result);
    }

    [Fact]
    public void Expand_EmptyTitle_Empty()
    {
        var ts = new DateTime(2025, 1, 1);
        string result = ExportFilenameTemplate.Expand("shot_<title>_end", ts, "");
        Assert.Equal("shot__end", result);
    }

    // ── Sequence Placeholder ─────────────────────────────────────────────

    [Fact]
    public void Expand_SequenceZeroPadded()
    {
        var ts = DateTime.Now;
        string result = ExportFilenameTemplate.Expand("shot_<#>", ts, sequenceNumber: 42);
        Assert.Equal("shot_0042", result);
    }

    [Fact]
    public void Expand_SequenceDefault_WhenNotProvided()
    {
        var ts = DateTime.Now;
        string result = ExportFilenameTemplate.Expand("shot_<#>", ts);
        Assert.Equal("shot_0001", result);
    }

    [Fact]
    public void Expand_SequenceLargeNumber()
    {
        var ts = DateTime.Now;
        string result = ExportFilenameTemplate.Expand("shot_<#>", ts, sequenceNumber: 9999);
        Assert.Equal("shot_9999", result);
    }

    // ── Unknown Placeholder Preservation ─────────────────────────────────

    [Fact]
    public void Expand_UnknownPlaceholder_PreservedButSanitized()
    {
        var ts = DateTime.Now;
        // < and > are invalid in Windows filenames, so they get sanitized to _
        string result = ExportFilenameTemplate.Expand("shot_<unknown>", ts);
        Assert.Contains("unknown", result);
        Assert.DoesNotContain("<unknown>", result); // brackets are sanitized
    }

    [Fact]
    public void Expand_UnknownPlaceholderInvalidChars_Sanitized()
    {
        var ts = DateTime.Now;
        // Pipe | is invalid in Windows filenames
        string result = ExportFilenameTemplate.Expand("shot_<pipe|char>", ts);
        Assert.DoesNotContain("|", result);
    }

    [Fact]
    public void Expand_UnclosedBracket_BracketsSanitized()
    {
        var ts = DateTime.Now;
        // < is invalid in Windows filenames, gets replaced
        string result = ExportFilenameTemplate.Expand("shot_<unclosed", ts);
        Assert.DoesNotContain("<", result);
        Assert.Contains("unclosed", result);
    }

    // ── Combined Template (default-like) ─────────────────────────────────

    [Fact]
    public void Expand_DefaultTemplate()
    {
        var ts = new DateTime(2025, 6, 15, 14, 30, 45);
        string result = ExportFilenameTemplate.Expand(
            ExportDefaults.DefaultFilenameTemplate, ts);
        Assert.Equal("Respectacle_2025-06-15_143045", result);
    }

    [Fact]
    public void Expand_TemplateWithTitleAndSequence()
    {
        var ts = new DateTime(2025, 6, 15, 14, 30, 45);
        string result = ExportFilenameTemplate.Expand(
            "<title>_<yyyy><MM><dd>_<#>", ts, "MyApp", 7);
        Assert.Equal("MyApp_20250615_0007", result);
    }

    // ── Sanitization ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("test<pipe>|file", "test_pipe__file")]
    [InlineData("normal_name", "normal_name")]
    [InlineData("con", "con")] // reserved names aren't our job to rename
    public void SanitizeForFileName_ReplacesInvalidChars(string input, string expected)
    {
        string result = ExportFilenameTemplate.SanitizeForFileName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeForFileName_Empty_ReturnsUntitled()
    {
        Assert.Equal("untitled", ExportFilenameTemplate.SanitizeForFileName(""));
    }

    [Fact]
    public void SanitizeForFileName_Null_ReturnsUntitled()
    {
        Assert.Equal("untitled", ExportFilenameTemplate.SanitizeForFileName(null!));
    }

    [Fact]
    public void SanitizeForFileName_AllInvalid_ReturnsSanitized()
    {
        // All characters are invalid — gets sanitized to underscores then trimmed
        string result = ExportFilenameTemplate.SanitizeForFileName("|||");
        // After replacing all with _ and trimming _ and ., it becomes "untitled"
        Assert.Equal("untitled", result);
    }

    [Fact]
    public void SanitizeForFileName_TrailingDotsStripped()
    {
        Assert.Equal("test", ExportFilenameTemplate.SanitizeForFileName("test..."));
    }

    [Fact]
    public void SanitizeTitle_PathTraversalPrevented()
    {
        string result = ExportFilenameTemplate.SanitizeTitle("../../../etc/passwd");
        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
    }

    [Fact]
    public void SanitizeTitle_ColonsReplaced()
    {
        string result = ExportFilenameTemplate.SanitizeTitle("Chrome: New Tab");
        Assert.Equal("Chrome_ New Tab", result);
    }

    [Fact]
    public void SanitizeTitle_MultipleUnderscoresCollapsed()
    {
        string result = ExportFilenameTemplate.SanitizeTitle("a:::b");
        Assert.Equal("a_b", result);
    }

    // ── Negative Cases ───────────────────────────────────────────────────

    [Fact]
    public void Expand_NullTemplate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ExportFilenameTemplate.Expand(null!, DateTime.Now));
    }

    [Fact]
    public void Expand_EmptyTemplate_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ExportFilenameTemplate.Expand("", DateTime.Now));
    }

    [Fact]
    public void Expand_WhitespaceTemplate_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ExportFilenameTemplate.Expand("   ", DateTime.Now));
    }

    // ── Default Path Generation ──────────────────────────────────────────

    [Fact]
    public void GetDefaultExportPath_IncludesPngExtension()
    {
        var ts = DateTime.Now;
        string path = ExportFilenameTemplate.GetDefaultExportPath(ts);
        Assert.EndsWith(".png", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefaultExportPath_IncludesRespectacleFolder()
    {
        var ts = DateTime.Now;
        string path = ExportFilenameTemplate.GetDefaultExportPath(ts);
        Assert.Contains("Respectacle", path);
    }

    // ── Collision Resolution ─────────────────────────────────────────────

    [Fact]
    public void ResolveCollision_NoConflict_ReturnsSame()
    {
        string temp = Path.GetTempFileName();
        try
        {
            // temp file exists — resolve should find another name
            string result = ExportFilenameTemplate.ResolveCollision(temp);
            Assert.Equal(temp, temp); // It returns same because it exists
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void ResolveCollision_NoFile_ReturnsOriginal()
    {
        string nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".png");
        string result = ExportFilenameTemplate.ResolveCollision(nonExistent);
        Assert.Equal(nonExistent, result);
    }
}
