// AutomaticDeliverySettingsSessionIntegrationTests.cs — Headless composed-session
// tests for the Behavior tab (automatic save, Copy Frame, Copy Path).
//
// Verifies the Behavior tab's participation in the SettingsSession's atomic
// Apply/OK/Cancel/Reset semantics: Apply persists the Behavior slice with the
// same single save as every other tab and writes the runtime back; Cancel
// reverts; Reset restores the Behavior defaults; a failing save moves nothing;
// and OK closes only on success.

using System;
using System.IO;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class AutomaticDeliverySettingsSessionIntegrationTests
{
    [Fact]
    public void View_OnOpen_ReflectsCommittedAutomaticExportSettings()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.AutomaticExport = new AutomaticExportSettings(true, false, true);

        var session = NewSession(runtime);

        Assert.True(session.View.Behavior.AutoSave);
        Assert.False(session.View.Behavior.AutoCopyFrame);
        Assert.True(session.View.Behavior.AutoCopyPath);
    }

    [Fact]
    public void Apply_PersistsBehaviorAtomicallyAndWritesRuntimeBack()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditAutomaticSave(true);
        session.EditAutomaticCopyFrame(true);
        var view = session.Apply();

        Assert.Equal(1, recorder.SaveCount);
        Assert.Equal(new AutomaticExportSettings(true, true, false), recorder.LastSaved!.AutomaticExport);
        Assert.True(runtime.AutomaticExport.AutoSave);
        Assert.True(runtime.AutomaticExport.AutoCopyFrame);
        Assert.False(runtime.AutomaticExport.AutoCopyPath);
        Assert.False(view.StatusIsError);
        Assert.True(view.CanApply);
    }

    [Fact]
    public void Confirm_PersistsBehaviorAndSignalsClose()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditAutomaticCopyPath(true);
        var view = session.Confirm();

        Assert.True(recorder.LastSaved!.AutomaticExport.AutoCopyPath);
        Assert.True(view.ShouldClose);
        Assert.True(runtime.AutomaticExport.AutoCopyPath);
    }

    [Fact]
    public void FailingSave_NothingMoves_AllOrNothing()
    {
        var runtime = AppSettings.WithDefaults();
        var forcing = new RecordingConfiguration(new ConfigurationSaveResult
        {
            Success = false,
            Phase = "WriteTemp",
            ErrorMessage = "Disk unavailable.",
        });
        var session = new SettingsSession(runtime, forcing, new NoOpGlobalHotkeys());

        session.EditAutomaticSave(true);
        var view = session.Apply();

        Assert.True(view.StatusIsError);
        Assert.NotNull(view.StatusMessage);
        // Runtime untouched; the working edit survives for a retry.
        Assert.False(runtime.AutomaticExport.AutoSave);
        Assert.True(session.View.Behavior.AutoSave);
    }

    [Fact]
    public void Cancel_RevertsBehaviorEditsWithoutPersisting()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditAutomaticSave(true);
        session.EditAutomaticCopyFrame(true);
        var view = session.Cancel();

        Assert.False(view.Behavior.AutoSave);
        Assert.False(view.Behavior.AutoCopyFrame);
        Assert.Equal(0, recorder.SaveCount);
        Assert.False(runtime.AutomaticExport.AutoSave);
    }

    [Fact]
    public void Reset_RestoresBehaviorDefaultsAndCancelStillRevertsTheReset()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.AutomaticExport = new AutomaticExportSettings(true, true, true);
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        var reset = session.Reset();

        Assert.False(reset.Behavior.AutoSave);
        Assert.False(reset.Behavior.AutoCopyFrame);
        Assert.False(reset.Behavior.AutoCopyPath);
        Assert.Equal(0, recorder.SaveCount);

        var cancelled = session.Cancel();

        Assert.True(cancelled.Behavior.AutoSave);
        Assert.True(cancelled.Behavior.AutoCopyFrame);
        Assert.True(cancelled.Behavior.AutoCopyPath);
    }

    private static SettingsSession NewSession(AppSettings runtime) =>
        new(runtime, new ConfigurationService(Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}")), new NoOpGlobalHotkeys());

    private sealed class RecordingConfiguration : ConfigurationService
    {
        private readonly ConfigurationSaveResult? _forcedFailure;

        public RecordingConfiguration(ConfigurationSaveResult? forcedFailure = null)
            : base(Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}"))
        {
            _forcedFailure = forcedFailure;
        }

        public int SaveCount { get; private set; }
        public AppSettings? LastSaved { get; private set; }

        public override ConfigurationSaveResult Save(AppSettings settings)
        {
            SaveCount++;
            LastSaved = settings;
            return _forcedFailure ?? new ConfigurationSaveResult { Success = true };
        }
    }

    private sealed class NoOpGlobalHotkeys : IGlobalHotkeyAdapter
    {
        public System.Collections.Generic.IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults =>
            Array.Empty<GlobalHotkeyRegistrationResult>();

        public System.Collections.Generic.IReadOnlyList<GlobalHotkeyRegistrationResult> ApplyEnabledStates(
            System.Collections.Generic.IReadOnlySet<int> enabledIds) =>
            Array.Empty<GlobalHotkeyRegistrationResult>();
    }
}
