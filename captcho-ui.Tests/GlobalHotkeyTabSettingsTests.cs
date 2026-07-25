// GlobalHotkeyTabSettingsTests.cs — Headless tests for the Global Hotkeys tab editing seam.
//
// Verifies the pure C# GlobalHotkeyTabSettings collaborator: it loads the persisted
// per-Global-Hotkey enabled states into a working snapshot (never mutating the source),
// lists all four Global Hotkeys with their bindings, behavior descriptions, and
// current registration status (read live from the adapter), and lets the user
// enable/disable each Global Hotkey independently. Persistence and runtime reconcile are
// no longer this tab's job — it writes its working slice via WriteInto and advances
// its baseline via Commit at the SettingsSession's direction, so the composed
// Apply/OK flow (including runtime registration reconcile) is covered in
// SettingsSessionTests.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class GlobalHotkeyTabSettingsTests
{
    // ── Constructor guards ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GlobalHotkeyTabSettings(null!, new RecordingGlobalHotkeyAdapter()));
    }

    [Fact]
    public void Constructor_NullAdapter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GlobalHotkeyTabSettings(new AppSettings(), null!));
    }

    // ── Display: all four global hotkeys with bindings and behavior ────────────

    [Fact]
    public void GetRows_ListsAllFourGlobalHotkeysInStableIdOrder()
    {
        var tab = NewTab(new AppSettings());

        var rows = tab.GetRows();

        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void GetRows_BindingMatchesTheGlobalHotkeyBindingName()
    {
        var tab = NewTab(new AppSettings());

        var rows = tab.GetRows();

        foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
            Assert.Equal(spec.Name, rows.Single(r => r.Id == spec.Id).Binding);
    }

    [Fact]
    public void GetRows_EachRowHasAnAccurateNonEmptyBehaviorDescription()
    {
        var tab = NewTab(new AppSettings());

        var rows = tab.GetRows();

        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Behavior)));
        Assert.Equal(rows.Count, rows.Select(r => r.Behavior).Distinct().Count());
    }

    [Theory]
    [InlineData(GlobalHotkeyRoute.CurrentMonitor, "monitor the cursor is on")]
    [InlineData(GlobalHotkeyRoute.ActiveWindow, "active")]
    [InlineData(GlobalHotkeyRoute.FullDesktop, "full virtual desktop")]
    [InlineData(GlobalHotkeyRoute.RectangularRegion, "Selection")]
    public void GetRows_BehaviorDescribesTheCaptureMode(GlobalHotkeyRoute route, string expectedFragment)
    {
        var tab = NewTab(new AppSettings());
        var spec = GlobalHotkeyRouteMap.AllSpecs.Single(s => s.Route == route);

        var row = tab.GetRows().Single(r => r.Id == spec.Id);

        Assert.Contains(expectedFragment, row.Behavior, StringComparison.OrdinalIgnoreCase);
    }

    // ── Default enabled state (settings predating per-Global-Hotkey toggles) ───

    [Fact]
    public void Constructor_NullGlobalHotkeyStates_AllGlobalHotkeysEnabledByDefault()
    {
        var tab = NewTab(new AppSettings());

        foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
            Assert.True(tab.IsEnabled(spec.Id));
    }

    [Fact]
    public void Constructor_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings(); // GlobalHotkeyEnabledStates stays null

        var tab = NewTab(source);

        Assert.Null(source.GlobalHotkeyEnabledStates);
        Assert.True(tab.IsEnabled(1));
    }

    [Fact]
    public void Constructor_PersistedDisabledGlobalHotkey_IsDisabledInWorkingState()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool> { [GlobalHotkeyRoute.CurrentMonitor] = false },
        };

        var tab = NewTab(source);

        Assert.False(tab.IsEnabled(GlobalHotkeyRouteMap.IdPrintScreen));
        Assert.True(tab.IsEnabled(GlobalHotkeyRouteMap.IdWinPrintScreen));
    }

    // ── Registration status display (read live from the adapter) ───────

    [Fact]
    public void GetRows_RegisteredGlobalHotkey_ShowsRegisteredStatus()
    {
        var adapter = new RecordingGlobalHotkeyAdapter();
        adapter.SetResults(GlobalHotkeyRegistrationResult.Success(SpecForId(GlobalHotkeyRouteMap.IdPrintScreen)));

        var tab = new GlobalHotkeyTabSettings(new AppSettings(), adapter);

        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Registered, row.Status);
    }

    [Fact]
    public void GetRows_FailedRegistration_ShowsFailedStatusWithSanitizedError()
    {
        var adapter = new RecordingGlobalHotkeyAdapter();
        var failure = GlobalHotkeyRegistrationResult.Fail(
            SpecForId(GlobalHotkeyRouteMap.IdWinPrintScreen), "RegisterHotKey", "Win32 error 1409");
        adapter.SetResults(failure);

        var tab = new GlobalHotkeyTabSettings(new AppSettings(), adapter);

        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdWinPrintScreen);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Failed, row.Status);
        Assert.Contains("1409", row.StatusDetail);
    }

    [Fact]
    public void GetRows_EnabledGlobalHotkeyWithNoRegistrationResult_ShowsUnknownStatus()
    {
        var adapter = new RecordingGlobalHotkeyAdapter();

        var tab = new GlobalHotkeyTabSettings(new AppSettings(), adapter);

        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Unknown, row.Status);
    }

    [Fact]
    public void GetRows_DisabledGlobalHotkey_ShowsDisabledStatusRegardlessOfRegistration()
    {
        var adapter = new RecordingGlobalHotkeyAdapter();
        adapter.SetResults(GlobalHotkeyRegistrationResult.Success(SpecForId(GlobalHotkeyRouteMap.IdPrintScreen)));

        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool> { [GlobalHotkeyRoute.CurrentMonitor] = false },
        };
        var tab = new GlobalHotkeyTabSettings(source, adapter);

        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Disabled, row.Status);
    }

    [Fact]
    public void GetRows_ReflectsLiveAdapterResultsAfterAReconcile()
    {
        // The tab reads registration status live from the adapter, so after an external
        // reconcile (driven by the session) the next GetRows reflects the new results
        // without the tab itself being touched.
        var adapter = new RecordingGlobalHotkeyAdapter();
        var tab = new GlobalHotkeyTabSettings(new AppSettings(), adapter);

        // Initially no results -> Unknown.
        Assert.Equal(GlobalHotkeyRegistrationStatus.Unknown,
            tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen).Status);

        adapter.SetResults(GlobalHotkeyRegistrationResult.Success(SpecForId(GlobalHotkeyRouteMap.IdPrintScreen)));

        Assert.Equal(GlobalHotkeyRegistrationStatus.Registered,
            tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen).Status);
    }

    // ── Editing ─────────────────────────────────────────────────────────

    [Fact]
    public void EditEnabled_TogglingOff_DisablesTheGlobalHotkeyImmediately()
    {
        var tab = NewTab(new AppSettings());

        tab.EditEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);

        Assert.False(tab.IsEnabled(GlobalHotkeyRouteMap.IdPrintScreen));
        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen);
        Assert.Equal(GlobalHotkeyRegistrationStatus.Disabled, row.Status);
    }

    [Fact]
    public void EditEnabled_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings();
        var tab = NewTab(source);

        tab.EditEnabled(GlobalHotkeyRouteMap.IdWinPrintScreen, enabled: false);

        Assert.Null(source.GlobalHotkeyEnabledStates);
    }

    [Fact]
    public void EditEnabled_TogglingBackOn_ReEnables()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool> { [GlobalHotkeyRoute.CurrentMonitor] = false },
        };
        var tab = NewTab(source);

        tab.EditEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: true);

        Assert.True(tab.IsEnabled(GlobalHotkeyRouteMap.IdPrintScreen));
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

        tab.EditEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_FalseAfterRevertingToBaseline()
    {
        var tab = NewTab(new AppSettings());

        tab.EditEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        tab.EditEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: true);

        Assert.False(tab.IsDirty);
    }

    // ── Validation / gating (booleans are always valid) ─────────────────

    [Fact]
    public void IsValid_AlwaysTrue_AndHasNoFirstError()
    {
        var tab = NewTab(new AppSettings());

        Assert.True(tab.IsValid);
        Assert.Null(tab.FirstError);
    }

    // ── WriteInto merges only this tab's slice ──────────────────────────

    [Fact]
    public void WriteInto_DisabledGlobalHotkey_WritesExplicitDisabledState()
    {
        var tab = NewTab(new AppSettings());
        tab.EditEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        Assert.False(target.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));
        Assert.True(target.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.ActiveWindow));
    }

    [Fact]
    public void WriteInto_AllEnabled_WritesCleanNullStates()
    {
        var tab = NewTab(new AppSettings());

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        // Nothing disabled -> persist as null (clean default, omitted from JSON).
        Assert.Null(target.GlobalHotkeyEnabledStates);
    }

    [Fact]
    public void WriteInto_PreservesOtherTabsSlices()
    {
        var tab = NewTab(new AppSettings());
        tab.EditEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);

        var target = new AppSettings
        {
            SaveLocation = @"D:\Mine",
            FilenameTemplate = "custom-<title>",
        };

        tab.WriteInto(target);

        Assert.Equal(@"D:\Mine", target.SaveLocation);
        Assert.Equal("custom-<title>", target.FilenameTemplate);
    }

    [Fact]
    public void WriteInto_Null_Throws()
    {
        var tab = NewTab(new AppSettings());

        Assert.Throws<ArgumentNullException>(() => tab.WriteInto(null!));
    }

    // ── Commit advances the baseline so Cancel no longer reverts ────────

    [Fact]
    public void Commit_AdvancesBaselineSoCancelKeepsTheCommittedState()
    {
        var tab = NewTab(new AppSettings());

        tab.EditEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        tab.Commit();
        tab.Cancel();

        Assert.False(tab.IsEnabled(GlobalHotkeyRouteMap.IdPrintScreen));
        Assert.False(tab.IsDirty);
    }

    // ── Cancel ──────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_RevertsEditsToBaseline()
    {
        var tab = NewTab(new AppSettings());

        tab.EditEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        Assert.True(tab.IsDirty);

        tab.Cancel();

        Assert.True(tab.IsEnabled(GlobalHotkeyRouteMap.IdPrintScreen));
        Assert.False(tab.IsDirty);
    }

    // ── Reset to defaults: re-enables every Global Hotkey without persisting ──

    [Fact]
    public void Reset_RestoresEveryGlobalHotkeyEnabled()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.CurrentMonitor] = false,
                [GlobalHotkeyRoute.ActiveWindow] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();

        foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
            Assert.True(tab.IsEnabled(spec.Id));
    }

    [Fact]
    public void Reset_RowsShowEveryGlobalHotkeyAsEnabledNotDisabled()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.CurrentMonitor] = false,
            },
        };
        var tab = NewTab(source);

        Assert.Equal(GlobalHotkeyRegistrationStatus.Disabled,
            tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdPrintScreen).Status);

        tab.Reset();

        Assert.All(tab.GetRows(), r => Assert.NotEqual(GlobalHotkeyRegistrationStatus.Disabled, r.Status));
    }

    [Fact]
    public void Reset_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.CurrentMonitor] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();

        Assert.False(source.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));
    }

    [Fact]
    public void Reset_WhenBaselineDiffersFromDefaults_IsDirty()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.CurrentMonitor] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Reset_WhenBaselineAlreadyAllEnabled_IsNotDirty()
    {
        var source = new AppSettings();
        var tab = NewTab(source);

        tab.Reset();

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Cancel_AfterReset_PreservesEnabledStatesFromBeforeReset()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.CurrentMonitor] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();
        tab.Cancel();

        Assert.False(tab.IsEnabled(GlobalHotkeyRouteMap.IdPrintScreen));
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Reset_IsIdempotent()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.CurrentMonitor] = false,
                [GlobalHotkeyRoute.RectangularRegion] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();
        tab.Reset();
        tab.Reset();

        foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
            Assert.True(tab.IsEnabled(spec.Id));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static GlobalHotkeyTabSettings NewTab(AppSettings source)
        => new GlobalHotkeyTabSettings(source, new RecordingGlobalHotkeyAdapter());

    private static GlobalHotkeySpec SpecForId(int id) => GlobalHotkeyRouteMap.AllSpecs.Single(s => s.Id == id);

    /// <summary>
    /// Fake Global Hotkey adapter that serves configurable registration results, so
    /// display behavior can be verified without a Win32 Global Hotkey manager.
    /// </summary>
    private sealed class RecordingGlobalHotkeyAdapter : IGlobalHotkeyAdapter
    {
        private readonly List<GlobalHotkeyRegistrationResult> _results = new();

        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults => _results;

        public void SetResults(params GlobalHotkeyRegistrationResult[] results)
        {
            _results.Clear();
            _results.AddRange(results);
        }

        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds)
            => RegistrationResults;
    }
}
