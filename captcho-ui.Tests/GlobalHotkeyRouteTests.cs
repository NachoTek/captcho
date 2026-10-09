// GlobalHotkeyRouteTests.cs — Tests for Global Hotkey route mapping, spec ids, modifiers, and unknown id behavior.
//
// Verifies the six Global Hotkey specifications (one per Capture Mode) match the slice
// contract exactly: Print Screen → Current Monitor, Win+Print → Active Window, Shift+Print
// → Full Desktop, Win+Shift+Print → Rectangular Region, Alt+Print → Selected Window,
// Ctrl+Print → Selected Monitor. All tests are headless (no Win32/WinUI required).

using System;
using System.Linq;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class GlobalHotkeyRouteTests
{
    // ── Spec count ──────────────────────────────────────────────────────

    [Fact]
    public void AllSpecs_ContainsExactlySixEntries()
    {
        Assert.Equal(6, GlobalHotkeyRouteMap.AllSpecs.Count);
    }

    // ── Print Screen → Current Monitor ──────────────────────────────────

    [Fact]
    public void PrintScreen_MapsToCurrentMonitor()
    {
        var spec = GlobalHotkeyRouteMap.AllSpecs.First(s => s.Id == GlobalHotkeyRouteMap.IdPrintScreen);
        Assert.Equal(GlobalHotkeyRoute.CurrentMonitor, spec.Route);
        Assert.Equal("Print Screen", spec.Name);
        Assert.Equal(GlobalHotkeyRouteMap.VK_SNAPSHOT, spec.VirtualKey);
        Assert.Equal(0, spec.Modifiers); // no modifiers
    }

    // ── Win + Print Screen → Active Window ─────────────────────────────

    [Fact]
    public void WinPrintScreen_MapsToActiveWindow()
    {
        var spec = GlobalHotkeyRouteMap.AllSpecs.First(s => s.Id == GlobalHotkeyRouteMap.IdWinPrintScreen);
        Assert.Equal(GlobalHotkeyRoute.ActiveWindow, spec.Route);
        Assert.Equal("Win + Print Screen", spec.Name);
        Assert.Equal(GlobalHotkeyRouteMap.VK_SNAPSHOT, spec.VirtualKey);
        Assert.Equal(GlobalHotkeyRouteMap.MOD_WIN, spec.Modifiers);
    }

    // ── Shift + Print Screen → Full Desktop ─────────────────────────────

    [Fact]
    public void ShiftPrintScreen_MapsToFullDesktop()
    {
        var spec = GlobalHotkeyRouteMap.AllSpecs.First(s => s.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal(GlobalHotkeyRoute.FullDesktop, spec.Route);
        Assert.Equal("Shift + Print Screen", spec.Name);
        Assert.Equal(GlobalHotkeyRouteMap.VK_SNAPSHOT, spec.VirtualKey);
        Assert.Equal(GlobalHotkeyRouteMap.MOD_SHIFT, spec.Modifiers);
    }

    // ── Win + Shift + Print Screen → Rectangular Region ────────────────

    [Fact]
    public void WinShiftPrintScreen_MapsToRectangularRegion()
    {
        var spec = GlobalHotkeyRouteMap.AllSpecs.First(s => s.Id == GlobalHotkeyRouteMap.IdWinShiftPrintScreen);
        Assert.Equal(GlobalHotkeyRoute.RectangularRegion, spec.Route);
        Assert.Equal("Win + Shift + Print Screen", spec.Name);
        Assert.Equal(GlobalHotkeyRouteMap.VK_SNAPSHOT, spec.VirtualKey);
        Assert.Equal(GlobalHotkeyRouteMap.MOD_WIN | GlobalHotkeyRouteMap.MOD_SHIFT, spec.Modifiers);
    }

    // ── Alt + Print Screen → Selected Window ───────────────────────────

    [Fact]
    public void AltPrintScreen_MapsToSelectedWindow()
    {
        var spec = GlobalHotkeyRouteMap.AllSpecs.First(s => s.Id == GlobalHotkeyRouteMap.IdAltPrintScreen);
        Assert.Equal(GlobalHotkeyRoute.SelectedWindow, spec.Route);
        Assert.Equal("Alt + Print Screen", spec.Name);
        Assert.Equal(GlobalHotkeyRouteMap.VK_SNAPSHOT, spec.VirtualKey);
        Assert.Equal(GlobalHotkeyRouteMap.MOD_ALT, spec.Modifiers);
    }

    // ── Ctrl + Print Screen → Selected Monitor ─────────────────────────

    [Fact]
    public void CtrlPrintScreen_MapsToSelectedMonitor()
    {
        var spec = GlobalHotkeyRouteMap.AllSpecs.First(s => s.Id == GlobalHotkeyRouteMap.IdCtrlPrintScreen);
        Assert.Equal(GlobalHotkeyRoute.SelectedMonitor, spec.Route);
        Assert.Equal("Ctrl + Print Screen", spec.Name);
        Assert.Equal(GlobalHotkeyRouteMap.VK_SNAPSHOT, spec.VirtualKey);
        Assert.Equal(GlobalHotkeyRouteMap.MOD_CONTROL, spec.Modifiers);
    }

    // ── Id uniqueness ───────────────────────────────────────────────────

    [Fact]
    public void AllSpecs_HaveUniqueIds()
    {
        var ids = GlobalHotkeyRouteMap.AllSpecs.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ── All use VK_SNAPSHOT ─────────────────────────────────────────────

    [Fact]
    public void AllSpecs_UseVKSnapshot()
    {
        Assert.All(GlobalHotkeyRouteMap.AllSpecs, spec =>
            Assert.Equal(GlobalHotkeyRouteMap.VK_SNAPSHOT, spec.VirtualKey));
    }

    // ── TryResolveRoute returns correct route for known ids ─────────────

    [Theory]
    [InlineData(GlobalHotkeyRouteMap.IdPrintScreen, GlobalHotkeyRoute.CurrentMonitor)]
    [InlineData(GlobalHotkeyRouteMap.IdWinPrintScreen, GlobalHotkeyRoute.ActiveWindow)]
    [InlineData(GlobalHotkeyRouteMap.IdShiftPrintScreen, GlobalHotkeyRoute.FullDesktop)]
    [InlineData(GlobalHotkeyRouteMap.IdWinShiftPrintScreen, GlobalHotkeyRoute.RectangularRegion)]
    [InlineData(GlobalHotkeyRouteMap.IdAltPrintScreen, GlobalHotkeyRoute.SelectedWindow)]
    [InlineData(GlobalHotkeyRouteMap.IdCtrlPrintScreen, GlobalHotkeyRoute.SelectedMonitor)]
    public void TryResolveRoute_ReturnsCorrectRoute(int id, GlobalHotkeyRoute expectedRoute)
    {
        Assert.True(GlobalHotkeyRouteMap.TryResolveRoute(id, out var route));
        Assert.Equal(expectedRoute, route);
    }

    // ── Unknown id does not resolve ─────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(99)]
    [InlineData(int.MaxValue)]
    public void TryResolveRoute_UnknownId_ReturnsFalse(int unknownId)
    {
        Assert.False(GlobalHotkeyRouteMap.TryResolveRoute(unknownId, out var route));
        Assert.Equal(default(GlobalHotkeyRoute), route);
    }

    // ── FindSpec ────────────────────────────────────────────────────────

    [Fact]
    public void FindSpec_KnownId_ReturnsSpec()
    {
        var spec = GlobalHotkeyRouteMap.FindSpec(GlobalHotkeyRouteMap.IdPrintScreen);
        Assert.NotNull(spec);
        Assert.Equal("Print Screen", spec!.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(999)]
    public void FindSpec_UnknownId_ReturnsNull(int unknownId)
    {
        Assert.Null(GlobalHotkeyRouteMap.FindSpec(unknownId));
    }

    // ── Win32 constants ─────────────────────────────────────────────────

    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal(0x0312, GlobalHotkeyRouteMap.WM_HOTKEY);
        Assert.Equal(0x2C, GlobalHotkeyRouteMap.VK_SNAPSHOT);
        Assert.Equal(0x0004, GlobalHotkeyRouteMap.MOD_SHIFT);
        Assert.Equal(0x0008, GlobalHotkeyRouteMap.MOD_WIN);
    }

    // ── Stable ids are 1–6 ──────────────────────────────────────────────

    [Fact]
    public void StableIds_AreOneThroughSix()
    {
        var ids = GlobalHotkeyRouteMap.AllSpecs.Select(s => s.Id).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, ids);
    }

    // ── GlobalHotkeyRegistrationResult sanitization ────────────────────────────

    [Fact]
    public void RegistrationResult_Fail_SanitizesFilePaths()
    {
        var spec = GlobalHotkeyRouteMap.AllSpecs[0];
        var result = GlobalHotkeyRegistrationResult.Fail(spec, "RegisterHotKey",
            "Error in C:\\Users\\test\\app.dll at line 42");

        Assert.False(result.Succeeded);
        Assert.Equal("RegisterHotKey", result.Phase);
        Assert.DoesNotContain("C:\\Users\\test\\app.dll", result.Error);
        Assert.Contains("[path]", result.Error);
    }

    [Fact]
    public void RegistrationResult_Fail_SanitizesStackTrace()
    {
        var spec = GlobalHotkeyRouteMap.AllSpecs[0];
        var message = "Operation failed\n   at System.Runtime.Method()\n   at App.Main()";
        var result = GlobalHotkeyRegistrationResult.Fail(spec, "RegisterHotKey", message);

        Assert.DoesNotContain("System.Runtime", result.Error);
        Assert.Contains("Operation failed", result.Error);
    }

    [Fact]
    public void RegistrationResult_Success_HasNoError()
    {
        var spec = GlobalHotkeyRouteMap.AllSpecs[0];
        var result = GlobalHotkeyRegistrationResult.Success(spec);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Error);
        Assert.Empty(result.Phase);
    }
}
