// LaunchBehaviorWorkflowTests.cs — Production-side tests for startup launch
// routing through the Workflow Session (issue #53).
//
// Verifies the WinUI-free pieces the startup path composes:
// LaunchBehaviorState records the last Capture Mode only while some launch
// behavior needs it (Last Capture Mode), and persists through Configuration
// atomically; the resolved startup Trigger dispatches to the same five
// production workflow routes the hotkeys use — never bespoke capture code;
// and the workflow session itself records the Capture Mode of every
// successful capture through the wired state.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Last Capture Mode recording (LaunchBehaviorState) ───────────────────

public class LaunchBehaviorStateTests
{
    private sealed class RecordingPersistence : ILaunchBehaviorPersistence
    {
        public CaptureMode? LastSaved { get; private set; }
        public int SaveCount { get; private set; }
        public bool NextFails { get; set; }

        public bool Save(CaptureMode? mode)
        {
            SaveCount++;
            if (NextFails)
                return false;
            LastSaved = mode;
            return true;
        }
    }

    [Fact]
    public void Record_DoNothing_PersistsNothing()
    {
        // "Remember the last mode only when required": with Do nothing (or an
        // invalid action) configured, recording is a no-op.
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.DoNothing, null),
        };
        var persistence = new RecordingPersistence();
        var state = new LaunchBehaviorState(settings, persistence);

        state.Record(CaptureMode.Selection);

        Assert.Equal(0, persistence.SaveCount);
    }

    [Fact]
    public void Record_ConfiguredMode_PersistsNothing()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, CaptureMode.FullDesktop),
        };
        var persistence = new RecordingPersistence();
        var state = new LaunchBehaviorState(settings, persistence);

        state.Record(CaptureMode.Selection);

        Assert.Equal(0, persistence.SaveCount);
    }

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void Record_LastCaptureMode_PersistsEveryMode(CaptureMode mode)
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
        };
        var persistence = new RecordingPersistence();
        var state = new LaunchBehaviorState(settings, persistence);

        state.Record(mode);

        Assert.Equal(1, persistence.SaveCount);
        Assert.Equal(mode, persistence.LastSaved);
        // The runtime settings object observes the recorded mode.
        Assert.Equal(mode, settings.LastCaptureMode);
    }

    [Fact]
    public void Record_InvalidAction_PersistsNothing()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings((LaunchAction)99, null),
        };
        var persistence = new RecordingPersistence();
        var state = new LaunchBehaviorState(settings, persistence);

        state.Record(CaptureMode.Selection);

        Assert.Equal(0, persistence.SaveCount);
    }

    [Fact]
    public void Record_InvalidModeEnum_IsRejected()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
        };
        var persistence = new RecordingPersistence();
        var state = new LaunchBehaviorState(settings, persistence);

        state.Record((CaptureMode)77);

        Assert.Equal(0, persistence.SaveCount);
    }

    [Fact]
    public void Record_PersistenceFailure_NonFatalAndRuntimeUntouched()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
        };
        var persistence = new RecordingPersistence { NextFails = true };
        var state = new LaunchBehaviorState(settings, persistence);

        state.Record(CaptureMode.Selection);

        Assert.Equal(1, persistence.SaveCount);
        Assert.Null(settings.LastCaptureMode);
    }

    [Fact]
    public void Record_NullPersistence_UpdatesInMemorySettingsOnly()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
        };
        var state = new LaunchBehaviorState(settings);

        state.Record(CaptureMode.SelectedMonitor);

        Assert.Equal(CaptureMode.SelectedMonitor, settings.LastCaptureMode);
    }

    [Fact]
    public void Record_ReadsTheLiveSettingsSoCommittedChangesApplyImmediately()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.DoNothing, null),
        };
        var persistence = new RecordingPersistence();
        var state = new LaunchBehaviorState(settings, persistence);

        state.Record(CaptureMode.Selection);
        settings.LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null);
        state.Record(CaptureMode.Selection);

        Assert.Equal(1, persistence.SaveCount);
    }
}

// ── Persistence through Configuration ───────────────────────────────────

public class LaunchBehaviorPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public LaunchBehaviorPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"captchoLaunchUi_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Save_PersistsLastModeOnly_LeavingOtherSlicesUntouched()
    {
        var settings = AppSettings.WithDefaults();
        settings.SaveLocation = @"D:\Preserved";
        var configuration = new ConfigurationService(_tempDir);
        var persistence = new ConfigurationLaunchBehaviorPersistence(settings, configuration);

        Assert.True(persistence.Save(CaptureMode.SelectedMonitor));

        var loaded = configuration.Load();
        Assert.True(loaded.Success);
        Assert.Equal(CaptureMode.SelectedMonitor, loaded.Settings.LastCaptureMode);
        Assert.Equal(@"D:\Preserved", loaded.Settings.SaveLocation);
        // The launch behavior itself is untouched by the recording.
        Assert.Equal(LaunchAction.DoNothing, loaded.Settings.LaunchBehavior.Action);
        // The runtime settings object observes the recorded mode.
        Assert.Equal(CaptureMode.SelectedMonitor, settings.LastCaptureMode);
    }

    [Fact]
    public void Save_Failure_ReturnsFalseWithoutTouchingRuntime()
    {
        var settings = AppSettings.WithDefaults();
        var configuration = new ThrowingConfiguration(_tempDir);
        var persistence = new ConfigurationLaunchBehaviorPersistence(settings, configuration);

        Assert.False(persistence.Save(CaptureMode.Selection));
        Assert.Null(settings.LastCaptureMode);
    }

    private sealed class ThrowingConfiguration : ConfigurationService
    {
        public ThrowingConfiguration(string directory) : base(directory) { }

        public override ConfigurationSaveResult Save(AppSettings settings) =>
            new() { Success = false, Phase = "WriteTemp", ErrorMessage = "Disk unavailable." };
    }
}

// ── Startup Trigger routing through the production workflow ────────────

public class LaunchBehaviorRoutingTests
{
    private sealed class RouteRecorder
    {
        public string? LastRoute { get; private set; }
        public int Count { get; private set; }

        public Task Route(string name)
        {
            LastRoute = name;
            Count++;
            return Task.CompletedTask;
        }
    }

    private static (LaunchTriggerRouter Router, RouteRecorder Recorder) CreateRouter()
    {
        var recorder = new RouteRecorder();
        var routes = new ProductionWorkflowRoutes(
            () => recorder.Route("FullDesktop"),
            () => recorder.Route("ActiveWindow"),
            () => recorder.Route("Selection"),
            () => recorder.Route("SelectedMonitor"),
            () => recorder.Route("SelectedWindow"));
        return (new LaunchTriggerRouter(routes), recorder);
    }

    [Fact]
    public async Task RunStartup_DoNothing_StaysIdle()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.DoNothing, null),
        };
        var (router, recorder) = CreateRouter();

        var dispatched = await router.RunStartupAsync(settings);

        Assert.False(dispatched);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task RunStartup_LastCaptureMode_Absent_StaysIdle()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
            LastCaptureMode = null,
        };
        var (router, recorder) = CreateRouter();

        var dispatched = await router.RunStartupAsync(settings);

        Assert.False(dispatched);
        Assert.Equal(0, recorder.Count);
    }

    [Theory]
    [InlineData(CaptureMode.FullDesktop, "FullDesktop")]
    [InlineData(CaptureMode.ActiveWindow, "ActiveWindow")]
    [InlineData(CaptureMode.Selection, "Selection")]
    [InlineData(CaptureMode.SelectedMonitor, "SelectedMonitor")]
    [InlineData(CaptureMode.SelectedWindow, "SelectedWindow")]
    public async Task RunStartup_EveryResolvedMode_RoutesToTheProductionWorkflow(
        CaptureMode mode, string expectedRoute)
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, mode),
        };
        var (router, recorder) = CreateRouter();

        var dispatched = await router.RunStartupAsync(settings);

        Assert.True(dispatched);
        Assert.Equal(expectedRoute, recorder.LastRoute);
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task RunStartup_ConfiguredMode_InvalidEnum_StaysIdle()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.ConfiguredCaptureMode, (CaptureMode)9),
        };
        var (router, recorder) = CreateRouter();

        var dispatched = await router.RunStartupAsync(settings);

        Assert.False(dispatched);
        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task RunStartup_NullSettings_Throws()
    {
        var (router, _) = CreateRouter();

        await Assert.ThrowsAsync<ArgumentNullException>(() => router.RunStartupAsync(null!));
    }

    [Fact]
    public async Task RunStartup_LastCaptureMode_Present_TriggersTheRememberedMode()
    {
        var settings = new AppSettings
        {
            LaunchBehavior = new LaunchBehaviorSettings(LaunchAction.LastCaptureMode, null),
            LastCaptureMode = CaptureMode.ActiveWindow,
        };
        var (router, recorder) = CreateRouter();

        var dispatched = await router.RunStartupAsync(settings);

        Assert.True(dispatched);
        Assert.Equal("ActiveWindow", recorder.LastRoute);
    }
}

// ── Workflow session records the Capture Mode of successful captures ───

public class LaunchBehaviorWorkflowRecordingTests
{
    private sealed class RecordingState : ILaunchBehaviorRecorder
    {
        public int RecordCount { get; private set; }
        public CaptureMode? LastRecorded { get; private set; }

        public void Record(CaptureMode mode)
        {
            RecordCount++;
            LastRecorded = mode;
        }
    }

    private static CaptureWorkflowSession<object> CreateSession(
        ILaunchBehaviorRecorder? recorder,
        out FakeCaptureAdapter capture,
        out FakePreviewAdapter preview,
        FakeSelectionOverlayAdapter? selection = null,
        FakeMonitorPickerOverlayAdapter? monitorPicker = null,
        FakeWindowPickerOverlayAdapter? windowPicker = null)
    {
        capture = new FakeCaptureAdapter
        {
            NextResult = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1),
        };
        preview = new FakePreviewAdapter
        {
            NextResult = PreviewPresentResult<object>.Ok(new object(), 1),
        };
        return new CaptureWorkflowSession<object>(
            capture,
            preview,
            selection ?? new FakeSelectionOverlayAdapter(),
            monitorPicker ?? new FakeMonitorPickerOverlayAdapter(),
            windowPicker ?? new FakeWindowPickerOverlayAdapter(),
            new SessionCaptureOptions(AppSettings.WithDefaults()),
            RememberedSelectionState.Disabled,
            new FakeAnnotationOverlayAdapter
            {
                NextResult = AnnotationPresentResult.Fail("unused"),
            },
            static () => false,
            static () => AnnotationToolState.WithDefaults(),
            new FakeOcrEngine(),
            static () => null,
            new ScriptedExportAdapter(),
            new FakeSaveAsDialogAdapter(),
            static () => AutomaticExportSettings.WithDefaults(),
            recorder);
    }

    [Fact]
    public async Task CaptureSuccess_RecordsTheTriggeredMode()
    {
        var recorder = new RecordingState();
        var session = CreateSession(recorder, out _, out _);

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CaptureMode.FullDesktop, result.TriggeredCaptureMode);
        Assert.Equal(1, recorder.RecordCount);
        Assert.Equal(CaptureMode.FullDesktop, recorder.LastRecorded);
    }

    [Theory]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.Selection)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.SelectedWindow)]
    public async Task CaptureSuccess_EveryRouteRecordsItsMode(CaptureMode mode)
    {
        var recorder = new RecordingState();
        var selection = new FakeSelectionOverlayAdapter
        {
            NextGeometry = new SelectionGeometry { X = 0, Y = 0, Width = 8, Height = 4 },
        };
        var monitorPicker = new FakeMonitorPickerOverlayAdapter
        {
            NextTarget = new MonitorTarget { X = 0, Y = 0, Width = 8, Height = 4 },
        };
        var windowPicker = new FakeWindowPickerOverlayAdapter
        {
            NextResult = new WindowPickerResult
            {
                Outcome = WindowPickerOutcome.WindowConfirmed,
                Target = new WindowTarget { Handle = new IntPtr(1), Width = 8, Height = 4 },
            },
        };
        var session = CreateSession(recorder, out var capture, out _, selection, monitorPicker, windowPicker);
        ConfigureRoute(mode, capture);

        var result = await RouteAsync(session, mode);

        Assert.True(result.IsSuccess);
        Assert.Equal(mode, result.TriggeredCaptureMode);
        Assert.Equal(mode, recorder.LastRecorded);
    }

    [Fact]
    public async Task CaptureFailure_RecordsNothing()
    {
        var recorder = new RecordingState();
        var session = CreateSession(recorder, out var capture, out _);
        capture.NextResult = CaptureFrameResult.Fail("adapter failed");

        var result = await session.CaptureFullDesktopAsync();

        Assert.Equal(WorkflowStatus.CaptureFailed, result.Status);
        Assert.Null(result.TriggeredCaptureMode);
        Assert.Equal(0, recorder.RecordCount);
    }

    [Fact]
    public async Task UnwiredSession_RecordsNothingAndStillSucceeds()
    {
        // The recorder is optional: sessions built without launch wiring
        // (tests, or a future host) keep working unchanged.
        var session = CreateSession(null, out _, out _);

        var result = await session.CaptureFullDesktopAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(CaptureMode.FullDesktop, result.TriggeredCaptureMode);
    }

    private static void ConfigureRoute(CaptureMode mode, FakeCaptureAdapter capture)
    {
        var frame = CaptureFrameResult.Ok(ExportTestHelpers.CreateTestBitmap(8, 4), "8×4", 1);
        switch (mode)
        {
            case CaptureMode.ActiveWindow:
                capture.NextActiveWindowResult = frame;
                break;
            case CaptureMode.Selection:
                capture.NextSelectionResult = frame;
                break;
            case CaptureMode.SelectedMonitor:
                capture.NextMonitorResult = frame;
                break;
            case CaptureMode.SelectedWindow:
                capture.NextWindowResult = frame;
                break;
        }
    }

    private static Task<WorkflowResult<object>> RouteAsync(CaptureWorkflowSession<object> session, CaptureMode mode) => mode switch
    {
        CaptureMode.ActiveWindow => session.CaptureActiveWindowAsync(),
        CaptureMode.Selection => session.CaptureSelectionAsync(),
        CaptureMode.SelectedMonitor => session.CaptureSelectedMonitorAsync(),
        CaptureMode.SelectedWindow => session.CaptureSelectedWindowAsync(),
        _ => session.CaptureFullDesktopAsync(),
    };
}
