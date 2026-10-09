// GlobalHotkeyRemapTabSettingsTests.cs — Headless tests for the Global Hotkeys
// tab recording seam (issue #51).
//
// Verifies the GlobalHotkeyTabSettings editing slice for remapping: recording a
// Full Desktop combination inline updates the working binding without rejecting
// suspected conflicts (warning only, value kept), dirty tracking across record
// and cancel/revert, Reset restoring the default binding, WriteInto persisting
// the recorded binding (and normalizing an all-default map away), and GetRows
// displaying the working binding name.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class GlobalHotkeyRemapTabSettingsTests
{
    private static readonly HotkeyBinding CtrlF5 = new(0x0002, 0x74);
    private static readonly HotkeyBinding AltS = new(0x0001, 0x53);

    // ── Recording a binding inline ─────────────────────────────────────

    [Fact]
    public void RecordBinding_FullDesktop_UpdatesTheWorkingBinding()
    {
        var tab = NewTab(new AppSettings());

        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);

        Assert.Equal(CtrlF5, tab.WorkingBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen));
    }

    [Fact]
    public void RecordBinding_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings();

        var tab = NewTab(source);
        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);

        Assert.Null(source.GlobalHotkeyBindings);
    }

    [Fact]
    public void RecordBinding_IsDirty()
    {
        var tab = NewTab(new AppSettings());

        Assert.False(tab.IsDirty);

        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void RecordBinding_DoesNotAffectEnabledStates()
    {
        var tab = NewTab(new AppSettings());

        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);

        Assert.True(tab.IsEnabled(GlobalHotkeyRouteMap.IdShiftPrintScreen));
    }

    // ── Conflict detection: warn on record, never reject ───────────────

    [Fact]
    public void RecordBinding_CombinationMatchingAnotherEnabledGlobalHotkey_WarnsWithoutRejecting()
    {
        var tab = NewTab(new AppSettings());

        // Ctrl+F5 is a fresh combination — no warning.
        Assert.Null(tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5));

        // Win+Print Screen is Active Window's default — recording it for Full
        // Desktop is a suspected conflict: warned, but kept.
        var legacyWin = new HotkeyBinding(GlobalHotkeyRouteMap.MOD_WIN, GlobalHotkeyRouteMap.VK_SNAPSHOT);
        var warning = tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyWin);

        Assert.NotNull(warning);
        Assert.Contains("Win + Print Screen", warning);
        Assert.Equal(legacyWin, tab.WorkingBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen));
    }

    [Fact]
    public void RecordBinding_CombinationMatchingADisabledGlobalHotkey_NoWarning()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.ActiveWindow] = false,
            },
        };
        var tab = NewTab(source);

        var legacyWin = new HotkeyBinding(GlobalHotkeyRouteMap.MOD_WIN, GlobalHotkeyRouteMap.VK_SNAPSHOT);
        var warning = tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyWin);

        Assert.Null(warning);
    }

    [Fact]
    public void RecordBinding_ConflictWarning_IsSurfacedOnTheRow()
    {
        var tab = NewTab(new AppSettings());
        var legacyWin = new HotkeyBinding(GlobalHotkeyRouteMap.MOD_WIN, GlobalHotkeyRouteMap.VK_SNAPSHOT);

        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyWin);
        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);

        Assert.NotNull(row.BindingWarning);
        Assert.Contains("Win + Print Screen", row.BindingWarning);
    }

    [Fact]
    public void RecordBinding_ClearsTheWarningWhenTheConflictIsGone()
    {
        var tab = NewTab(new AppSettings());
        var legacyWin = new HotkeyBinding(GlobalHotkeyRouteMap.MOD_WIN, GlobalHotkeyRouteMap.VK_SNAPSHOT);

        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyWin);
        var warned = tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);

        Assert.Null(warned);
        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Null(row.BindingWarning);
    }

    [Fact]
    public void RecordBinding_ConflictIsSymmetric_BothRowsAreWarned()
    {
        var tab = NewTab(new AppSettings());
        var legacyWin = new HotkeyBinding(GlobalHotkeyRouteMap.MOD_WIN, GlobalHotkeyRouteMap.VK_SNAPSHOT);

        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyWin);

        // Both the recorded row and the row it collides with carry the warning —
        // the conflict is mutual.
        var rows = tab.GetRows();
        Assert.NotNull(rows.Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen).BindingWarning);
        Assert.NotNull(rows.Single(r => r.Id == GlobalHotkeyRouteMap.IdWinPrintScreen).BindingWarning);
    }

    [Fact]
    public void Cancel_ClearsConflictWarningsWithTheSnapshot()
    {
        var tab = NewTab(new AppSettings());
        var legacyWin = new HotkeyBinding(GlobalHotkeyRouteMap.MOD_WIN, GlobalHotkeyRouteMap.VK_SNAPSHOT);
        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyWin);
        Assert.NotNull(tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen).BindingWarning);

        tab.Cancel();

        Assert.All(tab.GetRows(), r => Assert.Null(r.BindingWarning));
    }

    // ── Display: the working binding ───────────────────────────────────

    [Fact]
    public void GetRows_BindingReflectsTheRecordedCombination()
    {
        var persisted = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = AltS,
            },
        };
        var tab = NewTab(persisted);

        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);

        Assert.Equal("Alt + S", row.Binding);
    }

    [Fact]
    public void GetRows_BindingForDefaults_MatchesTheLegacyName()
    {
        var tab = NewTab(new AppSettings());

        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);

        Assert.Equal("Shift + Print Screen", row.Binding);
    }

    // ── Cancel / Reset ─────────────────────────────────────────────────

    [Fact]
    public void Cancel_RevertsARecordedBindingToTheBaseline()
    {
        var tab = NewTab(new AppSettings());

        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);
        tab.Cancel();

        Assert.Equal(
            new HotkeyBinding(GlobalHotkeyRouteMap.MOD_SHIFT, GlobalHotkeyRouteMap.VK_SNAPSHOT),
            tab.WorkingBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen));
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Cancel_RestoresAPersistedCustomBinding()
    {
        var persisted = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = AltS,
            },
        };
        var tab = NewTab(persisted);

        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);
        tab.Cancel();

        Assert.Equal(AltS, tab.WorkingBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen));
    }

    [Fact]
    public void Reset_RestoresTheDefaultBinding()
    {
        var persisted = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = AltS,
            },
        };
        var tab = NewTab(persisted);

        tab.Reset();

        Assert.Equal(
            new HotkeyBinding(GlobalHotkeyRouteMap.MOD_SHIFT, GlobalHotkeyRouteMap.VK_SNAPSHOT),
            tab.WorkingBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen));
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Reset_ClearsRecordedConflicts()
    {
        var tab = NewTab(new AppSettings());
        var legacyWin = new HotkeyBinding(GlobalHotkeyRouteMap.MOD_WIN, GlobalHotkeyRouteMap.VK_SNAPSHOT);
        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyWin);

        tab.Reset();

        var row = tab.GetRows().Single(r => r.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen);
        Assert.Null(row.BindingWarning);
    }

    // ── WriteInto / persistence slice ──────────────────────────────────

    [Fact]
    public void WriteInto_RecordedBinding_IsPersisted()
    {
        var tab = NewTab(new AppSettings());
        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        Assert.Equal(CtrlF5, target.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.FullDesktop));
    }

    [Fact]
    public void WriteInto_AllDefaultBindings_PersistCleanNullMap()
    {
        var tab = NewTab(new AppSettings());

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        Assert.Null(target.GlobalHotkeyBindings);
    }

    [Fact]
    public void WriteInto_PreservesPersistedBindingsForOtherRoutes()
    {
        var persisted = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.ActiveWindow] = AltS,
            },
        };
        var tab = NewTab(persisted);

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        Assert.Equal(AltS, target.EffectiveGlobalHotkeyBinding(GlobalHotkeyRoute.ActiveWindow));
    }

    [Fact]
    public void WriteInto_PreservesEnabledStatesSlice()
    {
        var source = new AppSettings
        {
            GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool>
            {
                [GlobalHotkeyRoute.CurrentMonitor] = false,
            },
        };
        var tab = NewTab(source);
        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        Assert.False(target.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));
        Assert.True(target.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.ActiveWindow));
    }

    [Fact]
    public void WriteInto_DropsBindingsThatReturnedToDefault()
    {
        var persisted = new AppSettings
        {
            GlobalHotkeyBindings = new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
            {
                [GlobalHotkeyRoute.FullDesktop] = AltS,
            },
        };
        var tab = NewTab(persisted);
        var legacyFullDesktop =
            new HotkeyBinding(GlobalHotkeyRouteMap.MOD_SHIFT, GlobalHotkeyRouteMap.VK_SNAPSHOT);
        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, legacyFullDesktop);

        var target = AppSettings.WithDefaults();

        tab.WriteInto(target);

        Assert.Null(target.GlobalHotkeyBindings);
    }

    // ── Validation ─────────────────────────────────────────────────────

    [Fact]
    public void IsValid_RecordedRealCombination_StaysValid()
    {
        var tab = NewTab(new AppSettings());

        tab.RecordBinding(GlobalHotkeyRouteMap.IdShiftPrintScreen, CtrlF5);

        Assert.True(tab.IsValid);
        Assert.Null(tab.FirstError);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static GlobalHotkeyTabSettings NewTab(AppSettings source)
        => new GlobalHotkeyTabSettings(source, new RecordingGlobalHotkeyAdapter());

    private sealed class RecordingGlobalHotkeyAdapter : IGlobalHotkeyAdapter
    {
        private readonly List<GlobalHotkeyRegistrationResult> _results = new();

        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults => _results;

        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplySettings(AppSettings settings)
            => RegistrationResults;
    }
}
