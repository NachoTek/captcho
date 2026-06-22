// SettingsWindowCoordinator.cs — Testable lifecycle coordinator for settings window.
//
// Manages singleton settings window lifecycle: create-or-activate, close cleanup,
// and save-result reporting. Uses delegates to avoid WinUI control dependencies
// and enable headless testing. Mirrors MainWindow settings window logic.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using captcho.Capture;

[assembly: InternalsVisibleTo("captcho-ui.Tests")]

namespace captcho.UI;

/// <summary>
/// Factory delegate for creating a settings window instance.
/// Returns a handle that can be used for activation and closed tracking.
/// </summary>
/// <returns>A window handle, or null if creation failed.</returns>
public delegate object? CreateSettingsWindow();

/// <summary>
/// Delegate to activate an existing settings window.
/// </summary>
/// <param name="windowHandle">Handle returned from CreateSettingsWindow.</param>
public delegate void ActivateSettingsWindow(object windowHandle);

/// <summary>
/// Delegate to subscribe to window closed events.
/// </summary>
/// <param name="windowHandle">Handle returned from CreateSettingsWindow.</param>
/// <param name="onClosed">Callback invoked when the window closes.</param>
public delegate void SubscribeToWindowClosed(object windowHandle, Action onClosed);

/// <summary>
/// Result of a settings save operation formatted for UI display.
/// </summary>
public sealed class SettingsSaveReport
{
    /// <summary>Whether the save succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Formatted message suitable for UI display (sanitized, no stack traces).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Phase where failure occurred, if any.</summary>
    public string? Phase { get; init; }

    /// <summary>Full path to the configuration file.</summary>
    public string ConfigPath { get; init; } = string.Empty;
}

/// <summary>
/// Manages singleton settings window lifecycle with testable delegates.
/// Ensures only one settings window exists at a time and tracks state
/// across create/activate/close operations.
/// </summary>
public sealed class SettingsWindowCoordinator
{
    private readonly ConfigurationService _configurationService;
    private readonly CreateSettingsWindow _createWindow;
    private readonly ActivateSettingsWindow _activateWindow;
    private readonly SubscribeToWindowClosed _subscribeToClosed;

    // Lifecycle state
    private object? _currentWindow;

    /// <summary>
    /// Creates a coordinator with production defaults.
    /// </summary>
    public SettingsWindowCoordinator()
        : this(
            new ConfigurationService(),
            () => throw new NotImplementedException("Production CreateSettingsWindow must be provided"),
            _ => throw new NotImplementedException("Production ActivateSettingsWindow must be provided"),
            (_, _) => throw new NotImplementedException("Production SubscribeToWindowClosed must be provided"))
    {
    }

    /// <summary>
    /// Creates a coordinator with explicit delegates (for testing or dependency injection).
    /// </summary>
    public SettingsWindowCoordinator(
        ConfigurationService configurationService,
        CreateSettingsWindow createWindow,
        ActivateSettingsWindow activateWindow,
        SubscribeToWindowClosed subscribeToClosed)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
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
    /// Creates or activates the settings window.
    /// If a window already exists, activates it instead of creating a new one.
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

        // Create new window
        var newWindow = _createWindow();
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
    /// Saves settings and returns a formatted report.
    /// Never throws; returns a report with success/failure information.
    /// </summary>
    /// <param name="settings">Settings to save.</param>
    /// <returns>Report with success status and sanitized error messages.</returns>
    public SettingsSaveReport SaveSettings(AppSettings settings)
    {
        var result = _configurationService.Save(settings);

        if (result.Success)
        {
            return new SettingsSaveReport
            {
                Success = true,
                Message = "Settings saved successfully.",
                ConfigPath = result.ConfigPath,
            };
        }
        else
        {
            // Sanitized error message from ConfigurationService
            var message = FormatSaveFailureMessage(result);

            return new SettingsSaveReport
            {
                Success = false,
                Message = message,
                Phase = result.Phase,
                ConfigPath = result.ConfigPath,
            };
        }
    }

    /// <summary>
    /// Loads settings from the configuration file.
    /// Never throws; returns defaults if load fails.
    /// </summary>
    /// <returns>Loaded settings or defaults.</returns>
    public AppSettings LoadSettings()
    {
        var result = _configurationService.Load();
        return result.Settings;
    }

    /// <summary>
    /// Cleanup callback invoked when the settings window closes.
    /// Clears the current window reference.
    /// </summary>
    private void OnWindowClosed()
    {
        _currentWindow = null;
    }

    /// <summary>
    /// Formats a save failure message for UI display.
    /// Keeps sanitized error messages visible without throwing.
    /// Internal for testability.
    /// </summary>
    internal static string FormatSaveFailureMessage(ConfigurationSaveResult result)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(result.Phase))
        {
            parts.Add($"Failed during '{result.Phase}'.");
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            parts.Add(result.ErrorMessage);
        }

        if (parts.Count == 0)
        {
            return "Failed to save settings.";
        }

        return string.Join(" ", parts);
    }
}