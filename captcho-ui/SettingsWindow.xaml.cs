// SettingsWindow.xaml.cs — Settings dialog with tabbed configuration pages.
//
// Provides a Window subclass (not ContentDialog) with TabView containing four tabs:
// General, Hotkeys, Export, and Interface. Tab content is placeholder until downstream
// slices implement each tab. Bottom row contains OK (save+close), Cancel (close),
// and Apply (save) buttons with inline save status feedback.
// Window position and size are persisted to ApplicationData.Current.LocalSettings.

using System;
using Microsoft.UI.Xaml;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Settings dialog window with tabbed configuration pages.
/// Single-instance behavior is enforced by SettingsWindowCoordinator.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ConfigurationService? _configService;
    private readonly Windows.Storage.ApplicationDataContainer _localSettings;

    private const string WindowPlacementKey = "SettingsWindowPlacement";

    /// <summary>
    /// Production constructor that receives current settings and optional configuration service.
    /// </summary>
    /// <param name="settings">Current application settings (will not be mutated in S01).</param>
    /// <param name="configService">Optional configuration service for persisting settings (nullable for design-time).</param>
    public SettingsWindow(AppSettings settings, ConfigurationService? configService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configService = configService;
        _localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

        InitializeComponent();

        // Set a reasonable default window size
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(700, 500));

        // Restore saved window placement if available
        LoadWindowPlacement();

        // Hook Closed event for coordinator cleanup
        this.Closed += OnWindowClosed;
    }

    /// <summary>
    /// Handles OK button click — saves settings and closes the window.
    /// </summary>
    private void OK_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowPlacement();
        SaveSettings();
        Close();
    }

    /// <summary>
    /// Handles Cancel button click — closes the window without saving.
    /// </summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowPlacement();
        Close();
    }

    /// <summary>
    /// Handles Apply button click — saves settings while keeping the window open.
    /// </summary>
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    /// <summary>
    /// Saves current settings using ConfigurationService and displays inline status.
    /// In S01, this saves the original settings without field mutations since no editable
    /// fields exist yet. Downstream tabs will add field bindings and update _settings.
    /// </summary>
    private void SaveSettings()
    {
        if (_configService == null)
        {
            SaveStatusText.Text = "Cannot save: ConfigurationService not available (design-time).";
            SaveStatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorCautionBrush"];
            return;
        }

        try
        {
            var result = _configService.Save(_settings);

            if (result.Success)
            {
                SaveStatusText.Text = "Settings saved successfully.";
                SaveStatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            }
            else
            {
                // Use sanitized error message from ConfigurationSaveResult
                var message = FormatSaveResultMessage(result);
                SaveStatusText.Text = $"Save failed: {message}";
                SaveStatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorCriticalBrush"];
            }
        }
        catch (Exception ex)
        {
            // Unexpected exceptions should not crash the window; surface inline instead
            SaveStatusText.Text = $"Save failed: {ex.Message}";
            SaveStatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorCriticalBrush"];
        }
    }

    /// <summary>
    /// Formats a ConfigurationSaveResult into a user-friendly message using sanitized fields.
    /// </summary>
    private static string FormatSaveResultMessage(ConfigurationSaveResult result)
    {
        if (result.Phase != null && result.ErrorMessage != null)
        {
            return $"{result.Phase}: {result.ErrorMessage}";
        }
        else if (result.Phase != null)
        {
            return $"Failed during {result.Phase}.";
        }
        else if (result.ErrorMessage != null)
        {
            return result.ErrorMessage;
        }
        else
        {
            return "Unknown error.";
        }
    }

    /// <summary>
    /// Loads saved window placement (position and size) from LocalSettings.
    /// Uses safe defaults if saved values are missing or malformed.
    /// </summary>
    private void LoadWindowPlacement()
    {
        try
        {
            var placement = _localSettings.Values[WindowPlacementKey] as string;
            if (string.IsNullOrEmpty(placement))
                return;

            var parts = placement.Split(',');
            if (parts.Length != 4)
                return;

            // Parse with safe defaults on failure
            if (int.TryParse(parts[0], out int x) &&
                int.TryParse(parts[1], out int y) &&
                int.TryParse(parts[2], out int width) &&
                int.TryParse(parts[3], out int height))
            {
                // Clamp to reasonable minimum size
                width = Math.Max(width, 400);
                height = Math.Max(height, 300);

                var appWindow = this.AppWindow;
                appWindow.Move(new Windows.Graphics.PointInt32(x, y));
                appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            }
        }
        catch
        {
            // Malformed placement data — ignore and use defaults
        }
    }

    /// <summary>
    /// Saves current window placement (position and size) to LocalSettings.
    /// </summary>
    private void SaveWindowPlacement()
    {
        try
        {
            var appWindow = this.AppWindow;
            var position = appWindow.Position;
            var size = appWindow.Size;

            var placement = $"{position.X},{position.Y},{size.Width},{size.Height}";
            _localSettings.Values[WindowPlacementKey] = placement;
        }
        catch
        {
            // Placement save failure is non-fatal; continue
        }
    }

    /// <summary>
    /// Handles window closed event — unhooks event handler and notifies coordinator
    /// (the coordinator's OnWindowClosed delegate will clear the current reference).
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        this.Closed -= OnWindowClosed;
    }
}