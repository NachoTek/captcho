// BehaviorTabSettingsTests.cs — Headless tests for the Behavior tab editing
// seam (automatic save, Copy Frame, Copy Path).
//
// Verifies the pure C# BehaviorTabSettings collaborator: it loads the persisted
// automatic Export settings into a working snapshot (never mutating the
// source), exposes the three independent toggles, and honors the shared
// snapshot/apply/cancel/reset semantics. The composed Apply/OK flow is covered
// in AutomaticDeliverySettingsSessionIntegrationTests.

using System;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class BehaviorTabSettingsTests
{
    // ── Construction ────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BehaviorTabSettings(null!));
    }

    [Fact]
    public void Constructor_DisplaysPersistedToggles_AndDoesNotMutateSource()
    {
        var source = new AppSettings
        {
            AutomaticExport = new AutomaticExportSettings(true, false, true),
        };

        var tab = new BehaviorTabSettings(source);

        Assert.True(tab.AutoSave);
        Assert.False(tab.AutoCopyFrame);
        Assert.True(tab.AutoCopyPath);
        // Source untouched.
        Assert.False(source.AutomaticExport.AutoCopyFrame);
    }

    // ── Independent edits ───────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryToggleIsIndependentlyEditable(int slot)
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        switch (slot)
        {
            case 0: tab.EditAutoSave(true); break;
            case 1: tab.EditAutoCopyFrame(true); break;
            case 2: tab.EditAutoCopyPath(true); break;
            default:
                tab.EditAutoSave(true);
                tab.EditAutoCopyFrame(true);
                tab.EditAutoCopyPath(true);
                break;
        }

        Assert.Equal(slot == 0 || slot == 3, tab.AutoSave);
        Assert.Equal(slot == 1 || slot == 3, tab.AutoCopyFrame);
        Assert.Equal(slot == 2 || slot == 3, tab.AutoCopyPath);
    }

    [Fact]
    public void ToggleEdits_NeverTouchOtherSettingsSlices()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Preserved",
            FilenameTemplate = "preserved-<title>",
            AnnotationEnabled = false,
        };
        var tab = new BehaviorTabSettings(source);

        tab.EditAutoSave(true);
        tab.EditAutoCopyFrame(true);
        tab.EditAutoCopyPath(true);

        Assert.Equal(@"D:\Preserved", source.SaveLocation);
        Assert.Equal("preserved-<title>", source.FilenameTemplate);
        Assert.False(source.AnnotationEnabled);
    }

    // ── IsDirty / Cancel / Reset ────────────────────────────────────────

    [Fact]
    public void IsDirty_NoEdits_False()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_AfterAnyToggleEdit_True()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditAutoCopyPath(true);

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Cancel_RevertsWorkingStateToBaseline()
    {
        var source = new AppSettings
        {
            AutomaticExport = new AutomaticExportSettings(false, false, false),
        };
        var tab = new BehaviorTabSettings(source);
        tab.EditAutoSave(true);
        tab.EditAutoCopyFrame(true);

        tab.Cancel();

        Assert.False(tab.AutoSave);
        Assert.False(tab.AutoCopyFrame);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Reset_RestoresDefaultsOnBehaviorSlice()
    {
        var source = new AppSettings
        {
            AutomaticExport = new AutomaticExportSettings(true, true, true),
        };
        var tab = new BehaviorTabSettings(source);

        tab.Reset();

        Assert.Equal(AutomaticExportSettings.WithDefaults().AutoSave, tab.AutoSave);
        Assert.Equal(AutomaticExportSettings.WithDefaults().AutoCopyFrame, tab.AutoCopyFrame);
        Assert.Equal(AutomaticExportSettings.WithDefaults().AutoCopyPath, tab.AutoCopyPath);
    }

    [Fact]
    public void Reset_LeavesBaselineSoCancelRevertsTheReset()
    {
        // Match sibling-tab semantics: Reset does not advance the baseline, so
        // Cancel after Reset returns to the persisted value.
        var source = new AppSettings
        {
            AutomaticExport = new AutomaticExportSettings(true, false, false),
        };
        var tab = new BehaviorTabSettings(source);

        tab.Reset();
        tab.Cancel();

        Assert.True(tab.AutoSave);
        Assert.False(tab.AutoCopyFrame);
    }

    // ── WriteInto (merge) ───────────────────────────────────────────────

    [Fact]
    public void WriteInto_WritesBehaviorSlice_LeavingOthersUntouched()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditAutoSave(true);
        tab.EditAutoCopyPath(true);

        var target = new AppSettings
        {
            SaveLocation = @"D:\Preserved",
            FilenameTemplate = "preserved-<title>",
            AnnotationEnabled = false,
        };

        tab.WriteInto(target);

        Assert.True(target.AutomaticExport.AutoSave);
        Assert.False(target.AutomaticExport.AutoCopyFrame);
        Assert.True(target.AutomaticExport.AutoCopyPath);
        // Other slices preserved.
        Assert.Equal(@"D:\Preserved", target.SaveLocation);
        Assert.Equal("preserved-<title>", target.FilenameTemplate);
        Assert.False(target.AnnotationEnabled);
    }

    [Fact]
    public void WriteInto_NullTarget_Throws()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        Assert.Throws<ArgumentNullException>(() => tab.WriteInto(null!));
    }

    // ── Validity ────────────────────────────────────────────────────────

    [Fact]
    public void IsValid_IsAlwaysTrue_BooleansHaveNoInvalidValue()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditAutoSave(true);
        tab.EditAutoCopyFrame(true);
        tab.EditAutoCopyPath(true);

        Assert.True(tab.IsValid);
        Assert.Null(tab.FirstError);
    }
}
