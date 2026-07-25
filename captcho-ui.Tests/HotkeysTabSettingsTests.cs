// HotkeysTabSettingsTests.cs — Headless tests for the Hotkeys tab editing seam.
//
// Verifies the pure C# HotkeysTabSettings coordinator: it loads the persisted
// per-hotkey enabled states into a working snapshot (never mutating the source),
// lists all four Global Hotkeys with their bindings, behavior descriptions, and
// current registration status, lets the user enable/disable each hotkey
// independently, and drives the Apply/OK/Cancel session — persisting enabled
// states and reconciling runtime registration through injected delegates and an
// injected Global Hotkey adapter so no Win32 or WinUI dependency is required.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class HotkeysTabSettingsTests
{
    // ── Constructor guards ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HotkeysTabSettings(null!, _ => SuccessfulSave(), new RecordingHotkeyAdapter()));
    }

    [Fact]
    public void Constructor_NullSave_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HotkeysTabSettings(new AppSettings(), null!, new RecordingHotkeyAdapter()));
    }

    [Fact]
    public void Constructor_NullAdapter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HotkeysTabSettings(new AppSettings(), _ => SuccessfulSave(), null!));
    }

    // ── Display: all four hotkeys with bindings and behavior ────────────

    [Fact]
    public void GetRows_ListsAllFourHotkeysInStableIdOrder()
    {
        var tab = NewTab(new AppSettings());

        var rows = tab.GetRows();

        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void GetRows_BindingMatchesTheHotkeyShortcutName()
    {
        var tab = NewTab(new AppSettings());

        var rows = tab.GetRows();

        foreach (var spec in HotkeyRouteMap.AllSpecs)
            Assert.Equal(spec.Name, rows.Single(r => r.Id == spec.Id).Binding);
    }

    [Fact]
    public void GetRows_EachRowHasAnAccurateNonEmptyBehaviorDescription()
    {
        var tab = NewTab(new AppSettings());

        var rows = tab.GetRows();

        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Behavior)));
        // Behaviors are distinct per route.
        Assert.Equal(rows.Count, rows.Select(r => r.Behavior).Distinct().Count());
    }

    [Theory]
    [InlineData(HotkeyRoute.CurrentMonitor, "monitor the cursor is on")]
    [InlineData(HotkeyRoute.ActiveWindow, "active")]
    [InlineData(HotkeyRoute.FullDesktop, "full virtual desktop")]
    [InlineData(HotkeyRoute.RectangularRegion, "Selection")]
    public void GetRows_BehaviorDescribesTheCaptureMode(HotkeyRoute route, string expectedFragment)
    {
        var tab = NewTab(new AppSettings());
        var spec = HotkeyRouteMap.AllSpecs.Single(s => s.Route == route);

        var row = tab.GetRows().Single(r => r.Id == spec.Id);

        Assert.Contains(expectedFragment, row.Behavior, StringComparison.OrdinalIgnoreCase);
    }

    // ── Default enabled state (settings predating per-hotkey toggles) ───

    [Fact]
    public void Constructor_NullHotkeyStates_AllHotkeysEnabledByDefault()
    {
        var tab = NewTab(new AppSettings());

        foreach (var spec in HotkeyRouteMap.AllSpecs)
            Assert.True(tab.IsEnabled(spec.Id));
    }

    [Fact]
    public void Constructor_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings(); // HotkeyEnabledStates stays null

        var tab = NewTab(source);

        Assert.Null(source.HotkeyEnabledStates);
        Assert.True(tab.IsEnabled(1));
    }

    [Fact]
    public void Constructor_PersistedDisabledHotkey_IsDisabledInWorkingState()
    {
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool> { [HotkeyRouteMap.IdPrintScreen] = false },
        };

        var tab = NewTab(source);

        Assert.False(tab.IsEnabled(HotkeyRouteMap.IdPrintScreen));
        Assert.True(tab.IsEnabled(HotkeyRouteMap.IdWinPrintScreen));
    }

    // ── Registration status display ─────────────────────────────────────

    [Fact]
    public void GetRows_RegisteredHotkey_ShowsRegisteredStatus()
    {
        var adapter = new RecordingHotkeyAdapter();
        adapter.SetResults(HotkeyRegistrationResult.Success(SpecForId(HotkeyRouteMap.IdPrintScreen)));

        var tab = new HotkeysTabSettings(new AppSettings(), _ => SuccessfulSave(), adapter);

        var row = tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdPrintScreen);
        Assert.Equal(HotkeyRegistrationStatus.Registered, row.Status);
    }

    [Fact]
    public void GetRows_FailedRegistration_ShowsFailedStatusWithSanitizedError()
    {
        var adapter = new RecordingHotkeyAdapter();
        var failure = HotkeyRegistrationResult.Fail(
            SpecForId(HotkeyRouteMap.IdWinPrintScreen), "RegisterHotKey", "Win32 error 1409");
        adapter.SetResults(failure);

        var tab = new HotkeysTabSettings(new AppSettings(), _ => SuccessfulSave(), adapter);

        var row = tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdWinPrintScreen);
        Assert.Equal(HotkeyRegistrationStatus.Failed, row.Status);
        Assert.Contains("1409", row.StatusDetail);
    }

    [Fact]
    public void GetRows_EnabledHotkeyWithNoRegistrationResult_ShowsUnknownStatus()
    {
        // Adapter reports no results at all (e.g. hotkeys never initialized).
        var adapter = new RecordingHotkeyAdapter();

        var tab = new HotkeysTabSettings(new AppSettings(), _ => SuccessfulSave(), adapter);

        var row = tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal(HotkeyRegistrationStatus.Unknown, row.Status);
    }

    [Fact]
    public void GetRows_DisabledHotkey_ShowsDisabledStatusRegardlessOfRegistration()
    {
        // Registration reports success, but the user disabled the hotkey.
        var adapter = new RecordingHotkeyAdapter();
        adapter.SetResults(HotkeyRegistrationResult.Success(SpecForId(HotkeyRouteMap.IdPrintScreen)));

        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool> { [HotkeyRouteMap.IdPrintScreen] = false },
        };
        var tab = new HotkeysTabSettings(source, _ => SuccessfulSave(), adapter);

        var row = tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdPrintScreen);
        Assert.Equal(HotkeyRegistrationStatus.Disabled, row.Status);
    }

    // ── Editing ─────────────────────────────────────────────────────────

    [Fact]
    public void EditEnabled_TogglingOff_DisablesTheHotkeyImmediately()
    {
        var tab = NewTab(new AppSettings());

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);

        Assert.False(tab.IsEnabled(HotkeyRouteMap.IdPrintScreen));
        var row = tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdPrintScreen);
        Assert.Equal(HotkeyRegistrationStatus.Disabled, row.Status);
    }

    [Fact]
    public void EditEnabled_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings();
        var tab = NewTab(source);

        tab.EditEnabled(HotkeyRouteMap.IdWinPrintScreen, enabled: false);

        Assert.Null(source.HotkeyEnabledStates);
    }

    [Fact]
    public void EditEnabled_TogglingBackOn_ReEnables()
    {
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool> { [HotkeyRouteMap.IdPrintScreen] = false },
        };
        var tab = NewTab(source);

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: true);

        Assert.True(tab.IsEnabled(HotkeyRouteMap.IdPrintScreen));
    }

    // ── Dirty tracking ──────────────────────────────────────────────────

    [Fact]
    public void IsDirty_FalseOnFreshConstruct()
    {
        var tab = NewTab(new AppSettings());

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_TrueAfterEdit()
    {
        var tab = NewTab(new AppSettings());

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_FalseAfterRevertingToBaseline()
    {
        var tab = NewTab(new AppSettings());

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);
        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: true);

        Assert.False(tab.IsDirty);
    }

    // ── Apply: persists enabled states ──────────────────────────────────

    [Fact]
    public void Apply_DisabledHotkey_PersistsExplicitDisabledState()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new HotkeysTabSettings(new AppSettings(), recorder.Save, new RecordingHotkeyAdapter());

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);
        tab.Apply();

        Assert.NotNull(recorder.LastSaved);
        Assert.False(recorder.LastSaved!.IsHotkeyEnabled(HotkeyRouteMap.IdPrintScreen));
        // Other hotkeys remain enabled.
        Assert.True(recorder.LastSaved.IsHotkeyEnabled(HotkeyRouteMap.IdWinPrintScreen));
    }

    [Fact]
    public void Apply_AllEnabled_PersistsCleanNullStates()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new HotkeysTabSettings(new AppSettings(), recorder.Save, new RecordingHotkeyAdapter());

        tab.Apply();

        // Nothing disabled -> persist as null (clean default, omitted from JSON).
        Assert.Null(recorder.LastSaved!.HotkeyEnabledStates);
    }

    [Fact]
    public void Apply_OnSuccess_KeepsWindowOpen()
    {
        var tab = NewTab(new AppSettings());

        var result = tab.Apply();

        Assert.True(result.Success);
        Assert.False(result.ShouldClose);
    }

    [Fact]
    public void Apply_OnSuccess_PersistsExactlyOnce()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new HotkeysTabSettings(new AppSettings(), recorder.Save, new RecordingHotkeyAdapter());

        tab.Apply();

        Assert.Equal(1, recorder.CallCount);
    }

    [Fact]
    public void Apply_OnSuccess_UpdatesBaselineSoCancelNoLongerReverts()
    {
        var tab = NewTab(new AppSettings());

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);
        tab.Apply();
        tab.Cancel();

        Assert.False(tab.IsEnabled(HotkeyRouteMap.IdPrintScreen));
        Assert.False(tab.IsDirty);
    }

    // ── Apply: reconciles runtime registration ──────────────────────────

    [Fact]
    public void Apply_OnSuccess_ReconcilesRuntimeRegistrationToEnabledSet()
    {
        var adapter = new RecordingHotkeyAdapter();
        var tab = new HotkeysTabSettings(new AppSettings(), _ => SuccessfulSave(), adapter);

        tab.EditEnabled(HotkeyRouteMap.IdWinPrintScreen, enabled: false);
        tab.EditEnabled(HotkeyRouteMap.IdShiftPrintScreen, enabled: false);
        tab.Apply();

        Assert.Equal(1, adapter.ApplyCallsCount);
        var applied = adapter.LastAppliedEnabledIds!;
        Assert.DoesNotContain(HotkeyRouteMap.IdWinPrintScreen, applied);
        Assert.DoesNotContain(HotkeyRouteMap.IdShiftPrintScreen, applied);
        Assert.Contains(HotkeyRouteMap.IdPrintScreen, applied);
        Assert.Contains(HotkeyRouteMap.IdWinShiftPrintScreen, applied);
    }

    [Fact]
    public void Apply_WhenARegistrationFails_SurfacesItInTheStatusWithoutOverclaimingActive()
    {
        // One enabled hotkey fails to register during reconcile.
        var adapter = new RecordingHotkeyAdapter();
        adapter.SetApplyFailingIds(HotkeyRouteMap.IdWinPrintScreen);
        var tab = new HotkeysTabSettings(new AppSettings(), _ => SuccessfulSave(), adapter);

        var result = tab.Apply();

        // The save itself succeeded, so Success is true and the window may close...
        Assert.True(result.Success);
        // ...but the status message must not just say "saved" — it surfaces the
        // registration failure so the user is told the binding is not active.
        Assert.Contains("failed to register", result.Message);
        // ...and the offending row shows Failed (not Registered).
        var row = tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdWinPrintScreen);
        Assert.Equal(HotkeyRegistrationStatus.Failed, row.Status);
        Assert.Contains("1409", row.StatusDetail);
    }

    [Fact]
    public void Apply_WhenEveryRegistrationSucceeds_ReportsPlainSavedMessage()
    {
        var adapter = new RecordingHotkeyAdapter();
        var tab = new HotkeysTabSettings(new AppSettings(), _ => SuccessfulSave(), adapter);

        var result = tab.Apply();

        Assert.Equal(SettingsWindowCoordinator.SavedMessage, result.Message);
    }

    [Fact]
    public void Apply_OnFailure_DoesNotReconcileAndStaysOpen()
    {
        var adapter = new RecordingHotkeyAdapter();
        var recorder = new RecordingSave(FailedSave("WriteTemp", "disk full"));
        var tab = new HotkeysTabSettings(new AppSettings(), recorder.Save, adapter);

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);
        var result = tab.Apply();

        Assert.False(result.Success);
        Assert.False(result.ShouldClose);
        Assert.Equal(0, adapter.ApplyCallsCount);
        Assert.Contains("disk full", result.Message);
        // Still dirty — the edit was not committed.
        Assert.True(tab.IsDirty);
    }

    // ── Confirm and Cancel ──────────────────────────────────────────────

    [Fact]
    public void Confirm_OnSuccess_PersistsAndCloses()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new HotkeysTabSettings(new AppSettings(), recorder.Save, new RecordingHotkeyAdapter());

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);
        var result = tab.Confirm();

        Assert.True(result.Success);
        Assert.True(result.ShouldClose);
        Assert.Equal(1, recorder.CallCount);
    }

    [Fact]
    public void Confirm_OnFailure_StaysOpenWithoutReconcile()
    {
        var adapter = new RecordingHotkeyAdapter();
        var tab = new HotkeysTabSettings(new AppSettings(), _ => FailedSave("Move", "locked"), adapter);

        var result = tab.Confirm();

        Assert.False(result.Success);
        Assert.False(result.ShouldClose);
        Assert.Equal(0, adapter.ApplyCallsCount);
    }

    [Fact]
    public void Cancel_RevertsEditsAndLeavesNothingPersistedOrReconciled()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var adapter = new RecordingHotkeyAdapter();
        var tab = new HotkeysTabSettings(new AppSettings(), recorder.Save, adapter);

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);
        tab.Cancel();

        Assert.True(tab.IsEnabled(HotkeyRouteMap.IdPrintScreen));
        Assert.False(tab.IsDirty);
        Assert.Equal(0, recorder.CallCount);
        Assert.Equal(0, adapter.ApplyCallsCount);
    }

    // ── Validation / gating (booleans are always valid) ─────────────────

    [Fact]
    public void IsValid_AlwaysTrue_AndButtonsCanBeApplied()
    {
        var tab = NewTab(new AppSettings());

        Assert.True(tab.IsValid);
        Assert.True(tab.CanApply);
        Assert.True(tab.CanConfirm);
    }

    // ── Reset to defaults: re-enables every Global Hotkey without persisting ──

    [Fact]
    public void Reset_RestoresEveryGlobalHotkeyEnabled()
    {
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
                [HotkeyRouteMap.IdWinPrintScreen] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();

        foreach (var spec in HotkeyRouteMap.AllSpecs)
            Assert.True(tab.IsEnabled(spec.Id));
    }

    [Fact]
    public void Reset_RowsShowEveryHotkeyAsEnabledNotDisabled()
    {
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
            },
        };
        var tab = NewTab(source);

        Assert.Equal(HotkeyRegistrationStatus.Disabled,
            tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdPrintScreen).Status);

        tab.Reset();

        // After Reset no hotkey reads as Disabled — status comes from registration results.
        Assert.All(tab.GetRows(), r => Assert.NotEqual(HotkeyRegistrationStatus.Disabled, r.Status));
    }

    [Fact]
    public void Reset_DoesNotPersist()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
            },
        };
        var tab = new HotkeysTabSettings(source, recorder.Save, new RecordingHotkeyAdapter());

        tab.Reset();

        Assert.Equal(0, recorder.CallCount);
        Assert.Null(recorder.LastSaved);
    }

    [Fact]
    public void Reset_DoesNotReconcileRuntimeRegistration()
    {
        var adapter = new RecordingHotkeyAdapter();
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
            },
        };
        var tab = new HotkeysTabSettings(source, _ => SuccessfulSave(), adapter);

        tab.Reset();

        Assert.Equal(0, adapter.ApplyCallsCount); // runtime registration untouched
    }

    [Fact]
    public void Reset_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();

        Assert.False(source.IsHotkeyEnabled(HotkeyRouteMap.IdPrintScreen)); // source untouched
    }

    [Fact]
    public void Reset_WhenBaselineDiffersFromDefaults_IsDirty()
    {
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();

        Assert.True(tab.IsDirty); // re-enabling a disabled hotkey is a pending change
    }

    [Fact]
    public void Reset_WhenBaselineAlreadyAllEnabled_IsNotDirty()
    {
        var source = new AppSettings(); // all enabled by default
        var tab = NewTab(source);

        tab.Reset();

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Cancel_AfterReset_PreservesEnabledStatesFromBeforeReset()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var adapter = new RecordingHotkeyAdapter();
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
            },
        };
        var tab = new HotkeysTabSettings(source, recorder.Save, adapter);

        tab.Reset();
        tab.Cancel();

        Assert.False(tab.IsEnabled(HotkeyRouteMap.IdPrintScreen)); // baseline restored
        Assert.False(tab.IsDirty);
        Assert.Equal(0, recorder.CallCount);
        Assert.Equal(0, adapter.ApplyCallsCount);
    }

    [Fact]
    public void Apply_AfterReset_PersistsAllEnabledAndReconcilesRuntime()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var adapter = new RecordingHotkeyAdapter();
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
            },
        };
        var tab = new HotkeysTabSettings(source, recorder.Save, adapter);

        tab.Reset();
        var result = tab.Apply();

        Assert.True(result.Success);
        Assert.Equal(1, recorder.CallCount);
        // Persisted as a clean all-enabled (null) map.
        Assert.Null(recorder.LastSaved!.HotkeyEnabledStates);
        // Runtime registration reconciled to every hotkey.
        Assert.Equal(1, adapter.ApplyCallsCount);
        foreach (var spec in HotkeyRouteMap.AllSpecs)
            Assert.Contains(spec.Id, adapter.LastAppliedEnabledIds!);
        Assert.False(tab.IsDirty); // baseline advanced to defaults
    }

    [Fact]
    public void Confirm_AfterReset_PersistsAndSignalsClose()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
            },
        };
        var tab = new HotkeysTabSettings(source, recorder.Save, new RecordingHotkeyAdapter());

        tab.Reset();
        var result = tab.Confirm();

        Assert.True(result.Success);
        Assert.True(result.ShouldClose);
        Assert.Equal(1, recorder.CallCount);
        Assert.Null(recorder.LastSaved!.HotkeyEnabledStates);
    }

    [Fact]
    public void Reset_IsIdempotent()
    {
        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
                [HotkeyRouteMap.IdWinShiftPrintScreen] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();
        tab.Reset();
        tab.Reset();

        foreach (var spec in HotkeyRouteMap.AllSpecs)
            Assert.True(tab.IsEnabled(spec.Id));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static HotkeysTabSettings NewTab(AppSettings source)
        => new HotkeysTabSettings(source, _ => SuccessfulSave(), new RecordingHotkeyAdapter());

    private static HotkeySpec SpecForId(int id) => HotkeyRouteMap.AllSpecs.Single(s => s.Id == id);

    private static ConfigurationSaveResult SuccessfulSave() => new()
    {
        Success = true,
        ConfigPath = Path.Combine(Path.GetTempPath(), "settings.json"),
    };

    private static ConfigurationSaveResult FailedSave(string phase, string message) => new()
    {
        Success = false,
        Phase = phase,
        ErrorMessage = message,
        ConfigPath = Path.Combine(Path.GetTempPath(), "settings.json"),
    };

    /// <summary>
    /// Records save calls and returns a forced result, so persistence can be
    /// observed without touching the filesystem.
    /// </summary>
    private sealed class RecordingSave
    {
        private readonly ConfigurationSaveResult _result;
        public int CallCount { get; private set; }
        public AppSettings? LastSaved { get; private set; }

        public RecordingSave(ConfigurationSaveResult result) => _result = result;

        public ConfigurationSaveResult Save(AppSettings settings)
        {
            CallCount++;
            LastSaved = settings;
            return _result;
        }
    }

    /// <summary>
    /// Fake Global Hotkey adapter that records ApplyEnabledStates calls and serves
    /// configurable registration results, so display and reconcile behavior can be
    /// verified without a Win32 hotkey manager.
    /// </summary>
    private sealed class RecordingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        private readonly List<HotkeyRegistrationResult> _results = new();
        private readonly HashSet<int> _applyFailingIds = new();

        public int ApplyCallsCount { get; private set; }
        public IReadOnlySet<int>? LastAppliedEnabledIds { get; private set; }

        public IReadOnlyList<HotkeyRegistrationResult> RegistrationResults => _results;

        public void SetResults(params HotkeyRegistrationResult[] results)
        {
            _results.Clear();
            _results.AddRange(results);
        }

        /// <summary>Ids that ApplyEnabledStates should report as failed registrations.</summary>
        public void SetApplyFailingIds(params int[] ids)
        {
            _applyFailingIds.Clear();
            foreach (var id in ids)
                _applyFailingIds.Add(id);
        }

        public IReadOnlyList<HotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds)
        {
            ApplyCallsCount++;
            LastAppliedEnabledIds = enabledIds;
            _results.Clear();
            foreach (var id in enabledIds)
            {
                var spec = HotkeyRouteMap.AllSpecs.Single(s => s.Id == id);
                _results.Add(_applyFailingIds.Contains(id)
                    ? HotkeyRegistrationResult.Fail(spec, "RegisterHotKey", "Win32 error 1409")
                    : HotkeyRegistrationResult.Success(spec));
            }
            return RegistrationResults;
        }
    }
}
