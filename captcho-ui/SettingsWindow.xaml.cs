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
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Guards the Capture-tab toggle handlers while <see cref="ApplyView"/> is
    /// programmatically setting toggle state, so reflecting a new view (Reset,
    /// Cancel, or the decoration/shadow dependency reconciliation) does not
    /// re-enter the session through the Toggled events.
    /// </summary>
    private bool _applyingView;

    /// <summary>
    /// The OCR-capable languages offered by the Recognition language combo.
    /// Populated once at open from the OCR engine's installed language packs
    /// (supplied by the coordinator's factory so tests can inject a fake
    /// list); index 0 is the Default entry, followed by one entry per
    /// installed language.
    /// </summary>
    private readonly IReadOnlyList<OcrLanguage> _ocrLanguages;

    private const string WindowPlacementKey = "SettingsWindowPlacement";

    /// <summary>
    /// Production constructor that receives the composed session (built by the
    /// coordinator from the live runtime settings, ConfigurationService, and Global
    /// Global Hotkey adapter) plus the OCR-capable languages to offer for
    /// Recognition. Uses the installed Windows OCR language packs when the
    /// engine is not supplied.
    /// </summary>
    /// <param name="session">Composed settings session that owns all tab behavior.</param>
    public SettingsWindow(SettingsSession session) : this(session, null)
    {
    }

    /// <summary>
    /// Full constructor with an injected OCR engine supplying the language
    /// list. Tests pass a fake engine; production omits it.
    /// </summary>
    public SettingsWindow(SettingsSession session, IOcrEngine? ocrEngine)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;
        _ocrLanguages = LoadOcrLanguages(ocrEngine);

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

        // Capture-tab toggles are bound from the view inside ApplyView (the
        // single source of truth for capture-toggle state, including the
        // decoration/shadow dependency).

        RebuildGlobalHotkeyRows(view.GlobalHotkeyRows);
        PopulateOcrLanguageCombo();
        ApplyView(view);

        // Inline key recorder: preview key presses at the content root so a
        // recording session captures the next combination before any control
        // consumes it.
        if (this.Content is UIElement contentRoot)
        {
            contentRoot.AddHandler(
                UIElement.KeyDownEvent,
                ContentRoot_PreviewKeyDown,
                true);
        }

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

    // ── Capture tab event routing ──────────────────────────────────────────────

    private void AnnotationEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingView)
            return;
        ApplyView(_session.EditAnnotationEnabled(AnnotationEnabledToggle.IsOn));
    }

    /// <summary>
    /// Routes a mouse-pointer toggle change into the session and rebinds. Suppressed
    /// while <see cref="ApplyView"/> is programmatically setting toggle state so the
    /// rebind does not re-enter the session.
    /// </summary>
    private void CapturePointerToggle_Toggled(object sender, RoutedEventArgs e) =>
        EditCapture(() => _session.EditCaptureIncludePointer(CapturePointerToggle.IsOn));

    /// <summary>
    /// Routes a window-decorations toggle change into the session and rebinds. The
    /// session reconciles the decoration/shadow dependency immediately, so the
    /// returned view carries the reconciled values; ApplyView reflects that by
    /// disabling and clearing the shadow toggle when decorations are off.
    /// </summary>
    private void CaptureDecorationsToggle_Toggled(object sender, RoutedEventArgs e) =>
        EditCapture(() => _session.EditCaptureIncludeDecorations(CaptureDecorationsToggle.IsOn));

    /// <summary>
    /// Routes a window-shadow toggle change into the session and rebinds. Suppressed
    /// while <see cref="ApplyView"/> is programmatically setting toggle state.
    /// </summary>
    private void CaptureShadowToggle_Toggled(object sender, RoutedEventArgs e) =>
        EditCapture(() => _session.EditCaptureIncludeShadow(CaptureShadowToggle.IsOn));

    /// <summary>
    /// Shared shape for a Capture-tab toggle edit: suppressed while
    /// <see cref="ApplyView"/> is programmatically setting toggle state, routes
    /// the edit through the session, and rebinds from the returned view.
    /// </summary>
    private void EditCapture(Func<SettingsView> edit)
    {
        if (_applyingView)
            return;
        ApplyView(edit());
    }

    private void RememberSelectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingView || RememberSelectionCombo.SelectedIndex < 0)
            return;

        var lifetime = (RememberSelectionLifetime)RememberSelectionCombo.SelectedIndex;
        if (!Enum.IsDefined(lifetime))
            return;

        ApplyView(_session.EditRememberSelection(lifetime));
    }

    // ── Capture tab: OCR language ─────────────────────────────────────

    /// <summary>
    /// Routes a Recognition language selection into the session. Index 0 is
    /// the Default entry (null tag — the engine's user-default language);
    /// every other index maps to the installed language at that position.
    /// Suppressed while <see cref="ApplyView"/> is programmatically setting
    /// combo state.
    /// </summary>
    private void OcrLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingView || OcrLanguageCombo.SelectedIndex < 0)
            return;

        string? tag = OcrLanguageCombo.SelectedIndex == 0
            ? null
            : _ocrLanguages[OcrLanguageCombo.SelectedIndex - 1].Tag;
        ApplyView(_session.EditOcrLanguageTag(tag));
    }

    /// <summary>
    /// Reads the installed OCR languages from the supplied engine (or the
    /// production Windows engine). Never throws — an engine failure yields an
    /// empty list, leaving the combo with just the Default entry.
    /// </summary>
    private static IReadOnlyList<OcrLanguage> LoadOcrLanguages(IOcrEngine? engine)
    {
        try
        {
            var effective = engine ?? new WindowsOcrEngine();
            return effective.GetAvailableLanguages();
        }
        catch
        {
            return Array.Empty<OcrLanguage>();
        }
    }

    /// <summary>
    /// Populates the Recognition language combo from the installed OCR
    /// languages: a Default entry followed by one entry per installed pack
    /// ("English (United States) [en-US]"). The current working selection is
    /// selected under the <see cref="_applyingView"/> guard so populating
    /// does not register as a user edit.
    /// </summary>
    private void PopulateOcrLanguageCombo()
    {
        _applyingView = true;
        try
        {
            OcrLanguageCombo.Items.Clear();
            OcrLanguageCombo.Items.Add("Default (Windows preferred languages)");
            foreach (var language in _ocrLanguages)
                OcrLanguageCombo.Items.Add(OcrLanguageResolver.FormatForDisplay(language));

            OcrLanguageCombo.SelectedIndex = SelectedOcrLanguageIndex(_session.View.Capture.OcrLanguageTag);
        }
        finally
        {
            _applyingView = false;
        }
    }

    /// <summary>
    /// Maps a persisted OCR language tag to its combo index: 0 for the
    /// Default entry (null/empty tag), 1 + position for an installed match,
    /// and 0 when the selected pack is no longer installed — so a removed
    /// pack visibly falls back to Default in the UI while recognition
    /// surfaces the retryable unsupported-language outcome for the stale
    /// persisted tag.
    /// </summary>
    private int SelectedOcrLanguageIndex(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return 0;

        for (int i = 0; i < _ocrLanguages.Count; i++)
        {
            if (string.Equals(_ocrLanguages[i].Tag, tag, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }

        return 0;
    }

    // ── Annotation tab event routing ─────────────────────────────────────────

    private void AnnotationToolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingView || AnnotationToolCombo.SelectedIndex < 0)
            return;

        ApplyView(_session.EditAnnotationDefaultTool(
            (AnnotationTool)AnnotationToolCombo.SelectedIndex));
    }

    private void AnnotationPenColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingView || AnnotationPenColorCombo.SelectedIndex < 0)
            return;

        ApplyView(_session.EditAnnotationPenColor(AnnotationColorForIndex(
            AnnotationPenColorCombo.SelectedIndex)));
    }

    private void AnnotationStrokeWidthInput_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_applyingView)
            return;

        int value = double.IsNaN(args.NewValue)
            ? 0
            : (int)Math.Round(args.NewValue);
        ApplyView(_session.EditAnnotationStrokeWidth(value));
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
        // Capture toggles are reflected from the reset view inside ApplyView.
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
    /// Reflects a view's preview, inline errors, button gating, capture-tab toggle
    /// state, and status into the controls. Each inline error is shown only for its
    /// own field. Does not touch input Text (owned by the user/program) or Global
    /// Hotkey rows (rebuilt explicitly). The Capture toggles are updated under the
    /// <see cref="_applyingView"/> guard so the programmatic IsOn/IsEnabled changes
    /// do not re-enter the session through the Toggled handlers.
    /// </summary>
    private void ApplyView(SettingsView view)
    {
        _applyingView = true;
        try
        {
            // Reflect the working Capture-options values. All three toggles are
            // driven from the view so Reset/Cancel/decoration-dependency changes
            // all rebind through this single site. The decoration/shadow
            // dependency (spec #30) is enforced by the session; the view already
            // carries shadow=off when decorations are off, so IsOn follows it
            // and IsEnabled explains why the control is unavailable.
            CapturePointerToggle.IsOn = view.Capture.IncludePointer;
            AnnotationEnabledToggle.IsOn = view.Capture.AnnotationEnabled;
            CaptureDecorationsToggle.IsOn = view.Capture.IncludeDecorations;
            CaptureShadowToggle.IsOn = view.Capture.IncludeShadow;
            RememberSelectionCombo.SelectedIndex = (int)view.Capture.RememberSelection;
            CaptureShadowToggle.IsEnabled = view.Capture.IncludeDecorations;
            CaptureShadowNoteText.Visibility = view.Capture.IncludeDecorations
                ? Visibility.Collapsed
                : Visibility.Visible;
            OcrLanguageCombo.SelectedIndex = SelectedOcrLanguageIndex(view.Capture.OcrLanguageTag);

            AnnotationToolCombo.SelectedIndex = (int)view.Annotation.DefaultTool;
            AnnotationPenColorCombo.SelectedIndex = AnnotationColorIndex(view.Annotation.PenColor);
            AnnotationStrokeWidthInput.Value = view.Annotation.StrokeWidth;
        }
        finally
        {
            _applyingView = false;
        }

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

        AnnotationSettingsErrorText.Text = view.Annotation.Error ?? string.Empty;
        AnnotationSettingsErrorText.Visibility = string.IsNullOrEmpty(view.Annotation.Error)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static AnnotationColor AnnotationColorForIndex(int index) => index switch
    {
        1 => AnnotationColor.BlueOpaque,
        2 => AnnotationColor.BlackOpaque,
        _ => AnnotationColor.RedOpaque,
    };

    private static int AnnotationColorIndex(AnnotationColor color) => color switch
    {
        var value when value == AnnotationColor.BlueOpaque => 1,
        var value when value == AnnotationColor.BlackOpaque => 2,
        _ => 0,
    };

    /// <summary>
    /// Clears and rebuilds the Global Hotkeys tab rows. Toggle switches are populated before
    /// their Toggled handler is attached so the initial value does not fire as an edit.
    /// The Full Desktop row carries an inline key recorder (issue #51): a Change… button
    /// enters capture mode and the next key press with modifiers records the combination.
    /// </summary>
    private void RebuildGlobalHotkeyRows(IReadOnlyList<GlobalHotkeyRow> rows)
    {
        _recordingHotkeyId = null;
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

            var bindingText = new TextBlock
            {
                Text = row.Binding,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            var warningText = new TextBlock
            {
                Text = row.BindingWarning ?? string.Empty,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorCautionBrush"],
                Visibility = string.IsNullOrEmpty(row.BindingWarning)
                    ? Visibility.Collapsed
                    : Visibility.Visible,
            };

            var info = new StackPanel { Spacing = 2 };
            info.Children.Add(bindingText);
            info.Children.Add(warningText);
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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 0);
            Grid.SetColumn(statusText, 1);
            Grid.SetColumn(toggle, 2);

            // Inline recorder for the Full Desktop Global Hotkey only (issue #51 —
            // the single-hotkey tracer bullet; remaining rows migrate in #52).
            if (row.Id == GlobalHotkeyRouteMap.IdShiftPrintScreen)
            {
                var recordButton = new Button
                {
                    Content = "Change…",
                    MinHeight = 36,
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = row.Id,
                };
                recordButton.Click += RecordBindingButton_Click;
                Grid.SetColumn(recordButton, 3);
                grid.Children.Add(recordButton);
            }

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

    // ── Inline key recorder (Full Desktop row, issue #51) ──────────────

    /// <summary>
    /// The Global Hotkey id currently being recorded, or null. While set, the
    /// next non-modifier key press (with whatever modifiers are held) records
    /// the combination through the session; Escape cancels recording.
    /// </summary>
    private int? _recordingHotkeyId;

    /// <summary>
    /// Win32 modifier flags mirrored for capture-mode key state reads.
    /// </summary>
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;

    // Virtual key codes used by the recorder (Win32 values; WinUI VirtualKey
    // maps the same integers for these keys).
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int nVirtKey);

    /// <summary>
    /// Enters capture mode for the row's Global Hotkey. The button re-labels to
    /// guide the user; the next key press is intercepted in the
    /// <see cref="KeyboardAccelerator_Typed"/>-style preview handler installed
    /// on this window's content root.
    /// </summary>
    private void RecordBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int id)
            return;

        _recordingHotkeyId = id;
        var row = _session.View.GlobalHotkeyRows.Single(r => r.Id == id);
        button.Content = $"Press keys for “{row.Behavior}”… (Esc cancels)";
    }

    /// <summary>
    /// While recording, intercepts key presses on the settings window: Escape
    /// cancels; a modifier alone does nothing; any other key with its held
    /// modifiers records the combination through the session and refreshes the
    /// rows (rebinding the recorded binding name and any conflict warning).
    /// </summary>
    private void ContentRoot_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_recordingHotkeyId is not int id)
            return;

        var vk = (int)e.Key;
        if (vk == VK_ESCAPE)
        {
            _recordingHotkeyId = null;
            RebuildGlobalHotkeyRows(_session.View.GlobalHotkeyRows);
            return;
        }

        // Modifier-only presses wait for the real key.
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
            return;

        int modifiers = 0;
        if (IsPressed(VK_SHIFT)) modifiers |= MOD_SHIFT;
        if (IsPressed(VK_CONTROL)) modifiers |= MOD_CONTROL;
        if (IsPressed(VK_MENU)) modifiers |= MOD_ALT;
        if (IsPressed(VK_LWIN) || IsPressed(VK_RWIN)) modifiers |= MOD_WIN;

        var view = _session.RecordGlobalHotkeyBinding(id, new HotkeyBinding(modifiers, vk));
        _recordingHotkeyId = null;
        RebuildGlobalHotkeyRows(view.GlobalHotkeyRows);
        ApplyView(view);
        e.Handled = true;
    }

    private static bool IsPressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

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
