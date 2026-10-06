// CaptureTabSettingsTests.cs — Headless tests for the Capture tab editing seam.
//
// Verifies the pure C# CaptureTabSettings collaborator: it loads the persisted
// CaptureOptions defaults into a working snapshot (never mutating the source),
// exposes the three flags for editing, enforces the decoration/shadow dependency
// at edit time (turning decorations off reconciles shadow immediately so the
// UI can never offer an impossible combination — spec #30), and reverts/resets
// its working state. Persistence is this tab's job only via WriteInto — the
// composed Apply/OK flow is covered in SettingsSessionTests.

using System;
using captcho.Capture;
using Xunit;

namespace captcho.UI.Tests;

public class CaptureTabSettingsTests
{
    [Fact]
    public void AnnotationToggle_IsEditableResettableAndWrittenWithCaptureSettings()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());
        var target = AppSettings.WithDefaults();

        tab.EditAnnotationEnabled(false);
        tab.WriteInto(target);

        Assert.False(tab.AnnotationEnabled);
        Assert.False(target.AnnotationEnabled);
        tab.Reset();
        Assert.True(tab.AnnotationEnabled);
    }

    // ── Construction ────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CaptureTabSettings(null!));
    }

    [Fact]
    public void Constructor_DisplaysPersistedCaptureOptions_AndDoesNotMutateSource()
    {
        var source = new AppSettings
        {
            CaptureOptions = new CaptureOptions(
                IncludePointer: true,
                IncludeDecorations: false,
                IncludeShadow: false),
        };

        var tab = new CaptureTabSettings(source);

        Assert.True(tab.IncludePointer);
        Assert.False(tab.IncludeDecorations);
        Assert.False(tab.IncludeShadow);
        // Source untouched.
        Assert.True(source.CaptureOptions.IncludePointer);
    }

    [Fact]
    public void Constructor_LoadsDefaultsWhenSourceCaptureOptionsIsDefault()
    {
        var source = AppSettings.WithDefaults();

        var tab = new CaptureTabSettings(source);

        Assert.Equal(CaptureOptions.WithDefaults().IncludePointer, tab.IncludePointer);
        Assert.Equal(CaptureOptions.WithDefaults().IncludeDecorations, tab.IncludeDecorations);
        Assert.Equal(CaptureOptions.WithDefaults().IncludeShadow, tab.IncludeShadow);
    }

    // ── Edit pointer ────────────────────────────────────────────────────

    [Fact]
    public void EditIncludePointer_UpdatesValueInWorkingSnapshot()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());

        tab.EditIncludePointer(true);

        Assert.True(tab.IncludePointer);
    }

    [Fact]
    public void EditIncludePointer_DoesNotMutateSource()
    {
        var source = AppSettings.WithDefaults();
        var tab = new CaptureTabSettings(source);

        tab.EditIncludePointer(true);

        Assert.False(source.CaptureOptions.IncludePointer);
    }

    // ── Edit decorations ────────────────────────────────────────────────

    [Fact]
    public void EditIncludeDecorations_UpdatesValueInWorkingSnapshot()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());

        tab.EditIncludeDecorations(false);

        Assert.False(tab.IncludeDecorations);
    }

    [Fact]
    public void EditIncludeDecorations_TurningOff_ForcesShadowOffImmediately()
    {
        // Spec #30: shadow is dependent on decorations. Turning decorations off
        // reconciles shadow immediately so the UI can never show "shadow on
        // without decorations" — even before Apply. The workflow never observes
        // an impossible combination through this tab.
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());

        tab.EditIncludeDecorations(false);

        Assert.False(tab.IncludeShadow);
    }

    [Fact]
    public void EditIncludeDecorations_TurningBackOn_RestoresRememberedShadow()
    {
        // Turning decorations off forces shadow off and remembers that shadow was
        // on; turning decorations back on restores the remembered value, so a
        // decorations round-trip does not silently discard the user's shadow choice.
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());

        tab.EditIncludeDecorations(false);
        tab.EditIncludeDecorations(true);

        Assert.True(tab.IncludeShadow);
    }

    [Fact]
    public void EditIncludeDecorations_RoundTrip_PreservesShadowOffChoice()
    {
        // The remember/restore is symmetric for the other starting point: a user
        // who had shadow off does not get shadow switched on by a decorations
        // round-trip.
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());
        tab.EditIncludeShadow(false);

        tab.EditIncludeDecorations(false);
        tab.EditIncludeDecorations(true);

        Assert.False(tab.IncludeShadow);
    }

    // ── Edit shadow ─────────────────────────────────────────────────────

    [Fact]
    public void EditIncludeShadow_WhenDecorationsOn_UpdatesValueInWorkingSnapshot()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());

        tab.EditIncludeShadow(false);

        Assert.False(tab.IncludeShadow);
    }

    [Fact]
    public void EditIncludeShadow_WhenDecorationsOff_IsRefusedAndStaysOff()
    {
        // Spec #30: when decorations are off, shadow cannot be turned on. The edit
        // is silently refused — the tab never carries an inconsistent combination.
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());
        tab.EditIncludeDecorations(false);

        tab.EditIncludeShadow(true);

        Assert.False(tab.IncludeShadow);
        Assert.False(tab.IncludeDecorations);
    }

    // ── IsDirty / Commit / Cancel / Reset ───────────────────────────────

    [Fact]
    public void IsDirty_NoEdits_False()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_AfterEdit_True()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());

        tab.EditIncludePointer(true);

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Cancel_RevertsWorkingStateToBaseline()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());
        tab.EditIncludePointer(true);

        tab.Cancel();

        Assert.False(tab.IncludePointer);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Reset_RestoresDefaultsOnCaptureOptionsSlice()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());
        tab.EditIncludePointer(true);
        tab.EditIncludeDecorations(false);

        tab.Reset();

        Assert.Equal(CaptureOptions.WithDefaults().IncludePointer, tab.IncludePointer);
        Assert.Equal(CaptureOptions.WithDefaults().IncludeDecorations, tab.IncludeDecorations);
        Assert.Equal(CaptureOptions.WithDefaults().IncludeShadow, tab.IncludeShadow);
    }

    [Fact]
    public void Reset_LeavesBaselineSoCancelRevertsTheReset()
    {
        // Match GeneralTab/GlobalHotkeyTab semantics: Reset does not advance the
        // baseline, so Cancel after Reset returns to the persisted value.
        var source = new AppSettings
        {
            CaptureOptions = new CaptureOptions(
                IncludePointer: true,
                IncludeDecorations: false,
                IncludeShadow: false),
        };
        var tab = new CaptureTabSettings(source);

        tab.Reset();
        tab.Cancel();

        Assert.True(tab.IncludePointer);
        Assert.False(tab.IncludeDecorations);
    }

    // ── WriteInto (merge) ───────────────────────────────────────────────

    [Fact]
    public void WriteInto_WritesCaptureOptionsSlice_LeavingOthersUntouched()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());
        tab.EditIncludePointer(true);
        tab.EditIncludeDecorations(false);
        // tab also forces IncludeShadow off via the decoration/shadow dependency.

        var target = new AppSettings
        {
            SaveLocation = @"D:\Preserved",
            FilenameTemplate = "preserved-<title>",
        };

        tab.WriteInto(target);

        Assert.True(target.CaptureOptions.IncludePointer);
        Assert.False(target.CaptureOptions.IncludeDecorations);
        Assert.False(target.CaptureOptions.IncludeShadow);
        // Other slices preserved.
        Assert.Equal(@"D:\Preserved", target.SaveLocation);
        Assert.Equal("preserved-<title>", target.FilenameTemplate);
    }

    [Fact]
    public void WriteInto_NullTarget_Throws()
    {
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());

        Assert.Throws<ArgumentNullException>(() => tab.WriteInto(null!));
    }

    // ── IsValid is always true (no persisted validity rule) ─────────────

    [Fact]
    public void IsValid_IsAlwaysTrue_BecauseDependencyEnforcedAtEdit()
    {
        // CaptureOptions has no persisted validation rule (the decoration/shadow
        // dependency is enforced at edit time and reconciled on read), so this tab
        // never contributes to the composed Apply/OK gate. Other tabs may still
        // block it; this tab never does.
        var tab = new CaptureTabSettings(AppSettings.WithDefaults());

        Assert.True(tab.IsValid);
        Assert.Null(tab.FirstError);
    }
}
