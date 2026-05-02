// RegionSelectionStatusTests.cs — Tests for RegionSelectionStatusFormatter.
//
// Verifies that region selection results are formatted into sanitized,
// user-readable status strings following MEM037 convention (no raw pixel data,
// no raw exception dumps, no screen content).

using System;
using Windows.Foundation;
using Respectacle.UI;
using Xunit;

namespace Respectacle.UI.Tests;

public class RegionSelectionStatusTests
{
    // ── FormatConfirmed ────────────────────────────────────────────────

    [Fact]
    public void FormatConfirmed_NormalRegion_FormatsDimensionsAndPosition()
    {
        var region = new Rect(100, 200, 800, 600);
        var result = RegionSelectionStatusFormatter.FormatConfirmed(region);
        Assert.Equal("Region selected: 800×600 at (100, 200)", result);
    }

    [Fact]
    public void FormatConfirmed_ZeroOrigin_FormatsCorrectly()
    {
        var region = new Rect(0, 0, 1920, 1080);
        var result = RegionSelectionStatusFormatter.FormatConfirmed(region);
        Assert.Equal("Region selected: 1920×1080 at (0, 0)", result);
    }

    [Fact]
    public void FormatConfirmed_NegativeCoordinates_FormatsCorrectly()
    {
        // Multi-monitor: secondary monitor left of primary has negative X
        var region = new Rect(-1920, 0, 1920, 1080);
        var result = RegionSelectionStatusFormatter.FormatConfirmed(region);
        Assert.Equal("Region selected: 1920×1080 at (-1920, 0)", result);
    }

    [Fact]
    public void FormatConfirmed_BothNegativeCoordinates_FormatsCorrectly()
    {
        var region = new Rect(-1920, -1080, 1920, 1080);
        var result = RegionSelectionStatusFormatter.FormatConfirmed(region);
        Assert.Equal("Region selected: 1920×1080 at (-1920, -1080)", result);
    }

    [Fact]
    public void FormatConfirmed_SmallRegion_FormatsCorrectly()
    {
        var region = new Rect(50, 75, 4, 4);
        var result = RegionSelectionStatusFormatter.FormatConfirmed(region);
        Assert.Equal("Region selected: 4×4 at (50, 75)", result);
    }

    [Fact]
    public void FormatConfirmed_LargeRegion_FormatsCorrectly()
    {
        var region = new Rect(0, 0, 5760, 2160);
        var result = RegionSelectionStatusFormatter.FormatConfirmed(region);
        Assert.Equal("Region selected: 5760×2160 at (0, 0)", result);
    }

    // ── FormatCancelled ────────────────────────────────────────────────

    [Fact]
    public void FormatCancelled_ReturnsCancellationMessage()
    {
        var result = RegionSelectionStatusFormatter.FormatCancelled();
        Assert.Equal("Region selection cancelled.", result);
    }

    // ── FormatInvalid ──────────────────────────────────────────────────

    [Fact]
    public void FormatInvalid_ReturnsInvalidMessage()
    {
        var result = RegionSelectionStatusFormatter.FormatInvalid();
        Assert.Equal("No valid region selected.", result);
    }

    // ── FormatError ────────────────────────────────────────────────────

    [Fact]
    public void FormatError_WithException_IncludesSanitizedMessage()
    {
        var ex = new InvalidOperationException("Could not create overlay window");
        var result = RegionSelectionStatusFormatter.FormatError(ex);
        Assert.Equal("Region selector error: Could not create overlay window", result);
    }

    [Fact]
    public void FormatError_WithNullException_ReturnsUnknownFailure()
    {
        var result = RegionSelectionStatusFormatter.FormatError(null!);
        Assert.Equal("Region selector error: unknown failure.", result);
    }

    [Fact]
    public void FormatError_WithExceptionWithNullMessage_ReturnsUnknownFailure()
    {
        // Exception((string?)null) actually uses a default message like
        // "Exception of type 'System.Exception' was thrown."
        // The formatter handles this gracefully by using whatever message is present.
        var ex = new Exception((string?)null!);
        var result = RegionSelectionStatusFormatter.FormatError(ex);
        Assert.StartsWith("Region selector error:", result);
        Assert.True(result.Length > "Region selector error: ".Length);
    }

    [Fact]
    public void FormatError_WithStackTraceInMessage_TruncatesAtNewline()
    {
        var ex = new Exception("Something failed\n   at SomeMethod()\n   at AnotherMethod()");
        var result = RegionSelectionStatusFormatter.FormatError(ex);
        Assert.Equal("Region selector error: Something failed", result);
    }

    [Fact]
    public void FormatError_WithVeryLongMessage_TruncatesToMaxLength()
    {
        var longMessage = new string('x', 200);
        var ex = new Exception(longMessage);
        var result = RegionSelectionStatusFormatter.FormatError(ex);
        Assert.True(result.Length <= "Region selector error: ".Length + 120);
        Assert.EndsWith("...", result);
    }

    // ── FormatResult ───────────────────────────────────────────────────

    [Fact]
    public void FormatResult_WithRect_ReturnsConfirmedFormat()
    {
        Rect? result = new Rect(100, 200, 800, 600);
        var status = RegionSelectionStatusFormatter.FormatResult(result);
        Assert.Equal("Region selected: 800×600 at (100, 200)", status);
    }

    [Fact]
    public void FormatResult_WithNull_ReturnsCancelledFormat()
    {
        Rect? result = null;
        var status = RegionSelectionStatusFormatter.FormatResult(result);
        Assert.Equal("Region selection cancelled.", status);
    }

    // ── FormatCaptureError ──────────────────────────────────────────────

    [Fact]
    public void FormatCaptureError_WithModeAndError_FormatsCorrectly()
    {
        var result = RegionSelectionStatusFormatter.FormatCaptureError(
            "Rectangular Region (X=100, Y=200, 800×600)", "Capture failed: region out of bounds");
        Assert.Equal("Rectangular Region (X=100, Y=200, 800×600) — Capture failed: region out of bounds", result);
    }

    [Fact]
    public void FormatCaptureError_WithNullError_ShowsFallbackMessage()
    {
        var result = RegionSelectionStatusFormatter.FormatCaptureError(
            "Rectangular Region (X=0, Y=0, 100×100)", null);
        Assert.Equal("Rectangular Region (X=0, Y=0, 100×100) — capture failed.", result);
    }

    [Fact]
    public void FormatCaptureError_WithEmptyError_ShowsFallbackMessage()
    {
        var result = RegionSelectionStatusFormatter.FormatCaptureError(
            "Rectangular Region (X=0, Y=0, 100×100)", "");
        Assert.Equal("Rectangular Region (X=0, Y=0, 100×100) — capture failed.", result);
    }

    [Fact]
    public void FormatCaptureError_WithNewlineInError_TruncatesAtNewline()
    {
        var result = RegionSelectionStatusFormatter.FormatCaptureError(
            "Rectangular Region (X=0, Y=0, 100×100)", "Something failed\nstack trace here");
        Assert.Equal("Rectangular Region (X=0, Y=0, 100×100) — Something failed", result);
    }

    [Fact]
    public void FormatCaptureError_WithVeryLongError_TruncatesToMaxLength()
    {
        var longError = new string('e', 300);
        var result = RegionSelectionStatusFormatter.FormatCaptureError("Mode", longError);
        Assert.True(result.Length <= "Mode — ".Length + 200);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void FormatCaptureError_WithNegativeCoordinates_PreservesSigns()
    {
        var result = RegionSelectionStatusFormatter.FormatCaptureError(
            "Rectangular Region (X=-1920, Y=0, 1920×1080)", "native error");
        Assert.Contains("X=-1920", result);
    }

    // ── Negative tests (per plan Q7) ───────────────────────────────────

    [Fact]
    public void FormatConfirmed_ZeroSizeRegion_StillFormats()
    {
        // Edge case: overlay should never return zero-size, but the formatter
        // should not crash if it receives one
        var region = new Rect(100, 200, 0, 0);
        var result = RegionSelectionStatusFormatter.FormatConfirmed(region);
        Assert.Equal("Region selected: 0×0 at (100, 200)", result);
    }
}
