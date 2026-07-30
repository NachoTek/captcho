using System;
using System.Collections.Generic;
using System.IO;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public sealed class RememberedSelectionSettingsSessionTests
{
    [Fact]
    public void Apply_PersistsLifetimeAtomicallyAndUpdatesRuntime()
    {
        var runtime = AppSettings.WithDefaults();
        var configuration = new RecordingConfiguration();
        var session = NewSession(runtime, configuration);

        session.EditSaveLocation(@"D:\Captures");
        session.EditRememberSelection(RememberSelectionLifetime.Always);
        var view = session.Apply();

        Assert.Equal(1, configuration.CallCount);
        Assert.Equal(@"D:\Captures", configuration.LastSaved!.SaveLocation);
        Assert.Equal(RememberSelectionLifetime.Always, configuration.LastSaved.RememberSelection);
        Assert.Equal(RememberSelectionLifetime.Always, runtime.RememberSelection);
        Assert.Equal(RememberSelectionLifetime.Always, view.Capture.RememberSelection);
    }

    [Fact]
    public void Cancel_HasNoRuntimeOrGeometrySideEffects()
    {
        var remembered = Geometry();
        var runtime = AppSettings.WithDefaults();
        runtime.RememberSelection = RememberSelectionLifetime.Always;
        runtime.RememberedSelection = remembered;
        var session = NewSession(runtime, new RecordingConfiguration());

        session.EditRememberSelection(RememberSelectionLifetime.Never);
        var view = session.Cancel();

        Assert.Equal(RememberSelectionLifetime.Always, view.Capture.RememberSelection);
        Assert.Equal(RememberSelectionLifetime.Always, runtime.RememberSelection);
        Assert.Same(remembered, runtime.RememberedSelection);
    }

    [Fact]
    public void ResetThenApply_RestoresNeverAndClearsPersistedGeometry()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.RememberSelection = RememberSelectionLifetime.Always;
        runtime.RememberedSelection = Geometry();
        var configuration = new RecordingConfiguration();
        var session = NewSession(runtime, configuration);

        Assert.Equal(RememberSelectionLifetime.Never, session.Reset().Capture.RememberSelection);
        session.Apply();

        Assert.Equal(RememberSelectionLifetime.Never, runtime.RememberSelection);
        Assert.Null(runtime.RememberedSelection);
        Assert.Null(configuration.LastSaved!.RememberedSelection);
    }

    [Fact]
    public void Apply_WhenSaveFails_LeavesRuntimeLifetimeAndGeometryUntouched()
    {
        var remembered = Geometry();
        var runtime = AppSettings.WithDefaults();
        runtime.RememberSelection = RememberSelectionLifetime.Always;
        runtime.RememberedSelection = remembered;
        var session = NewSession(runtime, new RecordingConfiguration(fail: true));

        session.EditRememberSelection(RememberSelectionLifetime.Never);
        var view = session.Apply();

        Assert.True(view.StatusIsError);
        Assert.Equal(RememberSelectionLifetime.Always, runtime.RememberSelection);
        Assert.Same(remembered, runtime.RememberedSelection);
    }

    [Fact]
    public void Apply_DoesNotOverwriteGeometryCapturedWhileSettingsWasOpen()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.RememberSelection = RememberSelectionLifetime.Always;
        runtime.RememberedSelection = Geometry();
        var configuration = new RecordingConfiguration();
        var session = NewSession(runtime, configuration);
        var newer = Geometry();
        newer.X = 900;
        runtime.RememberedSelection = newer;

        session.EditCaptureIncludePointer(true);
        session.Apply();

        Assert.Equal(900, runtime.RememberedSelection!.X);
        Assert.Equal(900, configuration.LastSaved!.RememberedSelection!.X);
    }

    private static SettingsSession NewSession(AppSettings settings, ConfigurationService configuration) =>
        new(settings, configuration, new NoOpGlobalHotkeys());

    private static RememberedSelectionGeometry Geometry() => new()
    {
        X = 10,
        Y = 20,
        Width = 300,
        Height = 200,
        Topology = new List<MonitorRect> { new(0, 0, 1920, 1080) },
    };

    private sealed class RecordingConfiguration(bool fail = false) : ConfigurationService(
        Path.Combine(Path.GetTempPath(), $"captcho-settings-{Guid.NewGuid():N}"))
    {
        public int CallCount { get; private set; }
        public AppSettings? LastSaved { get; private set; }

        public override ConfigurationSaveResult Save(AppSettings settings)
        {
            CallCount++;
            LastSaved = settings;
            return fail
                ? new ConfigurationSaveResult { Success = false, Phase = "WriteTemp", ErrorMessage = "disk full" }
                : new ConfigurationSaveResult { Success = true };
        }
    }

    private sealed class NoOpGlobalHotkeys : IGlobalHotkeyAdapter
    {
        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults =>
            Array.Empty<GlobalHotkeyRegistrationResult>();

        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds) =>
            Array.Empty<GlobalHotkeyRegistrationResult>();
    }
}
