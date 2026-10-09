// OcrSettingsTests.cs — Configuration and selection tests for the OCR
// language setting (spec #49).
//
// Covers the persisted AppSettings slice (default null, normalization of
// whitespace, JSON round-trip through ConfigurationService, older-files
// compatibility) and the SettingsSession editing seam (Capture tab carries
// the selection, Apply persists it atomically and writes the runtime back so
// the workflow reads the committed tag, Cancel/Reset follow the shared
// snapshot semantics).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Persisted settings slice ────────────────────────────────────────────

public class OcrAppSettingsTests : IDisposable
{
    private readonly string _tempDir;

    public OcrAppSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"captchoOcr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void OcrLanguageTag_DefaultsToNull()
    {
        Assert.Null(AppSettings.WithDefaults().OcrLanguageTag);
        Assert.Null(new AppSettings().OcrLanguageTag);
    }

    [Fact]
    public void Normalized_BlankTagBecomesNull_TrimmedOtherwise()
    {
        Assert.Null(new AppSettings { OcrLanguageTag = "" }.Normalized().OcrLanguageTag);
        Assert.Null(new AppSettings { OcrLanguageTag = "   " }.Normalized().OcrLanguageTag);
        Assert.Equal("de-DE", new AppSettings { OcrLanguageTag = " de-DE " }.Normalized().OcrLanguageTag);
    }

    [Fact]
    public void OcrLanguageTag_RoundTripsThroughConfiguration()
    {
        var configuration = new ConfigurationService(_tempDir);
        var settings = AppSettings.WithDefaults();
        settings.OcrLanguageTag = "en-US";

        Assert.True(configuration.Save(settings).Success);

        var loaded = configuration.Load();
        Assert.True(loaded.Success);
        Assert.Equal("en-US", loaded.Settings.OcrLanguageTag);
    }

    [Fact]
    public void OcrLanguageTag_OlderFilesWithoutTheProperty_LoadAsDefault()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "settings.json"),
            "{\n  \"saveLocation\": \"C:\\\\Captures\",\n  \"filenameTemplate\": \"captcho_<yyyy>\"\n}");

        var loaded = new ConfigurationService(_tempDir).Load();

        Assert.True(loaded.Success);
        Assert.Null(loaded.Settings.OcrLanguageTag);
    }
}

// ── Settings session editing ────────────────────────────────────────────

public class OcrSettingsSessionTests
{
    [Fact]
    public void View_OnOpen_CarriesCommittedOcrLanguage()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.OcrLanguageTag = "de-DE";

        var session = NewSession(runtime);

        Assert.Equal("de-DE", session.View.Capture.OcrLanguageTag);
    }

    [Fact]
    public void EditOcrLanguageTag_UpdatesWorkingSelectionWithoutMutatingRuntime()
    {
        var runtime = AppSettings.WithDefaults();
        var session = NewSession(runtime);

        var view = session.EditOcrLanguageTag("ja");

        Assert.Equal("ja", view.Capture.OcrLanguageTag);
        Assert.Null(runtime.OcrLanguageTag);
    }

    [Fact]
    public void EditOcrLanguageTag_NullOrWhitespace_SelectsDefault()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.OcrLanguageTag = "de-DE";
        var session = NewSession(runtime);

        Assert.Null(session.EditOcrLanguageTag(null).Capture.OcrLanguageTag);
        Assert.Null(session.EditOcrLanguageTag("  ").Capture.OcrLanguageTag);
    }

    [Fact]
    public void Apply_PersistsOcrLanguageAtomicallyAndWritesRuntimeBack()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpOcrHotkeys());

        session.EditOcrLanguageTag("de-DE");
        var view = session.Apply();

        Assert.Equal(1, recorder.SaveCount);
        Assert.Equal("de-DE", recorder.LastSaved!.OcrLanguageTag);
        Assert.Equal("de-DE", runtime.OcrLanguageTag);
        Assert.False(view.StatusIsError);
    }

    [Fact]
    public void Apply_PersistsNullSelection_ClearsPersistedAndRuntimeTag()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.OcrLanguageTag = "de-DE";
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpOcrHotkeys());

        session.EditOcrLanguageTag(null);
        var view = session.Apply();

        Assert.True(view.CanApply);
        Assert.Null(recorder.LastSaved!.OcrLanguageTag);
        Assert.Null(runtime.OcrLanguageTag);
    }

    [Fact]
    public void Cancel_RevertsUnappliedOcrLanguageSelection()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.OcrLanguageTag = "de-DE";
        var session = NewSession(runtime);

        session.EditOcrLanguageTag("en-US");
        var view = session.Cancel();

        Assert.Equal("de-DE", view.Capture.OcrLanguageTag);
        Assert.Equal("de-DE", runtime.OcrLanguageTag);
    }

    [Fact]
    public void Reset_RestoresDefaultSelectionWithoutPersisting()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.OcrLanguageTag = "de-DE";
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpOcrHotkeys());

        var view = session.Reset();

        Assert.Null(view.Capture.OcrLanguageTag);
        Assert.Equal(0, recorder.SaveCount);
        // Baseline untouched: Cancel reverts the reset.
        Assert.Equal("de-DE", session.Cancel().Capture.OcrLanguageTag);
    }

    [Fact]
    public void OcrLanguageEdit_DoesNotTouchOtherCaptureSlice()
    {
        var session = NewSession(AppSettings.WithDefaults());

        var before = session.View.Capture;
        session.EditCaptureIncludePointer(!before.IncludePointer);
        var afterPointer = session.View.Capture;
        session.EditOcrLanguageTag("en-US");
        var afterOcr = session.View.Capture;

        Assert.Equal(!before.IncludePointer, afterPointer.IncludePointer);
        Assert.Equal(before.RememberSelection, afterOcr.RememberSelection);
        Assert.Equal(before.AnnotationEnabled, afterOcr.AnnotationEnabled);
        Assert.Equal("en-US", afterOcr.OcrLanguageTag);
    }

    private static SettingsSession NewSession(AppSettings runtime) =>
        new(runtime, new ConfigurationService(Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}")), new NoOpOcrHotkeys());

    private sealed class RecordingConfiguration : ConfigurationService
    {
        public RecordingConfiguration() : base(Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}")) { }
        public int SaveCount { get; private set; }
        public AppSettings? LastSaved { get; private set; }

        public override ConfigurationSaveResult Save(AppSettings settings)
        {
            SaveCount++;
            LastSaved = settings;
            return new ConfigurationSaveResult { Success = true };
        }
    }

    private sealed class NoOpOcrHotkeys : IGlobalHotkeyAdapter
    {
        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults =>
            Array.Empty<GlobalHotkeyRegistrationResult>();

        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds) =>
            Array.Empty<GlobalHotkeyRegistrationResult>();
    }
}

// ── WindowsOcrEngine adapter smoke tests ────────────────────────────────

public class WindowsOcrEngineTests
{
    [Fact]
    public void GetAvailableLanguages_NeverThrowsAndReturnsWellFormedEntries()
    {
        var engine = new WindowsOcrEngine();

        var languages = engine.GetAvailableLanguages();

        Assert.NotNull(languages);
        Assert.All(languages, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.Tag));
        });
    }

    [Fact]
    public async Task RecognizeAsync_NullFrame_ThrowsArgumentNullException()
    {
        var engine = new WindowsOcrEngine();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => engine.RecognizeAsync(null!, "en-US"));
    }

    [Fact]
    public async Task RecognizeAsync_InvalidFrameDimensions_FailsInsteadOfThrowing()
    {
        var engine = new WindowsOcrEngine();
        var zeroWidth = ExportTestHelpers.CreateTestBitmap(0, 4);

        var result = await engine.RecognizeAsync(zeroWidth, "en-US");

        Assert.Equal(OcrOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task RecognizeAsync_RealEngineSmoke_KnownTextOrCleanNoText()
    {
        // Real-OCR smoke: only meaningful when the environment has OCR
        // language packs; skipped when WinRT classes are not registered in
        // the headless runner. When it does run, a solid-color Frame must
        // yield a RecognizedText/NoText outcome — never an exception.
        var engine = new WindowsOcrEngine();
        if (engine.GetAvailableLanguages().Count == 0)
            return; // Skipped: no OCR language packs available headless.

        var frame = ExportTestHelpers.CreateTestBitmap(64, 64);

        var result = await TryWinRT(() => engine.RecognizeAsync(frame, null));
        if (result is null)
            return; // Skipped: WinRT not available.

        Assert.True(
            result.Outcome is OcrOutcome.RecognizedText or OcrOutcome.NoText,
            $"Expected a completion outcome, got {result.Outcome}: {result.Error}");
    }

    /// <summary>
    /// Tries a WinRT-dependent operation; returns null when WinRT classes are
    /// not registered in this environment (headless runner).
    /// </summary>
    private static async Task<OcrResult?> TryWinRT(Func<Task<OcrResult>> action)
    {
        try
        {
            return await action();
        }
        catch (System.Runtime.InteropServices.COMException ex)
            when (ex.ErrorCode == unchecked((int)0x80040154))
        {
            return null;
        }
    }
}
