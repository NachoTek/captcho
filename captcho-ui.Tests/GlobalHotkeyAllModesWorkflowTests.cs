// GlobalHotkeyAllModesWorkflowTests.cs — End-to-end Trigger-routing tests for
// every Capture Mode's remapped Global Hotkey (issue #52).
//
// For each interactive or immediate route: a remapped combination is persisted
// through the composed Settings session, reconciles registration through a
// GlobalHotkeyManager (fake registrar) with the new combination, the WM_HOTKEY
// id resolves to the right capture route, and that route dispatches through
// the production CaptureWorkflowSession — interactive routes drive their
// Target Selection overlay and Capture, immediate routes capture outright —
// keeping the existing Trigger -> Capture Mode routing intact.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class GlobalHotkeyAllModesWorkflowTests
{
    private static readonly HotkeyBinding CtrlF6 = new(0x0002, 0x75);

    // ── Selected Window: remap, register, route through the picker ──────

    [Fact]
    public async Task RemappedSelectedWindowBinding_RoutesThroughTheWindowPickerWorkflow()
    {
        var (workflow, capture) = NewWorkflow(
            windowPicker: ConfirmWindow(new IntPtr(0x10)),
            windowResult: OkFrame());

        var result = await RecordApplyAndDispatchAsync(
            GlobalHotkeyRouteMap.IdAltPrintScreen,
            GlobalHotkeyRoute.SelectedWindow,
            workflow);

        Assert.True(result.Status is WorkflowStatus.Succeeded);
        Assert.Equal(1, capture.WindowCallCount);
        Assert.Equal(new IntPtr(0x10), capture.LastWindowTarget!.Handle);
        Assert.NotNull(workflow.LastFrame);
    }

    // ── Selected Monitor: remap, register, route through the picker ─────

    [Fact]
    public async Task RemappedSelectedMonitorBinding_RoutesThroughTheMonitorPickerWorkflow()
    {
        var (workflow, capture) = NewWorkflow(
            monitorPicker: ConfirmMonitor(),
            monitorResult: OkFrame());

        var result = await RecordApplyAndDispatchAsync(
            GlobalHotkeyRouteMap.IdCtrlPrintScreen,
            GlobalHotkeyRoute.SelectedMonitor,
            workflow);

        Assert.True(result.Status is WorkflowStatus.Succeeded);
        Assert.Equal(1, capture.MonitorCallCount);
        Assert.NotNull(workflow.LastFrame);
    }

    // ── Selection: remap, register, route through the Selection overlay ─

    [Fact]
    public async Task RemappedSelectionBinding_RoutesThroughTheSelectionWorkflow()
    {
        var (workflow, capture) = NewWorkflow(
            selectionGeometry: new SelectionGeometry { X = 5, Y = 6, Width = 30, Height = 20 },
            selectionResult: OkFrame());

        var result = await RecordApplyAndDispatchAsync(
            GlobalHotkeyRouteMap.IdWinShiftPrintScreen,
            GlobalHotkeyRoute.RectangularRegion,
            workflow);

        Assert.True(result.Status is WorkflowStatus.Succeeded);
        Assert.Equal(1, capture.SelectionCallCount);
        Assert.Equal(5, capture.LastSelectionGeometry!.X);
        Assert.NotNull(workflow.LastFrame);
    }

    // ── Active Window: remap, register, route immediately ───────────────

    [Fact]
    public async Task RemappedActiveWindowBinding_RoutesThroughTheWorkflow()
    {
        var (workflow, capture) = NewWorkflow(activeWindowResult: OkFrame());

        var result = await RecordApplyAndDispatchAsync(
            GlobalHotkeyRouteMap.IdWinPrintScreen,
            GlobalHotkeyRoute.ActiveWindow,
            workflow);

        Assert.True(result.Status is WorkflowStatus.Succeeded);
        Assert.Equal(1, capture.ActiveWindowCallCount);
        Assert.NotNull(workflow.LastFrame);
    }

    // ── Full Desktop control: the #51 contract still holds ──────────────

    [Fact]
    public async Task RemappedFullDesktopBinding_StillRoutesThroughTheWorkflow()
    {
        var (workflow, capture) = NewWorkflow(fullDesktopResult: OkFrame());

        var result = await RecordApplyAndDispatchAsync(
            GlobalHotkeyRouteMap.IdShiftPrintScreen,
            GlobalHotkeyRoute.FullDesktop,
            workflow);

        Assert.True(result.Status is WorkflowStatus.Succeeded);
        Assert.Equal(1, capture.CallCount);
        Assert.NotNull(workflow.LastFrame);
    }

    // ── Cancellation of an interactive route never captures ─────────────

    [Fact]
    public async Task InteractiveTrigger_CancelledOverlay_ProducesNoFrame()
    {
        // Selected Monitor with a cancelling picker: the remapped Trigger
        // routes through the production workflow, the overlay cancels, and
        // the workflow ends Cancelled with no Capture and no Frame.
        var (workflow, capture) = NewWorkflow(monitorResult: OkFrame());

        var result = await RecordApplyAndDispatchAsync(
            GlobalHotkeyRouteMap.IdCtrlPrintScreen,
            GlobalHotkeyRoute.SelectedMonitor,
            workflow);

        Assert.True(result.Status is WorkflowStatus.Cancelled);
        Assert.Equal(0, capture.MonitorCallCount);
        Assert.Null(workflow.LastFrame);
    }

    // ── Shared end-to-end scaffold ──────────────────────────────────────

    /// <summary>
    /// Records Ctrl+F6 for the given row through the composed Settings
    /// session, Applies (persisting through a real ConfigurationService and
    /// reconciling registration through a real GlobalHotkeyManager with a
    /// fake registrar), then resolves the row's WM_HOTKEY id to its route and
    /// dispatches it through the production workflow — mirroring
    /// MainWindow's dispatch path.
    /// </summary>
    private static async Task<WorkflowResult<object>> RecordApplyAndDispatchAsync(
        int globalHotkeyId,
        GlobalHotkeyRoute expectedRoute,
        CaptureWorkflowSession<object> workflow)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configuration = new ConfigurationService(tempDir);
            var registrar = new FakeGlobalHotkeyRegistrar();
            var manager = new GlobalHotkeyManager(registrar);
            var adapter = new ManagerBackedAdapter(manager);
            var runtime = AppSettings.WithDefaults();

            // Startup: legacy registration with the default combinations.
            manager.Reconcile(IntPtr.Zero, runtime);
            int legacyRegisters = registrar.RegisterCalls.Count;

            var session = new SettingsSession(runtime, configuration, adapter);
            var applied = session.RecordGlobalHotkeyBinding(globalHotkeyId, CtrlF6);
            applied = session.Apply();
            Assert.Equal(SettingsSession.SavedMessage, applied.StatusMessage);

            // Persisted through Configuration.
            var loaded = configuration.Load().Settings;
            Assert.Equal(CtrlF6, loaded.EffectiveGlobalHotkeyBinding(expectedRoute));

            // Runtime registration moved to the new combination.
            var newCall = registrar.RegisterCalls.Skip(legacyRegisters)
                .Single(c => c.id == globalHotkeyId);
            Assert.Equal(CtrlF6.Modifiers, newCall.modifiers);
            Assert.Equal(CtrlF6.VirtualKey, newCall.vk);
            Assert.Contains(globalHotkeyId, registrar.UnregisterCalls);

            // WM_HOTKEY for the remapped Global Hotkey resolves to its route…
            Assert.True(manager.TryResolveRoute(globalHotkeyId, out var route));
            Assert.Equal(expectedRoute, route);

            // …and that route dispatches through the production workflow.
            return await DispatchRouteAsync(workflow, route);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Mirrors MainWindow.DispatchGlobalHotkeyRoute's switch. Current Monitor
    /// is the one route production still dispatches through the legacy capture
    /// service (not the workflow session), so it has no workflow mirror here;
    /// the workflow-routed modes map one-to-one.
    /// </summary>
    private static Task<WorkflowResult<object>> DispatchRouteAsync(
        CaptureWorkflowSession<object> workflow,
        GlobalHotkeyRoute route) => route switch
    {
        GlobalHotkeyRoute.ActiveWindow => workflow.CaptureActiveWindowAsync(),
        GlobalHotkeyRoute.FullDesktop => workflow.CaptureFullDesktopAsync(),
        GlobalHotkeyRoute.RectangularRegion => workflow.CaptureSelectionAsync(),
        GlobalHotkeyRoute.SelectedWindow => workflow.CaptureSelectedWindowAsync(),
        GlobalHotkeyRoute.SelectedMonitor => workflow.CaptureSelectedMonitorAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    private static CaptureFrameResult OkFrame() =>
        CaptureFrameResult.Ok(
            new ContiguousBitmap(4, 4, 16, new byte[64]), "4×4", 1);

    private static (CaptureWorkflowSession<object> Workflow, FakeCaptureAdapter Capture) NewWorkflow(
        SelectionGeometry? selectionGeometry = null,
        CaptureFrameResult? fullDesktopResult = null,
        CaptureFrameResult? activeWindowResult = null,
        CaptureFrameResult? selectionResult = null,
        FakeMonitorPickerOverlayAdapter? monitorPicker = null,
        CaptureFrameResult? monitorResult = null,
        FakeWindowPickerOverlayAdapter? windowPicker = null,
        CaptureFrameResult? windowResult = null)
    {
        var capture = new FakeCaptureAdapter
        {
            NextResult = fullDesktopResult ?? CaptureFrameResult.Fail("not configured"),
            NextActiveWindowResult = activeWindowResult ?? CaptureFrameResult.Fail("not configured"),
            NextSelectionResult = selectionResult ?? CaptureFrameResult.Fail("not configured"),
            NextMonitorResult = monitorResult ?? CaptureFrameResult.Fail("not configured"),
            NextWindowResult = windowResult ?? CaptureFrameResult.Fail("not configured"),
        };
        var selection = new FakeSelectionOverlayAdapter { NextGeometry = selectionGeometry };
        var workflow = new CaptureWorkflowSession<object>(
            capture,
            new FakePreviewAdapter(),
            selection,
            monitorPicker ?? new FakeMonitorPickerOverlayAdapter(),
            windowPicker ?? new FakeWindowPickerOverlayAdapter());
        return (workflow, capture);
    }

    private static FakeMonitorPickerOverlayAdapter ConfirmMonitor() => new()
    {
        NextTarget = new MonitorTarget { X = 0, Y = 0, Width = 10, Height = 10 },
    };

    private static FakeWindowPickerOverlayAdapter ConfirmWindow(IntPtr handle) => new()
    {
        NextResult = new WindowPickerResult
        {
            Outcome = WindowPickerOutcome.WindowConfirmed,
            Target = new WindowTarget { Handle = handle, Width = 10, Height = 10 },
        },
    };

    /// <summary>Production adapter over the manager, as MainWindow wires it.</summary>
    private sealed class ManagerBackedAdapter : IGlobalHotkeyAdapter
    {
        private readonly GlobalHotkeyManager _manager;
        public ManagerBackedAdapter(GlobalHotkeyManager manager) => _manager = manager;
        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults =>
            _manager.RegistrationResults;
        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplySettings(AppSettings settings) =>
            _manager.Reconcile(IntPtr.Zero, settings);
    }
}
