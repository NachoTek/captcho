// HotkeyManagerTests.cs — Fake-registrar tests for HotkeyManager.
//
// Tests registration success, partial failure, idempotent cleanup,
// duplicate registration prevention, unknown id dispatch, and structured error output.
// Uses a FakeHotkeyRegistrar to avoid Win32 dependencies — fully headless.

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Respectacle.UI.Tests;

/// <summary>
/// Fake registrar that tracks calls and simulates success/failure per hotkey id.
/// Allows tests to control which ids succeed or fail without Win32.
/// </summary>
internal sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    private readonly HashSet<int> _registeredIds = new();
    private readonly HashSet<int> _idsThatFail = new();
    private readonly List<(int id, int modifiers, int vk)> _registerCalls = new();
    private readonly List<int> _unregisterCalls = new();

    /// <summary>Set of hotkey ids that should fail registration.</summary>
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

public class HotkeyManagerTests
{
    private readonly FakeHotkeyRegistrar _fake = new();

    // ── Full success ────────────────────────────────────────────────────

    [Fact]
    public void RegisterAll_AllSucceed_AllRegistered()
    {
        var manager = new HotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.True(manager.AllRegistered);
    }

    [Fact]
    public void RegisterAll_AllSucceed_RegistrarReceivedFourCalls()
    {
        var manager = new HotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);

        Assert.Equal(4, _fake.RegisterCalls.Count);
    }

    [Fact]
    public void RegisterAll_CallsRegistrarWithCorrectSpecs()
    {
        var manager = new HotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);

        // Verify each spec was passed correctly to the registrar
        foreach (var spec in HotkeyRouteMap.AllSpecs)
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
        _fake.SetFailingIds(HotkeyRouteMap.IdWinPrintScreen);
        var manager = new HotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        Assert.Equal(4, results.Count);
        Assert.Equal(3, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => !r.Succeeded));
        Assert.False(manager.AllRegistered);
    }

    [Fact]
    public void RegisterAll_OneFails_FailureHasCorrectSpec()
    {
        _fake.SetFailingIds(HotkeyRouteMap.IdWinPrintScreen);
        var manager = new HotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        var failure = results.First(r => !r.Succeeded);
        Assert.Equal(HotkeyRouteMap.IdWinPrintScreen, failure.Spec.Id);
        Assert.Equal("RegisterHotKey", failure.Phase);
        Assert.NotEmpty(failure.Error);
    }

    [Fact]
    public void RegisterAll_AllFail_NoneRegistered()
    {
        _fake.SetFailingIds(1, 2, 3, 4);
        var manager = new HotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.False(r.Succeeded));
        Assert.False(manager.AllRegistered);
    }

    // ── Cleanup ─────────────────────────────────────────────────────────

    [Fact]
    public void UnregisterAll_AfterSuccess_UnregistersAllFour()
    {
        var manager = new HotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        manager.UnregisterAll();

        Assert.Equal(4, _fake.UnregisterCalls.Count);
        Assert.Equal(4, _fake.UnregisterCalls.Distinct().Count());
    }

    [Fact]
    public void UnregisterAll_Idempotent_DoesNotThrowOnDoubleCall()
    {
        var manager = new HotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        manager.UnregisterAll();
        manager.UnregisterAll(); // second call should be safe

        // First unregister sends 4, second sends 0 (set was cleared)
        Assert.Equal(4, _fake.UnregisterCalls.Count);
    }

    [Fact]
    public void UnregisterAll_AfterPartialFailure_OnlyUnregistersSuccessful()
    {
        _fake.SetFailingIds(HotkeyRouteMap.IdWinPrintScreen);
        var manager = new HotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        manager.UnregisterAll();

        // Only 3 were successfully registered, so only 3 should be unregistered
        Assert.Equal(3, _fake.UnregisterCalls.Count);
        Assert.DoesNotContain(HotkeyRouteMap.IdWinPrintScreen, _fake.UnregisterCalls);
    }

    // ── Idempotent registration ─────────────────────────────────────────

    [Fact]
    public void RegisterAll_CalledTwice_Idempotent()
    {
        var manager = new HotkeyManager(_fake);
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
    [InlineData(HotkeyRouteMap.IdPrintScreen, HotkeyRoute.CurrentMonitor)]
    [InlineData(HotkeyRouteMap.IdWinPrintScreen, HotkeyRoute.ActiveWindow)]
    [InlineData(HotkeyRouteMap.IdShiftPrintScreen, HotkeyRoute.FullDesktop)]
    [InlineData(HotkeyRouteMap.IdWinShiftPrintScreen, HotkeyRoute.RectangularRegion)]
    public void TryResolveRoute_ReturnsCorrectRoute(int id, HotkeyRoute expected)
    {
        var manager = new HotkeyManager(_fake);
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
        var manager = new HotkeyManager(_fake);
        Assert.False(manager.TryResolveRoute(unknownId, out var route));
        Assert.Equal(default(HotkeyRoute), route);
    }

    // ── Registration summary ────────────────────────────────────────────

    [Fact]
    public void GetRegistrationSummary_BeforeRegistration_ReportsNotRegistered()
    {
        var manager = new HotkeyManager(_fake);
        var summary = manager.GetRegistrationSummary();
        Assert.Contains("not registered", summary);
    }

    [Fact]
    public void GetRegistrationSummary_AllSuccess_ReportsAllRegistered()
    {
        var manager = new HotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);
        var summary = manager.GetRegistrationSummary();
        Assert.Contains("All 4 hotkeys registered", summary);
    }

    [Fact]
    public void GetRegistrationSummary_PartialFailure_ReportsConflicts()
    {
        _fake.SetFailingIds(HotkeyRouteMap.IdWinPrintScreen);
        var manager = new HotkeyManager(_fake);
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
        Assert.Throws<ArgumentNullException>(() => new HotkeyManager(null!));
    }

    // ── WM_HOTKEY dispatch simulation ───────────────────────────────────

    [Fact]
    public void SimulatedWmHotkey_KnownId_ResolveRoute()
    {
        // Simulates what MainWindow will do: receive WM_HOTKEY, extract id, resolve route
        var manager = new HotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);

        // Simulate each hotkey being pressed
        foreach (var spec in HotkeyRouteMap.AllSpecs)
        {
            Assert.True(manager.TryResolveRoute(spec.Id, out var route));
            Assert.Equal(spec.Route, route);
        }
    }

    [Fact]
    public void SimulatedWmHotkey_UnknownId_DoesNotTriggerRoute()
    {
        var manager = new HotkeyManager(_fake);
        manager.RegisterAll(IntPtr.Zero);

        // Simulate a random WM_HOTKEY with id 999
        Assert.False(manager.TryResolveRoute(999, out _));
    }

    // ── Structured error sanitization ────────────────────────────────────

    [Fact]
    public void RegistrationResults_NoStackTracesInErrors()
    {
        _fake.SetFailingIds(HotkeyRouteMap.IdPrintScreen);
        var manager = new HotkeyManager(_fake);
        var results = manager.RegisterAll(IntPtr.Zero);

        foreach (var result in results)
        {
            if (!result.Succeeded)
            {
                Assert.DoesNotContain("   at ", result.Error);
            }
        }
    }
}
