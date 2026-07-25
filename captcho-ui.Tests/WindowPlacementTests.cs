// WindowPlacementTests.cs — Headless tests for the pure window placement seam.
//
// Verifies the pure C# WindowPlacement value: parsing a persisted placement
// string ("x,y,width,height") into a placement, serializing a placement back to
// that string, and the minimum-size clamping applied on load. This is the only
// part of the settings-window placement logic that is headlessly testable — the
// WinUI AppWindow / LocalSettings I/O in SettingsWindow cannot run without the
// WinUI runtime and is exercised by a manual check.

using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class WindowPlacementTests
{
    // ── A valid persisted placement string parses back into a placement ──

    [Fact]
    public void TryParse_ValidFourPartIntegerString_ReturnsPlacement()
    {
        var placement = WindowPlacement.TryParse("100,200,700,500");

        Assert.NotNull(placement);
        Assert.Equal(100, placement.Value.X);
        Assert.Equal(200, placement.Value.Y);
        Assert.Equal(700, placement.Value.Width);
        Assert.Equal(500, placement.Value.Height);
    }

    // ── Missing or malformed placement strings yield no placement ───────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_NullOrEmptyOrWhitespace_ReturnsNull(string? raw)
    {
        Assert.Null(WindowPlacement.TryParse(raw));
    }

    [Theory]
    [InlineData("100,200,700")]            // too few parts
    [InlineData("100,200,700,500,999")]    // too many parts
    public void TryParse_WrongPartCount_ReturnsNull(string raw)
    {
        Assert.Null(WindowPlacement.TryParse(raw));
    }

    [Theory]
    [InlineData("100,200,abc,500")]        // non-numeric width
    [InlineData("x,200,700,500")]          // non-numeric x
    [InlineData("100,200,700.0,500")]      // float, not int
    public void TryParse_NonNumericPart_ReturnsNull(string raw)
    {
        Assert.Null(WindowPlacement.TryParse(raw));
    }

    // ── Dimensions below the minimum are clamped up on load ─────────────

    [Fact]
    public void TryParse_WidthBelowMinimum_IsClampedToMinWidth()
    {
        var placement = WindowPlacement.TryParse($"100,200,{WindowPlacement.MinWidth - 1},500");

        Assert.NotNull(placement);
        Assert.Equal(WindowPlacement.MinWidth, placement.Value.Width);
    }

    [Fact]
    public void TryParse_HeightBelowMinimum_IsClampedToMinHeight()
    {
        var placement = WindowPlacement.TryParse($"100,200,700,{WindowPlacement.MinHeight - 1}");

        Assert.NotNull(placement);
        Assert.Equal(WindowPlacement.MinHeight, placement.Value.Height);
    }

    [Fact]
    public void TryParse_WidthAndHeightAtMinimum_AreKeptAsIs()
    {
        var placement = WindowPlacement.TryParse(
            $"0,0,{WindowPlacement.MinWidth},{WindowPlacement.MinHeight}");

        Assert.NotNull(placement);
        Assert.Equal(WindowPlacement.MinWidth, placement.Value.Width);
        Assert.Equal(WindowPlacement.MinHeight, placement.Value.Height);
    }

    // ── Negative coordinates are preserved (multi-monitor layouts) ──────

    [Fact]
    public void TryParse_NegativeCoordinates_PreservedForMultiMonitor()
    {
        // A monitor to the left/above the primary has negative origin
        // coordinates; position must not be clamped or the window would jump.
        var placement = WindowPlacement.TryParse("-1920,-1080,700,500");

        Assert.NotNull(placement);
        Assert.Equal(-1920, placement.Value.X);
        Assert.Equal(-1080, placement.Value.Y);
    }

    // ── A placement serializes back to the persisted string format ──────

    [Fact]
    public void Serialize_ProducesCommaSeparatedFourPartString()
    {
        var placement = new WindowPlacement(100, 200, 700, 500);

        Assert.Equal("100,200,700,500", placement.Serialize());
    }

    [Fact]
    public void Serialize_AfterTryParse_RoundTripsValues()
    {
        // Whatever was persisted must survive a save → load cycle unchanged
        // (within minimum-size clamping) so reopening restores the last window.
        var original = new WindowPlacement(42, -17, 960, 720);
        var parsed = WindowPlacement.TryParse(original.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal(original, parsed);
    }
}
