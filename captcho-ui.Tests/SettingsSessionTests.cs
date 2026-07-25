// SettingsSessionTests.cs — Headless tests for the composed SettingsSession.
//
// Verifies the WinUI-free SettingsSession that owns the composed Apply/OK/Cancel/Reset
// flow across every editable tab. Covers: opening snapshots the runtime without
// mutating it; composed button gating; edits return refreshed views; atomic persistence
// (one save across all tabs, runtime write-back, baseline advance, Global Hotkey reconcile);
// all-or-nothing on save failure; Cancel rolls every tab back; Reset refreshes every
// tab; OK with a failing save leaves everything consistent; and status messaging.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class SettingsSessionTests
{
    // ── Construction / View ─────────────────────────────────────────────

    [Fact]
    public void Constructor_NullRuntime_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SettingsSession(null!, NewConfiguration(), new RecordingGlobalHotkeyAdapter()));
    }

    [Fact]
    public void Constructor_NullConfiguration_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SettingsSession(new AppSettings(), null!, new RecordingGlobalHotkeyAdapter()));
    }

    [Fact]
    public void Constructor_NullGlobalHotkeys_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SettingsSession(new AppSettings(), NewConfiguration(), null!));
    }

    [Fact]
    public void View_OnOpen_ReflectsTheRuntimeSnapshot()
    {
        var runtime = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };

        var session = NewSession(runtime);

        var view = session.View;
        Assert.Equal(@"D:\Captures", view.SaveLocation);
        Assert.Equal("custom-<title>", view.FilenameTemplate);
        Assert.Equal("custom-Screenshot.png", view.FilenameTemplatePreview);
        Assert.Equal(4, view.GlobalHotkeyRows.Count);
        Assert.Null(view.StatusMessage);
        Assert.False(view.ShouldClose);
    }

    [Fact]
    public void Constructor_DoesNotMutateRuntime()
    {
        var runtime = new AppSettings { FilenameTemplate = "original" };

        var session = NewSession(runtime);

        Assert.Equal("original", runtime.FilenameTemplate);
        // The view reflects the effective value, but the runtime field is untouched.
        Assert.Equal("original", session.View.FilenameTemplate);
    }

    [Fact]
    public void View_CarriesReadOnlyTabContent()
    {
        var session = NewSession(new AppSettings());

        var view = session.View;
        Assert.Equal("Export format", view.Export.Heading);
        Assert.False(string.IsNullOrEmpty(view.Export.FormatName));
        Assert.Equal("Interface settings", view.Interface.Heading);
        Assert.False(string.IsNullOrEmpty(view.Interface.Message));
    }

    // ── Composed gating ────────────────────────────────────────────────

    [Fact]
    public void CanApply_WhenAllTabsValid_IsTrue()
    {
        var session = NewSession(AppSettings.WithDefaults());

        Assert.True(session.View.CanApply);
        Assert.True(session.View.CanConfirm);
    }

    [Fact]
    public void CanApply_WhenGeneralTabInvalid_IsFalse()
    {
        var session = NewSession(AppSettings.WithDefaults());

        session.EditFilenameTemplate("");

        Assert.False(session.View.CanApply);
        Assert.False(session.View.CanConfirm);
    }

    [Fact]
    public void CanCancel_AndCanReset_AlwaysTrue()
    {
        var session = NewSession(new AppSettings());

        Assert.True(session.View.CanCancel);
        Assert.True(session.View.CanReset);
    }

    // ── Edits return a refreshed view ──────────────────────────────────

    [Fact]
    public void EditSaveLocation_UpdatesView()
    {
        var session = NewSession(AppSettings.WithDefaults());

        var view = session.EditSaveLocation(@"E:\New");

        Assert.Equal(@"E:\New", view.SaveLocation);
    }

    [Fact]
    public void EditFilenameTemplate_UpdatesPreviewInView()
    {
        var session = NewSession(AppSettings.WithDefaults());

        var view = session.EditFilenameTemplate("<title>_<yyyy>");

        Assert.Equal("<title>_<yyyy>", view.FilenameTemplate);
        Assert.Equal("Screenshot_2024.png", view.FilenameTemplatePreview);
    }

    [Fact]
    public void EditFilenameTemplate_ToInvalid_SurfacesErrorInView()
    {
        var session = NewSession(AppSettings.WithDefaults());

        var view = session.EditFilenameTemplate("");

        Assert.NotNull(view.FilenameTemplateError);
        Assert.False(view.CanApply);
    }

    [Fact]
    public void ApplyFolderPickerResult_WithSelection_UpdatesSaveLocation()
    {
        var session = NewSession(AppSettings.WithDefaults());

        var view = session.ApplyFolderPickerResult(@"F:\Picked");

        Assert.Equal(@"F:\Picked", view.SaveLocation);
    }

    [Fact]
    public void ApplyFolderPickerResult_WithNull_IsNoOp()
    {
        var session = NewSession(AppSettings.WithDefaults());

        var before = session.View.SaveLocation;
        var view = session.ApplyFolderPickerResult(null);

        Assert.Equal(before, view.SaveLocation);
    }

    [Fact]
    public void EditGlobalHotkeyEnabled_TogglesTheRowInView()
    {
        var session = NewSession(new AppSettings());

        var view = session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);

        var row = view.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen);
        Assert.False(row.IsEnabled);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Disabled, row.Status);
    }

    // ── AC: edit two tabs then Cancel rolls both back ───────────────────

    [Fact]
    public void Cancel_AfterEditingTwoTabs_RollsBothBackToBaseline()
    {
        var runtime = new AppSettings
        {
            SaveLocation = @"D:\Original",
            FilenameTemplate = "original-<title>",
        };
        var session = NewSession(runtime);

        session.EditSaveLocation(@"E:\Edited");
        session.EditFilenameTemplate("edited-<yyyy>");
        session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);

        var view = session.Cancel();

        // General tab reverted.
        Assert.Equal(@"D:\Original", view.SaveLocation);
        Assert.Equal("original-<title>", view.FilenameTemplate);
        // Global Hotkeys tab reverted.
        Assert.True(view.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen).IsEnabled);
    }

    [Fact]
    public void Cancel_DoesNotPersistOrReconcile()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new FakeConfiguration();
        var adapter = new RecordingGlobalHotkeyAdapter();
        var session = new SettingsSession(runtime, recorder, adapter);

        session.EditSaveLocation(@"E:\Edited");
        session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        session.Cancel();

        Assert.Equal(0, recorder.CallCount);
        Assert.Equal(0, adapter.ApplyCallsCount);
    }

    // ── AC: Reset refreshes all tabs ────────────────────────────────────

    [Fact]
    public void Reset_RestoresDefaultsAcrossAllEditableTabs()
    {
        var runtime = new AppSettings
        {
            SaveLocation = @"D:\Custom",
            FilenameTemplate = "custom-<title>",
            GlobalHotkeyEnabledStates = new Dictionary<int, bool>
            {
                [GlobalHotkeyRouteMap.IdPrintScreen] = false,
                [GlobalHotkeyRouteMap.IdWinPrintScreen] = false,
            },
        };
        var session = NewSession(runtime);

        var view = session.Reset();

        // General reset to defaults.
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, view.SaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, view.FilenameTemplate);
        // Global Hotkeys reset to all-enabled.
        Assert.All(view.GlobalHotkeyRows, r => Assert.True(r.IsEnabled));
    }

    [Fact]
    public void Reset_LeavesBaselineSoCancelRevertsTheReset()
    {
        var runtime = new AppSettings
        {
            SaveLocation = @"D:\Custom",
            FilenameTemplate = "custom-<title>",
        };
        var session = NewSession(runtime);

        session.Reset();
        var view = session.Cancel();

        Assert.Equal(@"D:\Custom", view.SaveLocation);
        Assert.Equal("custom-<title>", view.FilenameTemplate);
    }

    [Fact]
    public void Reset_DoesNotPersistOrReconcile()
    {
        var recorder = new FakeConfiguration();
        var adapter = new RecordingGlobalHotkeyAdapter();
        var session = new SettingsSession(AppSettings.WithDefaults(), recorder, adapter);

        session.Reset();

        Assert.Equal(0, recorder.CallCount);
        Assert.Equal(0, adapter.ApplyCallsCount);
    }

    // ── Apply: atomic persistence across all tabs ───────────────────────

    [Fact]
    public void Apply_PersistsAllTabsInASingleSave()
    {
        var recorder = new FakeConfiguration();
        var session = new SettingsSession(AppSettings.WithDefaults(), recorder, new RecordingGlobalHotkeyAdapter());

        session.EditSaveLocation(@"D:\New");
        session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        var view = session.Apply();

        Assert.Equal(1, recorder.CallCount);
        Assert.NotNull(recorder.LastSaved);
        Assert.Equal(@"D:\New", recorder.LastSaved!.SaveLocation);
        Assert.False(recorder.LastSaved.IsGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen));
        // Other Global Hotkey defaults preserved.
        Assert.True(recorder.LastSaved.IsGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdWinPrintScreen));
        Assert.Equal(SettingsSession.SavedMessage, view.StatusMessage);
        Assert.False(view.StatusIsError);
        Assert.False(view.ShouldClose);
    }

    [Fact]
    public void Apply_WritesMergedValuesBackIntoRuntime()
    {
        var runtime = AppSettings.WithDefaults();
        var session = NewSession(runtime);

        session.EditSaveLocation(@"D:\New");
        session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        session.Apply();

        Assert.Equal(@"D:\New", runtime.SaveLocation);
        Assert.False(runtime.IsGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen));
    }

    [Fact]
    public void Apply_OnSuccess_AdvancesBaselineSoCancelKeepsCommittedValues()
    {
        var session = NewSession(AppSettings.WithDefaults());

        session.EditSaveLocation(@"D:\Applied");
        session.Apply();
        var view = session.Cancel();

        Assert.Equal(@"D:\Applied", view.SaveLocation);
    }

    [Fact]
    public void Apply_OnSuccess_ReconcilesRuntimeRegistrationToEnabledSet()
    {
        var adapter = new RecordingGlobalHotkeyAdapter();
        var session = new SettingsSession(AppSettings.WithDefaults(), NewConfiguration(), adapter);

        session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdWinPrintScreen, enabled: false);
        session.Apply();

        Assert.Equal(1, adapter.ApplyCallsCount);
        Assert.DoesNotContain(GlobalHotkeyRouteMap.IdWinPrintScreen, adapter.LastAppliedEnabledIds!);
        Assert.Contains(GlobalHotkeyRouteMap.IdPrintScreen, adapter.LastAppliedEnabledIds!);
    }

    [Fact]
    public void Apply_OnSuccessWithRegistrationFailure_SurfacesItWithoutOverclaimingActive()
    {
        var adapter = new RecordingGlobalHotkeyAdapter();
        adapter.SetApplyFailingIds(GlobalHotkeyRouteMap.IdWinPrintScreen);
        var session = new SettingsSession(AppSettings.WithDefaults(), NewConfiguration(), adapter);

        var view = session.Apply();

        Assert.False(view.StatusIsError); // save succeeded; registration failure is a warning
        Assert.Contains("failed to register", view.StatusMessage);
        var row = view.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdWinPrintScreen);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Failed, row.Status);
    }

    [Fact]
    public void Apply_OnSuccess_ReportsSavedMessage()
    {
        var session = NewSession(AppSettings.WithDefaults());

        var view = session.Apply();

        Assert.Equal(SettingsSession.SavedMessage, view.StatusMessage);
        Assert.False(view.StatusIsError);
        Assert.False(view.ShouldClose);
    }

    [Fact]
    public void Apply_WhenInvalid_DoesNotPersistAndReportsError()
    {
        var recorder = new FakeConfiguration();
        var session = new SettingsSession(AppSettings.WithDefaults(), recorder, new RecordingGlobalHotkeyAdapter());

        session.EditFilenameTemplate("");
        var view = session.Apply();

        Assert.Equal(0, recorder.CallCount);
        Assert.True(view.StatusIsError);
        Assert.NotNull(view.StatusMessage);
    }

    // ── Status reflects the most recent action ──────────────────────────

    [Fact]
    public void Edit_AfterFailedApply_ClearsTheStaleErrorStatus()
    {
        var forcing = new FakeConfiguration(forcedFailure: FailedSave("WriteTemp", "disk full"));
        var session = new SettingsSession(AppSettings.WithDefaults(), forcing, new RecordingGlobalHotkeyAdapter());

        session.Apply();
        Assert.True(session.View.StatusIsError);

        // The next action is an edit; the view must reflect THAT action, not the stale error.
        var view = session.EditSaveLocation(@"D:\Editing");

        Assert.Null(view.StatusMessage);
        Assert.False(view.StatusIsError);
    }

    [Fact]
    public void Cancel_AfterSuccessfulApply_ClearsTheSavedStatus()
    {
        var session = NewSession(AppSettings.WithDefaults());

        session.Apply();
        Assert.Equal(SettingsSession.SavedMessage, session.View.StatusMessage);

        var view = session.Cancel();

        Assert.Null(view.StatusMessage);
    }

    [Fact]
    public void Reset_ClearsAnyPriorStatus()
    {
        var forcing = new FakeConfiguration(forcedFailure: FailedSave("WriteTemp", "denied"));
        var session = new SettingsSession(AppSettings.WithDefaults(), forcing, new RecordingGlobalHotkeyAdapter());

        session.Apply();
        Assert.True(session.View.StatusIsError);

        var view = session.Reset();

        Assert.Null(view.StatusMessage);
        Assert.False(view.StatusIsError);
    }

    // ── AC: OK with one tab failing (atomicity, #12 fix) ────────────────

    [Fact]
    public void Apply_OnSaveFailure_NothingMoves()
    {
        var runtime = AppSettings.WithDefaults();
        var adapter = new RecordingGlobalHotkeyAdapter();
        var forcing = new FakeConfiguration(forcedFailure: FailedSave("WriteTemp", "disk full"));
        var session = new SettingsSession(runtime, forcing, adapter);

        session.EditSaveLocation(@"D:\Attempted");
        session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        var view = session.Apply();

        // Failure surfaced as status.
        Assert.True(view.StatusIsError);
        Assert.Contains("disk full", view.StatusMessage);
        Assert.False(view.ShouldClose);

        // Runtime untouched — export flow and Global Hotkey runtime see no change.
        Assert.NotEqual(@"D:\Attempted", runtime.SaveLocation);
        Assert.True(runtime.IsGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen));

        // Baselines untouched — Cancel reverts the (still-uncommitted) edits.
        Assert.Equal(0, adapter.ApplyCallsCount); // no reconcile on failure
        var afterCancel = session.Cancel();
        Assert.NotEqual(@"D:\Attempted", afterCancel.SaveLocation);
        Assert.True(afterCancel.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen).IsEnabled);
    }

    [Fact]
    public void Apply_OnSaveFailure_PersistsAtMostOnce()
    {
        var forcing = new FakeConfiguration(forcedFailure: FailedSave("Move", "locked"));
        var session = new SettingsSession(AppSettings.WithDefaults(), forcing, new RecordingGlobalHotkeyAdapter());

        session.Apply();

        Assert.Equal(1, forcing.CallCount);
    }

    // ── Confirm (OK): closes only on success ────────────────────────────

    [Fact]
    public void Confirm_OnSuccess_PersistsOnceAndSignalsClose()
    {
        var recorder = new FakeConfiguration();
        var session = new SettingsSession(AppSettings.WithDefaults(), recorder, new RecordingGlobalHotkeyAdapter());

        session.EditSaveLocation(@"D:\Ok");
        var view = session.Confirm();

        Assert.True(view.ShouldClose);
        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(SettingsSession.SavedMessage, view.StatusMessage);
    }

    [Fact]
    public void Confirm_OnSaveFailure_StaysOpenAndReportsError()
    {
        var forcing = new FakeConfiguration(forcedFailure: FailedSave("WriteTemp", "denied"));
        var session = new SettingsSession(AppSettings.WithDefaults(), forcing, new RecordingGlobalHotkeyAdapter());

        session.EditFilenameTemplate("ok-<yyyy>");
        var view = session.Confirm();

        Assert.False(view.ShouldClose);
        Assert.True(view.StatusIsError);
        Assert.Contains("denied", view.StatusMessage);
    }

    [Fact]
    public void Confirm_WhenInvalid_StaysOpenWithoutPersisting()
    {
        var recorder = new FakeConfiguration();
        var session = new SettingsSession(AppSettings.WithDefaults(), recorder, new RecordingGlobalHotkeyAdapter());

        session.EditFilenameTemplate("");
        var view = session.Confirm();

        Assert.False(view.ShouldClose);
        Assert.Equal(0, recorder.CallCount);
        Assert.True(view.StatusIsError);
    }

    [Fact]
    public void Confirm_WhenSaveFailsWithTwoTabsEdited_NothingMovesForEitherTab()
    {
        // AC #3 "OK with one tab failing": OK is attempted with BOTH tabs edited and the
        // save fails. Nothing strands — neither tab's runtime value, baseline, or row
        // state moves, and the window stays open. (The atomicity fix for #12.)
        var runtime = AppSettings.WithDefaults();
        var adapter = new RecordingGlobalHotkeyAdapter();
        var forcing = new FakeConfiguration(forcedFailure: FailedSave("Move", "locked"));
        var session = new SettingsSession(runtime, forcing, adapter);

        session.EditSaveLocation(@"D:\Attempted");
        session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        var view = session.Confirm();

        Assert.False(view.ShouldClose);
        Assert.True(view.StatusIsError);
        Assert.Contains("locked", view.StatusMessage);
        Assert.Equal(1, forcing.CallCount);
        Assert.Equal(0, adapter.ApplyCallsCount); // no reconcile on failure

        // Runtime untouched on both tabs.
        Assert.NotEqual(@"D:\Attempted", runtime.SaveLocation);
        Assert.True(runtime.IsGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen));

        // Neither tab's baseline advanced — Cancel reverts both uncommitted edits.
        var afterCancel = session.Cancel();
        Assert.NotEqual(@"D:\Attempted", afterCancel.SaveLocation);
        Assert.True(afterCancel.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen).IsEnabled);
    }

    // ── Atomicity: one tab failing means nothing is committed ───────────

    [Fact]
    public void Apply_OnFailure_DoesNotAdvanceEitherTabBaseline()
    {
        var forcing = new FakeConfiguration(forcedFailure: FailedSave("Move", "locked"));
        var session = new SettingsSession(AppSettings.WithDefaults(), forcing, new RecordingGlobalHotkeyAdapter());

        session.EditSaveLocation(@"D:\Attempted");
        session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        session.Apply(); // fails atomically
        var view = session.Cancel();

        // Both tabs revert to baseline — neither was committed.
        Assert.NotEqual(@"D:\Attempted", view.SaveLocation);
        Assert.True(view.GlobalHotkeyRows.Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen).IsEnabled);
    }

    [Fact]
    public void Apply_AfterReset_PersistsDefaultsAtomically()
    {
        var recorder = new FakeConfiguration();
        var adapter = new RecordingGlobalHotkeyAdapter();
        var runtime = new AppSettings
        {
            SaveLocation = @"D:\Custom",
            FilenameTemplate = "custom-<title>",
            GlobalHotkeyEnabledStates = new Dictionary<int, bool> { [GlobalHotkeyRouteMap.IdPrintScreen] = false },
        };
        var session = new SettingsSession(runtime, recorder, adapter);

        session.Reset();
        var view = session.Apply();

        Assert.Equal(SettingsSession.SavedMessage, view.StatusMessage);
        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, recorder.LastSaved!.SaveLocation);
        Assert.Null(recorder.LastSaved.GlobalHotkeyEnabledStates);
        // Runtime reflects the persisted defaults.
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, runtime.SaveLocation);
        Assert.True(runtime.IsGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen));
        Assert.Equal(1, adapter.ApplyCallsCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static SettingsSession NewSession(AppSettings runtime)
        => new SettingsSession(runtime, NewConfiguration(), new RecordingGlobalHotkeyAdapter());

    private static ConfigurationService NewConfiguration()
        => new ConfigurationService(Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid()}"));

    private static ConfigurationSaveResult FailedSave(string phase, string message) => new()
    {
        Success = false,
        Phase = phase,
        ErrorMessage = message,
        ConfigurationPath = Path.Combine(Path.GetTempPath(), "settings.json"),
    };

    /// <summary>
    /// ConfigurationService subclass that records every save and optionally forces a
    /// result, so the success counts, persisted values, and atomicity / failure paths
    /// can all be exercised headlessly without an IO port. Delegates to the real
    /// temp-dir save when no failure is forced.
    /// </summary>
    private sealed class FakeConfiguration : ConfigurationService
    {
        private readonly ConfigurationSaveResult? _forcedFailure;

        public int CallCount { get; private set; }
        public AppSettings? LastSaved { get; private set; }

        public FakeConfiguration(ConfigurationSaveResult? forcedFailure = null)
            : base(Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid()}"))
        {
            _forcedFailure = forcedFailure;
        }

        public override ConfigurationSaveResult Save(AppSettings settings)
        {
            CallCount++;
            LastSaved = settings;
            return _forcedFailure ?? base.Save(settings);
        }
    }

    /// <summary>
    /// Fake Global Hotkey adapter that records ApplyEnabledStates calls and serves
    /// configurable registration results, so display and reconcile behavior can be
    /// verified without a Win32 Global Hotkey manager.
    /// </summary>
    private sealed class RecordingGlobalHotkeyAdapter : IGlobalHotkeyAdapter
    {
        private readonly List<GlobalHotkeyRegistrationResult> _results = new();
        private readonly HashSet<int> _applyFailingIds = new();

        public int ApplyCallsCount { get; private set; }
        public IReadOnlySet<int>? LastAppliedEnabledIds { get; private set; }

        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults => _results;

        public void SetApplyFailingIds(params int[] ids)
        {
            _applyFailingIds.Clear();
            foreach (var id in ids)
                _applyFailingIds.Add(id);
        }

        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds)
        {
            ApplyCallsCount++;
            LastAppliedEnabledIds = enabledIds;
            _results.Clear();
            foreach (var id in enabledIds)
            {
                var spec = GlobalHotkeyRouteMap.AllSpecs.Single(s => s.Id == id);
                _results.Add(_applyFailingIds.Contains(id)
                    ? GlobalHotkeyRegistrationResult.Fail(spec, "RegisterHotKey", "Win32 error 1409")
                    : GlobalHotkeyRegistrationResult.Success(spec));
            }
            return RegistrationResults;
        }
    }
}
