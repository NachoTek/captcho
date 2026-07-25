// SettingsWindow.xaml.cs — Settings dialog with tabbed configuration pages.
//
// Provides a Window subclass (not ContentDialog) with TabView containing four tabs:
// General, Hotkeys, Export, and Interface. The General tab edits the Filename Template
// through the pure C# GeneralTabSettings seam (preview, validation, Apply/OK/Cancel).
// Hotkeys/Export/Interface remain placeholders for later slices. The bottom row has
// OK (save+close on success), Cancel (discard+close), and Apply (save, stay open).
// Window position and size are persisted to ApplicationData.Current.LocalSettings.

using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>
    /// Pure C# editing seam for the General tab. Null only when the window fails to
    /// initialize (e.g., unexpected design-time state). All General-tab UI state and the
    /// Apply/OK/Cancel session flow through this coordinator.
    /// </summary>
    private GeneralTabSettings? _generalTab;

    private const string WindowPlacementKey = "SettingsWindowPlacement";

    /// <summary>
    /// Production constructor that receives current settings and optional configuration service.
    /// </summary>
    /// <param name="settings">Current application settings (snapshotted by the General tab; never mutated).</param>
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

        // Wire the General tab editing seam (snapshots settings; never mutates the active settings)
        InitializeGeneralTab();

        // Hook Closed event for coordinator cleanup
        this.Closed += OnWindowClosed;
    }

    // ── General tab seam wiring ──────────────────────────────────────────

    /// <summary>
    /// Constructs the General tab editing seam from a snapshot of the current settings
    /// and populates the template input, preview, and placeholder reference.
    /// </summary>
    private void InitializeGeneralTab()
    {
        // When no ConfigurationService is available (design-time), surface that as a save
        // failure rather than crashing; the seam still drives validation and preview.
        SaveSettingsDelegate save = _configService is not null
            ? s => _configService.Save(s)
            : _ => new ConfigurationSaveResult
            {
                Success = false,
                Phase = "DesignTime",
                ErrorMessage = "ConfigurationService not available (design-time).",
            };

        _generalTab = new GeneralTabSettings(_settings, save);

        // Placeholder reference is static documentation derived from ExportFilenameTemplate.
        PlaceholdersList.Text = string.Join("\n",
            GeneralTabSettings.SupportedPlaceholders.Select(
                p => $"{p.Token} — {p.Description} ({p.Example})"));

        // Loading the input fires TextChanged, which refreshes preview and button gating.
        FilenameTemplateInput.Text = _generalTab.FilenameTemplate;
        RefreshGeneralTabState();
    }

    /// <summary>
    /// Pushes template edits from the TextBox into the seam, then refreshes the
    /// preview, inline error, and Apply/OK button gating.
    /// </summary>
    private void FilenameTemplateInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_generalTab is null)
            return;

        _generalTab.EditFilenameTemplate(FilenameTemplateInput.Text);
        RefreshGeneralTabState();
    }

    /// <summary>
    /// Reflects the seam's preview, validation, and gating state into the controls.
    /// </summary>
    private void RefreshGeneralTabState()
    {
        if (_generalTab is null)
            return;

        FilenameTemplatePreviewText.Text = string.IsNullOrEmpty(_generalTab.FilenameTemplatePreview)
            ? "—"
            : _generalTab.FilenameTemplatePreview;

        string? error = _generalTab.FilenameTemplateError;
        FilenameTemplateErrorText.Text = error ?? string.Empty;
        FilenameTemplateErrorText.Visibility = _generalTab.IsValid
            ? Visibility.Collapsed
            : Visibility.Visible;

        ApplyButton.IsEnabled = _generalTab.CanApply;
        OKButton.IsEnabled = _generalTab.CanConfirm;
    }

    /// <summary>
    /// Displays an inline status message, colored by success or failure.
    /// </summary>
    private void ShowStatus(bool success, string message)
    {
        SaveStatusText.Text = message;
        SaveStatusText.Foreground = success
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorCriticalBrush"];
    }

    // ── Command buttons ──────────────────────────────────────────────────

    /// <summary>
    /// Handles OK button click — persists via the seam and closes only after a
    /// successful save. Save failures are shown inline and the window stays open.
    /// </summary>
    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (_generalTab is null)
        {
            Close();
            return;
        }

        var result = _generalTab.Confirm();
        ShowStatus(result.Success, result.Message);

        if (result.ShouldClose)
        {
            SaveWindowPlacement();
            Close();
        }
    }

    /// <summary>
    /// Handles Cancel button click — discards edits since the last Apply and closes.
    /// Persisted settings are left unchanged.
    /// </summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _generalTab?.Cancel();
        SaveWindowPlacement();
        Close();
    }

    /// <summary>
    /// Handles Apply button click — persists via the seam and keeps the window open.
    /// Save failures are shown inline without reporting success.
    /// </summary>
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_generalTab is null)
            return;

        var result = _generalTab.Apply();
        ShowStatus(result.Success, result.Message);
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