// ProgramSmokeTests.cs — Entrypoint-level smoke tests for help, invalid args,
// capture workflow via fake adapter, and no-UI project boundary verification.
//
// Tests run the full Program pipeline with injectable adapters.
// No references to respectacle-ui.

using System;
using System.IO;
using Respectacle.Capture;
using Respectacle.Cli;
using Xunit;

namespace Respectacle.Cli.Tests;

/// <summary>
/// Helper to capture Console stdout/stderr during tests.
/// </summary>
internal sealed class ConsoleCapture : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;

    public ConsoleCapture()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public string Stdout => _stdout.ToString();
    public string Stderr => _stderr.ToString();

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        _stdout.Dispose();
        _stderr.Dispose();
    }
}

public class ProgramSmokeTests
{
    [Fact]
    public void HelpArg_ReturnsSuccessExitCode()
    {
        using var cap = new ConsoleCapture();
        int exitCode = Program.ExecuteWithAdapter(new[] { "--help" }, new FakeCliCaptureAdapter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Capture Modes", cap.Stdout);
        Assert.Contains("--full", cap.Stdout);
    }

    [Fact]
    public void HelpArg_ReturnsHelpToStdout()
    {
        using var cap = new ConsoleCapture();
        int exitCode = Program.ExecuteWithAdapter(new[] { "-h" }, new FakeCliCaptureAdapter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Respectacle CLI", cap.Stdout);
    }

    [Fact]
    public void NoArgs_ReturnsInvalidArgumentsExitCode()
    {
        using var cap = new ConsoleCapture();
        int exitCode = Program.ExecuteWithAdapter(Array.Empty<string>(), new FakeCliCaptureAdapter());

        Assert.Equal((int)CliExitCode.InvalidArguments, exitCode);
        Assert.Contains("No arguments provided", cap.Stderr);
    }

    [Fact]
    public void UnknownOption_ReturnsInvalidArgumentsExitCode()
    {
        using var cap = new ConsoleCapture();
        int exitCode = Program.ExecuteWithAdapter(new[] { "--bogus" }, new FakeCliCaptureAdapter());

        Assert.Equal((int)CliExitCode.InvalidArguments, exitCode);
        Assert.Contains("Unknown option", cap.Stderr);
    }

    [Fact]
    public void MultipleModes_ReturnsInvalidArgumentsExitCode()
    {
        using var cap = new ConsoleCapture();
        int exitCode = Program.ExecuteWithAdapter(
            new[] { "--full", "--window-active" }, new FakeCliCaptureAdapter());

        Assert.Equal((int)CliExitCode.InvalidArguments, exitCode);
        Assert.Contains("Multiple capture modes", cap.Stderr);
    }

    [Fact]
    public void CaptureSuccess_WritesDiagnosticsToStdout()
    {
        using var cap = new ConsoleCapture();
        var fake = new FakeCliCaptureAdapter();
        int exitCode = Program.ExecuteWithAdapter(new[] { "--full" }, fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("CaptureMode=full", cap.Stdout);
        Assert.Contains("Status=success", cap.Stdout);
    }

    [Fact]
    public void CaptureFailure_WritesErrorToStderr()
    {
        using var cap = new ConsoleCapture();
        var fake = new FakeCliCaptureAdapter
        {
            NextCaptureResult = CaptureAdapterResult.Fail("CaptureUnavailable", "WGC not available")
        };
        int exitCode = Program.ExecuteWithAdapter(new[] { "--full" }, fake);

        Assert.Equal((int)CliExitCode.CaptureFailed, exitCode);
        Assert.Contains("CaptureMode=full", cap.Stdout);
        Assert.Contains("Status=failed", cap.Stdout);
        Assert.Contains("Phase=capture", cap.Stderr);
    }

    [Fact]
    public void ExportFailure_WritesErrorToStderr()
    {
        using var cap = new ConsoleCapture();
        var fake = new FakeCliCaptureAdapter
        {
            NextExportResult = ExportAdapterResult.Fail("Write", "Disk full", TimeSpan.FromMilliseconds(1))
        };
        int exitCode = Program.ExecuteWithAdapter(new[] { "--full" }, fake);

        Assert.Equal((int)CliExitCode.ExportFailed, exitCode);
        Assert.Contains("Status=failed", cap.Stdout);
        Assert.Contains("Phase=Write", cap.Stderr);
    }

    [Fact]
    public void VerboseMode_IncludesTimings()
    {
        using var cap = new ConsoleCapture();
        var fake = new FakeCliCaptureAdapter();
        int exitCode = Program.ExecuteWithAdapter(new[] { "--full", "--verbose" }, fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("CaptureMs=", cap.Stdout);
        Assert.Contains("TotalMs=", cap.Stdout);
    }

    [Fact]
    public void MonitorMode_WithIndex_Works()
    {
        using var cap = new ConsoleCapture();
        var fake = new FakeCliCaptureAdapter();
        int exitCode = Program.ExecuteWithAdapter(new[] { "--monitor", "1" }, fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("CaptureMode=monitor", cap.Stdout);
    }

    [Fact]
    public void RegionMode_Works()
    {
        using var cap = new ConsoleCapture();
        var fake = new FakeCliCaptureAdapter();
        int exitCode = Program.ExecuteWithAdapter(
            new[] { "--region", "100,200,800,600" }, fake);

        Assert.Equal(0, exitCode);
        Assert.Contains("CaptureMode=region", cap.Stdout);
    }
}

/// <summary>
/// Verifies the CLI project does NOT reference respectacle-ui.
/// This ensures the headless CLI stays independent of the WinUI app.
/// </summary>
public class NoUiReferenceTests
{
    [Fact]
    public void CliProject_DoesNotReference_RespectacleUi()
    {
        string csproj = File.ReadAllText(
            Path.Combine(TestHelpers.ProjectRoot, "respectacle-cli", "respectacle-cli.csproj"));

        Assert.DoesNotContain("respectacle-ui", csproj);
        Assert.DoesNotContain("Respectacle.UI", csproj);
    }

    [Fact]
    public void CliTestProject_DoesNotReference_RespectacleUi()
    {
        string csproj = File.ReadAllText(
            Path.Combine(TestHelpers.ProjectRoot, "respectacle-cli.Tests", "respectacle-cli.Tests.csproj"));

        Assert.DoesNotContain("respectacle-ui", csproj);
        Assert.DoesNotContain("Respectacle.UI", csproj);
    }
}

/// <summary>
/// Helper to find the project root directory from the test assembly location.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Returns the project root directory (containing the .sln file).
    /// Walks up from the test assembly location until a .sln is found.
    /// </summary>
    public static string ProjectRoot
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                if (Directory.GetFiles(dir, "*.sln").Length > 0)
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            throw new InvalidOperationException("Could not find project root with .sln file.");
        }
    }
}
