// GlobalHotkeyManagerTests.cs — Fake-registrar tests for GlobalHotkeyManager.
//
// Tests registration success, partial failure, idempotent cleanup,
// duplicate registration prevention, unknown id dispatch, and structured error output.
// Uses a FakeGlobalHotkeyRegistrar to avoid Win32 dependencies — fully headless.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

/// <summary>
/// Fake registrar that tracks calls and simulates success/failure per Global Hotkey id.
/// Allows tests to control which ids succeed or fail without Win32.
/// </summary>
internal sealed class FakeGlobalHotkeyRegistrar : IGlobalHotkeyRegistrar
{
    private readonly HashSet<int> _registeredIds = new();
    private readonly HashSet<int> _idsThatFail = new();
    private readonly List<(int id, int modifiers, int vk)> _registerCalls = new();
    private readonly List<int> _unregisterCalls = new();

    /// <summary>Set of Global Hotkey ids that should fail registration.</summary>
    public void SetFailingIds(params int[] ids)
    {
        foreach (var id in ids)
            _idsThatFail.Add(id);
    }

    /// <summary>All RegisterHotKey calls received, in order.</summary>
    public IReadOnlyList<(int id, int modifiers, int vk)> RegisterCalls => _registerCalls;

    /// <summary>All UnregisterHotKey calls received, in order.</summary>
    public IReadOnlyList<int> UnregisterCalls => _unregisterCalls;

    public bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int virtualKey)
    {
        _registerCalls.Add((id, modifiers, virtualKey));
        if (_idsThatFail.Contains(id))
            return false;
        _registeredIds.Add(id);
        return true;
    }

    public bool UnregisterHotKey(IntPtr hwnd, int id)
    {
        _unregisterCalls.Add(id);
        return _registeredIds.Remove(id);
    }
}

public class GlobalHotkeyManagerTests
{
    private readonly FakeGlobalHotkeyRegistrar _fake = new();

    // ── Full success ────────────────────────────────────────────────────

    [Fact]
    public void RegisterAll_AllSucceed_AllRegistered()
    {
        var manager = new GlobalHotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.True(manager.AllRegistered);
    }

    [Fact]
    public void RegisterAll_AllSucceed_RegistrarReceivedFourCalls()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);

        Assert.Equal(4, _fake.RegisterCalls.Count);
    }

    [Fact]
    public void RegisterAll_CallsRegistrarWithCorrectSpecs()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);

        // Verify each spec was passed correctly to the registrar
        foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
        {
            var call = _fake.RegisterCalls.FirstOrDefault(c => c.id == spec.Id);
            Assert.NotEqual(default, call);
            Assert.Equal(spec.Modifiers, call.modifiers);
            Assert.Equal(spec.VirtualKey, call.vk);
        }
    }

    // ── Partial failure ─────────────────────────────────────────────────

    [Fact]
    public void RegisterAll_OneFails_ThreeSucceed()
    {
        _fake.SetFailingIds(GlobalHotkeyRouteMap.IdWinPrintScreen);
        var manager = new GlobalHotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        Assert.Equal(4, results.Count);
        Assert.Equal(3, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => !r.Succeeded));
        Assert.False(manager.AllRegistered);
    }

    [Fact]
    public void RegisterAll_OneFails_FailureHasCorrectSpec()
    {
        _fake.SetFailingIds(GlobalHotkeyRouteMap.IdWinPrintScreen);
        var manager = new GlobalHotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        var failure = results.First(r => !r.Succeeded);
        Assert.Equal(GlobalHotkeyRouteMap.IdWinPrintScreen, failure.Spec.Id);
        Assert.Equal("RegisterHotKey", failure.Phase);
        Assert.NotEmpty(failure.Error);
    }

    [Fact]
    public void RegisterAll_AllFail_NoneRegistered()
    {
        _fake.SetFailingIds(1, 2, 3, 4);
        var manager = new GlobalHotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.False(r.Succeeded));
        Assert.False(manager.AllRegistered);
    }

    // ── Cleanup ─────────────────────────────────────────────────────────

    [Fact]
    public void UnregisterAll_AfterSuccess_UnregistersAllFour()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        manager.UnregisterAll();

        Assert.Equal(4, _fake.UnregisterCalls.Count);
        Assert.Equal(4, _fake.UnregisterCalls.Distinct().Count());
    }

    [Fact]
    public void UnregisterAll_Idempotent_DoesNotThrowOnDoubleCall()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        manager.UnregisterAll();
        manager.UnregisterAll(); // second call should be safe

        // First unregister sends 4, second sends 0 (set was cleared)
        Assert.Equal(4, _fake.UnregisterCalls.Count);
    }

    [Fact]
    public void UnregisterAll_AfterPartialFailure_OnlyUnregistersSuccessful()
    {
        _fake.SetFailingIds(GlobalHotkeyRouteMap.IdWinPrintScreen);
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        manager.UnregisterAll();

        // Only 3 were successfully registered, so only 3 should be unregistered
        Assert.Equal(3, _fake.UnregisterCalls.Count);
        Assert.DoesNotContain(GlobalHotkeyRouteMap.IdWinPrintScreen, _fake.UnregisterCalls);
    }

    // ── Idempotent registration ─────────────────────────────────────────

    [Fact]
    public void RegisterAll_CalledTwice_Idempotent()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        var secondResults = manager.RegisterAll(IntPtr.Zero);

        // Second call should report success for all (already registered)
        Assert.All(secondResults, r => Assert.True(r.Succeeded));
        // Registrar still receives 4 calls from the first batch only;
        // the second batch skips already-registered ids
        Assert.Equal(4, _fake.RegisterCalls.Count);
    }

    // ── Route resolution ────────────────────────────────────────────────

    [Theory]
    [InlineData(GlobalHotkeyRouteMap.IdPrintScreen, GlobalHotkeyRoute.CurrentMonitor)]
    [InlineData(GlobalHotkeyRouteMap.IdWinPrintScreen, GlobalHotkeyRoute.ActiveWindow)]
    [InlineData(GlobalHotkeyRouteMap.IdShiftPrintScreen, GlobalHotkeyRoute.FullDesktop)]
    [InlineData(GlobalHotkeyRouteMap.IdWinShiftPrintScreen, GlobalHotkeyRoute.RectangularRegion)]
    public void TryResolveRoute_ReturnsCorrectRoute(int id, GlobalHotkeyRoute expected)
    {
        var manager = new GlobalHotkeyManager(_fake);
        Assert.True(manager.TryResolveRoute(id, out var route));
        Assert.Equal(expected, route);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(999)]
    public void TryResolveRoute_UnknownId_ReturnsFalse(int unknownId)
    {
        var manager = new GlobalHotkeyManager(_fake);
        Assert.False(manager.TryResolveRoute(unknownId, out var route));
        Assert.Equal(default(GlobalHotkeyRoute), route);
    }

    // ── Registration summary ────────────────────────────────────────────

    [Fact]
    public void GetRegistrationSummary_BeforeRegistration_ReportsNotRegistered()
    {
        var manager = new GlobalHotkeyManager(_fake);
        var summary = manager.GetRegistrationSummary();
        Assert.Contains("not registered", summary);
    }

    [Fact]
    public void GetRegistrationSummary_AllSuccess_ReportsAllRegistered()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        var summary = manager.GetRegistrationSummary();
        Assert.Contains("All 4 global hotkeys registered", summary);
    }

    [Fact]
    public void GetRegistrationSummary_PartialFailure_ReportsConflicts()
    {
        _fake.SetFailingIds(GlobalHotkeyRouteMap.IdWinPrintScreen);
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        var summary = manager.GetRegistrationSummary();

        Assert.Contains("3/4", summary);
        Assert.Contains("Conflicts", summary);
        Assert.Contains("Win + Print Screen", summary);
    }

    // ── Null registrar ──────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullRegistrar_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new GlobalHotkeyManager(null!));
    }

    // ── WM_HOTKEY dispatch simulation ───────────────────────────────────

    [Fact]
    public void SimulatedWmGlobalHotkey_KnownId_ResolveRoute()
    {
        // Simulates what MainWindow will do: receive WM_HOTKEY, extract id, resolve route
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);

        // Simulate each Global Hotkey being pressed
        foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
        {
            Assert.True(manager.TryResolveRoute(spec.Id, out var route));
            Assert.Equal(spec.Route, route);
        }
    }

    [Fact]
    public void SimulatedWmGlobalHotkey_UnknownId_DoesNotTriggerRoute()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);

        // Simulate a random WM_HOTKEY with id 999
        Assert.False(manager.TryResolveRoute(999, out _));
    }

    // ── Structured error sanitization ────────────────────────────────────

    [Fact]
    public void RegistrationResults_NoStackTracesInErrors()
    {
        _fake.SetFailingIds(GlobalHotkeyRouteMap.IdPrintScreen);
        var manager = new GlobalHotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        foreach (var result in results)
        {
            if (!result.Succeeded)
            {
                Assert.DoesNotContain("   at ", result.Error);
            }
        }
    }

    // ── Reconcile (register enabled / unregister disabled) ──────────────

    [Fact]
    public void Reconcile_AllEnabledOnFreshManager_RegistersAllFour()
    {
        var manager = new GlobalHotkeyManager(_fake);
        var enabled = new HashSet<int> { 1, 2, 3, 4 };

        var results = manager.Reconcile(IntPtr.Zero, enabled);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(4, _fake.RegisterCalls.Count);
        Assert.Empty(_fake.UnregisterCalls);
    }

    [Fact]
    public void Reconcile_DisabledIds_AreNotRegistered()
    {
        var manager = new GlobalHotkeyManager(_fake);
        var enabled = new HashSet<int> { 1, 3 };

        var results = manager.Reconcile(IntPtr.Zero, enabled);

        // Only the enabled ids produce registration results.
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Contains(r.Spec.Id, enabled));
        Assert.Equal(2, _fake.RegisterCalls.Count);
        var registeredIds = _fake.RegisterCalls.Select(c => c.id).ToHashSet();
        Assert.Equal(enabled, registeredIds);
    }

    [Fact]
    public void Reconcile_OnActiveManager_UnregistersNewlyDisabled()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.Reconcile(IntPtr.Zero, new HashSet<int> { 1, 2, 3, 4 });

        var results = manager.Reconcile(IntPtr.Zero, new HashSet<int> { 1, 3 });

        // Id 2 and 4 should be unregistered.
        Assert.Contains(2, _fake.UnregisterCalls);
        Assert.Contains(4, _fake.UnregisterCalls);
        // Remaining results cover only the still-enabled ids.
        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.Spec.Id == 2 || r.Spec.Id == 4);
    }

    [Fact]
    public void Reconcile_RegistersNewlyEnabledWithoutReRegisteringExisting()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.Reconcile(IntPtr.Zero, new HashSet<int> { 1, 2 });
        int registerCountAfterFirst = _fake.RegisterCalls.Count;

        manager.Reconcile(IntPtr.Zero, new HashSet<int> { 1, 2, 3, 4 });

        // Only the two newly-enabled ids (3, 4) should be newly registered;
        // already-registered ids (1, 2) must not be registered again.
        int newRegistrations = _fake.RegisterCalls.Count - registerCountAfterFirst;
        Assert.Equal(2, newRegistrations);
        var newlyRegistered = _fake.RegisterCalls
            .Skip(registerCountAfterFirst)
            .Select(c => c.id)
            .ToHashSet();
        Assert.Equal(new HashSet<int> { 3, 4 }, newlyRegistered);
    }

    [Fact]
    public void Reconcile_PartialFailure_RecordsFailureForThatIdOnly()
    {
        _fake.SetFailingIds(GlobalHotkeyRouteMap.IdWinPrintScreen);
        var manager = new GlobalHotkeyManager(_fake);

        var results = manager.Reconcile(IntPtr.Zero, new HashSet<int> { 1, 2, 3, 4 });

        Assert.Equal(4, results.Count);
        Assert.Equal(3, results.Count(r => r.Succeeded));
        var failure = results.Single(r => !r.Succeeded);
        Assert.Equal(GlobalHotkeyRouteMap.IdWinPrintScreen, failure.Spec.Id);
        Assert.Equal("RegisterHotKey", failure.Phase);
    }

    [Fact]
    public void Reconcile_EmptyEnabledSet_UnregistersEverythingAndReportsNone()
    {
        var manager = new GlobalHotkeyManager(_fake);
        manager.Reconcile(IntPtr.Zero, new HashSet<int> { 1, 2, 3, 4 });

        var results = manager.Reconcile(IntPtr.Zero, new HashSet<int>());

        Assert.Empty(results);
        Assert.Equal(4, _fake.UnregisterCalls.Count);
    }

    [Fact]
    public void Reconcile_NullEnabledIds_Throws()
    {
        var manager = new GlobalHotkeyManager(_fake);
        Assert.Throws<ArgumentNullException>(() => manager.Reconcile(IntPtr.Zero, null!));
    }

    [Fact]
    public void GetRegistrationSummary_AfterReconcileWithDisabled_HasNoSpuriousConflicts()
    {
        var manager = new GlobalHotkeyManager(_fake);
        // Three enabled, one intentionally disabled.
        manager.Reconcile(IntPtr.Zero, new HashSet<int> { 1, 2, 3 });

        var summary = manager.GetRegistrationSummary();

        // All enabled ones registered — no conflict wording.
        Assert.Contains("All 3 global hotkeys registered", summary);
        Assert.DoesNotContain("Conflicts", summary);
    }
}
