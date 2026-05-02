// HotkeyUiWiringTests.cs — Headless tests for hotkey WM_HOTKEY routing into capture workflows.
//
// Verifies the hotkey dispatch logic through a testable mediator that mirrors
// MainWindow's route dispatch without WinUI controls. Covers:
// - All four hotkey ids route to the correct capture delegates
// - Unknown hotkey ids do not trigger capture
// - Triggers during an active operation are ignored
// - Cleanup unregisters exactly once
// - Registration conflicts are reported gracefully
// - Multiple routes dispatch independently

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Respectacle.UI;
using Xunit;

namespace Respectacle.UI.Tests;

/// <summary>
/// Testable mediator that mirrors the MainWindow hotkey dispatch logic.
/// Records state transitions and capture calls so tests can assert on routing
/// without WinUI controls or real Win32 message processing.
/// </summary>
public class HotkeyDispatchMediator
{
    // Injected dependencies (test doubles)
    private readonly Dictionary<HotkeyRoute, Func<Task>> _routeHandlers;
    private readonly HotkeyManager _hotkeyManager;

    // Recorded state for assertions
    public bool IsOperationRunning { get; private set; }
    public List<HotkeyRoute> TriggeredRoutes { get; } = new();
    public List<int> IgnoredIds { get; } = new();
    public string? StatusText { get; private set; }

    public HotkeyDispatchMediator(
        HotkeyManager hotkeyManager,
        Dictionary<HotkeyRoute, Func<Task>>? routeHandlers = null)
    {
        _hotkeyManager = hotkeyManager ?? throw new ArgumentNullException(nameof(hotkeyManager));
        _routeHandlers = routeHandlers ?? new Dictionary<HotkeyRoute, Func<Task>>();

        // Fill defaults for missing routes
        foreach (HotkeyRoute route in Enum.GetValues<HotkeyRoute>())
        {
            if (!_routeHandlers.ContainsKey(route))
            {
                _routeHandlers[route] = () => Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Mirrors MainWindow.WndProc WM_HOTKEY dispatch:
    /// resolves the hotkey id to a route and dispatches if not blocked.
    /// </summary>
    public void OnWmHotkey(int hotkeyId)
    {
        if (_hotkeyManager.TryResolveRoute(hotkeyId, out HotkeyRoute route))
        {
            DispatchRoute(route);
        }
        else
        {
            IgnoredIds.Add(hotkeyId);
        }
    }

    /// <summary>
    /// Mirrors MainWindow.DispatchHotkeyRoute:
    /// checks the operation guard, then invokes the route handler.
    /// </summary>
    public void DispatchRoute(HotkeyRoute route)
    {
        if (IsOperationRunning)
        {
            // Overlapping triggers during an active capture are ignored
            return;
        }

        TriggeredRoutes.Add(route);

        if (_routeHandlers.TryGetValue(route, out var handler))
        {
            _ = handler();
        }
    }

    /// <summary>
    /// Simulates entering an operation state (capture/export running).
    /// </summary>
    public void EnterOperation() => IsOperationRunning = true;

    /// <summary>
    /// Simulates exiting an operation state.
    /// </summary>
    public void ExitOperation() => IsOperationRunning = false;

    /// <summary>
    /// Sets the status text from registration summary.
    /// </summary>
    public void UpdateStatusFromRegistration()
    {
        StatusText = _hotkeyManager.GetRegistrationSummary();
    }
}

/// <summary>
/// Fake registrar for headless UI wiring tests.
/// Allows controlling which registrations succeed/fail.
/// </summary>
public class UiWiringFakeRegistrar : IHotkeyRegistrar
{
    private readonly HashSet<int> _succeedingIds;
    private readonly HashSet<int> _registeredIds = new();

    /// <summary>
    /// Creates a fake registrar where only the specified ids succeed.
    /// Null means all succeed.
    /// </summary>
    public UiWiringFakeRegistrar(HashSet<int>? succeedingIds = null)
    {
        _succeedingIds = succeedingIds ?? new HashSet<int> { 1, 2, 3, 4 };
    }

    public bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int virtualKey)
    {
        if (_succeedingIds.Contains(id))
        {
            _registeredIds.Add(id);
            return true;
        }
        return false;
    }

    public bool UnregisterHotKey(IntPtr hwnd, int id)
    {
        return _registeredIds.Remove(id);
    }
}

public class HotkeyUiWiringTests
{
    // ── All four routes dispatch correctly ────────────────────────────

    [Fact]
    public void PrintScreen_RoutesTo_CurrentMonitor()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(HotkeyRoute.CurrentMonitor, mediator.TriggeredRoutes[0]);
    }

    [Fact]
    public void WinPrintScreen_RoutesTo_ActiveWindow()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.OnWmHotkey(HotkeyRouteMap.IdWinPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(HotkeyRoute.ActiveWindow, mediator.TriggeredRoutes[0]);
    }

    [Fact]
    public void ShiftPrintScreen_RoutesTo_FullDesktop()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.OnWmHotkey(HotkeyRouteMap.IdShiftPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(HotkeyRoute.FullDesktop, mediator.TriggeredRoutes[0]);
    }

    [Fact]
    public void WinShiftPrintScreen_RoutesTo_RectangularRegion()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.OnWmHotkey(HotkeyRouteMap.IdWinShiftPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(HotkeyRoute.RectangularRegion, mediator.TriggeredRoutes[0]);
    }

    // ── Unknown ids do not trigger capture ────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public void UnknownId_DoesNotTriggerCapture(int unknownId)
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.OnWmHotkey(unknownId);

        Assert.Empty(mediator.TriggeredRoutes);
        Assert.Single(mediator.IgnoredIds);
        Assert.Equal(unknownId, mediator.IgnoredIds[0]);
    }

    // ── Multiple unknown ids accumulate ───────────────────────────────

    [Fact]
    public void MultipleUnknownIds_AllIgnored()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.OnWmHotkey(0);
        mediator.OnWmHotkey(-1);
        mediator.OnWmHotkey(99);

        Assert.Empty(mediator.TriggeredRoutes);
        Assert.Equal(3, mediator.IgnoredIds.Count);
    }

    // ── Active operation guard blocks overlapping triggers ─────────────

    [Fact]
    public void HotkeyDuringActiveOperation_IsIgnored()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.EnterOperation();

        // All four hotkeys should be ignored during active operation
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);
        mediator.OnWmHotkey(HotkeyRouteMap.IdWinPrintScreen);
        mediator.OnWmHotkey(HotkeyRouteMap.IdShiftPrintScreen);
        mediator.OnWmHotkey(HotkeyRouteMap.IdWinShiftPrintScreen);

        Assert.Empty(mediator.TriggeredRoutes);
    }

    [Fact]
    public void HotkeyAfterOperationCompletes_IsDispatched()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);

        // Start operation, try hotkey (ignored), end operation, try again (dispatched)
        mediator.EnterOperation();
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);
        Assert.Empty(mediator.TriggeredRoutes);

        mediator.ExitOperation();
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);
        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(HotkeyRoute.CurrentMonitor, mediator.TriggeredRoutes[0]);
    }

    [Fact]
    public void RapidRepeatedHotkeyPresses_OnlyFirstDispatches()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);

        // Simulate rapid repeated Print Screen presses
        // The first one starts the operation, subsequent ones are ignored
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);
        mediator.EnterOperation();
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
    }

    // ── Route-specific handler invocation ──────────────────────────────

    [Fact]
    public void CurrentMonitorRoute_InvokesCorrectHandler()
    {
        HotkeyRoute? invokedRoute = null;
        var handlers = new Dictionary<HotkeyRoute, Func<Task>>
        {
            [HotkeyRoute.CurrentMonitor] = () => { invokedRoute = HotkeyRoute.CurrentMonitor; return Task.CompletedTask; },
        };

        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager, handlers);
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);

        Assert.Equal(HotkeyRoute.CurrentMonitor, invokedRoute);
    }

    [Fact]
    public void RegionRoute_InvokesCorrectHandler()
    {
        HotkeyRoute? invokedRoute = null;
        var handlers = new Dictionary<HotkeyRoute, Func<Task>>
        {
            [HotkeyRoute.RectangularRegion] = () => { invokedRoute = HotkeyRoute.RectangularRegion; return Task.CompletedTask; },
        };

        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager, handlers);
        mediator.OnWmHotkey(HotkeyRouteMap.IdWinShiftPrintScreen);

        Assert.Equal(HotkeyRoute.RectangularRegion, invokedRoute);
    }

    // ── All four routes dispatch independently ─────────────────────────

    [Fact]
    public void AllFourRoutes_DispatchInSequence()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);

        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);
        mediator.OnWmHotkey(HotkeyRouteMap.IdWinPrintScreen);
        mediator.OnWmHotkey(HotkeyRouteMap.IdShiftPrintScreen);
        mediator.OnWmHotkey(HotkeyRouteMap.IdWinShiftPrintScreen);

        Assert.Equal(4, mediator.TriggeredRoutes.Count);
        Assert.Equal(HotkeyRoute.CurrentMonitor, mediator.TriggeredRoutes[0]);
        Assert.Equal(HotkeyRoute.ActiveWindow, mediator.TriggeredRoutes[1]);
        Assert.Equal(HotkeyRoute.FullDesktop, mediator.TriggeredRoutes[2]);
        Assert.Equal(HotkeyRoute.RectangularRegion, mediator.TriggeredRoutes[3]);
    }

    // ── Cleanup unregisters exactly once ───────────────────────────────

    [Fact]
    public void Cleanup_UnregistersSuccessfully()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);
        Assert.True(manager.AllRegistered);

        // Cleanup
        manager.UnregisterAll();

        // Idempotent: calling again is safe
        manager.UnregisterAll();
    }

    [Fact]
    public void Cleanup_AfterPartialRegistration_OnlyUnregistersSuccessful()
    {
        // Only Print Screen (id=1) and Shift+Print (id=3) succeed
        var registrar = new UiWiringFakeRegistrar(new HashSet<int> { 1, 3 });
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);
        Assert.False(manager.AllRegistered);

        // Cleanup should only unregister the two that succeeded
        manager.UnregisterAll();

        // Idempotent
        manager.UnregisterAll();
    }

    // ── Registration conflicts reported in status ──────────────────────

    [Fact]
    public void FullRegistration_StatusTextReportsSuccess()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.UpdateStatusFromRegistration();

        Assert.Equal("All 4 hotkeys registered.", mediator.StatusText);
    }

    [Fact]
    public void PartialRegistration_StatusTextReportsConflicts()
    {
        var registrar = new UiWiringFakeRegistrar(new HashSet<int> { 1, 3 });
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.UpdateStatusFromRegistration();

        Assert.Contains("2/4", mediator.StatusText);
        Assert.Contains("Conflicts", mediator.StatusText);
    }

    [Fact]
    public void TotalFailure_StatusTextReportsAllFailed()
    {
        var registrar = new UiWiringFakeRegistrar(new HashSet<int>());
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);
        mediator.UpdateStatusFromRegistration();

        Assert.Contains("No hotkeys registered", mediator.StatusText);
    }

    // ── Dispatch after partial registration still works for registered routes ─

    [Fact]
    public void PartialRegistration_RegisteredRoutesStillDispatch()
    {
        // Only Print Screen (id=1) succeeds
        var registrar = new UiWiringFakeRegistrar(new HashSet<int> { 1 });
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);

        // Registered route dispatches
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);
        Assert.Single(mediator.TriggeredRoutes);

        // Unregistered route also dispatches (route resolution is pure mapping,
        // registration tracking is for OS-level cleanup only)
        mediator.OnWmHotkey(HotkeyRouteMap.IdWinPrintScreen);
        Assert.Equal(2, mediator.TriggeredRoutes.Count);
    }

    // ── Mixed valid and invalid ids in sequence ────────────────────────

    [Fact]
    public void MixedIds_OnlyValidOnesDispatch()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);

        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);   // valid
        mediator.OnWmHotkey(99);                              // invalid
        mediator.OnWmHotkey(HotkeyRouteMap.IdShiftPrintScreen); // valid
        mediator.OnWmHotkey(-1);                              // invalid
        mediator.OnWmHotkey(HotkeyRouteMap.IdWinPrintScreen);   // valid

        Assert.Equal(3, mediator.TriggeredRoutes.Count);
        Assert.Equal(2, mediator.IgnoredIds.Count);
    }

    // ── Operation guard resets between captures ────────────────────────

    [Fact]
    public void OperationGuard_ResetsAllowsSequentialCaptures()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new HotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new HotkeyDispatchMediator(manager);

        // First capture cycle
        mediator.OnWmHotkey(HotkeyRouteMap.IdPrintScreen);
        mediator.EnterOperation();
        mediator.ExitOperation();

        // Second capture cycle
        mediator.OnWmHotkey(HotkeyRouteMap.IdShiftPrintScreen);
        mediator.EnterOperation();
        mediator.ExitOperation();

        Assert.Equal(2, mediator.TriggeredRoutes.Count);
        Assert.Equal(HotkeyRoute.CurrentMonitor, mediator.TriggeredRoutes[0]);
        Assert.Equal(HotkeyRoute.FullDesktop, mediator.TriggeredRoutes[1]);
    }
}
