using System;
using System.IO;
using Xunit;

namespace captcho.Capture.Tests;

public class AnnotationSettingsTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"captcho-annotation-settings-{Guid.NewGuid():N}");

    public AnnotationSettingsTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch { }
    }

    [Fact]
    public void WithDefaults_ProvidesUsablePenState()
    {
        var settings = AppSettings.WithDefaults();

        Assert.Equal(AnnotationTool.Pen, settings.EffectiveAnnotationSettings.DefaultTool);
        Assert.Equal(AnnotationSettings.DefaultPenColor, settings.EffectiveAnnotationSettings.PenColor);
        Assert.Equal(AnnotationSettings.DefaultStrokeWidth, settings.EffectiveAnnotationSettings.StrokeWidth);
    }

    [Fact]
    public void Validate_RejectsUnknownToolAndOutOfRangeStrokeWidth()
    {
        var settings = AppSettings.WithDefaults();
        settings.AnnotationSettings = new AnnotationSettings(
            (AnnotationTool)999,
            settings.EffectiveAnnotationSettings.PenColor,
            AnnotationSettings.MaximumStrokeWidth + 1);

        var issues = settings.Validate();

        var issue = Assert.Single(issues);
        Assert.Equal(SettingsField.AnnotationSettings, issue.Field);
        Assert.Contains("Annotation", issue.Message);
    }

    [Fact]
    public void SaveThenLoad_PreservesAnnotationSettingsAndExistingFields()
    {
        var service = new ConfigurationService(_tempDirectory);
        var original = AppSettings.WithDefaults();
        original.SaveLocation = @"D:\Captures";
        original.AnnotationEnabled = false;
        original.AnnotationSettings = new AnnotationSettings(
            AnnotationTool.Pen,
            new AnnotationColor(12, 34, 56, 200),
            9);

        Assert.True(service.Save(original).Success);
        var loaded = service.Load().Settings;

        Assert.Equal(@"D:\Captures", loaded.SaveLocation);
        Assert.False(loaded.AnnotationEnabled);
        Assert.Equal(original.AnnotationSettings, loaded.EffectiveAnnotationSettings);
    }

    [Fact]
    public void Load_OldConfigurationGetsAnnotationDefaultsWithoutLosingOtherFields()
    {
        var service = new ConfigurationService(_tempDirectory);
        File.WriteAllText(
            service.ConfigurationPath,
            "{\"saveLocation\":\"D:\\\\Captures\",\"annotationEnabled\":false}");

        var loaded = service.Load().Settings;

        Assert.Equal(@"D:\Captures", loaded.SaveLocation);
        Assert.False(loaded.AnnotationEnabled);
        Assert.Equal(AnnotationSettings.WithDefaults(), loaded.EffectiveAnnotationSettings);
    }
}
