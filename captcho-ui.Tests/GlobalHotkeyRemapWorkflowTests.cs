// GlobalHotkeyRemapWorkflowTests.cs — End-to-end trigger-routing tests for the
// remapped Full Desktop Global Hotkey (issue #51).
//
// The tracer bullet through every layer, headless: a recorded combination is
// persisted through the composed Settings session, reconciles registration
// through a GlobalHotkeyManager (fake registrar) with the new combination, the
// WM_HOTKEY id for the remapped Global Hotkey resolves to the Full Desktop
// capture route, and that route dispatches Full Desktop through the production
// CaptureWorkflowSession with a real Frame captured — keeping the existing
// Trigger -> Capture Mode routing intact.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class GlobalHotkeyRemapWorkflowTests
{
    private static readonly HotkeyBinding CtrlF5 = new(0x0002, 0x74);

    [Fact]
    public async Task RemappedBinding_Persists_ReRegisters_AndRoutesFullDesktopThroughTheWorkflow()
    {
        // ── Arrange: production collaborators, headless fakes at the edges ──
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configuration = new ConfigurationService(tempDir);
            var registrar = new FakeGlobalHotkeyRegistrar();
            var manager = new GlobalHotkeyManager(registrar);
            var adapter = new ManagerBackedAdapter(manager);
            var runtime = AppSettings.WithDefaults();

            // Startup: legacy registration.
            manager.Reconcile(IntPtr.Zero, runtime);
            int legacyRegisters = registrar.RegisterCalls.Count;
            var legacyCall = registrar.RegisterCalls.Single(c =>
                c.id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
            Assert.Equal(GlobalHotkeyRouteMap.MOD_SHIFT, legacyCall.modifiers);

            // ── Act: record Ctrl+F5 through the composed Settings session and Apply ──
            var session = new SettingsSession(runtime, configuration, adapter);
            var recorded = session.RecordGlobalHotkeyBinding(
                GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);
            Assert.Equal("Ctrl + F5",
                recorded.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen).Binding);

            var applied = session.Apply();
            Assert.Equal(SettingsSession.SavedMessage, applied.StatusMessage);

            // ── Persisted through Configuration ──
            var loaded = configuration.Load().Settings;
            Assert.Equal(CtrlF5, loaded.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));

            // ── Runtime registration moved to the new combination ──
            var newCall = registrar.RegisterCalls.Skip(legacyRegisters)
                .Single(c => c.id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
            Assert.Equal(CtrlF5.Modifiers, newCall.modifiers);
            Assert.Equal(CtrlF5.VirtualKey, newCall.vk);
            Assert.Contains(GlobalHotkeyRouteMap.IdShiftPrintScreen, registrar.UnregisterCalls);

            // ── WM_HOTKEY for the remapped Global Hotkey resolves to Full Desktop ──
            Assert.True(manager.TryResolveRoute(GlobalHotkeyRouteMap.IdShiftPrintScreen, out var route));
            Assert.Equal(GlobalHotkeyRoute.FullDesktop, route);

            // ── And that route dispatches Full Desktop through the production workflow ──
            var capture = new FakeCaptureAdapter
            {
                NextResult = CaptureFrameResult.Ok(
                    new ContiguousBitmap(4, 4, 16, new byte[64]), "4×4", 1),
            };
            var preview = new FakePreviewAdapter();
            var workflow = new CaptureWorkflowSession<object>(
                capture,
                preview,
                new FakeSelectionOverlayAdapter(),
                new FakeMonitorPickerOverlayAdapter(),
                new FakeWindowPickerOverlayAdapter());

            var result = await DispatchRouteAsync(workflow, route);

            Assert.True(result.Status is WorkflowStatus.Succeeded);
            Assert.Equal(1, capture.CallCount);
            Assert.NotNull(workflow.LastFrame);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task RemappedBinding_FailedRegistration_MarksRowFailedAndDoesNotRoute()
    {
        var registrar = new FakeGlobalHotkeyRegistrar();
        registrar.SetFailingIds(GlobalHotkeyRouteMap.IdShiftPrintScreen);
        var manager = new GlobalHotkeyManager(registrar);
        var adapter = new ManagerBackedAdapter(manager);
        var tempDir = Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configuration = new ConfigurationService(tempDir);
            var runtime = AppSettings.WithDefaults();
            var session = new SettingsSession(runtime, configuration, adapter);

            var applied = session.RecordGlobalHotkeyBinding(
                GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);
            applied = session.Apply();

            var row = applied.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
            Assert.Equal(GlobalHotkeyRegistrationStatus.Failed, row.Status);
            Assert.Contains("failed to register", applied.StatusMessage);

            // The failed Global Hotkey never registered, so no registration result
            // reports success for it and the runtime holds no active combination.
            Assert.DoesNotContain(manager.RegistrationResults,
                r => r.Spec.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen && r.Succeeded);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Mirrors MainWindow's dispatch: WM_HOTKEY id -> route -> workflow Full
    /// Desktop call, via the manager's resolution (the production path).
    /// </summary>
    private static async Task<WorkflowResult<object>> DispatchRouteAsync(
        CaptureWorkflowSession<object> workflow,
        GlobalHotkeyRoute route)
    {
        Assert.Equal(GlobalHotkeyRoute.FullDesktop, route);
        return await workflow.CaptureFullDesktopAsync();
    }

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
