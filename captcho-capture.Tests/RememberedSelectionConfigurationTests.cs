using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace captcho.Capture.Tests;

public sealed class RememberedSelectionConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"captchoRememberedSelection_{Guid.NewGuid():N}");

    [Fact]
    public void Defaults_StartWithNeverAndNoGeometry()
    {
        var settings = AppSettings.WithDefaults();

        Assert.Equal(RememberSelectionLifetime.Never, settings.RememberSelection);
        Assert.Null(settings.RememberedSelection);
    }

    [Fact]
    public void Load_OldConfigurationWithoutRememberedSelection_UsesDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{}\n");

        var loaded = new ConfigurationService(_directory).Load();

        Assert.True(loaded.Success);
        Assert.Equal(RememberSelectionLifetime.Never, loaded.Settings.RememberSelection);
        Assert.Null(loaded.Settings.RememberedSelection);
    }

    [Fact]
    public void SaveThenLoad_AlwaysGeometryAndTopology_RoundTrips()
    {
        var service = new ConfigurationService(_directory);
        var settings = AppSettings.WithDefaults();
        settings.RememberSelection = RememberSelectionLifetime.Always;
        settings.RememberedSelection = new RememberedSelectionGeometry
        {
            X = -300,
            Y = 40,
            Width = 900,
            Height = 500,
            Topology = new List<MonitorRect>
            {
                new(0, 0, 1920, 1080),
                new(-1280, 0, 0, 1024),
            },
        };

        Assert.True(service.Save(settings).Success);
        var loaded = service.Load().Settings;

        Assert.Equal(RememberSelectionLifetime.Always, loaded.RememberSelection);
        Assert.NotNull(loaded.RememberedSelection);
        Assert.Equal(-300, loaded.RememberedSelection!.X);
        Assert.Equal(40, loaded.RememberedSelection.Y);
        Assert.Equal(900u, loaded.RememberedSelection.Width);
        Assert.Equal(500u, loaded.RememberedSelection.Height);
        Assert.Equal(
            new[] { new MonitorRect(-1280, 0, 0, 1024), new MonitorRect(0, 0, 1920, 1080) },
            loaded.Normalized().RememberedSelection!.Topology);
    }

    [Fact]
    public void Save_SessionLifetime_DoesNotPersistGeometry()
    {
        var service = new ConfigurationService(_directory);
        var settings = AppSettings.WithDefaults();
        settings.RememberSelection = RememberSelectionLifetime.Session;
        settings.RememberedSelection = new RememberedSelectionGeometry
        {
            Width = 100,
            Height = 100,
            Topology = new List<MonitorRect> { new(0, 0, 1920, 1080) },
        };

        Assert.True(service.Save(settings).Success);
        var loaded = service.Load().Settings;

        Assert.Equal(RememberSelectionLifetime.Session, loaded.RememberSelection);
        Assert.Null(loaded.RememberedSelection);
    }

    [Fact]
    public void InvalidLifetime_UsesNeverAsEffectiveMigrationDefault()
    {
        var settings = new AppSettings { RememberSelection = (RememberSelectionLifetime)99 };

        Assert.Equal(RememberSelectionLifetime.Never, settings.EffectiveRememberSelection);
        Assert.Equal(RememberSelectionLifetime.Never, settings.Normalized().RememberSelection);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
