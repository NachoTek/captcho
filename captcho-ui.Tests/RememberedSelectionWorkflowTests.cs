using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public sealed class RememberedSelectionWorkflowTests
{
    private static readonly SelectionGeometry Geometry = new()
    {
        X = -200,
        Y = 100,
        Width = 640,
        Height = 480,
    };

    [Fact]
    public async Task Never_StartsCleanForEveryCapture()
    {
        var settings = AppSettings.WithDefaults();
        var overlay = new FakeSelectionOverlayAdapter { NextGeometry = Geometry };
        var session = NewWorkflow(settings, overlay, new MutableTopology());

        await session.CaptureSelectionAsync();
        await session.CaptureSelectionAsync();

        Assert.Equal(new SelectionGeometry?[] { null, null }, overlay.InitialGeometries);
        Assert.Null(settings.RememberedSelection);
    }

    [Fact]
    public async Task Session_ReusesGeometryUntilWorkflowIsRecreated()
    {
        var settings = AppSettings.WithDefaults();
        settings.RememberSelection = RememberSelectionLifetime.Session;
        var topology = new MutableTopology();
        var firstOverlay = new FakeSelectionOverlayAdapter { NextGeometry = Geometry };
        var first = NewWorkflow(settings, firstOverlay, topology);

        await first.CaptureSelectionAsync();
        await first.CaptureSelectionAsync();

        Assert.Equal(new SelectionGeometry?[] { null, Geometry }, firstOverlay.InitialGeometries);
        Assert.Null(settings.RememberedSelection);

        var restartedOverlay = new FakeSelectionOverlayAdapter { NextGeometry = Geometry };
        await NewWorkflow(settings, restartedOverlay, topology).CaptureSelectionAsync();
        Assert.Null(Assert.Single(restartedOverlay.InitialGeometries));
    }

    [Fact]
    public async Task Always_PersistsAndRestoresGeometryAcrossWorkflowInstances()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"captcho-restart-{Guid.NewGuid():N}");
        try
        {
            var settings = AppSettings.WithDefaults();
            settings.RememberSelection = RememberSelectionLifetime.Always;
            var topology = new MutableTopology();
            var configuration = new ConfigurationService(directory);
            var firstOverlay = new FakeSelectionOverlayAdapter { NextGeometry = Geometry };

            await NewWorkflow(settings, firstOverlay, topology, configuration).CaptureSelectionAsync();

            var restartedSettings = configuration.Load().Settings;
            var restartedOverlay = new FakeSelectionOverlayAdapter { NextGeometry = Geometry };
            await NewWorkflow(restartedSettings, restartedOverlay, topology, configuration)
                .CaptureSelectionAsync();
            Assert.Equal(Geometry, Assert.Single(restartedOverlay.InitialGeometries));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Always_IncompatibleTopology_DiscardsStoredGeometry()
    {
        var settings = AppSettings.WithDefaults();
        settings.RememberSelection = RememberSelectionLifetime.Always;
        settings.RememberedSelection = new RememberedSelectionGeometry
        {
            X = 10,
            Y = 10,
            Width = 100,
            Height = 100,
            Topology = new List<MonitorRect> { new(0, 0, 1920, 1080) },
        };
        var topology = new MutableTopology
        {
            Monitors = new[] { new MonitorRect(0, 0, 2560, 1440) },
        };
        var configuration = new RecordingConfiguration();
        var overlay = new FakeSelectionOverlayAdapter { NextGeometry = null };

        await NewWorkflow(settings, overlay, topology, configuration).CaptureSelectionAsync();

        Assert.Null(Assert.Single(overlay.InitialGeometries));
        Assert.Null(settings.RememberedSelection);
        Assert.Null(configuration.LastSaved!.RememberedSelection);
    }

    [Fact]
    public async Task Always_CancellationDoesNotOverwriteStoredGeometry()
    {
        var settings = AppSettings.WithDefaults();
        settings.RememberSelection = RememberSelectionLifetime.Always;
        settings.RememberedSelection = new RememberedSelectionGeometry
        {
            X = Geometry.X,
            Y = Geometry.Y,
            Width = Geometry.Width,
            Height = Geometry.Height,
            Topology = new List<MonitorRect>
            {
                new(-1280, 0, 0, 1024),
                new(0, 0, 1920, 1080),
            },
        };
        var overlay = new FakeSelectionOverlayAdapter { NextGeometry = null };

        await NewWorkflow(settings, overlay, new MutableTopology(), new RecordingConfiguration())
            .CaptureSelectionAsync();

        Assert.Equal(Geometry, Assert.Single(overlay.InitialGeometries));
        Assert.Equal(Geometry.X, settings.RememberedSelection!.X);
    }

    [Fact]
    public async Task Always_WhenConfigurationSaveFails_DoesNotMoveRuntimeGeometry()
    {
        var settings = AppSettings.WithDefaults();
        settings.RememberSelection = RememberSelectionLifetime.Always;
        var overlay = new FakeSelectionOverlayAdapter { NextGeometry = Geometry };

        await NewWorkflow(settings, overlay, new MutableTopology(), new RecordingConfiguration(fail: true))
            .CaptureSelectionAsync();

        Assert.Null(settings.RememberedSelection);
    }

    [Fact]
    public async Task Session_RelevantTopologyChange_DiscardsGeometry()
    {
        var settings = AppSettings.WithDefaults();
        settings.RememberSelection = RememberSelectionLifetime.Session;
        var topology = new MutableTopology();
        var overlay = new FakeSelectionOverlayAdapter { NextGeometry = Geometry };
        var workflow = NewWorkflow(settings, overlay, topology);

        await workflow.CaptureSelectionAsync();
        topology.Monitors = new[] { new MonitorRect(0, 0, 1920, 1080) };
        await workflow.CaptureSelectionAsync();

        Assert.Null(overlay.InitialGeometries[1]);
    }

    private static CaptureWorkflowSession<object> NewWorkflow(
        AppSettings settings,
        FakeSelectionOverlayAdapter overlay,
        IVirtualDesktopTopologyProvider topology,
        ConfigurationService? configuration = null)
    {
        var capture = new FakeCaptureAdapter
        {
            NextSelectionResult = CaptureFrameResult.Ok(
                ExportTestHelpers.CreateTestBitmap(8, 4), "8x4", 0),
        };
        return new CaptureWorkflowSession<object>(
            capture,
            new FakePreviewAdapter(),
            overlay,
            new FakeMonitorPickerOverlayAdapter(),
            new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(settings),
            new RememberedSelectionState(
                settings,
                topology,
                configuration is null
                    ? null
                    : new ConfigurationRememberedSelectionPersistence(settings, configuration)));
    }

    private sealed class MutableTopology : IVirtualDesktopTopologyProvider
    {
        public IReadOnlyList<MonitorRect> Monitors { get; set; } = new[]
        {
            new MonitorRect(-1280, 0, 0, 1024),
            new MonitorRect(0, 0, 1920, 1080),
        };

        public IReadOnlyList<MonitorRect> GetCurrent() => Monitors;
    }

    private sealed class RecordingConfiguration(bool fail = false) : ConfigurationService(
        Path.Combine(Path.GetTempPath(), $"captcho-workflow-{Guid.NewGuid():N}"))
    {
        public int CallCount { get; private set; }
        public AppSettings? LastSaved { get; private set; }

        public override ConfigurationSaveResult Save(AppSettings settings)
        {
            CallCount++;
            LastSaved = settings;
            return new ConfigurationSaveResult { Success = !fail };
        }
    }
}
