// SettingsSession.cs — WinUI-free composed session for the Settings window.
//
// Owns the composed Apply/OK/Cancel/Reset flow across every editable tab. The session
// snapshots the shared runtime settings into per-tab working/baseline copies on
// construction, recomputes composed validation + button gating after each edit, and
// persists ALL editable tabs atomically with a single ConfigurationService.Save: on
// success it writes the merged values back into the shared runtime (so the export flow
// and Global Hotkey runtime pick up changes without an app restart), advances each
// tab's baseline, and reconciles runtime Global Hotkey registration; on failure nothing moves
// (all-or-nothing). Every method is non-throwing on user paths — failures surface as
// StatusMessage + StatusIsError on the returned SettingsView, never as exceptions.
//
// This is the class the Settings window code-behind calls: it routes WinUI events to
// one Edit…/verb method and rebinds controls from the returned SettingsView.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Composes the editable settings tabs and drives the Apply/OK/Cancel/Reset session.
/// WinUI-free so the entire cross-tab behavior is covered by headless tests. Each edit
/// or verb returns a fresh <see cref="SettingsView"/> carrying everything the code-behind
/// binds. Persistence is atomic: one merged <see cref="AppSettings"/> is saved, and on
/// failure no tab advances its baseline, no runtime value moves, and no Global Hotkey is
/// reconciled.
/// </summary>
public sealed class SettingsSession
{
    /// <summary>Shared inline message used when settings are persisted successfully.</summary>
    internal const string SavedMessage = "Settings saved successfully.";

    private readonly AppSettings _runtime;
    private readonly ConfigurationService _configuration;
    private readonly IGlobalHotkeyAdapter _globalHotkeys;

    private readonly GeneralTabSettings _general;
    private readonly GlobalHotkeyTabSettings _globalHotkeyTab;
    private readonly ExportTabSettings _export;
    private readonly InterfaceTabSettings _interface;

    // All editable tabs, in display order. The session drives persistence composed across
    // exactly these tabs; it depends on the EditableTabSession seam (snapshot/commit/
    // cancel/reset live there, tab-specific slices stay on each concrete tab).
    private readonly EditableTabSession[] _editableTabs;

    // Status from the most recent verb. Only Apply/Confirm set it; every Edit, Cancel,
    // and Reset clears it so the View always reflects the most recent action (a fresh
    // edit has no success/error status to show).
    private string? _statusMessage;
    private bool _statusIsError;
    private bool _shouldClose;

    /// <summary>
    /// Snapshots <paramref name="runtime"/> into per-tab working/baseline copies. The
    /// <paramref name="runtime"/> reference is retained so a successful persist can write
    /// the merged values back, keeping the export flow and Global Hotkey runtime in sync
    /// without an app restart. <paramref name="runtime"/> is never mutated on open or edit.
    /// </summary>
    public SettingsSession(
        AppSettings runtime,
        ConfigurationService configuration,
        IGlobalHotkeyAdapter globalHotkeys)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _globalHotkeys = globalHotkeys ?? throw new ArgumentNullException(nameof(globalHotkeys));

        _general = new GeneralTabSettings(runtime);
        _globalHotkeyTab = new GlobalHotkeyTabSettings(runtime, globalHotkeys);
        _export = new ExportTabSettings();
        _interface = new InterfaceTabSettings();

        _editableTabs = new EditableTabSession[] { _general, _globalHotkeyTab };
    }

    // ── View ────────────────────────────────────────────────────────────

    /// <summary>
    /// Immutable snapshot of everything the code-behind binds right now: per-tab editable
    /// content, read-only tab content, composed button gating, and the sticky status.
    /// </summary>
    public SettingsView View => BuildView();

    // ── Editable General-tab edits ──────────────────────────────────────

    /// <summary>Sets the working Save Location and returns the refreshed view.</summary>
    public SettingsView EditSaveLocation(string path)
    {
        _general.EditSaveLocation(path);
        return ClearTransientStatus();
    }

    /// <summary>Sets the working Filename Template and returns the refreshed view.</summary>
    public SettingsView EditFilenameTemplate(string template)
    {
        _general.EditFilenameTemplate(template);
        return ClearTransientStatus();
    }

    /// <summary>
    /// Routes a folder-picker outcome into the General tab. A null/empty
    /// <paramref name="folderPath"/> (picker cancelled) is a no-op; a real selection
    /// replaces the working Save Location. Returns the refreshed view.
    /// </summary>
    public SettingsView ApplyFolderPickerResult(string? folderPath)
    {
        _general.ApplyFolderPickerResult(folderPath);
        return ClearTransientStatus();
    }

    // ── Editable Global Hotkeys-tab edits ───────────────────────────────

    /// <summary>
    /// Sets the working enabled state for one Global Hotkey and returns the refreshed view.
    /// </summary>
    public SettingsView EditGlobalHotkeyEnabled(int globalHotkeyId, bool enabled)
    {
        _globalHotkeyTab.EditEnabled(globalHotkeyId, enabled);
        return ClearTransientStatus();
    }

    // ── Session verbs ───────────────────────────────────────────────────

    /// <summary>
    /// Persists ALL editable tabs atomically with one save and keeps the window open. On
    /// success: writes the merged values back into the shared runtime, advances each tab's
    /// baseline, and reconciles runtime Global Hotkey registration. On failure: nothing moves —
    /// no baseline advance, no runtime write, no reconcile — and the error surfaces as
    /// status. An invalid tab blocks the save with a validation status message.
    /// </summary>
    public SettingsView Apply() => Persist(shouldCloseOnSuccess: false);

    /// <summary>
    /// Same as <see cref="Apply"/> but signals the window to close on a successful save.
    /// </summary>
    public SettingsView Confirm() => Persist(shouldCloseOnSuccess: true);

    /// <summary>
    /// Reverts every editable tab's working state to its baseline. Never persists or
    /// reconciles runtime registration. Returns the refreshed view.
    /// </summary>
    public SettingsView Cancel()
    {
        foreach (var tab in _editableTabs)
            tab.Cancel();
        return ClearTransientStatus();
    }

    /// <summary>
    /// Restores every editable tab's working state to defaults; baselines are left
    /// untouched so a later Cancel still reverts the reset. Never persists or reconciles
    /// runtime registration. Returns the refreshed view.
    /// </summary>
    public SettingsView Reset()
    {
        foreach (var tab in _editableTabs)
            tab.Reset();
        return ClearTransientStatus();
    }

    // ── Atomic persist ──────────────────────────────────────────────────

    private SettingsView Persist(bool shouldCloseOnSuccess)
    {
        // Composed validation: an invalid tab blocks the save with its first error.
        var invalid = _editableTabs.FirstOrDefault(t => !t.IsValid);
        if (invalid is not null)
        {
            _statusMessage = invalid.FirstError ?? "Cannot apply while settings are invalid.";
            _statusIsError = true;
            _shouldClose = false;
            return BuildView();
        }

        // Merge every editable tab's working slice onto a fresh copy of the runtime
        // settings so disjoint slices (General vs Global Hotkeys) combine without clobbering,
        // and future fields not owned by any tab are preserved.
        var merged = _runtime.Normalized();
        foreach (var tab in _editableTabs)
            tab.WriteInto(merged);

        var result = _configuration.Save(merged);
        if (!result.Success)
        {
            // All-or-nothing: nothing moves on failure. Baselines stay put, the runtime
            // is untouched, and no Global Hotkey registration is reconciled.
            _statusMessage = FormatSaveFailureMessage(result);
            _statusIsError = true;
            _shouldClose = false;
            return BuildView();
        }

        // Success: write each tab's committed slice back into the shared runtime so the
        // export flow and the Global Hotkey runtime see the new values without an app
        // restart. This mirrors how `merged` was built, so adding an editable field later
        // only touches the tab (no hand-rolled copy here). WriteInto writes independent
        // copies, so the runtime never aliases a tab's working state.
        foreach (var tab in _editableTabs)
            tab.WriteInto(_runtime);

        // Advance each tab's baseline so Cancel no longer reverts the committed change.
        foreach (var tab in _editableTabs)
            tab.Commit();

        // Reconcile runtime registration to match the persisted enabled states. The
        // adapter never throws for expected conflicts; failures surface as Failed rows
        // (read live from the adapter) and are summarized in the status so a failed
        // binding is visible, not overclaimed as active.
        var registrationResults = _globalHotkeys.ApplyEnabledStates(
            GlobalHotkeyRouteMap.EnabledGlobalHotkeyIds(merged));
        int failedRegistrations = registrationResults.Count(r => !r.Succeeded);

        _statusMessage = failedRegistrations > 0
            ? $"{SavedMessage} {failedRegistrations} global hotkey{(failedRegistrations == 1 ? "" : "s")} failed to register — see below."
            : SavedMessage;
        _statusIsError = false;
        _shouldClose = shouldCloseOnSuccess;
        return BuildView();
    }

    // ── View assembly ───────────────────────────────────────────────────

    /// <summary>
    /// Clears any Apply/Confirm status so the View reflects the most recent (non-verb)
    /// action, then returns the refreshed view. Called by every Edit, Cancel, and Reset.
    /// </summary>
    private SettingsView ClearTransientStatus()
    {
        _statusMessage = null;
        _statusIsError = false;
        _shouldClose = false;
        return BuildView();
    }

    private SettingsView BuildView() => new(
        SaveLocation: _general.SaveLocation,
        FilenameTemplate: _general.FilenameTemplate,
        FilenameTemplatePreview: _general.FilenameTemplatePreview,
        SaveLocationError: _general.SaveLocationError,
        FilenameTemplateError: _general.FilenameTemplateError,

        GlobalHotkeyRows: _globalHotkeyTab.GetRows(),

        Export: new ExportTabContent(
            Heading: _export.Heading,
            FormatName: _export.FormatName,
            FileExtension: _export.FileExtension,
            FormatDescription: _export.FormatDescription,
            PlannedFormatsNote: _export.PlannedFormatsNote),
        Interface: new InterfaceTabContent(
            Heading: _interface.Heading,
            Message: _interface.Message,
            PlannedSettingsNote: _interface.PlannedSettingsNote),

        CanApply: ComposedIsValid,
        CanConfirm: ComposedIsValid,
        CanCancel: true,
        CanReset: true,

        StatusMessage: _statusMessage,
        StatusIsError: _statusIsError,
        ShouldClose: _shouldClose);

    private bool ComposedIsValid
    {
        get
        {
            foreach (var tab in _editableTabs)
            {
                if (!tab.IsValid)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Formats a save failure message for UI display. Keeps sanitized ConfigurationService
    /// messages visible without throwing. Internal so tests and the session share it.
    /// </summary>
    internal static string FormatSaveFailureMessage(ConfigurationSaveResult result)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(result.Phase))
            parts.Add($"Failed during '{result.Phase}'.");

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            parts.Add(result.ErrorMessage);

        if (parts.Count == 0)
            return "Failed to save settings.";

        return string.Join(" ", parts);
    }
}
