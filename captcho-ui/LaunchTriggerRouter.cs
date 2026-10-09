// LaunchTriggerRouter.cs — Startup launch routing through the production
// workflow (issue #53).
//
// The startup path resolves the configured launch behavior through
// AppSettings.ResolveStartupMode and dispatches the resolved Capture Mode to
// the same five production workflow routes the hotkeys and buttons use —
// never bespoke capture code. Recording the last Capture Mode (when the
// launch behavior needs it) is owned by the session's LaunchBehaviorState,
// wired exactly as it is for hotkey and button Triggers.

using System;
using System.Threading.Tasks;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// The five production workflow entry points a startup Trigger dispatches
/// to — the same routes the main window's buttons and Global Hotkeys use.
/// </summary>
public sealed class ProductionWorkflowRoutes
{
    private readonly Func<Task> _fullDesktop;
    private readonly Func<Task> _activeWindow;
    private readonly Func<Task> _selection;
    private readonly Func<Task> _selectedMonitor;
    private readonly Func<Task> _selectedWindow;

    public ProductionWorkflowRoutes(
        Func<Task> fullDesktop,
        Func<Task> activeWindow,
        Func<Task> selection,
        Func<Task> selectedMonitor,
        Func<Task> selectedWindow)
    {
        _fullDesktop = fullDesktop ?? throw new ArgumentNullException(nameof(fullDesktop));
        _activeWindow = activeWindow ?? throw new ArgumentNullException(nameof(activeWindow));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _selectedMonitor = selectedMonitor ?? throw new ArgumentNullException(nameof(selectedMonitor));
        _selectedWindow = selectedWindow ?? throw new ArgumentNullException(nameof(selectedWindow));
    }

    /// <summary>Dispatches the mode to its production workflow route.</summary>
    public Task DispatchAsync(CaptureMode mode) => mode switch
    {
        CaptureMode.FullDesktop => _fullDesktop(),
        CaptureMode.ActiveWindow => _activeWindow(),
        CaptureMode.Selection => _selection(),
        CaptureMode.SelectedMonitor => _selectedMonitor(),
        CaptureMode.SelectedWindow => _selectedWindow(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

/// <summary>
/// Routes the configured launch behavior at startup: resolves the startup
/// Capture Mode through <see cref="LaunchBehaviorSettingsExtensions.ResolveStartupMode"/>
/// and dispatches it through the production workflow routes — the same
/// Trigger paths the hotkeys use. A null or invalid resolved mode keeps
/// startup idle.
/// </summary>
public sealed class LaunchTriggerRouter
{
    private readonly ProductionWorkflowRoutes _routes;

    public LaunchTriggerRouter(ProductionWorkflowRoutes routes)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
    }

    /// <summary>
    /// Runs the configured launch behavior for the supplied settings.
    /// Returns true when a startup Trigger was dispatched; false when startup
    /// stayed idle (Do nothing, nothing recorded, or invalid persisted
    /// values).
    /// </summary>
    public Task<bool> RunStartupAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var mode = settings.ResolveStartupMode();
        if (mode is null || !Enum.IsDefined(mode.Value))
            return Task.FromResult(false);

        return DispatchAsync(mode.Value);
    }

    private async Task<bool> DispatchAsync(CaptureMode mode)
    {
        await _routes.DispatchAsync(mode);
        return true;
    }
}
