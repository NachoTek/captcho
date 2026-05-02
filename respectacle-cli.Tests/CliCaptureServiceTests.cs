// CliCaptureServiceTests.cs — Fake-driven workflow tests for all capture modes,
// output path handling, capture failure, export failure, bitmap conversion failure,
// verbose diagnostics, and error sanitization.
//
// Tests use FakeCliCaptureAdapter to avoid native WGC dependency.
// No references to respectacle-ui.

using System;
using System.Collections.Generic;
using System.IO;
using Respectacle.Capture;
using Respectacle.Cli;
using Xunit;

namespace Respectacle.Cli.Tests;

// ── Fake Adapter ────────────────────────────────────────────────────────

/// <summary>
/// Fake capture/export adapter for testing CliCaptureService without native WGC.
/// </summary>
internal sealed class FakeCliCaptureAdapter : ICliCaptureAdapter
{
    // Configurable behaviors
    public CaptureAdapterResult? NextCaptureResult { get; set; }
    public BitmapConversionResult? NextBitmapResult { get; set; }
    public ExportAdapterResult? NextExportResult { get; set; }
    public MonitorResolveResult? NextMonitorResolveResult { get; set; }

    // Capture mode tracking
    public string? LastCaptureMode { get; private set; }
    public uint? LastMonitorIndex { get; private set; }
    public (int x, int y, uint w, uint h)? LastRegion { get; private set; }

    // Default: successful capture of a 4x2 image (contiguous, stride=16)
    public FakeCliCaptureAdapter()
    {
        // 4 pixels wide × 2 pixels tall × 4 bytes per pixel = 32 bytes
        NextCaptureResult = CaptureAdapterResult.Ok(
            new byte[32], width: 4, height: 2, stride: 16);
        NextBitmapResult = null; // Will use real conversion by default
        NextExportResult = ExportAdapterResult.Ok(
            "/tmp/test.png", width: 4, height: 2, byteCount: 100,
            elapsed: TimeSpan.FromMilliseconds(5));
    }

    public CaptureAdapterResult CaptureAllMonitors()
    {
        LastCaptureMode = "all-monitors";
        return NextCaptureResult!;
    }

    public CaptureAdapterResult CaptureMonitorByIndex(uint index)
    {
        LastCaptureMode = "monitor-by-index";
        LastMonitorIndex = index;
        return NextCaptureResult!;
    }

    public CaptureAdapterResult CaptureActiveWindow()
    {
        LastCaptureMode = "active-window";
        return NextCaptureResult!;
    }

    public CaptureAdapterResult CaptureWindowUnderCursor()
    {
        LastCaptureMode = "window-under-cursor";
        return NextCaptureResult!;
    }

    public CaptureAdapterResult CaptureRegion(int x, int y, uint width, uint height)
    {
        LastCaptureMode = "region";
        LastRegion = (x, y, width, height);
        return NextCaptureResult!;
    }

    public MonitorResolveResult ResolveCurrentMonitor()
    {
        return NextMonitorResolveResult ?? MonitorResolveResult.Success(0);
    }

    public BitmapConversionResult ConvertToContiguousBitmap(byte[] pixels, int width, int height, int stride)
    {
        if (NextBitmapResult is not null)
            return NextBitmapResult;

        // Use real conversion logic
        try
        {
            var bitmap = BitmapBufferConverter.StripPadding(pixels, width, height, stride);
            return BitmapConversionResult.Ok(bitmap);
        }
        catch (Exception ex)
        {
            return BitmapConversionResult.Fail(ex.Message);
        }
    }

    public ExportAdapterResult ExportPng(ContiguousBitmap bitmap, string outputPath)
    {
        return NextExportResult!;
    }
}

// ── Success Tests ───────────────────────────────────────────────────────

public class CliCaptureServiceSuccessTests
{
    private static CliOptions MakeOptions(CliCaptureMode mode,
        int? monitorIndex = null, CliRegion? region = null,
        string? outputPath = null, bool verbose = false)
    {
        return new CliOptions
        {
            Mode = mode,
            MonitorIndex = monitorIndex,
            Region = region,
            OutputPath = outputPath ?? Path.Combine(Path.GetTempPath(), "test.png"),
            Verbose = verbose,
        };
    }

    [Fact]
    public void FullMode_CapturesAllMonitors()
    {
        var fake = new FakeCliCaptureAdapter();
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Full);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
        Assert.Equal("all-monitors", fake.LastCaptureMode);
        Assert.Equal("full", diag.CaptureMode);
        Assert.Equal("success", diag.Status);
    }

    [Fact]
    public void MonitorMode_WithIndex_CapturesSpecificMonitor()
    {
        var fake = new FakeCliCaptureAdapter();
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Monitor, monitorIndex: 2);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
        Assert.Equal("monitor-by-index", fake.LastCaptureMode);
        Assert.Equal(2u, fake.LastMonitorIndex);
    }

    [Fact]
    public void MonitorMode_BareIndex_ResolvesCurrentMonitor()
    {
        var fake = new FakeCliCaptureAdapter
        {
            NextMonitorResolveResult = MonitorResolveResult.Success(1)
        };
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Monitor);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
        Assert.Equal("monitor-by-index", fake.LastCaptureMode);
        Assert.Equal(1u, fake.LastMonitorIndex);
    }

    [Fact]
    public void MonitorMode_BareIndex_FallbackUsesPrimary()
    {
        var fake = new FakeCliCaptureAdapter
        {
            NextMonitorResolveResult = MonitorResolveResult.Fallback("Cursor outside bounds")
        };
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Monitor);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
        Assert.Equal(0u, fake.LastMonitorIndex);
    }

    [Fact]
    public void WindowActiveMode_CapturesActiveWindow()
    {
        var fake = new FakeCliCaptureAdapter();
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.WindowActive);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
        Assert.Equal("active-window", fake.LastCaptureMode);
    }

    [Fact]
    public void WindowCursorMode_CapturesWindowUnderCursor()
    {
        var fake = new FakeCliCaptureAdapter();
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.WindowCursor);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
        Assert.Equal("window-under-cursor", fake.LastCaptureMode);
    }

    [Fact]
    public void RegionMode_CapturesCorrectRegion()
    {
        var fake = new FakeCliCaptureAdapter();
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Region,
            region: new CliRegion(-100, 200, 800, 600));

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
        Assert.Equal("region", fake.LastCaptureMode);
        Assert.Equal((-100, 200, 800u, 600u), fake.LastRegion);
    }

    [Fact]
    public void MonitorIndex_Zero_IsValid()
    {
        var fake = new FakeCliCaptureAdapter();
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Monitor, monitorIndex: 0);

        var (exitCode, _) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
        Assert.Equal(0u, fake.LastMonitorIndex);
    }
}

// ── Failure Tests ───────────────────────────────────────────────────────

public class CliCaptureServiceFailureTests
{
    private static CliOptions MakeOptions(CliCaptureMode mode,
        int? monitorIndex = null, CliRegion? region = null,
        string? outputPath = null)
    {
        return new CliOptions
        {
            Mode = mode,
            MonitorIndex = monitorIndex,
            Region = region,
            OutputPath = outputPath ?? Path.Combine(Path.GetTempPath(), "test.png"),
            Verbose = false,
        };
    }

    [Fact]
    public void CaptureFailure_ReturnsCaptureFailedExitCode()
    {
        var fake = new FakeCliCaptureAdapter
        {
            NextCaptureResult = CaptureAdapterResult.Fail("CaptureUnavailable", "WGC not available")
        };
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Full);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.CaptureFailed, exitCode);
        Assert.Equal("capture", diag.FailurePhase);
        Assert.Contains("CaptureUnavailable", diag.FailureMessage!);
    }

    [Fact]
    public void CaptureTimeout_ReturnsCaptureFailed()
    {
        var fake = new FakeCliCaptureAdapter
        {
            NextCaptureResult = CaptureAdapterResult.Fail("Timeout", "Timed out waiting for frame")
        };
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Full);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.CaptureFailed, exitCode);
        Assert.Equal("capture", diag.FailurePhase);
        Assert.Contains("Timeout", diag.FailureMessage!);
    }

    [Fact]
    public void BitmapConversionFailure_ReturnsCaptureFailed()
    {
        var fake = new FakeCliCaptureAdapter
        {
            NextBitmapResult = BitmapConversionResult.Fail("Invalid stride metadata")
        };
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Full);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.CaptureFailed, exitCode);
        Assert.Equal("bitmap", diag.FailurePhase);
        Assert.Contains("Invalid stride metadata", diag.FailureMessage!);
    }

    [Fact]
    public void ExportFailure_ReturnsExportFailedExitCode()
    {
        var fake = new FakeCliCaptureAdapter
        {
            NextExportResult = ExportAdapterResult.Fail("Write", "Unauthorized access", TimeSpan.FromMilliseconds(1))
        };
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Full);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.ExportFailed, exitCode);
        Assert.Equal("Write", diag.FailurePhase);
        Assert.Contains("Unauthorized access", diag.FailureMessage!);
    }

    [Fact]
    public void MonitorResolutionFailure_ReturnsCaptureFailed()
    {
        var fake = new FakeCliCaptureAdapter
        {
            // Failure with IsFallback=false means total resolution failure
            NextMonitorResolveResult = new MonitorResolveResult(0, false, "GetCursorPos returned false")
        };
        var service = new CliCaptureService(fake);
        var options = MakeOptions(CliCaptureMode.Monitor);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.CaptureFailed, exitCode);
        Assert.Equal("capture", diag.FailurePhase);
        Assert.Contains("MonitorResolutionFailed", diag.FailureMessage!);
    }

    [Fact]
    public void CaptureFailure_DoesNotCreateOutputFile()
    {
        var fake = new FakeCliCaptureAdapter
        {
            NextCaptureResult = CaptureAdapterResult.Fail("CaptureUnavailable")
        };
        var service = new CliCaptureService(fake);
        var path = Path.Combine(Path.GetTempPath(), $"no-file-{Guid.NewGuid()}.png");
        var options = MakeOptions(CliCaptureMode.Full, outputPath: path);

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.CaptureFailed, exitCode);
        Assert.Null(diag.OutputPath);
    }
}

// ── Diagnostics Tests ───────────────────────────────────────────────────

public class CliCaptureDiagnosticsTests
{
    [Fact]
    public void Success_NormalMode_IncludesRequiredFields()
    {
        var diag = new CliCaptureDiagnostics
        {
            CaptureMode = "full", Status = "success", ExitCode = 0,
            OutputPath = "/tmp/test.png", Width = 4, Height = 2,
            Verbose = false,
        };
        string output = CliDiagnostics.Format(diag);

        Assert.Contains("CaptureMode=full", output);
        Assert.Contains("Status=success", output);
        Assert.Contains("OutputPath=/tmp/test.png", output);
        Assert.Contains("ExitCode=0", output);
        Assert.Contains("Dimensions=4x2", output);
    }

    [Fact]
    public void Success_VerboseMode_IncludesTimings()
    {
        var diag = new CliCaptureDiagnostics
        {
            CaptureMode = "full", Status = "success", ExitCode = 0,
            OutputPath = "/tmp/test.png", Width = 4, Height = 2,
            CaptureDuration = TimeSpan.FromMilliseconds(10),
            BitmapDuration = TimeSpan.FromMilliseconds(2),
            ExportDuration = TimeSpan.FromMilliseconds(5),
            TotalDuration = TimeSpan.FromMilliseconds(17),
            Verbose = true,
        };
        string output = CliDiagnostics.Format(diag);

        Assert.Contains("CaptureMs=10.", output);
        Assert.Contains("BitmapMs=2.", output);
        Assert.Contains("ExportMs=5.", output);
        Assert.Contains("TotalMs=17.", output);
    }

    [Fact]
    public void Failure_IncludesPhaseAndMessage()
    {
        var diag = new CliCaptureDiagnostics
        {
            CaptureMode = "full", Status = "failed", ExitCode = 2,
            FailurePhase = "capture",
            FailureMessage = "Status=CaptureUnavailable: WGC not available",
            Verbose = false,
        };
        string error = CliDiagnostics.FormatError(diag);

        Assert.Contains("Phase=capture", error);
        Assert.Contains("CaptureUnavailable", error);
    }

    [Fact]
    public void Sanitize_RemovesFilePaths()
    {
        string result = CliDiagnostics.Sanitize("Failed to write C:\\Users\\test\\image.png");
        Assert.DoesNotContain("C:\\Users", result);
        Assert.Contains("<path>", result);
    }

    [Fact]
    public void Sanitize_RemovesNativePointers()
    {
        string result = CliDiagnostics.Sanitize("Pointer at 0x7FFA1234 was null");
        Assert.DoesNotContain("0x7FFA1234", result);
        Assert.Contains("<ptr>", result);
    }

    [Fact]
    public void Sanitize_RemovesStackTraces()
    {
        string result = CliDiagnostics.Sanitize(
            "Error at System.RuntimeMethodHandle.InvokeMethod(Object target, Void** arguments)");
        Assert.DoesNotContain("System.RuntimeMethodHandle", result);
    }

    [Fact]
    public void Sanitize_RemovesEnvVars()
    {
        string result = CliDiagnostics.Sanitize("Path includes %USERPROFILE%\\test");
        Assert.DoesNotContain("%USERPROFILE%", result);
        Assert.Contains("<env>", result);
    }

    [Fact]
    public void Sanitize_EmptyInput_ReturnsUnknownError()
    {
        Assert.Equal("Unknown error", CliDiagnostics.Sanitize(""));
        Assert.Equal("Unknown error", CliDiagnostics.Sanitize(null!));
    }
}

// ── Boundary Tests ──────────────────────────────────────────────────────

public class CliCaptureServiceBoundaryTests
{
    [Fact]
    public void Region_NegativeXY_PositiveDimensions_Captures()
    {
        var fake = new FakeCliCaptureAdapter();
        var service = new CliCaptureService(fake);
        var options = new CliOptions
        {
            Mode = CliCaptureMode.Region,
            Region = new CliRegion(-500, -200, 800, 600),
            OutputPath = Path.Combine(Path.GetTempPath(), "region-test.png"),
            Verbose = false,
        };

        var (exitCode, _) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
        Assert.Equal((-500, -200, 800u, 600u), fake.LastRegion);
    }

    [Fact]
    public void OutputPath_IsDirectoryWithPng_CombinedCorrectly()
    {
        var fake = new FakeCliCaptureAdapter();
        var service = new CliCaptureService(fake);
        var dir = Path.Combine(Path.GetTempPath(), "cli-test-dir");
        var options = new CliOptions
        {
            Mode = CliCaptureMode.Full,
            OutputPath = dir,
            Verbose = false,
        };

        var (exitCode, _) = service.Execute(options);

        Assert.Equal(CliExitCode.Success, exitCode);
    }

    [Fact]
    public void CaptureFailure_NativeStatus_IncludedInDiagnostics()
    {
        var fake = new FakeCliCaptureAdapter
        {
            NextCaptureResult = CaptureAdapterResult.Fail("PermissionDenied", "User denied screen capture")
        };
        var service = new CliCaptureService(fake);
        var options = new CliOptions
        {
            Mode = CliCaptureMode.Full,
            OutputPath = Path.Combine(Path.GetTempPath(), "test.png"),
            Verbose = false,
        };

        var (exitCode, diag) = service.Execute(options);

        Assert.Equal(CliExitCode.CaptureFailed, exitCode);
        Assert.Contains("PermissionDenied", diag.FailureMessage!);
        Assert.Contains("capture", diag.FailurePhase!);
    }
}
