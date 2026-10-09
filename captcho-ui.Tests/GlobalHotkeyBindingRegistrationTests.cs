// GlobalHotkeyBindingRegistrationTests.cs — Settings-driven Global Hotkey
// registration tests (issue #51).
//
// Verifies the registration slice of remapping one Global Hotkey end to end:
// the route map resolves persisted bindings into Win32 specs, Reconcile
// re-registers a Global Hotkey whose combination changed (and only that one),
// a registration failure is recorded with a sanitized error, and a successful
// reconcile with a remapped binding still resolves WM_HOTKEY ids to the right
// capture route.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class GlobalHotkeyBindingRegistrationTests
{
    private static readonly HotkeyBinding CtrlF5 = new(0x0002, 0x74);

    // ── Route map: persisted bindings resolve into Win32 specs ─────────

    [Fact]
    public void SpecsFor_SettingsWithoutBindings_MatchLegacySpecs()
    {
        var settings = AppSettings.WithDefaults();

        var specs = GlobalHotkeyRouteMap.SpecsFor(settings);

        Assert.Equal(GlobalHotkeyRouteMap.AllSpecs, specs);
    }

    [Fact]
    public void SpecsFor_RemappedFullDesktop_UsesTheRecordedCombination()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = CtrlF5,
            },
        };

        var specs = GlobalHotkeyRouteMap.SpecsFor(settings);

        var fullDesktop = specs.Single(s => s.Route == GlobalHotkeyRoute.FullDesktop);
        Assert.Equal(CtrlF5.Modifiers, fullDesktop.Modifiers);
        Assert.Equal(CtrlF5.VirtualKey, fullDesktop.VirtualKey);
    }

    [Fact]
    public void SpecsFor_RemappedFullDesktop_LeavesOtherRoutesAtDefaults()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = CtrlF5,
            },
        };

        var specs = GlobalHotkeyRouteMap.SpecsFor(settings);

        var unchanged = specs.Where(s => s.Route != GlobalHotkeyRoute.FullDesktop);
        foreach (var legacy in GlobalHotkeyRouteMap.AllSpecs.Where(s => s.Route != GlobalHotkeyRoute.FullDesktop))
        {
            var spec = unchanged.Single(s => s.Route == legacy.Route);
            Assert.Equal(legacy.Modifiers, spec.Modifiers);
            Assert.Equal(legacy.VirtualKey, spec.VirtualKey);
        }
    }

    [Fact]
    public void SpecsFor_RemappedBinding_KeepsStableIdAndRoute()
    {
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = CtrlF5,
            },
        };

        var spec = GlobalHotkeyRouteMap.SpecsFor(settings)
            .Single(s => s.Route == GlobalHotkeyRoute.FullDesktop);

        Assert.Equal(GlobalHotkeyRouteMap.IdShiftPrintScreen, spec.Id);
    }

    // ── Manager: reconcile with settings ───────────────────────────────

    [Fact]
    public void Reconcile_AfterBindingChange_UnregistersAndReRegistersOnlyThatGlobalHotkey()
    {
        var registrar = new FakeGlobalHotkeyRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        var legacy = AppSettings.WithDefaults();
        manager.Reconcile(IntPtr.Zero, legacy);
        int registerCallsAfterLegacy = registrar.RegisterCalls.Count;

        var remapped = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = CtrlF5,
            },
        };

        manager.Reconcile(IntPtr.Zero, remapped);

        // The Full Desktop Global Hotkey was unregistered (legacy combination) and
        // re-registered exactly once with the new combination; nothing else moved.
        Assert.Contains(GlobalHotkeyRouteMap.IdShiftPrintScreen, registrar.UnregisterCalls);
        var callsAfterLegacy = registrar.RegisterCalls.Skip(registerCallsAfterLegacy).ToList();
        var newCall = callsAfterLegacy.Single(c => c.id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal(CtrlF5.Modifiers, newCall.modifiers);
        Assert.Equal(CtrlF5.VirtualKey, newCall.vk);
        Assert.Single(callsAfterLegacy);
    }

    [Fact]
    public void Reconcile_SameSettings_DoesNotTouchRegistrations()
    {
        var registrar = new FakeGlobalHotkeyRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = CtrlF5,
            },
        };

        manager.Reconcile(IntPtr.Zero, settings);
        int registers = registrar.RegisterCalls.Count;
        int unregisters = registrar.UnregisterCalls.Count;

        manager.Reconcile(IntPtr.Zero, settings);

        Assert.Equal(registers, registrar.RegisterCalls.Count);
        Assert.Equal(unregisters, registrar.UnregisterCalls.Count);
    }

    [Fact]
    public void Reconcile_FailedRegistration_RecordsSanitizedFailureForThatGlobalHotkey()
    {
        var registrar = new FakeGlobalHotkeyRegistrar();
        registrar.SetFailingIds(GlobalHotkeyRouteMap.IdShiftPrintScreen);
        var manager = new GlobalHotkeyManager(registrar);
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = CtrlF5,
            },
        };

        var results = manager.Reconcile(IntPtr.Zero, settings);

        var failure = results.Single(r => r.Spec.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.False(failure.Succeeded);
        Assert.Equal("RegisterHotKey", failure.Phase);
        Assert.NotEmpty(failure.Error);
    }

    // ── WM_HOTKEY routing after remap ──────────────────────────────────

    [Fact]
    public void Reconcile_RemappedBinding_StillsResolveItsIdToFullDesktop()
    {
        var registrar = new FakeGlobalHotkeyRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        var settings = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = CtrlF5,
            },
        };

        manager.Reconcile(IntPtr.Zero, settings);

        Assert.True(manager.TryResolveRoute(GlobalHotkeyRouteMap.IdShiftPrintScreen, out var route));
        Assert.Equal(GlobalHotkeyRoute.FullDesktop, route);
    }
}
