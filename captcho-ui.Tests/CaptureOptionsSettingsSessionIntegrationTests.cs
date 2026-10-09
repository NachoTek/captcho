// CaptureOptionsSettingsSessionIntegrationTests.cs — Integration coverage at the
// composed SettingsSession seam for the new Capture tab.
//
// The Capture tab joins General and Global Hotkeys as a third editable tab in
// SettingsSession. These tests pin the cross-tab behaviors that the new tab
// must satisfy when composed: snapshot-on-open without mutating the runtime,
// atomic persistence in the same save as the other tabs, write-back into the
// runtime on success so the workflow session picks up the new committed
// defaults, Cancel rolls back every tab including Capture, and Reset restores
// Capture to defaults alongside the other tabs.

using System;
using System.IO;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class CaptureOptionsSettingsSessionIntegrationTests
{
    [Fact]
    public void View_OnOpen_ReflectsCommittedCaptureOptionsWithoutMutatingRuntime()
    {
        var runtime = new AppSettings
        {
            CaptureOptions = new CaptureOptions(
                IncludePointer: true,
                IncludeDecorations: false,
                IncludeShadow: false),
        };

        var session = NewSession(runtime);

        Assert.True(session.View.Capture.IncludePointer);
        Assert.False(session.View.Capture.IncludeDecorations);
        Assert.False(session.View.Capture.IncludeShadow);
        // Source untouched.
        Assert.True(runtime.CaptureOptions.IncludePointer);
    }

    [Fact]
    public void EditCaptureOptions_FlowsThroughComposedGate()
    {
        // CaptureTab is always valid (no persisted validation rule), so editing it
        // never toggles CanApply/CanConfirm. The composed gate still reflects the
        // other tabs' validity. The edit returns a refreshed view carrying the new
        // value.
        var session = NewSession(AppSettings.WithDefaults());

        var view = session.EditCaptureIncludePointer(true);

        Assert.True(view.CanApply);
        Assert.True(view.CanConfirm);
        Assert.True(view.Capture.IncludePointer);
    }

    [Fact]
    public void EditCaptureDecorations_TurningOff_ReconcilesShadowInViewImmediately()
    {
        // The decoration/shadow dependency is enforced at edit time at the
        // composed-session seam too — the view never carries an inconsistent
        // combination, even before Apply.
        var session = NewSession(AppSettings.WithDefaults());

        var view = session.EditCaptureIncludeDecorations(false);

        Assert.False(view.Capture.IncludeDecorations);
        Assert.False(view.Capture.IncludeShadow);
    }

    [Fact]
    public void Apply_PersistsCaptureOptionsAtomicallyWithOtherTabs()
    {
        // AC #79 (spec): Apply validates and persists ALL editable tabs atomically.
        // Capture options join General and Global Hotkeys in one save.
        var recorder = new FakeConfiguration();
        var session = new SettingsSession(AppSettings.WithDefaults(), recorder, new RecordingGlobalHotkeyAdapter());

        session.EditSaveLocation(@"D:\New");
        session.EditGlobalHotkeyEnabled(GlobalHotkeyRouteMap.IdPrintScreen, enabled: false);
        session.EditCaptureIncludePointer(true);
        var view = session.Apply();

        Assert.Equal(1, recorder.CallCount);
        Assert.NotNull(recorder.LastSaved);
        // Every tab's edit landed in the same persisted AppSettings.
        Assert.Equal(@"D:\New", recorder.LastSaved!.SaveLocation);
        Assert.False(recorder.LastSaved.IsGlobalHotkeyEnabled(GlobalHotkeyRoute.CurrentMonitor));
        Assert.True(recorder.LastSaved.CaptureOptions.IncludePointer);
        Assert.Equal(SettingsSession.SavedMessage, view.StatusMessage);
    }

    [Fact]
    public void Apply_WritesCaptureOptionsBackIntoRuntime()
    {
        // The runtime AppSettings is the same reference the workflow session
        // (SessionCaptureOptions) reads from — write-back lets later Captures see
        // the new committed defaults without an app restart.
        var runtime = AppSettings.WithDefaults();
        var session = NewSession(runtime);

        session.EditCaptureIncludePointer(true);
        session.EditCaptureIncludeDecorations(false);
        session.Apply();

        Assert.True(runtime.CaptureOptions.IncludePointer);
        Assert.False(runtime.CaptureOptions.IncludeDecorations);
        Assert.False(runtime.CaptureOptions.IncludeShadow);
    }

    [Fact]
    public void Cancel_AfterEditingCaptureOptions_RollsBackToBaseline()
    {
        var runtime = new AppSettings
        {
            CaptureOptions = new CaptureOptions(
                IncludePointer: false,
                IncludeDecorations: true,
                IncludeShadow: true),
        };
        var session = NewSession(runtime);

        session.EditCaptureIncludePointer(true);
        var view = session.Cancel();

        Assert.False(view.Capture.IncludePointer);
    }

    [Fact]
    public void Reset_RestoresCaptureOptionsToDefaultsAlongsideOtherTabs()
    {
        var runtime = new AppSettings
        {
            SaveLocation = @"D:\Custom",
            CaptureOptions = new CaptureOptions(
                IncludePointer: true,
                IncludeDecorations: false,
                IncludeShadow: false),
        };
        var session = NewSession(runtime);

        var view = session.Reset();

        Assert.Equal(ExportDefaults.DefaultSaveDirectory, view.SaveLocation);
        Assert.Equal(CaptureOptions.WithDefaults().IncludePointer, view.Capture.IncludePointer);
        Assert.Equal(CaptureOptions.WithDefaults().IncludeDecorations, view.Capture.IncludeDecorations);
        Assert.Equal(CaptureOptions.WithDefaults().IncludeShadow, view.Capture.IncludeShadow);
    }

    [Fact]
    public void Apply_OnSaveFailure_NothingMovesIncludingCaptureTab()
    {
        // Atomicity holds across the new tab too: a save failure leaves the
        // CaptureOptions runtime value, baseline, and view state untouched, and
        // the later Cancel still reverts the uncommitted Capture edit.
        var runtime = AppSettings.WithDefaults();
        var forcing = new FakeConfiguration(forcedFailure: FailedSave("WriteTemp", "disk full"));
        var session = new SettingsSession(runtime, forcing, new RecordingGlobalHotkeyAdapter());

        session.EditCaptureIncludePointer(true);
        var view = session.Apply();

        Assert.True(view.StatusIsError);
        Assert.False(runtime.CaptureOptions.IncludePointer); // runtime untouched

        var afterCancel = session.Cancel();
        Assert.False(afterCancel.Capture.IncludePointer);
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

    private sealed class RecordingGlobalHotkeyAdapter : IGlobalHotkeyAdapter
    {
        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults
            => Array.Empty<GlobalHotkeyRegistrationResult>();

        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplySettings(AppSettings settings)
            => Array.Empty<GlobalHotkeyRegistrationResult>();
    }
}
