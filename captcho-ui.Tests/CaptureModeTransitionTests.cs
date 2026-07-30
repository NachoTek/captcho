using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public sealed class CaptureModeTransitionTests
{
    public static IEnumerable<object[]> Transitions()
    {
        var sources = new[]
        {
            CaptureMode.SelectedWindow,
            CaptureMode.SelectedMonitor,
            CaptureMode.Selection,
        };

        foreach (var source in sources)
        foreach (CaptureMode destination in Enum.GetValues<CaptureMode>())
            yield return new object[] { source, destination };
    }

    public static IEnumerable<object[]> InteractiveTransitions()
    {
        foreach (var transition in Transitions())
        {
            var destination = (CaptureMode)transition[1];
            if (destination is CaptureMode.SelectedWindow or CaptureMode.SelectedMonitor or CaptureMode.Selection)
                yield return transition;
        }
    }

    public static IEnumerable<object[]> PickerTransitions()
    {
        foreach (var transition in Transitions())
        {
            var destination = (CaptureMode)transition[1];
            if (destination is CaptureMode.SelectedWindow or CaptureMode.SelectedMonitor)
                yield return transition;
        }
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task RoutingCaptureMode_RoutesOnlyTheDestination(
        CaptureMode source,
        CaptureMode destination)
    {
        var fixture = new TransitionFixture();
        fixture.EnqueueRoute(source, destination);
        fixture.EnqueueConfirmation(destination);

        var result = await fixture.Trigger(source);

        Assert.Equal(WorkflowStatus.Succeeded, result.Status);
        Assert.Equal(ExpectedLabel(destination), result.Mode);
        Assert.Equal(1, fixture.CaptureCalls(destination));
        Assert.Equal(1, fixture.TotalCaptureCalls);
    }

    [Theory]
    [MemberData(nameof(InteractiveTransitions))]
    public async Task RoutingThenCancelling_DoesNotCapture(
        CaptureMode source,
        CaptureMode destination)
    {
        var fixture = new TransitionFixture();
        fixture.EnqueueRoute(source, destination);
        fixture.EnqueueCancellation(destination);

        var result = await fixture.Trigger(source);

        Assert.Equal(WorkflowStatus.Cancelled, result.Status);
        Assert.Equal(ExpectedLabel(destination), result.Mode);
        Assert.Equal(0, fixture.TotalCaptureCalls);
    }

    [Theory]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public async Task RoutingToSelection_DoesNotReuseRememberedGeometry(CaptureMode source)
    {
        var settings = AppSettings.WithDefaults();
        settings.RememberSelection = RememberSelectionLifetime.Session;
        var fixture = new TransitionFixture(settings);
        var remembered = new SelectionGeometry { X = 10, Y = 20, Width = 300, Height = 200 };
        var replacement = new SelectionGeometry { X = -500, Y = 40, Width = 80, Height = 60 };
        fixture.Selection.Enqueue(TargetSelectionResult<SelectionGeometry>.Confirmed(remembered));

        await fixture.Trigger(CaptureMode.Selection);

        fixture.EnqueueRoute(source, CaptureMode.Selection);
        fixture.Selection.Enqueue(TargetSelectionResult<SelectionGeometry>.Confirmed(replacement));
        await fixture.Trigger(source);

        Assert.Null(fixture.Selection.InitialGeometries[^1]);
        Assert.Equal(replacement, fixture.Capture.LastSelectionGeometry);
    }

    [Theory]
    [MemberData(nameof(PickerTransitions))]
    public async Task RoutingToPicker_UsesFreshDestinationTarget(
        CaptureMode source,
        CaptureMode destination)
    {
        var fixture = new TransitionFixture();
        if (destination == CaptureMode.SelectedWindow)
        {
            fixture.Window.Enqueue(TargetSelectionResult<WindowPickerResult>.Confirmed(new WindowPickerResult
            {
                Outcome = WindowPickerOutcome.WindowConfirmed,
                Target = new WindowTarget { Handle = (IntPtr)1, Width = 20, Height = 10 },
            }));
        }
        else
        {
            fixture.Monitor.Enqueue(TargetSelectionResult<MonitorTarget>.Confirmed(new MonitorTarget
            {
                X = -100,
                Width = 20,
                Height = 10,
            }));
        }

        await fixture.Trigger(destination);
        fixture.EnqueueRoute(source, destination);
        fixture.EnqueueConfirmation(destination);
        await fixture.Trigger(source);

        if (destination == CaptureMode.SelectedWindow)
            Assert.Equal((IntPtr)42, fixture.Capture.LastWindowTarget!.Handle);
        else
            Assert.Equal((uint)1920, fixture.Capture.LastMonitorTarget!.Width);
    }

    private static string ExpectedLabel(CaptureMode mode) => mode switch
    {
        CaptureMode.FullDesktop => "Full Desktop",
        CaptureMode.ActiveWindow => "Active Window",
        CaptureMode.SelectedWindow => "Selected Window",
        CaptureMode.SelectedMonitor => "Selected Monitor",
        CaptureMode.Selection => "Selection",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private sealed class TransitionFixture
    {
        public FakeCaptureAdapter Capture { get; } = new()
        {
            NextResult = Success(),
            NextActiveWindowResult = Success(),
            NextWindowResult = Success(),
            NextMonitorResult = Success(),
            NextSelectionResult = Success(),
        };

        public ScriptedSelectionOverlay Selection { get; } = new();
        public ScriptedMonitorOverlay Monitor { get; } = new();
        public ScriptedWindowOverlay Window { get; } = new();
        private readonly CaptureWorkflowSession<object> _session;

        public TransitionFixture(AppSettings? settings = null)
        {
            settings ??= AppSettings.WithDefaults();
            _session = new CaptureWorkflowSession<object>(
                Capture,
                new FakePreviewAdapter(),
                Selection,
                Monitor,
                Window,
                new SessionCaptureOptions(settings),
                new RememberedSelectionState(settings, new FixedTopology()));
        }

        public int TotalCaptureCalls =>
            Capture.CallCount + Capture.ActiveWindowCallCount + Capture.WindowCallCount
            + Capture.MonitorCallCount + Capture.SelectionCallCount;

        public int CaptureCalls(CaptureMode mode) => mode switch
        {
            CaptureMode.FullDesktop => Capture.CallCount,
            CaptureMode.ActiveWindow => Capture.ActiveWindowCallCount,
            CaptureMode.SelectedWindow => Capture.WindowCallCount,
            CaptureMode.SelectedMonitor => Capture.MonitorCallCount,
            CaptureMode.Selection => Capture.SelectionCallCount,
            _ => 0,
        };

        public Task<WorkflowResult<object>> Trigger(CaptureMode mode) => mode switch
        {
            CaptureMode.FullDesktop => _session.CaptureFullDesktopAsync(),
            CaptureMode.ActiveWindow => _session.CaptureActiveWindowAsync(),
            CaptureMode.SelectedWindow => _session.CaptureSelectedWindowAsync(),
            CaptureMode.SelectedMonitor => _session.CaptureSelectedMonitorAsync(),
            CaptureMode.Selection => _session.CaptureSelectionAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        public void EnqueueRoute(CaptureMode source, CaptureMode destination)
        {
            switch (source)
            {
                case CaptureMode.SelectedWindow:
                    Window.Enqueue(TargetSelectionResult<WindowPickerResult>.RouteTo(destination));
                    break;
                case CaptureMode.SelectedMonitor:
                    Monitor.Enqueue(TargetSelectionResult<MonitorTarget>.RouteTo(destination));
                    break;
                case CaptureMode.Selection:
                    Selection.Enqueue(TargetSelectionResult<SelectionGeometry>.RouteTo(destination));
                    break;
            }
        }

        public void EnqueueConfirmation(CaptureMode mode)
        {
            switch (mode)
            {
                case CaptureMode.SelectedWindow:
                    Window.Enqueue(TargetSelectionResult<WindowPickerResult>.Confirmed(new WindowPickerResult
                    {
                        Outcome = WindowPickerOutcome.WindowConfirmed,
                        Target = new WindowTarget { Handle = (IntPtr)42, Width = 100, Height = 80 },
                    }));
                    break;
                case CaptureMode.SelectedMonitor:
                    Monitor.Enqueue(TargetSelectionResult<MonitorTarget>.Confirmed(new MonitorTarget
                    {
                        Width = 1920,
                        Height = 1080,
                    }));
                    break;
                case CaptureMode.Selection:
                    Selection.Enqueue(TargetSelectionResult<SelectionGeometry>.Confirmed(new SelectionGeometry
                    {
                        X = 5,
                        Y = 10,
                        Width = 100,
                        Height = 80,
                    }));
                    break;
            }
        }

        public void EnqueueCancellation(CaptureMode mode)
        {
            switch (mode)
            {
                case CaptureMode.SelectedWindow:
                    Window.Enqueue(null);
                    break;
                case CaptureMode.SelectedMonitor:
                    Monitor.Enqueue(null);
                    break;
                case CaptureMode.Selection:
                    Selection.Enqueue(null);
                    break;
            }
        }

        private static CaptureFrameResult Success() => CaptureFrameResult.Ok(
            ExportTestHelpers.CreateTestBitmap(8, 4), "8x4", 0);
    }

    private sealed class ScriptedSelectionOverlay : ISelectionOverlayAdapter
    {
        private readonly Queue<TargetSelectionResult<SelectionGeometry>?> _results = new();
        public List<SelectionGeometry?> InitialGeometries { get; } = new();

        public void Enqueue(TargetSelectionResult<SelectionGeometry>? result) => _results.Enqueue(result);

        public Task<TargetSelectionResult<SelectionGeometry>?> ShowAsync(SelectionGeometry? initialGeometry)
        {
            InitialGeometries.Add(initialGeometry);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ScriptedMonitorOverlay : IMonitorPickerOverlayAdapter
    {
        private readonly Queue<TargetSelectionResult<MonitorTarget>?> _results = new();
        public void Enqueue(TargetSelectionResult<MonitorTarget>? result) => _results.Enqueue(result);
        public Task<TargetSelectionResult<MonitorTarget>?> ShowAsync() => Task.FromResult(_results.Dequeue());
    }

    private sealed class ScriptedWindowOverlay : IWindowPickerOverlayAdapter
    {
        private readonly Queue<TargetSelectionResult<WindowPickerResult>?> _results = new();
        public void Enqueue(TargetSelectionResult<WindowPickerResult>? result) => _results.Enqueue(result);
        public Task<TargetSelectionResult<WindowPickerResult>?> ShowAsync() => Task.FromResult(_results.Dequeue());
    }

    private sealed class FixedTopology : IVirtualDesktopTopologyProvider
    {
        public IReadOnlyList<MonitorRect> GetCurrent() => new[] { new MonitorRect(0, 0, 1920, 1080) };
    }
}
