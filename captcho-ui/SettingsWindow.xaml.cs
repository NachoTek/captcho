// SettingsWindow.xaml.cs — Settings dialog with tabbed configuration pages.
//
// Provides a Window subclass (not ContentDialog) with TabView containing four tabs:
// General, Hotkeys, Export, and Interface. The General tab edits the Save Location
// (text or Windows folder picker) and the Filename Template through the pure C#
// GeneralTabSettings seam (preview, validation, Apply/OK/Cancel). The Hotkeys tab
// lists every Global Hotkey with its binding, behavior, and registration status,
// and enables/disables each through the HotkeysTabSettings seam (Apply/OK persist
// enabled states and reconcile runtime registration via the Global Hotkey adapter;
// Cancel reverts). Both tabs persist through the shared in-memory settings so the
// export flow and hotkey runtime pick up changes without a restart. The Export and
// Interface tabs render read-only content through their own seams. The bottom row
// has OK (save+close on success), Cancel (discard+close), and Apply (save, stay
// open). Window position and size are persisted to
// ApplicationData.Current.LocalSettings.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    /// <summary>
    /// Pure C# editing seam for the Hotkeys tab. Lists every Global Hotkey with its
    /// binding, behavior, and registration status, and enables/disables each hotkey.
    /// Apply/OK persist enabled states and reconcile runtime registration; Cancel
    /// reverts. Null only when the window fails to initialize.
    /// </summary>
    private HotkeysTabSettings? _hotkeysTab;

    /// <summary>
    /// Runtime Global Hotkey adapter used by the Hotkeys tab to read registration
    /// status and reconcile registration on Apply/OK. Stored so the row builder can
    /// refresh status after a save.
    /// </summary>
    private readonly IGlobalHotkeyAdapter _hotkeyAdapter;

    /// <summary>
    /// Status TextBlock per hotkey id, kept so a toggle can refresh one row's status
    /// without rebuilding the whole list (which would lose focus and re-fire toggles).
    /// </summary>
    private readonly Dictionary<int, TextBlock> _hotkeyStatusCells = new();

    /// <summary>
    /// Read-only display seam for the Export tab. Surfaces the current export format
    /// (PNG) and supporting text. Null only when the window fails to initialize.
    /// </summary>
    private ExportTabSettings? _exportTab;

    /// <summary>
    /// Read-only display seam for the Interface tab. Surfaces the not-yet-available
    /// message and planned-settings note. Null only when the window fails to initialize.
    /// </summary>
    private InterfaceTabSettings? _interfaceTab;

    private const string WindowPlacementKey = "SettingsWindowPlacement";

    /// <summary>
    /// Production constructor that receives current settings, an optional configuration
    /// service, and the Global Hotkey adapter used to read registration status and
    /// reconcile runtime registration on the Hotkeys tab.
    /// </summary>
    /// <param name="settings">Current application settings (snapshotted by each tab; never mutated).</param>
    /// <param name="configService">Optional configuration service for persisting settings (nullable for design-time).</param>
    /// <param name="hotkeyAdapter">Global Hotkey adapter for registration status and runtime reconcile.</param>
    public SettingsWindow(AppSettings settings, ConfigurationService? configService, IGlobalHotkeyAdapter hotkeyAdapter)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configService = configService;
        _hotkeyAdapter = hotkeyAdapter ?? throw new ArgumentNullException(nameof(hotkeyAdapter));
        _localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

        InitializeComponent();

        // Set a reasonable default window size
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(700, 500));

        // Restore saved window placement if available
        LoadWindowPlacement();

        // Wire the General tab editing seam (snapshots settings; never mutates the active settings)
        InitializeGeneralTab();

        // Wire the Hotkeys tab editing seam (snapshots settings; reads registration status).
        InitializeHotkeysTab();

        // Wire the read-only Export and Interface tab seams (display content only).
        InitializeExportTab();
        InitializeInterfaceTab();

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
        // The save delegate merges this tab's slice onto the shared in-memory settings
        // before persisting, so a General Apply does not clobber Hotkey-tab changes
        // already applied to (or snapshotted into) _settings. Design-time (no
        // ConfigurationService) surfaces as a save failure rather than crashing; the
        // seam still drives validation and preview.
        SaveSettingsDelegate save = s =>
        {
            var merged = MergeSettingsSlice(_settings, saveLocation: s.SaveLocation, filenameTemplate: s.FilenameTemplate);
            var result = _configService is not null
                ? _configService.Save(merged)
                : DesignTimeSaveFailure();

            if (result.Success)
            {
                // Propagate the persisted General slice onto the live in-memory settings
                // so the export flow picks up the new Save Location (and Filename Template)
                // without requiring an application restart, and so the Hotkeys tab's merge
                // sees the latest General values.
                _settings.SaveLocation = merged.SaveLocation;
                _settings.FilenameTemplate = merged.FilenameTemplate;
            }

            return result;
        };

        _generalTab = new GeneralTabSettings(_settings, save);

        // Placeholder reference is static documentation derived from ExportFilenameTemplate.
        PlaceholdersList.Text = string.Join("\n",
            GeneralTabSettings.SupportedPlaceholders.Select(
                p => $"{p.Token} — {p.Description} ({p.Example})"));

        // Loading the inputs fires TextChanged, which refreshes validation and button gating.
        SaveLocationInput.Text = _generalTab.SaveLocation;
        FilenameTemplateInput.Text = _generalTab.FilenameTemplate;
        RefreshGeneralTabState();
    }

    // ── Hotkeys tab seam wiring ──────────────────────────────────────────

    /// <summary>
    /// Constructs the Hotkeys tab editing seam from a snapshot of the current
    /// settings and the runtime Global Hotkey adapter, then renders one row per
    /// Global Hotkey (binding, behavior, registration status, enable toggle).
    /// </summary>
    private void InitializeHotkeysTab()
    {
        // Same merge pattern as the General tab: overlay this tab's slice onto the
        // shared in-memory settings before persisting, so a Hotkeys Apply does not
        // clobber General-tab changes.
        SaveSettingsDelegate save = s =>
        {
            var merged = MergeSettingsSlice(_settings, hotkeyEnabledStates: s.HotkeyEnabledStates);
            var result = _configService is not null
                ? _configService.Save(merged)
                : DesignTimeSaveFailure();

            if (result.Success)
            {
                _settings.HotkeyEnabledStates = merged.HotkeyEnabledStates is null
                    ? null
                    : new Dictionary<int, bool>(merged.HotkeyEnabledStates);
            }

            return result;
        };

        _hotkeysTab = new HotkeysTabSettings(_settings, save, _hotkeyAdapter);
        RebuildHotkeyRows();
    }

    /// <summary>
    /// Clears and rebuilds the Hotkeys tab rows from the coordinator's current
    /// display state. Toggle switches are populated before their Toggled handler is
    /// attached so the initial value does not fire as an edit.
    /// </summary>
    private void RebuildHotkeyRows()
    {
        if (_hotkeysTab is null)
            return;

        HotkeysRowsPanel.Children.Clear();
        _hotkeyStatusCells.Clear();

        foreach (var row in _hotkeysTab.GetRows())
        {
            var statusText = new TextBlock
            {
                Text = StatusDisplay(row.Status, row.StatusDetail),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = StatusBrush(row.Status),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _hotkeyStatusCells[row.Id] = statusText;

            // Toggle is set to the working enabled state BEFORE the handler is
            // attached, so populating it does not register as a user edit.
            var toggle = new ToggleSwitch
            {
                OnContent = "On",
                OffContent = "Off",
                IsOn = row.IsEnabled,
                MinWidth = 90,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = row.Id,
            };
            toggle.Toggled += HotkeyToggle_Toggled;

            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(new TextBlock
            {
                Text = row.Binding,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = row.Behavior,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 0);
            Grid.SetColumn(statusText, 1);
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(info);
            grid.Children.Add(statusText);
            grid.Children.Add(toggle);

            HotkeysRowsPanel.Children.Add(new Border
            {
                Child = grid,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            });
        }
    }

    /// <summary>
    /// Pushes a toggle change into the seam and refreshes that row's registration
    /// status without rebuilding the whole list (which would lose focus and re-fire
    /// toggles).
    /// </summary>
    private void HotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_hotkeysTab is null || sender is not ToggleSwitch toggle || toggle.Tag is not int id)
            return;

        _hotkeysTab.EditEnabled(id, toggle.IsOn);

        if (_hotkeyStatusCells.TryGetValue(id, out var cell))
        {
            var row = _hotkeysTab.GetRows().Single(r => r.Id == id);
            cell.Text = StatusDisplay(row.Status, row.StatusDetail);
            cell.Foreground = StatusBrush(row.Status);
        }
    }

    private static string StatusDisplay(HotkeyRegistrationStatus status, string detail) => status switch
    {
        HotkeyRegistrationStatus.Registered => "Active",
        HotkeyRegistrationStatus.Failed => string.IsNullOrWhiteSpace(detail) ? "Registration failed" : $"Registration failed: {detail}",
        HotkeyRegistrationStatus.Disabled => "Disabled",
        _ => "Status unavailable",
    };

    private static Brush StatusBrush(HotkeyRegistrationStatus status) => status switch
    {
        HotkeyRegistrationStatus.Registered => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
        HotkeyRegistrationStatus.Failed => (Brush)Application.Current.Resources["TextFillColorCriticalBrush"],
        HotkeyRegistrationStatus.Disabled => (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        _ => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    /// <summary>
    /// Builds a settings instance for persistence by overlaying one tab's slice onto
    /// the shared in-memory settings. Each slice argument is applied only when non-null,
    /// so a tab persists only its own values while inheriting the other tab's latest
    /// applied values from <paramref name="source"/>.
    /// </summary>
    private static AppSettings MergeSettingsSlice(
        AppSettings source,
        string? saveLocation = null,
        string? filenameTemplate = null,
        Dictionary<int, bool>? hotkeyEnabledStates = null)
    {
        var merged = source.Normalized();
        if (saveLocation is not null)
            merged.SaveLocation = saveLocation;
        if (filenameTemplate is not null)
            merged.FilenameTemplate = filenameTemplate;
        if (hotkeyEnabledStates is not null)
            merged.HotkeyEnabledStates = new Dictionary<int, bool>(hotkeyEnabledStates);
        return merged;
    }

    private static ConfigurationSaveResult DesignTimeSaveFailure() => new()
    {
        Success = false,
        Phase = "DesignTime",
        ErrorMessage = "ConfigurationService not available (design-time).",
    };

    // ── Export tab seam wiring ───────────────────────────────────────────

    /// <summary>
    /// Constructs the read-only Export tab seam and populates the format display
    /// (name, extension, description, and planned-formats note) from the coordinator.
    /// Read-only: no editing, validation, or persistence.
    /// </summary>
    private void InitializeExportTab()
    {
        _exportTab = new ExportTabSettings();

        ExportFormatHeading.Text = _exportTab.Heading;
        ExportFormatNameText.Text = _exportTab.FormatName;
        ExportFormatExtensionText.Text = $"({_exportTab.FileExtension})";
        ExportFormatDescriptionText.Text = _exportTab.FormatDescription;
        ExportPlannedFormatsText.Text = _exportTab.PlannedFormatsNote;
    }

    // ── Interface tab seam wiring ────────────────────────────────────────

    /// <summary>
    /// Constructs the read-only Interface tab seam and populates the heading,
    /// not-yet-available message, and planned-settings note from the coordinator.
    /// Read-only: no editing, validation, or persistence.
    /// </summary>
    private void InitializeInterfaceTab()
    {
        _interfaceTab = new InterfaceTabSettings();

        InterfaceHeading.Text = _interfaceTab.Heading;
        InterfaceMessageText.Text = _interfaceTab.Message;
        InterfacePlannedText.Text = _interfaceTab.PlannedSettingsNote;
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
    /// Pushes Save Location edits from the TextBox into the seam, then refreshes the
    /// inline error and Apply/OK button gating.
    /// </summary>
    private void SaveLocationInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_generalTab is null)
            return;

        _generalTab.EditSaveLocation(SaveLocationInput.Text);
        RefreshGeneralTabState();
    }

    /// <summary>
    /// Opens the Windows folder picker and routes its outcome through the seam. A
    /// cancelled picker (null result) leaves the current edit unchanged; a selection
    /// replaces the working Save Location and is reflected back into the input.
    /// </summary>
    private async void BrowseSaveLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_generalTab is null)
            return;

        var picker = new Windows.Storage.Pickers.FolderPicker();
        // FolderPicker requires an owner window handle in a WinUI 3 desktop app.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();

        // Route the picker outcome through the seam so cancellation is a no-op and the
        // resulting working value is reflected back into the input.
        _generalTab.ApplyFolderPickerResult(folder?.Path);
        SaveLocationInput.Text = _generalTab.SaveLocation;
        RefreshGeneralTabState();
    }

    /// <summary>
    /// Reflects the seam's preview, validation, and gating state into the controls.
    /// Each inline error is shown only for its own field.
    /// </summary>
    private void RefreshGeneralTabState()
    {
        if (_generalTab is null)
            return;

        FilenameTemplatePreviewText.Text = string.IsNullOrEmpty(_generalTab.FilenameTemplatePreview)
            ? "—"
            : _generalTab.FilenameTemplatePreview;

        string? templateError = _generalTab.FilenameTemplateError;
        FilenameTemplateErrorText.Text = templateError ?? string.Empty;
        FilenameTemplateErrorText.Visibility = string.IsNullOrEmpty(templateError)
            ? Visibility.Collapsed
            : Visibility.Visible;

        string? locationError = _generalTab.SaveLocationError;
        SaveLocationErrorText.Text = locationError ?? string.Empty;
        SaveLocationErrorText.Visibility = string.IsNullOrEmpty(locationError)
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
    /// Handles OK button click — persists via both the General and Hotkeys seams and
    /// closes only after every tab saves successfully. General is confirmed first (it
    /// can fail validation); on any failure the window stays open with an inline
    /// message and runtime/persisted state is left consistent. Save failures are
    /// shown inline and the window stays open. Window placement is persisted by the
    /// Closed handler shared with Cancel and the title-bar X.
    /// </summary>
    private void OK_Click(object sender, RoutedEventArgs e)
    {
        // General first (it can fail validation); short-circuit on failure so a
        // General validation error does not trigger a Hotkeys save.
        if (_generalTab is not null)
        {
            var generalResult = _generalTab.Confirm();
            if (!generalResult.Success)
            {
                ShowStatus(false, generalResult.Message);
                return;
            }
        }

        if (_hotkeysTab is not null)
        {
            var hotkeysResult = _hotkeysTab.Confirm();
            RebuildHotkeyRows();
            if (!hotkeysResult.Success)
            {
                ShowStatus(false, hotkeysResult.Message);
                return;
            }
        }

        ShowStatus(true, SettingsWindowCoordinator.SavedMessage);
        Close();
    }

    /// <summary>
    /// Handles Cancel button click — discards edits since the last Apply on every tab
    /// and closes. Persisted settings and runtime registration are left unchanged.
    /// Window placement is persisted by the Closed handler shared with OK and X-close.
    /// </summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _generalTab?.Cancel();
        if (_hotkeysTab is not null)
        {
            _hotkeysTab.Cancel();
            RebuildHotkeyRows();
        }
        Close();
    }

    /// <summary>
    /// Handles Reset to Defaults button click — restores every editable setting to its
    /// default in the current editing session so the user can inspect the defaults,
    /// preview, and validation results immediately. Reset alone does not write
    /// Configuration or reconcile runtime Global Hotkey registration; the baseline is
    /// left untouched so a later Cancel reverts the reset, and Apply/OK persist the
    /// defaults. Inputs, preview, validation, button gating, and Hotkey rows all
    /// refresh to reflect the reset working state.
    /// </summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_generalTab is not null)
        {
            _generalTab.Reset();
            // Reflect the reset working values back into the inputs. Setting Text fires
            // TextChanged, which pushes the value through the seam and refreshes the
            // preview, inline errors, and Apply/OK gating — the same pattern as the
            // folder-picker outcome.
            SaveLocationInput.Text = _generalTab.SaveLocation;
            FilenameTemplateInput.Text = _generalTab.FilenameTemplate;
            RefreshGeneralTabState();
        }

        if (_hotkeysTab is not null)
        {
            _hotkeysTab.Reset();
            RebuildHotkeyRows();
        }
    }

    /// <summary>
    /// Handles Apply button click — persists via both the General and Hotkeys seams
    /// and keeps the window open. Save failures are shown inline without reporting
    /// success; the Hotkeys tab reconciles runtime registration on a successful save.
    /// </summary>
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_generalTab is not null)
        {
            var generalResult = _generalTab.Apply();
            if (!generalResult.Success)
            {
                ShowStatus(false, generalResult.Message);
                return;
            }
        }

        if (_hotkeysTab is not null)
        {
            var hotkeysResult = _hotkeysTab.Apply();
            RebuildHotkeyRows();
            if (!hotkeysResult.Success)
            {
                ShowStatus(false, hotkeysResult.Message);
                return;
            }
        }

        ShowStatus(true, SettingsWindowCoordinator.SavedMessage);
    }

    /// <summary>
    /// Loads saved window placement (position and size) from LocalSettings via the
    /// pure <see cref="WindowPlacement"/> seam, which owns parsing and minimum-size
    /// clamping. Applies the restored placement to the AppWindow, or leaves the
    /// default size in place when no valid placement is stored.
    /// </summary>
    private void LoadWindowPlacement()
    {
        try
        {
            var raw = _localSettings.Values[WindowPlacementKey] as string;
            var placement = WindowPlacement.TryParse(raw);
            if (placement is null)
                return;

            var appWindow = this.AppWindow;
            appWindow.Move(new Windows.Graphics.PointInt32(placement.Value.X, placement.Value.Y));
            appWindow.Resize(new Windows.Graphics.SizeInt32(placement.Value.Width, placement.Value.Height));
        }
        catch
        {
            // TryParse absorbs malformed placement data (returns null); this catch
            // only guards LocalSettings/AppWindow access failures — ignore and use defaults
        }
    }

    /// <summary>
    /// Saves current window placement (position and size) to LocalSettings via the
    /// pure <see cref="WindowPlacement"/> seam, which owns the serialization format.
    /// </summary>
    private void SaveWindowPlacement()
    {
        try
        {
            var appWindow = this.AppWindow;
            var position = appWindow.Position;
            var size = appWindow.Size;

            var placement = new WindowPlacement(position.X, position.Y, size.Width, size.Height);
            _localSettings.Values[WindowPlacementKey] = placement.Serialize();
        }
        catch
        {
            // Placement save failure is non-fatal; continue
        }
    }

    /// <summary>
    /// Handles window closed event — persists window placement (so OK, Cancel,
    /// and the title-bar X all save position and size through this single path),
    /// unhooks the event handler, and notifies the coordinator (the coordinator's
    /// OnWindowClosed delegate will clear the current reference).
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Persist placement on every close path — OK_Click and Cancel_Click both
        // call Close(), which fires this event, and the title-bar X closes the
        // window directly. Routing the save here means no close path can drop it.
        SaveWindowPlacement();
        this.Closed -= OnWindowClosed;
    }
}