// GlobalHotkeyAllModesRemapTests.cs — Headless tests extending remapping to
// every Capture Mode's Global Hotkey row (issue #52).
//
// Verifies the #51 recorder contract generalized to all six rows: recording a
// combination on any row (including the new Selected Window and Selected
// Monitor routes) updates the working binding, cross-row conflict detection
// warns symmetrically across every pair of rows, the route map resolves
// persisted bindings for the new routes into Win32 specs, and Reconcile
// re-registers a changed binding while leaving the other five alone.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class GlobalHotkeyAllModesRemapTests
{
    private static readonly HotkeyBinding CtrlF6 = new(0x0002, 0x75);

    // One (row id, route) pair per Capture Mode row.
    public static IEnumerable<object[]> AllRows()
    {
        yield return new object[] { GlobalHotkeyRouteMap.IdPrintScreen, GlobalHotkeyRoute.CurrentMonitor };
        yield return new object[] { GlobalHotkeyRouteMap.IdWinPrintScreen, GlobalHotkeyRoute.ActiveWindow };
        yield return new object[] { GlobalHotkeyRouteMap.IdShiftPrintScreen, GlobalHotkeyRoute.FullDesktop };
        yield return new object[] { GlobalHotkeyRouteMap.IdWinShiftPrintScreen, GlobalHotkeyRoute.RectangularRegion };
        yield return new object[] { GlobalHotkeyRouteMap.IdAltPrintScreen, GlobalHotkeyRoute.SelectedWindow };
        yield return new object[] { GlobalHotkeyRouteMap.IdCtrlPrintScreen, GlobalHotkeyRoute.SelectedMonitor };
    }

    // ── Recording works identically on every row ────────────────────────

    [Theory]
    [MemberData(nameof(AllRows))]
    public void RecordBinding_OnAnyRow_UpdatesTheWorkingBinding(int id, GlobalHotkeyRoute route)
    {
        var tab = NewTab(new AppSettings());

        tab.RecordBinding(id, CtrlF6);

        Assert.Equal(CtrlF6, tab.WorkingBinding(id));

        // The working binding survives a WriteInto merge for this route.
        var target = AppSettings.WithDefaults();
        tab.WriteInto(target);
        Assert.Equal(CtrlF6, target.EffectiveGlobalHotkeyBinding(route));
    }

    [Theory]
    [MemberData(nameof(AllRows))]
    public void RecordBinding_OnAnyRow_ShowsOnTheRowAndDirties(int id, GlobalHotkeyRoute _)
    {
        var tab = NewTab(new AppSettings());
        Assert.False(tab.IsDirty);

        tab.RecordBinding(id, CtrlF6);

        Assert.Equal("Ctrl + F6", tab.GetRows().Single(r => r.Id == id).Binding);
        Assert.True(tab.IsDirty);
    }

    [Theory]
    [MemberData(nameof(AllRows))]
    public void Cancel_OnAnyRow_RevertsTheRecordedBinding(int id, GlobalHotkeyRoute route)
    {
        var tab = NewTab(new AppSettings());

        tab.RecordBinding(id, CtrlF6);
        tab.Cancel();

        Assert.Equal(
            GlobalHotkeyBindingDefaults.For(route),
            tab.WorkingBinding(id));
        Assert.False(tab.IsDirty);
    }

    [Theory]
    [MemberData(nameof(AllRows))]
    public void Reset_OnAnyRow_RestoresTheDefaultBinding(int id, GlobalHotkeyRoute route)
    {
        var persisted = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [route] = CtrlF6,
            },
        };
        var tab = NewTab(persisted);

        tab.Reset();

        Assert.Equal(GlobalHotkeyBindingDefaults.For(route), tab.WorkingBinding(id));
    }

    [Theory]
    [MemberData(nameof(AllRows))]
    public void WriteInto_OnAnyRow_PersistsTheRecordedBinding(int id, GlobalHotkeyRoute route)
    {
        var tab = NewTab(new AppSettings());
        tab.RecordBinding(id, CtrlF6);

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        Assert.Equal(CtrlF6, target.EffectiveGlobalHotkeyBinding(route));
    }

    // ── Cross-row conflicts across every pair, including the new rows ───

    [Theory]
    [MemberData(nameof(AllRows))]
    public void RecordBinding_MatchingAnotherEnabledRow_WarnsSymmetrically(int id, GlobalHotkeyRoute _)
    {
        // Record row A's default combination onto row B (any other enabled
        // row): both rows carry the warning — the conflict is mutual.
        var tab = NewTab(new AppSettings());
        var rows = GlobalHotkeyRouteMap.AllSpecs;
        var source = rows.First(s => s.Id == id);
        var target = rows.First(s => s.Id != id);
        var sourceDefault = GlobalHotkeyBindingDefaults.For(source.Route);

        var warning = tab.RecordBinding(target.Id, sourceDefault);

        Assert.NotNull(warning);
        var view = tab.GetRows();
        Assert.NotNull(view.Single(r => r.Id == target.Id).BindingWarning);
        Assert.NotNull(view.Single(r => r.Id == source.Id).BindingWarning);
    }

    [Fact]
    public void RecordBinding_MatchingNewRouteDefault_WarnsAndKeeps()
    {
        // Recording Selected Window's Alt + Print Screen onto Selected
        // Monitor is a suspected conflict: warned, never rejected.
        var tab = NewTab(new AppSettings());

        var warning = tab.RecordBinding(
            GlobalHotkeyRouteMap.IdCtrlPrintScreen,
            GlobalHotkeyBindingDefaults.For(GlobalHotkeyRoute.SelectedWindow));

        Assert.NotNull(warning);
        Assert.Contains("Alt + Print Screen", warning);
        Assert.Equal(
            GlobalHotkeyBindingDefaults.For(GlobalHotkeyRoute.SelectedWindow),
            tab.WorkingBinding(GlobalHotkeyRouteMap.IdCtrlPrintScreen));
    }

    [Fact]
    public void RecordBinding_MatchingADisabledNewRoute_NoWarning()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.SelectedWindow] = false,
            },
        };
        var tab = NewTab(source);

        var warning = tab.RecordBinding(
            GlobalHotkeyRouteMap.IdCtrlPrintScreen,
            GlobalHotkeyBindingDefaults.For(GlobalHotkeyRoute.SelectedWindow));

        Assert.Null(warning);
    }

    // ── Route map + reconcile for the new routes ────────────────────────

    [Fact]
    public void SpecsFor_Defaults_ResolveNewRoutesToTheirLegacyCombinations()
    {
        var settings = AppSettings.WithDefaults();

        var specs = GlobalHotkeyRouteMap.SpecsFor(settings);

        var selectedWindow = specs.Single(s => s.Route == GlobalHotkeyRoute.SelectedWindow);
        Assert.Equal(GlobalHotkeyRouteMap.MOD_ALT, selectedWindow.Modifiers);
        Assert.Equal(GlobalHotkeyRouteMap.VK_SNAPSHOT, selectedWindow.VirtualKey);
        Assert.Equal(GlobalHotkeyRouteMap.IdAltPrintScreen, selectedWindow.Id);
        var selectedMonitor = specs.Single(s => s.Route == GlobalHotkeyRoute.SelectedMonitor);
        Assert.Equal(GlobalHotkeyRouteMap.MOD_CONTROL, selectedMonitor.Modifiers);
        Assert.Equal(GlobalHotkeyRouteMap.VK_SNAPSHOT, selectedMonitor.VirtualKey);
        Assert.Equal(GlobalHotkeyRouteMap.IdCtrlPrintScreen, selectedMonitor.Id);
    }

    [Fact]
    public void Reconcile_RemappedSelectedWindow_UnregistersAndReRegistersOnlyThatGlobalHotkey()
    {
        var registrar = new FakeGlobalHotkeyRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.Reconcile(IntPtr.Zero, AppSettings.WithDefaults());
        int registerCallsAfterLegacy = registrar.RegisterCalls.Count;

        var remapped = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.SelectedWindow] = CtrlF6,
            },
        };

        manager.Reconcile(IntPtr.Zero, remapped);

        Assert.Contains(GlobalHotkeyRouteMap.IdAltPrintScreen, registrar.UnregisterCalls);
        var callsAfterLegacy = registrar.RegisterCalls.Skip(registerCallsAfterLegacy).ToList();
        var newCall = callsAfterLegacy.Single(c => c.id == GlobalHotkeyRouteMap.IdAltPrintScreen);
        Assert.Equal(CtrlF6.Modifiers, newCall.modifiers);
        Assert.Equal(CtrlF6.VirtualKey, newCall.vk);
        Assert.Single(callsAfterLegacy);
    }

    [Fact]
    public void Reconcile_AllSixDefaults_RegistersEveryCaptureModeRow()
    {
        var registrar = new FakeGlobalHotkeyRegistrar();
        var manager = new GlobalHotkeyManager(registrar);

        var results = manager.Reconcile(IntPtr.Zero, AppSettings.WithDefaults());

        Assert.Equal(6, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(
            new[] { 1, 2, 3, 4, 5, 6 },
            registrar.RegisterCalls.Select(c => c.id).OrderBy(x => x).ToArray());
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static GlobalHotkeyTabSettings NewTab(AppSettings source)
        => new GlobalHotkeyTabSettings(source, new RecordingGlobalHotkeyAdapter());

    private sealed class RecordingGlobalHotkeyAdapter : IGlobalHotkeyAdapter
    {
        private readonly List<GlobalHotkeyRegistrationResult> _results = new();

        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults => _results;

        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplySettings(AppSettings settings)
            => RegistrationResults;
    }
}
