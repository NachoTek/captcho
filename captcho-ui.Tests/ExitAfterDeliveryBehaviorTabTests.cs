// ExitAfterDeliveryBehaviorTabTests.cs — Headless tests for the Behavior
// tab's exit-after-delivery editing seam (issue #54).
//
// Verifies the tab-level editing semantics — the toggle loads into the
// working snapshot without mutating the source, edits never touch other
// slices, dirty/cancel/reset participate in the shared snapshot semantics,
// WriteInto merges only the Behavior slice — and the composed Apply/OK/
// Cancel/Reset flow across the SettingsSession, including safe migration
// (a persisted settings file predating the toggle loads as off) and atomic
// Apply with a single save.

using System;
using System.IO;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Tab seam ─────────────────────────────────────────────────────────────

public class ExitAfterDeliveryTabSettingsTests
{
    [Fact]
    public void Constructor_DisplaysPersistedToggle_AndDoesNotMutateSource()
    {
        var source = new AppSettings { ExitAfterDelivery = true };

        var tab = new BehaviorTabSettings(source);

        Assert.True(tab.ExitAfterDelivery);
        // Source untouched.
        Assert.True(source.ExitAfterDelivery);
    }

    [Fact]
    public void Constructor_DefaultsToOff()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        Assert.False(tab.ExitAfterDelivery);
    }

    [Fact]
    public void EditExitAfterDelivery_UpdatesWorkingToggle()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        tab.EditExitAfterDelivery(true);

        Assert.True(tab.ExitAfterDelivery);
    }

    [Fact]
    public void ExitEdits_NeverTouchOtherSettingsSlices()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Preserved",
            AutomaticExport = new AutomaticExportSettings(false, false, false),
        };
        var tab = new BehaviorTabSettings(source);

        tab.EditExitAfterDelivery(true);

        Assert.Equal(@"D:\Preserved", source.SaveLocation);
        Assert.False(source.AutomaticExport.AutoSave);
    }

    // ── IsDirty / Cancel / Reset ────────────────────────────────────────

    [Fact]
    public void IsDirty_NoEdits_False()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_AfterExitEdit_True()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditExitAfterDelivery(true);

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Cancel_RevertsExitEditsToBaseline()
    {
        var source = new AppSettings { ExitAfterDelivery = false };
        var tab = new BehaviorTabSettings(source);
        tab.EditExitAfterDelivery(true);

        tab.Cancel();

        Assert.False(tab.ExitAfterDelivery);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Reset_RestoresOffDefault()
    {
        var source = new AppSettings { ExitAfterDelivery = true };
        var tab = new BehaviorTabSettings(source);

        tab.Reset();

        Assert.False(tab.ExitAfterDelivery);
    }

    [Fact]
    public void Reset_LeavesBaselineSoCancelRevertsTheReset()
    {
        var source = new AppSettings { ExitAfterDelivery = true };
        var tab = new BehaviorTabSettings(source);

        tab.Reset();
        tab.Cancel();

        Assert.True(tab.ExitAfterDelivery);
    }

    // ── WriteInto (merge) ───────────────────────────────────────────────

    [Fact]
    public void WriteInto_WritesExitToggle_LeavingOthersUntouched()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditExitAfterDelivery(true);

        var target = new AppSettings
        {
            SaveLocation = @"D:\Preserved",
            AnnotationEnabled = false,
            LastCaptureMode = CaptureMode.Selection,
        };

        tab.WriteInto(target);

        Assert.True(target.ExitAfterDelivery);
        // Other slices preserved — including runtime-recorded state no tab owns.
        Assert.Equal(@"D:\Preserved", target.SaveLocation);
        Assert.False(target.AnnotationEnabled);
        Assert.Equal(CaptureMode.Selection, target.LastCaptureMode);
    }
}

// ── Composed session ─────────────────────────────────────────────────────

public class ExitAfterDeliverySessionTests
{
    [Fact]
    public void View_OnOpen_ReflectsCommittedToggle()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.ExitAfterDelivery = true;

        var session = NewSession(runtime);

        Assert.True(session.View.Behavior.ExitAfterDelivery);
    }

    [Fact]
    public void EditExitAfterDelivery_UpdatesWorkingToggleWithoutMutatingRuntime()
    {
        var runtime = AppSettings.WithDefaults();
        var session = NewSession(runtime);

        var view = session.EditExitAfterDelivery(true);

        Assert.True(view.Behavior.ExitAfterDelivery);
        Assert.False(runtime.ExitAfterDelivery);
    }

    [Fact]
    public void Apply_PersistsExitToggleAtomicallyAndWritesRuntimeBack()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditExitAfterDelivery(true);
        var view = session.Apply();

        Assert.Equal(1, recorder.SaveCount);
        Assert.True(recorder.LastSaved!.ExitAfterDelivery);
        Assert.True(runtime.ExitAfterDelivery);
        Assert.False(view.StatusIsError);
        Assert.True(view.CanApply);
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

        session.EditExitAfterDelivery(true);
        var view = session.Apply();

        Assert.True(view.StatusIsError);
        // Runtime untouched; the working edit survives for a retry.
        Assert.False(runtime.ExitAfterDelivery);
        Assert.True(session.View.Behavior.ExitAfterDelivery);
    }

    [Fact]
    public void Confirm_PersistsExitToggleAndSignalsClose()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditExitAfterDelivery(true);
        var view = session.Confirm();

        Assert.True(recorder.LastSaved!.ExitAfterDelivery);
        Assert.True(view.ShouldClose);
        Assert.True(runtime.ExitAfterDelivery);
    }

    [Fact]
    public void Cancel_RevertsExitEditsWithoutPersisting()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditExitAfterDelivery(true);
        var view = session.Cancel();

        Assert.False(view.Behavior.ExitAfterDelivery);
        Assert.Equal(0, recorder.SaveCount);
        Assert.False(runtime.ExitAfterDelivery);
    }

    [Fact]
    public void Reset_RestoresOffAndCancelStillRevertsTheReset()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.ExitAfterDelivery = true;
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        var reset = session.Reset();

        Assert.False(reset.Behavior.ExitAfterDelivery);
        Assert.Equal(0, recorder.SaveCount);

        var cancelled = session.Cancel();

        Assert.True(cancelled.Behavior.ExitAfterDelivery);
    }

    [Fact]
    public void Apply_IsAlwaysValid_BooleanHasNoInvalidValue()
    {
        var session = NewSession(AppSettings.WithDefaults());

        var view = session.EditExitAfterDelivery(true);

        Assert.True(view.CanApply);
        Assert.True(view.CanConfirm);
    }

    // ── Safe migration through the composed session ────────────────────

    [Fact]
    public void SessionOverPreToggleSettingsFile_OpensWithExitOff()
    {
        // Migration through the real ConfigurationService: a settings file
        // written before the toggle shipped opens with the safe off
        // default, and an Apply over it persists the slice with exit off.
        var dir = Path.Combine(Path.GetTempPath(), $"captcho-exit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                /*lang=json,strict*/ "{\"saveLocation\": \"D:\\\\Captures\", \"launchBehavior\": {\"action\": 1, \"configuredMode\": null}}");
            var configuration = new ConfigurationService(dir);

            var load = configuration.Load();
            Assert.True(load.Success);
            var session = new SettingsSession(load.Settings, configuration, new NoOpGlobalHotkeys());

            Assert.False(session.View.Behavior.ExitAfterDelivery);
            var view = session.Apply();

            Assert.False(view.StatusIsError);
            Assert.False(configuration.Load().Settings.ExitAfterDelivery);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
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

        public System.Collections.Generic.IReadOnlyList<GlobalHotkeyRegistrationResult> ApplySettings(
            AppSettings settings) =>
            Array.Empty<GlobalHotkeyRegistrationResult>();
    }
}
