// HotkeysTabSettingsTests.cs — Headless tests for the Hotkeys tab editing seam.
//
// Verifies the pure C# HotkeysTabSettings collaborator: it loads the persisted
// per-hotkey enabled states into a working snapshot (never mutating the source),
// lists all four Global Hotkeys with their bindings, behavior descriptions, and
// current registration status (read live from the adapter), and lets the user
// enable/disable each hotkey independently. Persistence and runtime reconcile are
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

public class HotkeysTabSettingsTests
{
    // ── Constructor guards ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HotkeysTabSettings(null!, new RecordingHotkeyAdapter()));
    }

    [Fact]
    public void Constructor_NullAdapter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HotkeysTabSettings(new AppSettings(), null!));
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

    // ── Registration status display (read live from the adapter) ───────

    [Fact]
    public void GetRows_RegisteredHotkey_ShowsRegisteredStatus()
    {
        var adapter = new RecordingHotkeyAdapter();
        adapter.SetResults(HotkeyRegistrationResult.Success(SpecForId(HotkeyRouteMap.IdPrintScreen)));

        var tab = new HotkeysTabSettings(new AppSettings(), adapter);

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

        var tab = new HotkeysTabSettings(new AppSettings(), adapter);

        var row = tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdWinPrintScreen);
        Assert.Equal(HotkeyRegistrationStatus.Failed, row.Status);
        Assert.Contains("1409", row.StatusDetail);
    }

    [Fact]
    public void GetRows_EnabledHotkeyWithNoRegistrationResult_ShowsUnknownStatus()
    {
        var adapter = new RecordingHotkeyAdapter();

        var tab = new HotkeysTabSettings(new AppSettings(), adapter);

        var row = tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdShiftPrintScreen);
        Assert.Equal(HotkeyRegistrationStatus.Unknown, row.Status);
    }

    [Fact]
    public void GetRows_DisabledHotkey_ShowsDisabledStatusRegardlessOfRegistration()
    {
        var adapter = new RecordingHotkeyAdapter();
        adapter.SetResults(HotkeyRegistrationResult.Success(SpecForId(HotkeyRouteMap.IdPrintScreen)));

        var source = new AppSettings
        {
            HotkeyEnabledStates = new Dictionary<int, bool> { [HotkeyRouteMap.IdPrintScreen] = false },
        };
        var tab = new HotkeysTabSettings(source, adapter);

        var row = tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdPrintScreen);
        Assert.Equal(HotkeyRegistrationStatus.Disabled, row.Status);
    }

    [Fact]
    public void GetRows_ReflectsLiveAdapterResultsAfterAReconcile()
    {
        // The tab reads registration status live from the adapter, so after an external
        // reconcile (driven by the session) the next GetRows reflects the new results
        // without the tab itself being touched.
        var adapter = new RecordingHotkeyAdapter();
        var tab = new HotkeysTabSettings(new AppSettings(), adapter);

        // Initially no results -> Unknown.
        Assert.Equal(HotkeyRegistrationStatus.Unknown,
            tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdPrintScreen).Status);

        adapter.SetResults(HotkeyRegistrationResult.Success(SpecForId(HotkeyRouteMap.IdPrintScreen)));

        Assert.Equal(HotkeyRegistrationStatus.Registered,
            tab.GetRows().Single(r => r.Id == HotkeyRouteMap.IdPrintScreen).Status);
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
    public void WriteInto_DisabledHotkey_WritesExplicitDisabledState()
    {
        var tab = NewTab(new AppSettings());
        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        Assert.False(target.IsHotkeyEnabled(HotkeyRouteMap.IdPrintScreen));
        Assert.True(target.IsHotkeyEnabled(HotkeyRouteMap.IdWinPrintScreen));
    }

    [Fact]
    public void WriteInto_AllEnabled_WritesCleanNullStates()
    {
        var tab = NewTab(new AppSettings());

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        // Nothing disabled -> persist as null (clean default, omitted from JSON).
        Assert.Null(target.HotkeyEnabledStates);
    }

    [Fact]
    public void WriteInto_PreservesOtherTabsSlices()
    {
        var tab = NewTab(new AppSettings());
        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);

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

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);
        tab.Commit();
        tab.Cancel();

        Assert.False(tab.IsEnabled(HotkeyRouteMap.IdPrintScreen));
        Assert.False(tab.IsDirty);
    }

    // ── Cancel ──────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_RevertsEditsToBaseline()
    {
        var tab = NewTab(new AppSettings());

        tab.EditEnabled(HotkeyRouteMap.IdPrintScreen, enabled: false);
        Assert.True(tab.IsDirty);

        tab.Cancel();

        Assert.True(tab.IsEnabled(HotkeyRouteMap.IdPrintScreen));
        Assert.False(tab.IsDirty);
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

        Assert.All(tab.GetRows(), r => Assert.NotEqual(HotkeyRegistrationStatus.Disabled, r.Status));
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

        Assert.False(source.IsHotkeyEnabled(HotkeyRouteMap.IdPrintScreen));
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
            HotkeyEnabledStates = new Dictionary<int, bool>
            {
                [HotkeyRouteMap.IdPrintScreen] = false,
            },
        };
        var tab = NewTab(source);

        tab.Reset();
        tab.Cancel();

        Assert.False(tab.IsEnabled(HotkeyRouteMap.IdPrintScreen));
        Assert.False(tab.IsDirty);
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
        => new HotkeysTabSettings(source, new RecordingHotkeyAdapter());

    private static HotkeySpec SpecForId(int id) => HotkeyRouteMap.AllSpecs.Single(s => s.Id == id);

    /// <summary>
    /// Fake Global Hotkey adapter that serves configurable registration results, so
    /// display behavior can be verified without a Win32 hotkey manager.
    /// </summary>
    private sealed class RecordingHotkeyAdapter : IGlobalHotkeyAdapter
    {
        private readonly List<HotkeyRegistrationResult> _results = new();

        public IReadOnlyList<HotkeyRegistrationResult> RegistrationResults => _results;

        public void SetResults(params HotkeyRegistrationResult[] results)
        {
            _results.Clear();
            _results.AddRange(results);
        }

        public IReadOnlyList<HotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds)
            => RegistrationResults;
    }
}
