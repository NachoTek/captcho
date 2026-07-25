// GeneralTabSettingsTests.cs — Headless tests for the General tab editing seam.
//
// Verifies the pure C# GeneralTabSettings collaborator: it loads the persisted
// Save Location and Filename Template into a working snapshot (never mutating the
// source), previews the expanded filename, lists supported placeholders, validates
// inline (relative save paths rejected; empty templates rejected), routes folder
// picker outcomes, and reverts/resets its working state. Persistence is no longer
// this tab's job — it writes its working slice via WriteInto and advances its
// baseline via Commit at the SettingsSession's direction, so the composed Apply/OK
// flow is covered in SettingsSessionTests.

using System;
using System.Collections.Generic;
using System.IO;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class GeneralTabSettingsTests
{
    // ── Opening displays the persisted template without mutating the source ──

    [Fact]
    public void Constructor_DisplaysPersistedTemplate_AndDoesNotMutateSource()
    {
        var source = new AppSettings { FilenameTemplate = "my-custom-template" };

        var tab = new GeneralTabSettings(source);

        Assert.Equal("my-custom-template", tab.FilenameTemplate);
        Assert.Equal("my-custom-template", source.FilenameTemplate);
    }

    [Fact]
    public void Constructor_DisplaysDefaultTemplateWhenPersistedIsNull_AndLeavesSourceNull()
    {
        var source = new AppSettings { FilenameTemplate = null };

        var tab = new GeneralTabSettings(source);

        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, tab.FilenameTemplate);
        Assert.Null(source.FilenameTemplate);
    }

    // ── Supported placeholders are listed for reference ─────────────────

    [Fact]
    public void SupportedPlaceholders_IncludesCoreDateAndTimeTokens()
    {
        var placeholders = GeneralTabSettings.SupportedPlaceholders;

        var tokens = new HashSet<string>();
        foreach (var p in placeholders)
            tokens.Add(p.Token);

        Assert.Contains("<yyyy>", tokens);
        Assert.Contains("<MM>", tokens);
        Assert.Contains("<dd>", tokens);
        Assert.Contains("<hh>", tokens);
        Assert.Contains("<mm>", tokens);
        Assert.Contains("<ss>", tokens);
    }

    [Fact]
    public void SupportedPlaceholders_IncludesTitleAndSequenceTokens()
    {
        var placeholders = GeneralTabSettings.SupportedPlaceholders;

        var tokens = new HashSet<string>();
        foreach (var p in placeholders)
            tokens.Add(p.Token);

        Assert.Contains("<title>", tokens);
        Assert.Contains("<#>", tokens);
    }

    // ── Editing updates a representative filename preview ───────────────

    [Fact]
    public void Preview_DefaultTemplate_ShowsRepresentativeExpandedFilename()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };

        var tab = new GeneralTabSettings(source);

        Assert.Equal("captcho_2024-01-15_143045.png", tab.FilenameTemplatePreview);
    }

    [Fact]
    public void EditFilenameTemplate_UpdatesValueAndPreview()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };

        var tab = new GeneralTabSettings(source);
        tab.EditFilenameTemplate("<title>_<yyyy>");

        Assert.Equal("<title>_<yyyy>", tab.FilenameTemplate);
        Assert.Equal("Screenshot_2024.png", tab.FilenameTemplatePreview);
    }

    // ── Empty/whitespace templates are invalid ──────────────────────────

    [Fact]
    public void EditFilenameTemplate_ToEmpty_MarksInvalid()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };

        var tab = new GeneralTabSettings(source);
        tab.EditFilenameTemplate("");

        Assert.False(tab.IsValid);
        Assert.NotNull(tab.FilenameTemplateError);
        Assert.NotNull(tab.FirstError);
    }

    [Fact]
    public void EditFilenameTemplate_ToWhitespace_MarksInvalid()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };

        var tab = new GeneralTabSettings(source);
        tab.EditFilenameTemplate("   ");

        Assert.False(tab.IsValid);
    }

    [Fact]
    public void ValidTemplate_IsValidWithNoError()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };

        var tab = new GeneralTabSettings(source);

        Assert.True(tab.IsValid);
        Assert.Null(tab.FilenameTemplateError);
        Assert.Null(tab.FirstError);
    }

    // ── Save Location editing and display ───────────────────────────────

    [Fact]
    public void Constructor_DisplaysPersistedSaveLocation_AndDoesNotMutateSource()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };

        var tab = new GeneralTabSettings(source);

        Assert.Equal(@"D:\Captures", tab.SaveLocation);
        Assert.Equal(@"D:\Captures", source.SaveLocation);
    }

    [Fact]
    public void Constructor_DisplaysDefaultSaveLocationWhenPersistedIsNull_AndLeavesSourceNull()
    {
        var source = new AppSettings
        {
            SaveLocation = null,
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };

        var tab = new GeneralTabSettings(source);

        Assert.Equal(ExportDefaults.DefaultSaveDirectory, tab.SaveLocation);
        Assert.Null(source.SaveLocation);
    }

    [Fact]
    public void EditSaveLocation_UpdatesWorkingValue()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };

        var tab = new GeneralTabSettings(source);
        tab.EditSaveLocation(@"E:\NewCaptures");

        Assert.Equal(@"E:\NewCaptures", tab.SaveLocation);
    }

    [Fact]
    public void EditSaveLocation_Null_Throws()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults());

        Assert.Throws<ArgumentNullException>(() => tab.EditSaveLocation(null!));
    }

    // ── Folder picker outcomes route through the seam ──────────────────

    [Fact]
    public void ApplyFolderPickerResult_WithSelection_UpdatesWorkingValue()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };

        var tab = new GeneralTabSettings(source);
        tab.ApplyFolderPickerResult(@"F:\Picked");

        Assert.Equal(@"F:\Picked", tab.SaveLocation);
    }

    [Fact]
    public void ApplyFolderPickerResult_WithNull_LeavesEditUnchanged()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };

        var tab = new GeneralTabSettings(source);
        tab.ApplyFolderPickerResult(null);

        Assert.Equal(@"D:\Captures", tab.SaveLocation);
    }

    [Fact]
    public void ApplyFolderPickerResult_WithEmpty_LeavesEditUnchanged()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };

        var tab = new GeneralTabSettings(source);
        tab.ApplyFolderPickerResult("   ");

        Assert.Equal(@"D:\Captures", tab.SaveLocation);
    }

    // ── Save Location validation: relative paths invalid; absolute/empty valid ──

    [Fact]
    public void EditSaveLocation_ToRelative_MarksInvalid()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults());
        tab.EditSaveLocation("relative/path");

        Assert.NotNull(tab.SaveLocationError);
        Assert.False(tab.IsValid);
    }

    [Fact]
    public void EditSaveLocation_ToAbsolute_StaysValid()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults());
        tab.EditSaveLocation(@"D:\Screens");

        Assert.Null(tab.SaveLocationError);
    }

    [Fact]
    public void EditSaveLocation_ToEmpty_StaysValid_AllowingDefaultFallback()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults());
        tab.EditSaveLocation("");

        Assert.Null(tab.SaveLocationError);
    }

    // ── Dirty tracking ──────────────────────────────────────────────────

    [Fact]
    public void IsDirty_FalseOnFreshConstruct()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults());

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_TrueWhenOnlySaveLocationChanged()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };
        var tab = new GeneralTabSettings(source);

        tab.EditSaveLocation(@"E:\Elsewhere");

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_TrueAfterTemplateEdit()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };
        var tab = new GeneralTabSettings(source);

        tab.EditFilenameTemplate("custom-<yyyy>");

        Assert.True(tab.IsDirty);
    }

    // ── WriteInto merges only this tab's slice ──────────────────────────

    [Fact]
    public void WriteInto_WritesSaveLocationAndTemplate_WithoutTouchingGlobalHotkeys()
    {
        var tab = new GeneralTabSettings(new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        });
        var target = AppSettings.WithDefaults();
        target.GlobalHotkeyEnabledStates = new Dictionary<GlobalHotkeyRoute, bool> { [GlobalHotkeyRoute.CurrentMonitor] = false };

        tab.WriteInto(target);

        Assert.Equal(@"D:\Captures", target.SaveLocation);
        Assert.Equal("custom-<title>", target.FilenameTemplate);
        // Global Hotkey slice owned by another tab is preserved.
        Assert.False(target.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));
    }

    [Fact]
    public void WriteInto_Null_Throws()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults());

        Assert.Throws<ArgumentNullException>(() => tab.WriteInto(null!));
    }

    // ── Commit advances the baseline so Cancel no longer reverts ────────

    [Fact]
    public void Commit_AdvancesBaselineSoCancelKeepsTheCommittedValue()
    {
        var source = new AppSettings { FilenameTemplate = "original-<yyyy>" };
        var tab = new GeneralTabSettings(source);

        tab.EditFilenameTemplate("committed-<yyyy>");
        tab.Commit();
        tab.Cancel();

        Assert.Equal("committed-<yyyy>", tab.FilenameTemplate);
        Assert.False(tab.IsDirty);
    }

    // ── Cancel discards edits since the last Commit ─────────────────────

    [Fact]
    public void Cancel_DiscardsEditsSinceOpen()
    {
        var source = new AppSettings { FilenameTemplate = "original-template" };
        var tab = new GeneralTabSettings(source);

        tab.EditFilenameTemplate("thrown-away-edit");
        Assert.True(tab.IsDirty);

        tab.Cancel();

        Assert.Equal("original-template", tab.FilenameTemplate);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Cancel_AfterCommit_DiscardsEditsSinceLastCommit()
    {
        var source = new AppSettings { FilenameTemplate = "original-template" };
        var tab = new GeneralTabSettings(source);

        tab.EditFilenameTemplate("committed-edit");
        tab.Commit();

        tab.EditFilenameTemplate("second-thrown-away-edit");
        Assert.True(tab.IsDirty);

        tab.Cancel();

        Assert.Equal("committed-edit", tab.FilenameTemplate);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Cancel_DiscardsSaveLocationEdits()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };
        var tab = new GeneralTabSettings(source);

        tab.EditSaveLocation(@"E:\ThrownAway");
        tab.Cancel();

        Assert.Equal(@"D:\Captures", tab.SaveLocation);
        Assert.False(tab.IsDirty);
    }

    // ── Reset to defaults: restores the editing session without persisting ──

    [Fact]
    public void Reset_RestoresDefaultSaveLocationAndFilenameTemplate()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Custom\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source);

        tab.Reset();

        Assert.Equal(ExportDefaults.DefaultSaveDirectory, tab.SaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, tab.FilenameTemplate);
    }

    [Fact]
    public void Reset_RefreshesPreviewToTheDefaultTemplate()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Custom\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source);

        tab.Reset();

        var expected = new GeneralTabSettings(
            new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate });
        Assert.Equal(expected.FilenameTemplatePreview, tab.FilenameTemplatePreview);
    }

    [Fact]
    public void Reset_ClearsValidationErrors()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };
        var tab = new GeneralTabSettings(source);

        tab.EditSaveLocation("relative/path");
        Assert.False(tab.IsValid);

        tab.Reset();

        Assert.True(tab.IsValid);
        Assert.Null(tab.FilenameTemplateError);
        Assert.Null(tab.SaveLocationError);
    }

    [Fact]
    public void Reset_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source);

        tab.Reset();

        Assert.Equal(@"D:\Captures", source.SaveLocation);
        Assert.Equal("custom-<title>", source.FilenameTemplate);
    }

    [Fact]
    public void Reset_DoesNotUpdateBaselineSoCancelCanRevertIt()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source);

        tab.Reset();
        tab.Cancel();

        Assert.Equal(@"D:\Captures", tab.SaveLocation);
        Assert.Equal("custom-<title>", tab.FilenameTemplate);
    }

    [Fact]
    public void Reset_WhenBaselineDiffersFromDefaults_IsDirty()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source);

        tab.Reset();

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Reset_WhenBaselineAlreadyDefaults_IsNotDirty()
    {
        var source = AppSettings.WithDefaults();
        var tab = new GeneralTabSettings(source);

        tab.Reset();

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Cancel_AfterReset_PreservesSettingsFromBeforeReset()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Original",
            FilenameTemplate = "original-<yyyy>",
        };
        var tab = new GeneralTabSettings(source);

        tab.Reset();
        tab.Cancel();

        Assert.Equal(@"D:\Original", tab.SaveLocation);
        Assert.Equal("original-<yyyy>", tab.FilenameTemplate);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Reset_IsIdempotent()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source);

        tab.Reset();
        tab.Reset();
        tab.Reset();

        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, tab.FilenameTemplate);
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, tab.SaveLocation);
    }

    // ── Constructor guards ──────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new GeneralTabSettings(null!));
    }
}
