// CliParserTests.cs — Tests for CLI argument parsing, validation, and path resolution.
//
// These tests verify the parser contract: valid modes, invalid inputs,
// output path resolution, help text, and exit code mapping.
// No native capture or filesystem I/O is performed.

using System;
using System.IO;
using Xunit;
using captcho.Cli;
using captcho.Capture;

namespace captcho.Cli.Tests;

public class CliParserTests
{
    // ── Help Tests ────────────────────────────────────────────────────────

    [Fact]
    public void Help_Long_Flag_ReturnsHelpText()
    {
        var result = CliParser.Parse(new[] { "--help" });
        Assert.NotNull(result.HelpText);
        Assert.Contains("Capture Modes", result.HelpText);
        Assert.Null(result.Options);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Help_Short_Flag_ReturnsHelpText()
    {
        var result = CliParser.Parse(new[] { "-h" });
        Assert.NotNull(result.HelpText);
        Assert.Contains("captcho CLI", result.HelpText);
    }

    [Fact]
    public void Help_Contains_ExitCodes()
    {
        var help = CliParser.BuildHelpText();
        Assert.Contains("Exit Codes", help);
        Assert.Contains("0  Success", help);
        Assert.Contains("1  Invalid arguments", help);
        Assert.Contains("2  Capture failed", help);
        Assert.Contains("3  Export failed", help);
        Assert.Contains("4  Internal error", help);
    }

    [Fact]
    public void Help_Contains_AllModes()
    {
        var help = CliParser.BuildHelpText();
        Assert.Contains("--full", help);
        Assert.Contains("--monitor", help);
        Assert.Contains("--window-active", help);
        Assert.Contains("--window-cursor", help);
        Assert.Contains("--region", help);
    }

    [Fact]
    public void Help_Contains_Examples()
    {
        var help = CliParser.BuildHelpText();
        Assert.Contains("Examples", help);
        Assert.Contains("--full", help);
        Assert.Contains("--monitor 1", help);
        Assert.Contains("--window-active", help);
        Assert.Contains("--region", help);
    }

    // ── Valid Mode Tests ──────────────────────────────────────────────────

    [Fact]
    public void FullMode_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--full" });
        Assert.NotNull(result.Options);
        Assert.Equal(CliCaptureMode.Full, result.Options.Mode);
    }

    [Fact]
    public void MonitorMode_WithIndex_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--monitor", "0" });
        Assert.NotNull(result.Options);
        Assert.Equal(CliCaptureMode.Monitor, result.Options.Mode);
        Assert.Equal(0, result.Options.MonitorIndex);
    }

    [Fact]
    public void MonitorMode_WithIndex2_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--monitor", "2" });
        Assert.NotNull(result.Options);
        Assert.Equal(CliCaptureMode.Monitor, result.Options.Mode);
        Assert.Equal(2, result.Options.MonitorIndex);
    }

    [Fact]
    public void MonitorMode_WithoutIndex_ParsesAsCurrentMonitor()
    {
        var result = CliParser.Parse(new[] { "--monitor", "--verbose" });
        Assert.NotNull(result.Options);
        Assert.Equal(CliCaptureMode.Monitor, result.Options.Mode);
        Assert.Null(result.Options.MonitorIndex);
    }

    [Fact]
    public void WindowActiveMode_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--window-active" });
        Assert.NotNull(result.Options);
        Assert.Equal(CliCaptureMode.WindowActive, result.Options.Mode);
    }

    [Fact]
    public void WindowCursorMode_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--window-cursor" });
        Assert.NotNull(result.Options);
        Assert.Equal(CliCaptureMode.WindowCursor, result.Options.Mode);
    }

    [Fact]
    public void RegionMode_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--region", "100,200,800,600" });
        Assert.NotNull(result.Options);
        Assert.Equal(CliCaptureMode.Region, result.Options.Mode);
        Assert.NotNull(result.Options.Region);
        Assert.Equal(100, result.Options.Region.X);
        Assert.Equal(200, result.Options.Region.Y);
        Assert.Equal(800, result.Options.Region.Width);
        Assert.Equal(600, result.Options.Region.Height);
    }

    [Fact]
    public void VerboseFlag_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--full", "--verbose" });
        Assert.NotNull(result.Options);
        Assert.True(result.Options.Verbose);
    }

    [Fact]
    public void VerboseShortFlag_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--full", "-v" });
        Assert.NotNull(result.Options);
        Assert.True(result.Options.Verbose);
    }

    [Fact]
    public void OutputOption_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--full", "--output", "C:\\test" });
        Assert.NotNull(result.Options);
        Assert.Contains("C:\\test", result.Options.OutputPath);
    }

    [Fact]
    public void OutputOption_PngPath_TreatedAsFile()
    {
        var result = CliParser.Parse(new[] { "--full", "--output", "C:\\test\\screenshot.png" });
        Assert.NotNull(result.Options);
        Assert.Equal("C:\\test\\screenshot.png", result.Options.OutputPath);
    }

    [Fact]
    public void FilenameOption_ParsesSuccessfully()
    {
        var result = CliParser.Parse(new[] { "--full", "--filename", "my_<yyyy>.png" });
        Assert.NotNull(result.Options);
        // Should contain expanded template
        Assert.DoesNotContain("<yyyy>", result.Options.OutputPath);
    }

    // ── Invalid Mode Tests ────────────────────────────────────────────────

    [Fact]
    public void NoArgs_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(Array.Empty<string>());
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void NullArgs_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(null!);
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
    }

    [Fact]
    public void NoMode_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--verbose" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("No capture mode", result.ErrorMessage);
    }

    [Fact]
    public void MultipleModes_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--full", "--window-active" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("Multiple capture modes", result.ErrorMessage);
    }

    [Fact]
    public void UnknownOption_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--full", "--nonexistent" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("Unknown option", result.ErrorMessage);
    }

    [Fact]
    public void InvalidMonitorIndex_NonNumeric_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--monitor", "nope" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("non-negative integer", result.ErrorMessage);
    }

    [Fact]
    public void InvalidMonitorIndex_Negative_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--monitor", "-1" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("non-negative integer", result.ErrorMessage);
    }

    [Fact]
    public void Region_ThreeValues_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--region", "1,2,3" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("4 comma-separated", result.ErrorMessage);
    }

    [Fact]
    public void Region_ZeroWidth_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--region", "0,0,0,100" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("width must be positive", result.ErrorMessage);
    }

    [Fact]
    public void Region_NegativeHeight_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--region", "0,0,100,-1" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("height must be positive", result.ErrorMessage);
    }

    [Fact]
    public void Region_NonNumericX_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--region", "abc,0,100,100" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("x must be an integer", result.ErrorMessage);
    }

    [Fact]
    public void Region_MissingValue_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--region", "--verbose" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("requires a value", result.ErrorMessage);
    }

    // ── Missing Option Value Tests ────────────────────────────────────────

    [Fact]
    public void Output_MissingValue_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--full", "--output" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("--output requires a path", result.ErrorMessage);
    }

    [Fact]
    public void Filename_MissingValue_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--full", "--filename" });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("--filename requires a template", result.ErrorMessage);
    }

    [Fact]
    public void Output_EmptyValue_ReturnsInvalidArguments()
    {
        var result = CliParser.Parse(new[] { "--full", "--output", "" });
        // Empty string is the value -- whitespace check
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
    }

    [Fact]
    public void Filename_WhitespaceOnly_ReturnsInvalidArguments()
    {
        // Whitespace-only string as filename
        var result = CliParser.Parse(new[] { "--full", "--filename", "   " });
        Assert.Equal(CliExitCode.InvalidArguments, result.ErrorCode);
        Assert.Contains("must not be empty or whitespace", result.ErrorMessage);
    }

    // ── Output Path Resolution Tests ──────────────────────────────────────

    [Fact]
    public void ResolveOutputPath_NoOutput_NoFilename_UsesDefaultDir()
    {
        var path = CliParser.ResolveOutputPath(null, null);
        Assert.StartsWith(ExportDefaults.DefaultSaveDirectory, path);
        Assert.EndsWith(".png", path);
    }

    [Fact]
    public void ResolveOutputPath_OutputDir_UsesDefaultFilename()
    {
        var path = CliParser.ResolveOutputPath("C:\\captures", null);
        Assert.StartsWith("C:\\captures", path);
        Assert.EndsWith(".png", path);
    }

    [Fact]
    public void ResolveOutputPath_OutputPng_TreatedAsFullFilePath()
    {
        var path = CliParser.ResolveOutputPath("C:\\captures\\test.png", null);
        Assert.Equal("C:\\captures\\test.png", path);
    }

    [Fact]
    public void ResolveOutputPath_FilenameTemplate_Expands()
    {
        var path = CliParser.ResolveOutputPath("C:\\out", "my_<yyyy>.png");
        Assert.StartsWith("C:\\out\\my_", path);
        Assert.EndsWith(".png", path);
        // Template should be expanded
        Assert.DoesNotContain("<yyyy>", path);
    }

    [Fact]
    public void ResolveOutputPath_FilenameTemplate_Sanitizes()
    {
        var path = CliParser.ResolveOutputPath("C:\\out", "test<>file");
        Assert.DoesNotContain("<", path);
        Assert.DoesNotContain(">", path);
    }

    [Fact]
    public void ResolveOutputPath_NoOutput_WithFilename()
    {
        var path = CliParser.ResolveOutputPath(null, "custom_<hh><mm><ss>");
        Assert.StartsWith(ExportDefaults.DefaultSaveDirectory, path);
        Assert.EndsWith(".png", path);
        Assert.DoesNotContain("<hh>", path);
    }

    [Fact]
    public void ResolveOutputPath_PngOutputPath_IgnoresFilename()
    {
        // When output is a full .png path, it should be used as-is
        var path = CliParser.ResolveOutputPath("C:\\out\\exact.png", "ignored_<yyyy>");
        Assert.Equal("C:\\out\\exact.png", path);
    }

    // ── Exit Code Mapping Tests ───────────────────────────────────────────

    [Fact]
    public void ExitCode_Success_IsZero()
    {
        Assert.Equal(0, (int)CliExitCode.Success);
    }

    [Fact]
    public void ExitCode_InvalidArguments_IsOne()
    {
        Assert.Equal(1, (int)CliExitCode.InvalidArguments);
    }

    [Fact]
    public void ExitCode_CaptureFailed_IsTwo()
    {
        Assert.Equal(2, (int)CliExitCode.CaptureFailed);
    }

    [Fact]
    public void ExitCode_ExportFailed_IsThree()
    {
        Assert.Equal(3, (int)CliExitCode.ExportFailed);
    }

    [Fact]
    public void ExitCode_InternalError_IsFour()
    {
        Assert.Equal(4, (int)CliExitCode.InternalError);
    }

    // ── Boundary Condition Tests ──────────────────────────────────────────

    [Fact]
    public void Monitor_ZeroIndex_IsAccepted()
    {
        var result = CliParser.Parse(new[] { "--monitor", "0" });
        Assert.NotNull(result.Options);
        Assert.Equal(0, result.Options.MonitorIndex);
    }

    [Fact]
    public void Monitor_WithoutIndex_AcceptedAsCurrentMonitor()
    {
        var result = CliParser.Parse(new[] { "--monitor" });
        Assert.NotNull(result.Options);
        Assert.Null(result.Options.MonitorIndex);
    }

    [Fact]
    public void Region_NegativeXY_WithPositiveDimensions_IsAccepted()
    {
        // Negative x/y are valid (virtual desktop coordinates)
        var result = CliParser.Parse(new[] { "--region", "-100,-200,800,600" });
        Assert.NotNull(result.Options);
        Assert.Equal(-100, result.Options.Region!.X);
        Assert.Equal(-200, result.Options.Region!.Y);
        Assert.Equal(800, result.Options.Region!.Width);
        Assert.Equal(600, result.Options.Region!.Height);
    }

    [Fact]
    public void LargeMonitorIndex_IsAccepted()
    {
        var result = CliParser.Parse(new[] { "--monitor", "999" });
        Assert.NotNull(result.Options);
        Assert.Equal(999, result.Options.MonitorIndex);
    }

    // ── Region Parsing Unit Tests ─────────────────────────────────────────

    [Fact]
    public void RegionParse_ValidInput_Succeeds()
    {
        var (region, error) = CliRegion.Parse("10,20,300,400");
        Assert.NotNull(region);
        Assert.Null(error);
        Assert.Equal(10, region.X);
        Assert.Equal(20, region.Y);
        Assert.Equal(300, region.Width);
        Assert.Equal(400, region.Height);
    }

    [Fact]
    public void RegionParse_FiveValues_ReturnsError()
    {
        var (region, error) = CliRegion.Parse("1,2,3,4,5");
        Assert.Null(region);
        Assert.Contains("4 comma-separated", error);
    }

    [Fact]
    public void RegionParse_FloatWidth_ReturnsError()
    {
        var (region, error) = CliRegion.Parse("0,0,100.5,200");
        Assert.Null(region);
        Assert.Contains("integer", error);
    }

    // ── Combined Options Tests ────────────────────────────────────────────

    [Fact]
    public void FullMode_WithAllOptions_ParsesCorrectly()
    {
        var result = CliParser.Parse(new[] {
            "--full", "--output", "C:\\out", "--filename", "test_<yyyy>.png", "--verbose"
        });
        Assert.NotNull(result.Options);
        Assert.Equal(CliCaptureMode.Full, result.Options.Mode);
        Assert.True(result.Options.Verbose);
        Assert.Contains("C:\\out", result.Options.OutputPath);
        Assert.EndsWith(".png", result.Options.OutputPath);
    }

    [Fact]
    public void OptionsOrder_DoesNotMatter()
    {
        var result = CliParser.Parse(new[] {
            "--verbose", "--output", "C:\\tmp", "--full"
        });
        Assert.NotNull(result.Options);
        Assert.Equal(CliCaptureMode.Full, result.Options.Mode);
        Assert.True(result.Options.Verbose);
    }
}
