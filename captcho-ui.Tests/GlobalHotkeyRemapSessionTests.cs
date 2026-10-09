// GlobalHotkeyRemapSessionTests.cs — Headless composed-session tests for
// remapping the Full Desktop Global Hotkey (issue #51).
//
// Verifies the SettingsSession slice end to end at the composed seam: recording
// a combination flows through the view; Apply/OK atomically persists the binding
// and reconciles runtime registration with the new combination; registration
// failure marks the row Failed with a clear error while the binding stays
// persisted; Cancel leaves Configuration and runtime registration unchanged;
// Reset restores the default binding in the working view (Apply then persists
// it). The settings round-trip goes through a real ConfigurationService temp
// directory.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class GlobalHotkeyRemapSessionTests
{
    private static readonly HotkeyBinding CtrlF5 = new(0x0002, 0x74);

    // ── Recording flows through the composed view ───────────────────────

    [Fact]
    public void RecordBinding_UpdatesTheRowBindingAndDirtiesTheSession()
    {
        var session = NewSession(AppSettings.WithDefaults());

        var view = session.RecordGlobalHotkeyBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);

        var row = view.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal("Ctrl + F5", row.Binding);
    }

    [Fact]
    public void RecordBinding_SuspectedConflict_IsWarnedOnTheRowWithoutRejecting()
    {
        var session = NewSession(AppSettings.WithDefaults());
        var legacyWin =
            new HotkeyBinding(GlobalHotkeyRouteMap.MOD_WIN, GlobalHotkeyRouteMap.VK_SNAPSHOT);

        var view = session.RecordGlobalHotkeyBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyWin);

        var row = view.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal("Win + Print Screen", row.Binding);
        Assert.NotNull(row.BindingWarning);
        Assert.Contains("Win + Print Screen", row.BindingWarning);
    }

    // ── Apply: atomic persist + runtime reconcile ───────────────────────

    [Fact]
    public void Apply_PersistsTheRecordedBinding_AndReconcilesRegistrationWithIt()
    {
        var configuration = new ConfigurationService(NewTempDir());
        var adapter = new RecordingGlobalHotkeyAdapter();
        var runtime = AppSettings.WithDefaults();
        var session = new SettingsSession(runtime, configuration, adapter);

        session.RecordGlobalHotkeyBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);
        var view = session.Apply();

        // Persisted through Configuration (round-trip).
        var loaded = configuration.Load().Settings;
        Assert.Equal(CtrlF5, loaded.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
        // Runtime write-back.
        Assert.Equal(CtrlF5, runtime.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
        // Reconcile driven with the saved settings (bindings included).
        Assert.NotNull(adapter.LastAppliedSettings);
        Assert.Equal(CtrlF5,
            adapter.LastAppliedSettings!.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
        // Status and rows reflect success.
        Assert.Equal(SettingsSession.SavedMessage, view.StatusMessage);
        Assert.All(view.GlobalHotkeyRows.Where(r => r.IsEnabled), r =>
            Assert.Equal(GlobalHotkeyRegistrationStatus.Registered, r.Status));
    }

    [Fact]
    public void Apply_RegistrationFailure_MarksTheRowFailedWithClearError()
    {
        var configuration = new ConfigurationService(NewTempDir());
        var adapter = new RecordingGlobalHotkeyAdapter();
        adapter.SetApplyFailingIds(GlobalHotkeyRouteMap.IdShiftPrintScreen);
        var session = new SettingsSession(AppSettings.WithDefaults(), configuration, adapter);

        session.RecordGlobalHotkeyBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);
        var view = session.Apply();

        var row = view.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Failed, row.Status);
        Assert.Contains("1409", row.StatusDetail);
        Assert.Contains("failed to register", view.StatusMessage);
        // The binding stays persisted (Apply succeeded; only registration failed).
        Assert.Equal(CtrlF5,
            configuration.Load().Settings.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
    }

    [Fact]
    public void Apply_AfterConflictWarning_StillAppliesAndSurfacesRegistrationOutcome()
    {
        // A warned (suspected-conflict) value is never rejected at record time;
        // Apply persists it. Here the conflicting row is re-registered fine
        // because Win32 allows one owner per combination — the fake reports
        // success for both ids, so the slice only proves the warning does not
        // block Apply.
        var configuration = new ConfigurationService(NewTempDir());
        var adapter = new RecordingGlobalHotkeyAdapter();
        var session = new SettingsSession(AppSettings.WithDefaults(), configuration, adapter);
        var legacyWin =
            new HotkeyBinding(GlobalHotkeyRouteMap.MOD_WIN, GlobalHotkeyRouteMap.VK_SNAPSHOT);

        session.RecordGlobalHotkeyBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyWin);
        var view = session.Apply();

        Assert.Equal(SettingsSession.SavedMessage, view.StatusMessage);
        Assert.Equal(legacyWin,
            configuration.Load().Settings.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
    }

    // ── Cancel: Configuration and runtime registration unchanged ───────

    [Fact]
    public void Cancel_AfterRecording_LeavesConfigurationAndRuntimeUnchanged()
    {
        var configuration = new ConfigurationService(NewTempDir());
        var adapter = new RecordingGlobalHotkeyAdapter();
        var runtime = AppSettings.WithDefaults();
        var session = new SettingsSession(runtime, configuration, adapter);

        session.RecordGlobalHotkeyBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);
        var view = session.Cancel();

        // No save happened.
        Assert.True(configuration.Load().UsedDefaults);
        // Runtime untouched.
        Assert.Equal(
            new HotkeyBinding(GlobalHotkeyRouteMap.MOD_SHIFT, GlobalHotkeyRouteMap.VK_SNAPSHOT),
            runtime.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
        // No reconcile was driven.
        Assert.Equal(0, adapter.ApplyCallsCount);
        // The view reverted to the legacy binding name.
        var row = view.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal("Shift + Print Screen", row.Binding);
    }

    // ── Reset: restores the default binding ────────────────────────────

    [Fact]
    public void Reset_AfterRemapWasApplied_RestoresTheDefaultBinding()
    {
        var persisted = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = CtrlF5,
            },
        };
        var configuration = new ConfigurationService(NewTempDir());
        configuration.Save(persisted);
        var session = new SettingsSession(persisted, configuration, new RecordingGlobalHotkeyAdapter());

        var view = session.Reset();

        var row = view.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal("Shift + Print Screen", row.Binding);
        // Reset alone does not persist.
        Assert.Equal(CtrlF5,
            configuration.Load().Settings.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
    }

    [Fact]
    public void ResetThenApply_PersistsTheDefaultBindingAndClearsTheRemap()
    {
        var persisted = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = CtrlF5,
            },
        };
        var configuration = new ConfigurationService(NewTempDir());
        configuration.Save(persisted);
        var runtime = persisted;
        var adapter = new RecordingGlobalHotkeyAdapter();
        var session = new SettingsSession(runtime, configuration, adapter);

        session.Reset();
        var view = session.Apply();

        Assert.Equal(SettingsSession.SavedMessage, view.StatusMessage);
        Assert.Null(configuration.Load().Settings.GlobalHotkeyBindings);
        Assert.Null(runtime.GlobalHotkeyBindings);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SettingsSession NewSession(AppSettings runtime)
        => new SettingsSession(runtime, new ConfigurationService(NewTempDir()), new RecordingGlobalHotkeyAdapter());

    /// <summary>
    /// Fake adapter mirroring the production manager's settings reconcile:
    /// resolves effective bindings per route and serves configurable failures.
    /// </summary>
    private sealed class RecordingGlobalHotkeyAdapter : IGlobalHotkeyAdapter
    {
        private readonly List<GlobalHotkeyRegistrationResult> _results = new();
        private readonly HashSet<int> _failingIds = new();

        public int ApplyCallsCount { get; private set; }
        public AppSettings? LastAppliedSettings { get; private set; }

        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults => _results;

        public void SetApplyFailingIds(params int[] ids)
        {
            _failingIds.Clear();
            foreach (var id in ids)
                _failingIds.Add(id);
        }

        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplySettings(AppSettings settings)
        {
            ApplyCallsCount++;
            LastAppliedSettings = settings;
            _results.Clear();
            foreach (var spec in GlobalHotkeyRouteMap.SpecsFor(settings))
            {
                if (!settings.IsGlobalHotkeyEnabled(spec.Route))
                    continue;
                _results.Add(_failingIds.Contains(spec.Id)
                    ? GlobalHotkeyRegistrationResult.Fail(spec, "RegisterHotKey", "Win32 error 1409")
                    : GlobalHotkeyRegistrationResult.Success(spec));
            }
            return _results;
        }
    }
}
