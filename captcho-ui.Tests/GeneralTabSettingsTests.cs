// GeneralTabSettingsTests.cs — Headless tests for the General tab editing seam.
//
// Verifies the pure C# GeneralTabSettings coordinator: it loads the persisted
// Save Location and Filename Template into a working snapshot (never mutating the
// source), previews the expanded filename, lists supported placeholders, validates
// inline (relative save paths rejected; empty templates rejected), routes folder
// picker outcomes, and drives the Apply/OK/Cancel settings session through an
// injected save delegate.

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

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        Assert.Equal("my-custom-template", tab.FilenameTemplate);
        Assert.Equal("my-custom-template", source.FilenameTemplate);
    }

    [Fact]
    public void Constructor_DisplaysDefaultTemplateWhenPersistedIsNull_AndLeavesSourceNull()
    {
        var source = new AppSettings { FilenameTemplate = null };

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

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

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        Assert.Equal("captcho_2024-01-15_143045.png", tab.FilenameTemplatePreview);
    }

    [Fact]
    public void EditFilenameTemplate_UpdatesValueAndPreview()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());
        tab.EditFilenameTemplate("<title>_<yyyy>");

        Assert.Equal("<title>_<yyyy>", tab.FilenameTemplate);
        Assert.Equal("Screenshot_2024.png", tab.FilenameTemplatePreview);
    }

    // ── Empty/whitespace templates are invalid and block Apply/OK ───────

    [Fact]
    public void EditFilenameTemplate_ToEmpty_MarksInvalidAndBlocksActions()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());
        tab.EditFilenameTemplate("");

        Assert.False(tab.IsValid);
        Assert.NotNull(tab.FilenameTemplateError);
        Assert.False(tab.CanApply);
        Assert.False(tab.CanConfirm);
    }

    [Fact]
    public void EditFilenameTemplate_ToWhitespace_MarksInvalidAndBlocksActions()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());
        tab.EditFilenameTemplate("   ");

        Assert.False(tab.IsValid);
        Assert.False(tab.CanApply);
        Assert.False(tab.CanConfirm);
    }

    [Fact]
    public void ValidTemplate_IsValidAndAllowsActions()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        Assert.True(tab.IsValid);
        Assert.Null(tab.FilenameTemplateError);
        Assert.True(tab.CanApply);
        Assert.True(tab.CanConfirm);
    }

    // ── Apply persists a valid edit and keeps the window open ───────────

    [Fact]
    public void Apply_ValidEdit_PersistsAndKeepsWindowOpen_AndUpdatesBaseline()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditFilenameTemplate("custom-<yyyy>");
        Assert.True(tab.IsDirty);

        var result = tab.Apply();

        Assert.True(result.Success);
        Assert.False(result.ShouldClose); // Apply keeps the window open
        Assert.Equal(1, recorder.CallCount);
        Assert.Equal("custom-<yyyy>", recorder.LastSaved!.FilenameTemplate);
        Assert.False(tab.IsDirty); // baseline updated to the applied value
    }

    [Fact]
    public void Apply_OnInvalidTemplate_DoesNotPersist_AndReportsError()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditFilenameTemplate("   ");
        var result = tab.Apply();

        Assert.False(result.Success);
        Assert.False(result.ShouldClose);
        Assert.Equal(0, recorder.CallCount); // nothing persisted
    }

    [Fact]
    public void Apply_OnSaveFailure_StaysOpenWithoutReportingSuccess()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };
        var recorder = new RecordingSave(FailedSave("WriteTemp", "Access is denied"));
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditFilenameTemplate("apply-<yyyy>");
        var result = tab.Apply();

        Assert.False(result.Success);
        Assert.False(result.ShouldClose); // failure keeps the window open
        Assert.Contains("Access is denied", result.Message);
        Assert.True(tab.IsDirty); // baseline not updated — edit still uncommitted
    }

    // ── OK persists and closes only on a successful save ────────────────

    [Fact]
    public void Confirm_ValidEdit_PersistsAndClosesOnSuccess()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditFilenameTemplate("ok-<yyyy>");
        var result = tab.Confirm();

        Assert.True(result.Success);
        Assert.True(result.ShouldClose); // OK closes after a successful save
        Assert.Equal(1, recorder.CallCount);
        Assert.Equal("ok-<yyyy>", recorder.LastSaved!.FilenameTemplate);
    }

    [Fact]
    public void Confirm_OnSaveFailure_StaysOpenWithoutReportingSuccess()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };
        var recorder = new RecordingSave(FailedSave("WriteTemp", "Access is denied"));
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditFilenameTemplate("ok-<yyyy>");
        var result = tab.Confirm();

        Assert.False(result.Success);
        Assert.False(result.ShouldClose); // failure does not close the window
        Assert.Contains("Access is denied", result.Message);
    }

    [Fact]
    public void Confirm_OnInvalidTemplate_DoesNotPersistOrClose()
    {
        var source = new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate };
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditFilenameTemplate("");
        var result = tab.Confirm();

        Assert.False(result.Success);
        Assert.False(result.ShouldClose);
        Assert.Equal(0, recorder.CallCount);
    }

    // ── Cancel discards edits since the last successful Apply ───────────

    [Fact]
    public void Cancel_DiscardsEditsSinceOpen_AndDoesNotPersist()
    {
        var source = new AppSettings { FilenameTemplate = "original-template" };
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditFilenameTemplate("thrown-away-edit");
        Assert.True(tab.IsDirty);

        tab.Cancel();

        Assert.Equal("original-template", tab.FilenameTemplate);
        Assert.False(tab.IsDirty);
        Assert.Equal(0, recorder.CallCount); // persisted settings unchanged
    }

    [Fact]
    public void Cancel_AfterApply_DiscardsEditsSinceLastApply()
    {
        var source = new AppSettings { FilenameTemplate = "original-template" };
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditFilenameTemplate("applied-edit");
        tab.Apply(); // baseline now "applied-edit"

        tab.EditFilenameTemplate("second-thrown-away-edit");
        Assert.True(tab.IsDirty);

        tab.Cancel();

        // Reverts to the last applied value, not the original.
        Assert.Equal("applied-edit", tab.FilenameTemplate);
        Assert.False(tab.IsDirty);
    }

    // ── Opening displays the persisted Save Location without mutating the source ──

    [Fact]
    public void Constructor_DisplaysPersistedSaveLocation_AndDoesNotMutateSource()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

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

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        Assert.Equal(ExportDefaults.DefaultSaveDirectory, tab.SaveLocation);
        Assert.Null(source.SaveLocation);
    }

    // ── Editing the Save Location updates the working value ───────────

    [Fact]
    public void EditSaveLocation_UpdatesWorkingValue()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());
        tab.EditSaveLocation(@"E:\NewCaptures");

        Assert.Equal(@"E:\NewCaptures", tab.SaveLocation);
    }

    [Fact]
    public void EditSaveLocation_Null_Throws()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults(), _ => SuccessfulSave());

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

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());
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

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());
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

        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());
        tab.ApplyFolderPickerResult("   ");

        Assert.Equal(@"D:\Captures", tab.SaveLocation);
    }

    // ── Save Location validation: relative paths invalid; absolute/empty valid ──

    [Fact]
    public void EditSaveLocation_ToRelative_MarksInvalidAndBlocksActions()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults(), _ => SuccessfulSave());
        tab.EditSaveLocation("relative/path");

        Assert.False(tab.IsSaveLocationValid);
        Assert.NotNull(tab.SaveLocationError);
        Assert.False(tab.IsValid);
        Assert.False(tab.CanApply);
        Assert.False(tab.CanConfirm);
    }

    [Fact]
    public void EditSaveLocation_ToAbsolute_StaysValid()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults(), _ => SuccessfulSave());
        tab.EditSaveLocation(@"D:\Screens");

        Assert.True(tab.IsSaveLocationValid);
        Assert.Null(tab.SaveLocationError);
    }

    [Fact]
    public void EditSaveLocation_ToEmpty_StaysValid_AllowingDefaultFallback()
    {
        var tab = new GeneralTabSettings(AppSettings.WithDefaults(), _ => SuccessfulSave());
        tab.EditSaveLocation("");

        Assert.True(tab.IsSaveLocationValid);
        Assert.Null(tab.SaveLocationError);
    }

    // ── Save Location participates in the Apply/OK/Cancel session ──────

    [Fact]
    public void IsDirty_TrueWhenOnlySaveLocationChanged()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };
        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        tab.EditSaveLocation(@"E:\Elsewhere");

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Apply_ValidSaveLocationEdit_PersistsNewLocation()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditSaveLocation(@"E:\NewCaptures");
        var result = tab.Apply();

        Assert.True(result.Success);
        Assert.Equal(@"E:\NewCaptures", recorder.LastSaved!.SaveLocation);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Apply_RelativeSaveLocation_DoesNotPersist()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(AppSettings.WithDefaults(), recorder.Save);

        tab.EditSaveLocation("relative/path");
        var result = tab.Apply();

        Assert.False(result.Success);
        Assert.Equal(0, recorder.CallCount);
    }

    [Fact]
    public void Cancel_DiscardsSaveLocationEdits()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.EditSaveLocation(@"E:\ThrownAway");
        tab.Cancel();

        Assert.Equal(@"D:\Captures", tab.SaveLocation);
        Assert.False(tab.IsDirty);
        Assert.Equal(0, recorder.CallCount);
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
        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

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
        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        tab.Reset();

        // Preview mirrors the value the constructor-derived default produces.
        var expected = new GeneralTabSettings(
            new AppSettings { FilenameTemplate = ExportDefaults.DefaultFilenameTemplate },
            _ => SuccessfulSave());
        Assert.Equal(expected.FilenameTemplatePreview, tab.FilenameTemplatePreview);
    }

    [Fact]
    public void Reset_ClearsValidationErrorsAndEnablesActions()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
        };
        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        // Make an invalid edit first so an error is showing.
        tab.EditSaveLocation("relative/path");
        Assert.False(tab.IsValid);
        Assert.NotNull(tab.SaveLocationError);

        tab.Reset();

        Assert.True(tab.IsValid);
        Assert.Null(tab.FilenameTemplateError);
        Assert.Null(tab.SaveLocationError);
        Assert.True(tab.CanApply);
        Assert.True(tab.CanConfirm);
    }

    [Fact]
    public void Reset_DoesNotPersist()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.Reset();

        Assert.Equal(0, recorder.CallCount);
        Assert.Null(recorder.LastSaved);
    }

    [Fact]
    public void Reset_DoesNotMutateSourceSettings()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

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
        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        tab.Reset();
        tab.Cancel();

        // Cancel reverts to the baseline (the pre-reset editing session), not defaults.
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
        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        tab.Reset();

        Assert.True(tab.IsDirty); // reset is a pending change awaiting Apply/OK
    }

    [Fact]
    public void Reset_WhenBaselineAlreadyDefaults_IsNotDirty()
    {
        var source = AppSettings.WithDefaults();
        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

        tab.Reset();

        Assert.False(tab.IsDirty); // already at defaults — nothing pending
    }

    [Fact]
    public void Cancel_AfterReset_PreservesSettingsFromBeforeReset()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Original",
            FilenameTemplate = "original-<yyyy>",
        };
        var recorder = new RecordingSave(SuccessfulSave());
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.Reset();
        tab.Cancel();

        Assert.Equal(@"D:\Original", tab.SaveLocation);
        Assert.Equal("original-<yyyy>", tab.FilenameTemplate);
        Assert.False(tab.IsDirty);
        Assert.Equal(0, recorder.CallCount); // nothing persisted by Reset or Cancel
    }

    [Fact]
    public void Apply_AfterReset_PersistsDefaults()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.Reset();
        var result = tab.Apply();

        Assert.True(result.Success);
        Assert.False(result.ShouldClose); // Apply keeps the window open
        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, recorder.LastSaved!.SaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, recorder.LastSaved!.FilenameTemplate);
        Assert.False(tab.IsDirty); // baseline advanced to defaults
    }

    [Fact]
    public void Confirm_AfterReset_PersistsDefaultsAndSignalsClose()
    {
        var recorder = new RecordingSave(SuccessfulSave());
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source, recorder.Save);

        tab.Reset();
        var result = tab.Confirm();

        Assert.True(result.Success);
        Assert.True(result.ShouldClose); // OK closes on success
        Assert.Equal(1, recorder.CallCount);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, recorder.LastSaved!.FilenameTemplate);
    }

    [Fact]
    public void Reset_IsIdempotent()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Captures",
            FilenameTemplate = "custom-<title>",
        };
        var tab = new GeneralTabSettings(source, _ => SuccessfulSave());

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
        Assert.Throws<ArgumentNullException>(() =>
            new GeneralTabSettings(null!, _ => SuccessfulSave()));
    }

    [Fact]
    public void Constructor_NullSave_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GeneralTabSettings(AppSettings.WithDefaults(), null!));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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
    /// Records save calls and returns a forced result. Used to observe the
    /// session's persistence behavior without touching the filesystem.
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
}
