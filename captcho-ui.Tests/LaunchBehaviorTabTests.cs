// LaunchBehaviorTabTests.cs — Headless tests for the Behavior tab's launch
// behavior editing seam (issue #53).
//
// Verifies the tab-level editing semantics: the launch behavior loads into
// the working snapshot without mutating the source, edits never touch other
// slices, the configured mode is only meaningful for the Configured Capture
// Mode action, Apply/OK/Cancel/Reset participate in the composed session
// atomically, and validation gates Apply/OK when the configured mode is
// missing.

using System;
using System.IO;
using System.Linq;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Tab seam ────────────────────────────────────────────────────────────

public class LaunchBehaviorTabSettingsTests
{
    [Fact]
    public void Constructor_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BehaviorTabSettings(null!));
    }

    [Fact]
    public void Constructor_DisplaysPersistedLaunchBehavior_AndDoesNotMutateSource()
    {
        var source = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.SelectedMonitor),
        };

        var tab = new BehaviorTabSettings(source);

        Assert.Equal(LaunchAction.ConfiguredCaptureMode, tab.LaunchAction);
        Assert.Equal(CaptureMode.SelectedMonitor, tab.LaunchConfiguredMode);
        // Source untouched.
        Assert.Equal(LaunchAction.ConfiguredCaptureMode, source.LaunchBehavior.Action);
        Assert.Equal(CaptureMode.SelectedMonitor, source.LaunchBehavior.ConfiguredMode);
    }

    [Theory]
    [InlineData(LaunchAction.DoNothing)]
    [InlineData(LaunchAction.LastCaptureMode)]
    [InlineData(LaunchAction.ConfiguredCaptureMode)]
    public void EditLaunchAction_UpdatesWorkingSelection(LaunchAction action)
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        // Configure a mode first so switching to Configured keeps it.
        tab.EditLaunchBehavior(CaptureMode.FullDesktop);
        tab.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);
        tab.EditLaunchAction(action);

        Assert.Equal(action, tab.LaunchAction);
    }

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void EditLaunchBehavior_UpdatesWorkingConfiguredMode(CaptureMode mode)
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        // The configured mode is only exposed for the Configured action, so
        // the edit flow selects that action first (as the UI does).
        tab.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);
        tab.EditLaunchBehavior(mode);

        Assert.Equal(mode, tab.LaunchConfiguredMode);
    }

    [Fact]
    public void SwitchingAwayFromConfigured_ClearsTheConfiguredMode()
    {
        // The configured mode is only meaningful for the Configured Capture
        // Mode action; Do nothing / Last Capture Mode carry no mode.
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditLaunchBehavior(CaptureMode.Selection);

        tab.EditLaunchAction(LaunchAction.DoNothing);

        Assert.Null(tab.LaunchConfiguredMode);
    }

    [Fact]
    public void SwitchingBackToConfigured_AfterClearing_RequiresAModeAgain()
    {
        var tab = new BehaviorTabSettings(
            new AppSettings
            {
                LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.Selection),
            });
        tab.EditLaunchAction(LaunchAction.DoNothing);

        tab.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);

        Assert.Equal(LaunchAction.ConfiguredCaptureMode, tab.LaunchAction);
        Assert.Null(tab.LaunchConfiguredMode);
        Assert.False(tab.IsValid);
    }

    [Fact]
    public void LaunchEdits_NeverTouchOtherSettingsSlices()
    {
        var source = new AppSettings
        {
            SaveLocation = @"D:\Preserved",
            AutomaticExport = new AutomaticExportSettings(true, false, false),
        };
        var tab = new BehaviorTabSettings(source);

        tab.EditLaunchBehavior(CaptureMode.ActiveWindow);
        tab.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);

        Assert.Equal(@"D:\Preserved", source.SaveLocation);
        Assert.False(source.AutomaticExport.AutoCopyFrame);
    }

    // ── IsDirty / Cancel / Reset ────────────────────────────────────────

    [Fact]
    public void IsDirty_NoEdits_False()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_AfterLaunchActionEdit_True()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditLaunchAction(LaunchAction.LastCaptureMode);

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void IsDirty_AfterConfiguredModeEdit_True()
    {
        var tab = new BehaviorTabSettings(new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.FullDesktop),
        });
        tab.EditLaunchBehavior(CaptureMode.Selection);

        Assert.True(tab.IsDirty);
    }

    [Fact]
    public void Cancel_RevertsLaunchEditsToBaseline()
    {
        var source = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.DoNothing, null),
        };
        var tab = new BehaviorTabSettings(source);
        tab.EditLaunchBehavior(CaptureMode.Selection);
        tab.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);

        tab.Cancel();

        Assert.Equal(LaunchAction.DoNothing, tab.LaunchAction);
        Assert.Null(tab.LaunchConfiguredMode);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void Reset_RestoresDoNothingDefaults()
    {
        var source = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.Selection),
        };
        var tab = new BehaviorTabSettings(source);

        tab.Reset();

        Assert.Equal(LaunchAction.DoNothing, tab.LaunchAction);
        Assert.Null(tab.LaunchConfiguredMode);
    }

    [Fact]
    public void Reset_LeavesBaselineSoCancelRevertsTheReset()
    {
        var source = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
        };
        var tab = new BehaviorTabSettings(source);

        tab.Reset();
        tab.Cancel();

        Assert.Equal(LaunchAction.LastCaptureMode, tab.LaunchAction);
    }

    // ── WriteInto (merge) ───────────────────────────────────────────────

    [Fact]
    public void WriteInto_WritesLaunchSlice_LeavingOthersUntouched()
    {
        // The tab's AutomaticExport working snapshot (unedited: all off) is
        // written as its slice; the target's differing toggles are replaced,
        // while every other slice — including the recorded last Capture Mode,
        // which no tab owns — survives untouched.
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);
        tab.EditLaunchBehavior(CaptureMode.SelectedWindow);

        var target = new AppSettings
        {
            SaveLocation = @"D:\Preserved",
            AutomaticExport = new AutomaticExportSettings(true, false, false),
            LastCaptureMode = CaptureMode.Selection,
        };

        tab.WriteInto(target);

        Assert.Equal(LaunchAction.ConfiguredCaptureMode, target.LaunchBehavior.Action);
        Assert.Equal(CaptureMode.SelectedWindow, target.LaunchBehavior.ConfiguredMode);
        // The tab's whole Behavior slice is written (its working toggles).
        Assert.Equal(tab.AutoSave, target.AutomaticExport.AutoSave);
        Assert.Equal(tab.AutoCopyFrame, target.AutomaticExport.AutoCopyFrame);
        // Other slices preserved — including the recorded last Capture Mode,
        // which the tab must never rewrite.
        Assert.Equal(@"D:\Preserved", target.SaveLocation);
        Assert.Equal(CaptureMode.Selection, target.LastCaptureMode);
    }

    [Fact]
    public void WriteInto_NullTarget_Throws()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());

        Assert.Throws<ArgumentNullException>(() => tab.WriteInto(null!));
    }

    // ── Validity ────────────────────────────────────────────────────────

    [Fact]
    public void IsValid_ConfiguredActionWithoutMode_FalseWithInlineError()
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);

        Assert.False(tab.IsValid);
        Assert.NotNull(tab.FirstError);
    }

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void IsValid_ConfiguredActionWithMode_True(CaptureMode mode)
    {
        var tab = new BehaviorTabSettings(AppSettings.WithDefaults());
        tab.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);
        tab.EditLaunchBehavior(mode);

        Assert.True(tab.IsValid);
        Assert.Null(tab.FirstError);
    }
}

// ── Composed session ────────────────────────────────────────────────────

public class LaunchBehaviorSessionTests
{
    [Fact]
    public void View_OnOpen_ReflectsCommittedLaunchBehavior()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.Selection);

        var session = NewSession(runtime);

        Assert.Equal(LaunchAction.ConfiguredCaptureMode, session.View.Behavior.LaunchAction);
        Assert.Equal(CaptureMode.Selection, session.View.Behavior.LaunchConfiguredMode);
    }

    [Fact]
    public void EditLaunchBehavior_UpdatesWorkingSelectionWithoutMutatingRuntime()
    {
        var runtime = AppSettings.WithDefaults();
        var session = NewSession(runtime);

        var view = session.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);

        Assert.Equal(LaunchAction.ConfiguredCaptureMode, view.Behavior.LaunchAction);
        Assert.Equal(LaunchAction.DoNothing, runtime.LaunchBehavior.Action);
    }

    [Fact]
    public void Apply_PersistsLaunchBehaviorAtomicallyAndWritesRuntimeBack()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);
        session.EditLaunchBehavior(CaptureMode.SelectedMonitor);
        var view = session.Apply();

        Assert.Equal(1, recorder.SaveCount);
        Assert.Equal(
            new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.SelectedMonitor),
            recorder.LastSaved!.LaunchBehavior);
        Assert.Equal(LaunchAction.ConfiguredCaptureMode, runtime.LaunchBehavior.Action);
        Assert.Equal(CaptureMode.SelectedMonitor, runtime.LaunchBehavior.ConfiguredMode);
        Assert.False(view.StatusIsError);
    }

    [Fact]
    public void Apply_InvalidConfiguredSelection_BlocksPersistWithError()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        var view = session.EditLaunchAction(LaunchAction.ConfiguredCaptureMode);

        Assert.False(view.CanApply);
        Assert.False(view.CanConfirm);
        var applied = session.Apply();
        Assert.True(applied.StatusIsError);
        Assert.Equal(0, recorder.SaveCount);
        Assert.Equal(LaunchAction.DoNothing, runtime.LaunchBehavior.Action);
    }

    [Fact]
    public void Confirm_PersistsLaunchBehaviorAndSignalsClose()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditLaunchAction(LaunchAction.LastCaptureMode);
        var view = session.Confirm();

        Assert.True(recorder.LastSaved!.LaunchBehavior.Action == LaunchAction.LastCaptureMode);
        Assert.True(view.ShouldClose);
        Assert.Equal(LaunchAction.LastCaptureMode, runtime.LaunchBehavior.Action);
    }

    [Fact]
    public void Cancel_RevertsLaunchEditsWithoutPersisting()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditLaunchAction(LaunchAction.LastCaptureMode);
        var view = session.Cancel();

        Assert.Equal(LaunchAction.DoNothing, view.Behavior.LaunchAction);
        Assert.Equal(0, recorder.SaveCount);
        Assert.Equal(LaunchAction.DoNothing, runtime.LaunchBehavior.Action);
    }

    [Fact]
    public void Reset_RestoresDoNothingAndCancelStillRevertsTheReset()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null);
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        var reset = session.Reset();

        Assert.Equal(LaunchAction.DoNothing, reset.Behavior.LaunchAction);
        Assert.Equal(0, recorder.SaveCount);

        var cancelled = session.Cancel();

        Assert.Equal(LaunchAction.LastCaptureMode, cancelled.Behavior.LaunchAction);
    }

    [Fact]
    public void Apply_PreservesRecordedLastCaptureMode()
    {
        // The last Capture Mode is runtime-recorded state, not a Behavior-tab
        // edit: Apply must carry it through the merged save untouched.
        var runtime = AppSettings.WithDefaults();
        runtime.LastCaptureMode = CaptureMode.SelectedWindow;
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditLaunchAction(LaunchAction.LastCaptureMode);
        session.Apply();

        Assert.Equal(CaptureMode.SelectedWindow, recorder.LastSaved!.LastCaptureMode);
    }

    private static SettingsSession NewSession(AppSettings runtime) =>
        new(runtime, new ConfigurationService(Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}")), new NoOpGlobalHotkeys());

    private sealed class RecordingConfiguration : ConfigurationService
    {
        public RecordingConfiguration()
            : base(Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}"))
        {
        }

        public int SaveCount { get; private set; }
        public AppSettings? LastSaved { get; private set; }

        public override ConfigurationSaveResult Save(AppSettings settings)
        {
            SaveCount++;
            LastSaved = settings;
            return new ConfigurationSaveResult { Success = true };
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
