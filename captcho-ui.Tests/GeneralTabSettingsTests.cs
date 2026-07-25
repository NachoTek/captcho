// GeneralTabSettingsTests.cs — Headless tests for the General tab editing seam.
//
// Verifies the pure C# GeneralTabSettings coordinator: it loads the persisted
// Filename Template into a working snapshot (never mutating the source), previews
// the expanded filename, lists supported placeholders, validates inline, and
// drives the Apply/OK/Cancel settings session through an injected save delegate.

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
