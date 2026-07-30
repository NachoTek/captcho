using System;
using System.Collections.Generic;
using System.IO;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class AnnotationSettingsSessionIntegrationTests
{
    [Fact]
    public void View_OnOpen_UsesCommittedAnnotationDefaults()
    {
        var runtime = AppSettings.WithDefaults();
        runtime.AnnotationSettings = new AnnotationSettings(
            AnnotationTool.Pen,
            new AnnotationColor(10, 20, 30),
            8);

        var session = NewSession(runtime);

        Assert.Equal(AnnotationTool.Pen, session.View.Annotation.DefaultTool);
        Assert.Equal(new AnnotationColor(10, 20, 30), session.View.Annotation.PenColor);
        Assert.Equal(8, session.View.Annotation.StrokeWidth);
    }

    [Fact]
    public void Apply_PersistsAnnotationDefaultsAtomicallyAndWritesRuntimeBack()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditAnnotationPenColor(new AnnotationColor(1, 2, 3));
        session.EditAnnotationStrokeWidth(13);
        var view = session.Apply();

        Assert.Equal(1, recorder.SaveCount);
        Assert.Equal(new AnnotationColor(1, 2, 3), recorder.LastSaved!.AnnotationSettings.PenColor);
        Assert.Equal(13, recorder.LastSaved.AnnotationSettings.StrokeWidth);
        Assert.Equal(13, runtime.AnnotationSettings.StrokeWidth);
        Assert.False(view.StatusIsError);
    }

    [Fact]
    public void CancelAndResetDoNotPersistAnnotationChanges()
    {
        var runtime = AppSettings.WithDefaults();
        var recorder = new RecordingConfiguration();
        var session = new SettingsSession(runtime, recorder, new NoOpGlobalHotkeys());

        session.EditAnnotationStrokeWidth(2);
        session.Reset();
        var view = session.Cancel();

        Assert.Equal(AnnotationSettings.DefaultStrokeWidth, view.Annotation.StrokeWidth);
        Assert.Equal(0, recorder.SaveCount);
        Assert.Equal(AnnotationSettings.DefaultStrokeWidth, runtime.AnnotationSettings.StrokeWidth);
    }

    private static SettingsSession NewSession(AppSettings runtime) =>
        new(runtime, new ConfigurationService(Path.Combine(Path.GetTempPath(), $"captcho-test-{Guid.NewGuid():N}")), new NoOpGlobalHotkeys());

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

    private sealed class NoOpGlobalHotkeys : IGlobalHotkeyAdapter
    {
        public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults =>
            Array.Empty<GlobalHotkeyRegistrationResult>();

        public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds) =>
            Array.Empty<GlobalHotkeyRegistrationResult>();
    }
}
