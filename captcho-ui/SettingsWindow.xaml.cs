// SettingsWindow.xaml — code-behind for the Settings window.
//
// This is a thin WinUI adapter: it constructs nothing business-logic-shaped. A
// SettingsSession (WinUI-free, composed across every editable tab) is handed in by the
// SettingsWindowCoordinator. Each XAML event routes to one session Edit…/verb method and
// the controls rebind from the returned SettingsView — the single binding site for
// preview, inline errors, button gating, and status. The only WinUI-specific work kept
// here is the folder picker and persisting window placement on Closed.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Settings dialog window with tabbed configuration pages. Thin adapter over a
/// <see cref="SettingsSession"/>. Single-instance behavior is enforced by
/// <see cref="SettingsWindowCoordinator"/>.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsSession _session;
    private readonly Windows.Storage.ApplicationDataContainer _localSettings;

    /// <summary>
    /// Status TextBlock per Global Hotkey id, kept so a toggle can refresh one row's status
    /// without rebuilding the whole list (which would lose focus and re-fire toggles).
    /// </summary>
    private readonly Dictionary<int, TextBlock> _globalHotkeyStatusCells = new();

    private const string WindowPlacementKey = "SettingsWindowPlacement";

    /// <summary>
    /// Production constructor that receives the composed session (built by the
    /// coordinator from the live runtime settings, ConfigurationService, and Global
    /// Global Hotkey adapter).
    /// </summary>
    /// <param name="session">Composed settings session that owns all tab behavior.</param>
    public SettingsWindow(SettingsSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

        InitializeComponent();

        // Set a reasonable default window size
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(700, 500));

        // Restore saved window placement if available
        LoadWindowPlacement();

        var view = _session.View;

        // Placeholder reference is static documentation derived from ExportFilenameTemplate.
        PlaceholdersList.Text = string.Join("\n",
            GeneralTabSettings.SupportedPlaceholders.Select(
                p => $"{p.Token} — {p.Description} ({p.Example})"));

        // Read-only tab content.
        ExportFormatHeading.Text = view.Export.Heading;
        ExportFormatNameText.Text = view.Export.FormatName;
        ExportFormatExtensionText.Text = $"({view.Export.FileExtension})";
        ExportFormatDescriptionText.Text = view.Export.FormatDescription;
        ExportPlannedFormatsText.Text = view.Export.PlannedFormatsNote;

        InterfaceHeading.Text = view.Interface.Heading;
        InterfaceMessageText.Text = view.Interface.Message;
        InterfacePlannedText.Text = view.Interface.PlannedSettingsNote;

        // Loading the inputs fires TextChanged, which routes the value through the
        // session (an idempotent re-set of the same working value) and rebinds.
        SaveLocationInput.Text = view.SaveLocation;
        FilenameTemplateInput.Text = view.FilenameTemplate;

        RebuildGlobalHotkeyRows(view.GlobalHotkeyRows);
        ApplyView(view);

        // Hook Closed event for coordinator cleanup
        this.Closed += OnWindowClosed;
    }

    // ── General tab event routing ───────────────────────────────────────

    /// <summary>
    /// Routes a Save Location text edit into the session and rebinds inline error and
    /// button gating from the returned view.
    /// </summary>
    private void SaveLocationInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var view = _session.EditSaveLocation(SaveLocationInput.Text);
        ApplyView(view);
    }

    /// <summary>
    /// Routes a Filename Template text edit into the session and rebinds preview,
    /// inline error, and button gating from the returned view.
    /// </summary>
    private void FilenameTemplateInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var view = _session.EditFilenameTemplate(FilenameTemplateInput.Text);
        ApplyView(view);
    }

    /// <summary>
    /// Opens the Windows folder picker and routes its outcome through the session. A
    /// cancelled picker (null result) is a no-op; a selection replaces the working Save
    /// Location and is reflected back into the input.
    /// </summary>
    private async void BrowseSaveLocationButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        // FolderPicker requires an owner window handle in a WinUI 3 desktop app.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();

        // Route the picker outcome through the session so cancellation is a no-op and the
        // resulting working value is reflected back into the input.
        var view = _session.ApplyFolderPickerResult(folder?.Path);
        SaveLocationInput.Text = view.SaveLocation;
        ApplyView(view);
    }

    // ── Global Hotkeys tab event routing ───────────────────────────────────────

    /// <summary>
    /// Pushes a toggle change into the session and refreshes that row's registration
    /// status without rebuilding the whole list (which would lose focus and re-fire
    /// toggles).
    /// </summary>
    private void GlobalHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.Tag is not int id)
            return;

        var view = _session.EditGlobalHotkeyEnabled(id, toggle.IsOn);

        if (_globalHotkeyStatusCells.TryGetValue(id, out var cell))
        {
            var row = view.GlobalHotkeyRows.Single(r => r.Id == id);
            cell.Text = StatusDisplay(row.Status, row.StatusDetail);
            cell.Foreground = StatusBrush(row.Status);
        }
    }

    // ── Command buttons ─────────────────────────────────────────────────

    /// <summary>
    /// Handles OK — asks the session to persist all tabs atomically and close only on a
    /// successful save. The session surfaces failures as status; on failure the window
    /// stays open and runtime/persisted state is left consistent. Window placement is
    /// persisted by the Closed handler shared with Cancel and the title-bar X.
    /// </summary>
    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var view = _session.Confirm();
        ApplyView(view);
        RebuildGlobalHotkeyRows(view.GlobalHotkeyRows);
        if (view.ShouldClose)
            Close();
    }

    /// <summary>
    /// Handles Cancel — asks the session to revert every editable tab and closes.
    /// Persisted settings and runtime registration are left unchanged. Window placement
    /// is persisted by the Closed handler.
    /// </summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var view = _session.Cancel();
        ApplyView(view);
        RebuildGlobalHotkeyRows(view.GlobalHotkeyRows);
        Close();
    }

    /// <summary>
    /// Handles Reset to Defaults — asks the session to restore every editable setting to
    /// its default in the editing session. Reset alone does not persist or reconcile
    /// runtime registration; the baseline is left untouched so a later Cancel reverts the
    /// reset, and Apply/OK persist the defaults. Inputs and Global Hotkey rows refresh to the
    /// reset working state.
    /// </summary>
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var view = _session.Reset();
        // Reflect the reset working values back into the inputs. Setting Text fires
        // TextChanged, which routes the value through the session and rebinds the
        // preview, inline errors, and Apply/OK gating — the same pattern as the
        // folder-picker outcome.
        SaveLocationInput.Text = view.SaveLocation;
        FilenameTemplateInput.Text = view.FilenameTemplate;
        RebuildGlobalHotkeyRows(view.GlobalHotkeyRows);
        ApplyView(view);
    }

    /// <summary>
    /// Handles Apply — asks the session to persist all tabs atomically and keeps the
    /// window open. Save failures are shown inline without reporting success; the session
    /// reconciles runtime registration on a successful save.
    /// </summary>
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var view = _session.Apply();
        ApplyView(view);
        RebuildGlobalHotkeyRows(view.GlobalHotkeyRows);
    }

    // ── Rebinding ───────────────────────────────────────────────────────

    /// <summary>
    /// Reflects a view's preview, inline errors, button gating, and status into the
    /// controls. Each inline error is shown only for its own field. Does not touch input
    /// Text (owned by the user/program) or Global Hotkey rows (rebuilt explicitly).
    /// </summary>
    private void ApplyView(SettingsView view)
    {
        FilenameTemplatePreviewText.Text = string.IsNullOrEmpty(view.FilenameTemplatePreview)
            ? "—"
            : view.FilenameTemplatePreview;

        string? templateError = view.FilenameTemplateError;
        FilenameTemplateErrorText.Text = templateError ?? string.Empty;
        FilenameTemplateErrorText.Visibility = string.IsNullOrEmpty(templateError)
            ? Visibility.Collapsed
            : Visibility.Visible;

        string? locationError = view.SaveLocationError;
        SaveLocationErrorText.Text = locationError ?? string.Empty;
        SaveLocationErrorText.Visibility = string.IsNullOrEmpty(locationError)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ApplyButton.IsEnabled = view.CanApply;
        OKButton.IsEnabled = view.CanConfirm;

        SaveStatusText.Text = view.StatusMessage ?? string.Empty;
        SaveStatusText.Foreground = view.StatusIsError
            ? (Brush)Application.Current.Resources["TextFillColorCriticalBrush"]
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    /// <summary>
    /// Clears and rebuilds the Global Hotkeys tab rows. Toggle switches are populated before
    /// their Toggled handler is attached so the initial value does not fire as an edit.
    /// </summary>
    private void RebuildGlobalHotkeyRows(IReadOnlyList<GlobalHotkeyRow> rows)
    {
        GlobalHotkeysRowsPanel.Children.Clear();
        _globalHotkeyStatusCells.Clear();

        foreach (var row in rows)
        {
            var statusText = new TextBlock
            {
                Text = StatusDisplay(row.Status, row.StatusDetail),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = StatusBrush(row.Status),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _globalHotkeyStatusCells[row.Id] = statusText;

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
            toggle.Toggled += GlobalHotkeyToggle_Toggled;

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

            GlobalHotkeysRowsPanel.Children.Add(new Border
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

    private static string StatusDisplay(GlobalHotkeyRegistrationStatus status, string detail) => status switch
    {
        GlobalHotkeyRegistrationStatus.Registered => "Active",
        GlobalHotkeyRegistrationStatus.Failed => string.IsNullOrWhiteSpace(detail) ? "Registration failed" : $"Registration failed: {detail}",
        GlobalHotkeyRegistrationStatus.Disabled => "Disabled",
        _ => "Status unavailable",
    };

    private static Brush StatusBrush(GlobalHotkeyRegistrationStatus status) => status switch
    {
        GlobalHotkeyRegistrationStatus.Registered => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
        GlobalHotkeyRegistrationStatus.Failed => (Brush)Application.Current.Resources["TextFillColorCriticalBrush"],
        GlobalHotkeyRegistrationStatus.Disabled => (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        _ => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    // ── Window placement ────────────────────────────────────────────────

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
