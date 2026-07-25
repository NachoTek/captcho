// SettingsWindowCoordinator.cs — Testable lifecycle coordinator for the settings window.
//
// Manages singleton settings window lifecycle only: create-or-activate, close cleanup.
// On each window-open it constructs a fresh SettingsSession (the composed, WinUI-free
// session that owns the Apply/OK/Cancel/Reset flow) through a delegate and hands it to
// the window factory, so the window receives a fully-wired session and stays a thin
// adapter. Uses delegates throughout to avoid WinUI control dependencies and enable
// headless testing.

using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("captcho-ui.Tests")]

namespace captcho.UI;

/// <summary>
/// Factory delegate that constructs a fresh <see cref="SettingsSession"/> for a new
/// window-open. Captures the shared runtime settings, ConfigurationService, and Global
/// Global Hotkey adapter so the coordinator stays free of those dependencies.
/// </summary>
/// <returns>A new session bound to the live runtime settings.</returns>
public delegate SettingsSession CreateSettingsSession();

/// <summary>
/// Factory delegate that creates a settings window instance for a given session.
/// Returns a handle that can be used for activation and closed tracking.
/// </summary>
/// <param name="session">The composed session the window routes events to.</param>
/// <returns>A window handle, or null if creation failed.</returns>
public delegate object? CreateSettingsWindow(SettingsSession session);

/// <summary>
/// Delegate to activate an existing settings window.
/// </summary>
/// <param name="windowHandle">Handle returned from <see cref="CreateSettingsWindow"/>.</param>
public delegate void ActivateSettingsWindow(object windowHandle);

/// <summary>
/// Delegate to subscribe to window closed events.
/// </summary>
/// <param name="windowHandle">Handle returned from <see cref="CreateSettingsWindow"/>.</param>
/// <param name="onClosed">Callback invoked when the window closes.</param>
public delegate void SubscribeToWindowClosed(object windowHandle, Action onClosed);

/// <summary>
/// Manages singleton settings window lifecycle with testable delegates. On each open it
/// constructs one <see cref="SettingsSession"/> and a window bound to it. Ensures only
/// one settings window exists at a time and tracks state across create/activate/close.
/// </summary>
public sealed class SettingsWindowCoordinator
{
    private readonly CreateSettingsSession _createSession;
    private readonly CreateSettingsWindow _createWindow;
    private readonly ActivateSettingsWindow _activateWindow;
    private readonly SubscribeToWindowClosed _subscribeToClosed;

    // Lifecycle state
    private object? _currentWindow;

    /// <summary>
    /// Creates a coordinator with explicit delegates (production wiring and testing).
    /// The session/window/activate/closed delegates are all required — there is no
    /// production-default constructor because every delegate must be wired to real
    /// WinUI behavior by the host (MainWindow).
    /// </summary>
    public SettingsWindowCoordinator(
        CreateSettingsSession createSession,
        CreateSettingsWindow createWindow,
        ActivateSettingsWindow activateWindow,
        SubscribeToWindowClosed subscribeToClosed)
    {
        _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        _createWindow = createWindow ?? throw new ArgumentNullException(nameof(createWindow));
        _activateWindow = activateWindow ?? throw new ArgumentNullException(nameof(activateWindow));
        _subscribeToClosed = subscribeToClosed ?? throw new ArgumentNullException(nameof(subscribeToClosed));
    }

    /// <summary>
    /// Current settings window handle, or null if no window is open.
    /// </summary>
    public object? CurrentWindow => _currentWindow;

    /// <summary>
    /// Whether a settings window is currently open.
    /// </summary>
    public bool IsWindowOpen => _currentWindow != null;

    /// <summary>
    /// Creates or activates the settings window. If a window already exists, activates
    /// it instead of creating a new one. On a fresh open, constructs a new session and
    /// a window bound to it.
    /// </summary>
    /// <returns>True if a window exists (either created or activated), false if creation failed.</returns>
    public bool CreateOrActivate()
    {
        // If window already exists, activate it
        if (_currentWindow != null)
        {
            _activateWindow(_currentWindow);
            return true;
        }

        // One composed session per window-open.
        var session = _createSession();
        var newWindow = _createWindow(session);
        if (newWindow == null)
        {
            return false; // Creation failed
        }

        // Subscribe to closed events for cleanup
        _subscribeToClosed(newWindow, OnWindowClosed);

        _currentWindow = newWindow;
        return true;
    }

    /// <summary>
    /// Cleanup callback invoked when the settings window closes.
    /// Clears the current window reference.
    /// </summary>
    private void OnWindowClosed()
    {
        _currentWindow = null;
    }
}
