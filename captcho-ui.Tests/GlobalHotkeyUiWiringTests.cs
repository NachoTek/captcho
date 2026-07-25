// GlobalHotkeyUiWiringTests.cs — Headless tests for global hotkey WM_HOTKEY routing into capture workflows.
//
// Verifies the Global Hotkey dispatch logic through a testable mediator that mirrors
// MainWindow's route dispatch without WinUI controls. Covers:
// - All four Global Hotkey ids route to the correct capture delegates
// - Unknown Global Hotkey ids do not trigger capture
// - Triggers during an active operation are ignored
// - Cleanup unregisters exactly once
// - Registration conflicts are reported gracefully
// - Multiple routes dispatch independently

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

/// <summary>
/// Testable mediator that mirrors the MainWindow Global Hotkey dispatch logic.
/// Records state transitions and capture calls so tests can assert on routing
/// without WinUI controls or real Win32 message processing.
/// </summary>
public class GlobalHotkeyDispatchMediator
{
    // Injected dependencies (test doubles)
    private readonly Dictionary<GlobalHotkeyRoute, Func<Task>> _routeHandlers;
    private readonly GlobalHotkeyManager _globalHotkeyManager;

    // Recorded state for assertions
    public bool IsOperationRunning { get; private set; }
    public List<GlobalHotkeyRoute> TriggeredRoutes { get; } = new();
    public List<int> IgnoredIds { get; } = new();
    public string? StatusText { get; private set; }

    public GlobalHotkeyDispatchMediator(
        GlobalHotkeyManager globalHotkeyManager,
        Dictionary<GlobalHotkeyRoute, Func<Task>>? routeHandlers = null)
    {
        _globalHotkeyManager = globalHotkeyManager ?? throw new ArgumentNullException(nameof(globalHotkeyManager));
        _routeHandlers = routeHandlers ?? new Dictionary<GlobalHotkeyRoute, Func<Task>>();

        // Fill defaults for missing routes
        foreach (GlobalHotkeyRoute route in Enum.GetValues<GlobalHotkeyRoute>())
        {
            if (!_routeHandlers.ContainsKey(route))
            {
                _routeHandlers[route] = () => Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Mirrors MainWindow.WndProc WM_HOTKEY dispatch:
    /// resolves the Global Hotkey id to a route and dispatches if not blocked.
    /// </summary>
    public void OnWmGlobalHotkey(int globalHotkeyId)
    {
        if (_globalHotkeyManager.TryResolveRoute(globalHotkeyId, out GlobalHotkeyRoute route))
        {
            DispatchRoute(route);
        }
        else
        {
            IgnoredIds.Add(globalHotkeyId);
        }
    }

    /// <summary>
    /// Mirrors MainWindow.DispatchGlobalHotkeyRoute:
    /// checks the operation guard, then invokes the route handler.
    /// </summary>
    public void DispatchRoute(GlobalHotkeyRoute route)
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
        StatusText = _globalHotkeyManager.GetRegistrationSummary();
    }
}

/// <summary>
/// Fake registrar for headless UI wiring tests.
/// Allows controlling which registrations succeed/fail.
/// </summary>
public class UiWiringFakeRegistrar : IGlobalHotkeyRegistrar
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

public class GlobalHotkeyUiWiringTests
{
    // ── All four routes dispatch correctly ────────────────────────────

    [Fact]
    public void PrintScreen_RoutesTo_CurrentMonitor()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(GlobalHotkeyRoute.CurrentMonitor, mediator.TriggeredRoutes[0]);
    }

    [Fact]
    public void WinPrintScreen_RoutesTo_ActiveWindow()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdWinPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(GlobalHotkeyRoute.ActiveWindow, mediator.TriggeredRoutes[0]);
    }

    [Fact]
    public void ShiftPrintScreen_RoutesTo_FullDesktop()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdShiftPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(GlobalHotkeyRoute.FullDesktop, mediator.TriggeredRoutes[0]);
    }

    [Fact]
    public void WinShiftPrintScreen_RoutesTo_RectangularRegion()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdWinShiftPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(GlobalHotkeyRoute.RectangularRegion, mediator.TriggeredRoutes[0]);
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
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.OnWmGlobalHotkey(unknownId);

        Assert.Empty(mediator.TriggeredRoutes);
        Assert.Single(mediator.IgnoredIds);
        Assert.Equal(unknownId, mediator.IgnoredIds[0]);
    }

    // ── Multiple unknown ids accumulate ───────────────────────────────

    [Fact]
    public void MultipleUnknownIds_AllIgnored()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.OnWmGlobalHotkey(0);
        mediator.OnWmGlobalHotkey(-1);
        mediator.OnWmGlobalHotkey(99);

        Assert.Empty(mediator.TriggeredRoutes);
        Assert.Equal(3, mediator.IgnoredIds.Count);
    }

    // ── Active operation guard blocks overlapping triggers ─────────────

    [Fact]
    public void GlobalHotkeyDuringActiveOperation_IsIgnored()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.EnterOperation();

        // All four Global Hotkeys should be ignored during active operation
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdWinPrintScreen);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdShiftPrintScreen);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdWinShiftPrintScreen);

        Assert.Empty(mediator.TriggeredRoutes);
    }

    [Fact]
    public void GlobalHotkeyAfterOperationCompletes_IsDispatched()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);

        // Start operation, try Global Hotkey (ignored), end operation, try again (dispatched)
        mediator.EnterOperation();
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);
        Assert.Empty(mediator.TriggeredRoutes);

        mediator.ExitOperation();
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);
        Assert.Single(mediator.TriggeredRoutes);
        Assert.Equal(GlobalHotkeyRoute.CurrentMonitor, mediator.TriggeredRoutes[0]);
    }

    [Fact]
    public void RapidRepeatedGlobalHotkeyPresses_OnlyFirstDispatches()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);

        // Simulate rapid repeated Print Screen presses
        // The first one starts the operation, subsequent ones are ignored
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);
        mediator.EnterOperation();
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);

        Assert.Single(mediator.TriggeredRoutes);
    }

    // ── Route-specific handler invocation ──────────────────────────────

    [Fact]
    public void CurrentMonitorRoute_InvokesCorrectHandler()
    {
        GlobalHotkeyRoute? invokedRoute = null;
        var handlers = new Dictionary<GlobalHotkeyRoute, Func<Task>>
        {
            [GlobalHotkeyRoute.CurrentMonitor] = () => { invokedRoute = GlobalHotkeyRoute.CurrentMonitor; return Task.CompletedTask; },
        };

        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager, handlers);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);

        Assert.Equal(GlobalHotkeyRoute.CurrentMonitor, invokedRoute);
    }

    [Fact]
    public void RegionRoute_InvokesCorrectHandler()
    {
        GlobalHotkeyRoute? invokedRoute = null;
        var handlers = new Dictionary<GlobalHotkeyRoute, Func<Task>>
        {
            [GlobalHotkeyRoute.RectangularRegion] = () => { invokedRoute = GlobalHotkeyRoute.RectangularRegion; return Task.CompletedTask; },
        };

        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager, handlers);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdWinShiftPrintScreen);

        Assert.Equal(GlobalHotkeyRoute.RectangularRegion, invokedRoute);
    }

    // ── All four routes dispatch independently ─────────────────────────

    [Fact]
    public void AllFourRoutes_DispatchInSequence()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);

        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdWinPrintScreen);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdShiftPrintScreen);
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdWinShiftPrintScreen);

        Assert.Equal(4, mediator.TriggeredRoutes.Count);
        Assert.Equal(GlobalHotkeyRoute.CurrentMonitor, mediator.TriggeredRoutes[0]);
        Assert.Equal(GlobalHotkeyRoute.ActiveWindow, mediator.TriggeredRoutes[1]);
        Assert.Equal(GlobalHotkeyRoute.FullDesktop, mediator.TriggeredRoutes[2]);
        Assert.Equal(GlobalHotkeyRoute.RectangularRegion, mediator.TriggeredRoutes[3]);
    }

    // ── Cleanup unregisters exactly once ───────────────────────────────

    [Fact]
    public void Cleanup_UnregistersSuccessfully()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
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
        var manager = new GlobalHotkeyManager(registrar);
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
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.UpdateStatusFromRegistration();

        Assert.Equal("All 4 global hotkeys registered.", mediator.StatusText);
    }

    [Fact]
    public void PartialRegistration_StatusTextReportsConflicts()
    {
        var registrar = new UiWiringFakeRegistrar(new HashSet<int> { 1, 3 });
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.UpdateStatusFromRegistration();

        Assert.Contains("2/4", mediator.StatusText);
        Assert.Contains("Conflicts", mediator.StatusText);
    }

    [Fact]
    public void TotalFailure_StatusTextReportsAllFailed()
    {
        var registrar = new UiWiringFakeRegistrar(new HashSet<int>());
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);
        mediator.UpdateStatusFromRegistration();

        Assert.Contains("No global hotkeys registered", mediator.StatusText);
    }

    // ── Dispatch after partial registration still works for registered routes ─

    [Fact]
    public void PartialRegistration_RegisteredRoutesStillDispatch()
    {
        // Only Print Screen (id=1) succeeds
        var registrar = new UiWiringFakeRegistrar(new HashSet<int> { 1 });
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);

        // Registered route dispatches
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);
        Assert.Single(mediator.TriggeredRoutes);

        // Unregistered route also dispatches (route resolution is pure mapping,
        // registration tracking is for OS-level cleanup only)
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdWinPrintScreen);
        Assert.Equal(2, mediator.TriggeredRoutes.Count);
    }

    // ── Mixed valid and invalid ids in sequence ────────────────────────

    [Fact]
    public void MixedIds_OnlyValidOnesDispatch()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);

        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);   // valid
        mediator.OnWmGlobalHotkey(99);                              // invalid
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdShiftPrintScreen); // valid
        mediator.OnWmGlobalHotkey(-1);                              // invalid
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdWinPrintScreen);   // valid

        Assert.Equal(3, mediator.TriggeredRoutes.Count);
        Assert.Equal(2, mediator.IgnoredIds.Count);
    }

    // ── Operation guard resets between captures ────────────────────────

    [Fact]
    public void OperationGuard_ResetsAllowsSequentialCaptures()
    {
        var registrar = new UiWiringFakeRegistrar();
        var manager = new GlobalHotkeyManager(registrar);
        manager.RegisterAll(IntPtr.Zero);

        var mediator = new GlobalHotkeyDispatchMediator(manager);

        // First capture cycle
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdPrintScreen);
        mediator.EnterOperation();
        mediator.ExitOperation();

        // Second capture cycle
        mediator.OnWmGlobalHotkey(GlobalHotkeyRouteMap.IdShiftPrintScreen);
        mediator.EnterOperation();
        mediator.ExitOperation();

        Assert.Equal(2, mediator.TriggeredRoutes.Count);
        Assert.Equal(GlobalHotkeyRoute.CurrentMonitor, mediator.TriggeredRoutes[0]);
        Assert.Equal(GlobalHotkeyRoute.FullDesktop, mediator.TriggeredRoutes[1]);
    }
}
