// CliDocumentationTests.cs — Tests that README.md and --help output agree on
// CLI modes, flags, and exit codes.
//
// These tests read the tracked README.md (never .gsd/ or other ignored paths)
// and compare it against the CLI's own help text. They verify structural
// consistency (all modes mentioned, all exit codes documented) without
// asserting exact prose wording.

using System;
using System.IO;
using System.Reflection;
using Xunit;
using captcho.Cli;

namespace captcho.Cli.Tests;

public class CliDocumentationTests
{
    /// <summary>
    /// Path to README.md at the repository root (three directories up from test bin).
    /// Tests resolve the path relative to the test assembly location so they work
    /// regardless of the working directory.
    /// </summary>
    private static readonly string ReadmePath = FindReadmePath();

    private static string FindReadmePath()
    {
        // Walk up from the assembly location to find README.md at repo root
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "README.md");
            if (File.Exists(candidate))
                return candidate;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            dir = parent;
        }
        // Fallback: assume standard layout
        return Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "README.md");
    }

    private static string ReadReadme()
    {
        var path = Path.GetFullPath(ReadmePath);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"README.md not found at {path}. Documentation tests require the README.");
        return File.ReadAllText(path);
    }

    private static readonly string HelpText = CliParser.BuildHelpText();

    // ── Mode Coverage ────────────────────────────────────────────────────

    [Fact]
    public void Readme_Mentions_All_Capture_Modes()
    {
        var readme = ReadReadme();
        Assert.Contains("--full", readme);
        Assert.Contains("--monitor", readme);
        Assert.Contains("--window-active", readme);
        Assert.Contains("--window-cursor", readme);
        Assert.Contains("--region", readme);
    }

    [Fact]
    public void Help_Mentions_All_Capture_Modes()
    {
        Assert.Contains("--full", HelpText);
        Assert.Contains("--monitor", HelpText);
        Assert.Contains("--window-active", HelpText);
        Assert.Contains("--window-cursor", HelpText);
        Assert.Contains("--region", HelpText);
    }

    // ── Output Flag Coverage ─────────────────────────────────────────────

    [Fact]
    public void Readme_Mentions_Output_Flags()
    {
        var readme = ReadReadme();
        Assert.Contains("--output", readme);
        Assert.Contains("--filename", readme);
    }

    [Fact]
    public void Help_Mentions_Output_Flags()
    {
        Assert.Contains("--output", HelpText);
        Assert.Contains("--filename", HelpText);
    }

    // ── Exit Code Coverage ───────────────────────────────────────────────

    [Fact]
    public void Readme_Mentions_All_Exit_Codes()
    {
        var readme = ReadReadme();
        // Check by numeric value and/or name — the README should document each
        Assert.Contains("0", readme); // Success
        Assert.Contains("Invalid", readme); // InvalidArguments
        Assert.Contains("Capture", readme); // CaptureFailed
        Assert.Contains("Export", readme); // ExportFailed
    }

    [Fact]
    public void Help_Mentions_All_Exit_Codes()
    {
        Assert.Contains("Exit Codes", HelpText);
        Assert.Contains("0", HelpText);
        Assert.Contains("1", HelpText);
        Assert.Contains("2", HelpText);
        Assert.Contains("3", HelpText);
        Assert.Contains("4", HelpText);
    }

    [Fact]
    public void Exit_Code_Values_Match_Enum()
    {
        Assert.Equal(0, (int)CliExitCode.Success);
        Assert.Equal(1, (int)CliExitCode.InvalidArguments);
        Assert.Equal(2, (int)CliExitCode.CaptureFailed);
        Assert.Equal(3, (int)CliExitCode.ExportFailed);
        Assert.Equal(4, (int)CliExitCode.InternalError);
    }

    // ── Diagnostics Coverage ─────────────────────────────────────────────

    [Fact]
    public void Readme_Mentions_CLI_Diagnostics()
    {
        var readme = ReadReadme();
        Assert.Contains("--verbose", readme);
    }

    [Fact]
    public void Help_Mentions_Verbose()
    {
        Assert.Contains("--verbose", HelpText);
    }

    // ── Region Syntax Coverage ───────────────────────────────────────────

    [Fact]
    public void Readme_Mentions_Region_Coordinate_Syntax()
    {
        var readme = ReadReadme();
        // The README should document that --region uses x,y,width,height coordinates
        Assert.Matches("region.*(x.*y.*width.*height|coordinate)", readme);
    }

    [Fact]
    public void Help_Mentions_Region_Coordinate_Syntax()
    {
        Assert.Contains("x,y,width,height", HelpText);
    }

    // ── Monitor Syntax Coverage ──────────────────────────────────────────

    [Fact]
    public void Readme_Mentions_Monitor_Index_Syntax()
    {
        var readme = ReadReadme();
        // The README should document --monitor [index] syntax
        Assert.Matches("--monitor.*(index|\\[)", readme);
    }

    [Fact]
    public void Help_Mentions_Monitor_Index_Syntax()
    {
        Assert.Matches(@"--monitor.*\[?index", HelpText);
    }

    // ── CLI Project Reference in README ──────────────────────────────────

    [Fact]
    public void Readme_Mentions_CLI_Project()
    {
        var readme = ReadReadme();
        Assert.Contains("captcho-cli", readme);
    }

    [Fact]
    public void Readme_Mentions_CLI_Capture_Without_UI()
    {
        var readme = ReadReadme();
        // Should mention headless/without UI somewhere
        Assert.Matches("(headless|without.*UI|no.*UI|command.line)", readme);
    }
}
