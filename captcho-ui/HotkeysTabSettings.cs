// HotkeysTabSettings.cs — Pure C# editing seam for the Hotkeys settings tab.
//
// Owns the editable per-Global-Hotkey enabled states plus the settings session
// used by the Settings window: a working snapshot of the persisted settings, the
// display rows (binding, behavior description, registration status) for all four
// Global Hotkeys, inline enable/disable editing, and Apply/OK/Cancel actions
// backed by an injected save delegate and an injected Global Hotkey adapter.
// Apply/OK persist the enabled states and reconcile runtime registration; Cancel
// reverts to the last applied baseline. Construction snapshots the persisted
// settings so the active and persisted Settings are never mutated by opening or
// editing. Global Hotkey remapping and capture-mode routing are out of scope —
// only enable/disable per hotkey is supported.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Current registration status of a Global Hotkey as displayed on the Hotkeys tab.
/// Combines the user's enabled choice with the latest runtime registration result.
/// </summary>
public enum HotkeyRegistrationStatus
{
    /// <summary>The hotkey is enabled and registered successfully.</summary>
    Registered,
    /// <summary>The hotkey is enabled but registration failed (e.g. a conflict).</summary>
    Failed,
    /// <summary>The user has disabled the hotkey; it is intentionally not registered.</summary>
    Disabled,
    /// <summary>No registration result is available (e.g. hotkeys never initialized).</summary>
    Unknown,
}

/// <summary>
/// One row on the Hotkeys tab: the shortcut binding, what it captures, whether it
/// is currently enabled, and its registration status with any sanitized detail.
/// </summary>
public sealed record HotkeyRow(
    int Id,
    string Binding,
    string Behavior,
    bool IsEnabled,
    HotkeyRegistrationStatus Status,
    string StatusDetail);

/// <summary>
/// Pure C# coordinator for the Hotkeys settings tab. Holds a working snapshot of
/// the persisted per-hotkey enabled states, exposes the four Global Hotkeys with
/// their bindings and behavior descriptions, reflects the current runtime
/// registration status per hotkey, and drives the Apply/OK/Cancel session. Apply
/// and OK persist enabled states and reconcile runtime registration through the
/// injected Global Hotkey adapter; Cancel leaves runtime and persisted state
/// unchanged. Never mutates the active or persisted Settings on open or edit.
/// </summary>
public sealed class HotkeysTabSettings
{
    private readonly SaveSettingsDelegate _save;
    private readonly IGlobalHotkeyAdapter _hotkeys;

    private AppSettings _working;
    private AppSettings _baseline;
    private IReadOnlyList<HotkeyRegistrationResult> _registrationResults;

    /// <summary>
    /// Snapshots the persisted settings into working and baseline copies, stores
    /// the save delegate, and captures the current registration results for
    /// display. The <paramref name="persisted"/> object is never mutated by this
    /// instance.
    /// </summary>
    public HotkeysTabSettings(AppSettings persisted, SaveSettingsDelegate save, IGlobalHotkeyAdapter hotkeys)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(hotkeys);

        _save = save;
        _hotkeys = hotkeys;
        // Normalized() yields independent deep copies, so the active and persisted
        // settings are never mutated by opening or editing.
        _working = persisted.Normalized();
        _baseline = persisted.Normalized();
        _registrationResults = hotkeys.RegistrationResults;
    }

    // ── Working editable state ──────────────────────────────────────────

    /// <summary>
    /// Whether the hotkey with the given stable id is currently enabled in the
    /// working (editable) state.
    /// </summary>
    public bool IsEnabled(int hotkeyId) => _working.IsHotkeyEnabled(hotkeyId);

    /// <summary>
    /// Sets the working enabled state for a hotkey from user input. Never persists
    /// or mutates the original persisted settings. Toggling a hotkey off marks it
    /// disabled in the displayed rows immediately, before Apply.
    /// </summary>
    public void EditEnabled(int hotkeyId, bool enabled)
    {
        _working.HotkeyEnabledStates ??= new Dictionary<int, bool>();
        _working.HotkeyEnabledStates[hotkeyId] = enabled;
    }

    // ── Display rows ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the display rows for all four Global Hotkeys in stable-id order.
    /// Each row carries the shortcut binding, the capture-mode behavior, the
    /// working enabled state, and the registration status derived from the latest
    /// registration results. A disabled hotkey always reads as Disabled regardless
    /// of its registration result.
    /// </summary>
    public IReadOnlyList<HotkeyRow> GetRows()
    {
        var rows = new List<HotkeyRow>(HotkeyRouteMap.AllSpecs.Count);
        foreach (var spec in HotkeyRouteMap.AllSpecs)
        {
            bool enabled = IsEnabled(spec.Id);
            var (status, detail) = StatusFor(spec, enabled, _registrationResults);
            rows.Add(new HotkeyRow(
                Id: spec.Id,
                Binding: spec.Name,
                Behavior: BehaviorFor(spec.Route),
                IsEnabled: enabled,
                Status: status,
                StatusDetail: detail));
        }
        return rows;
    }

    private static (HotkeyRegistrationStatus status, string detail) StatusFor(
        HotkeySpec spec,
        bool enabled,
        IReadOnlyList<HotkeyRegistrationResult> results)
    {
        if (!enabled)
            return (HotkeyRegistrationStatus.Disabled, string.Empty);

        var match = results.FirstOrDefault(r => r.Spec.Id == spec.Id);
        if (match is null)
            return (HotkeyRegistrationStatus.Unknown, string.Empty);

        return match.Succeeded
            ? (HotkeyRegistrationStatus.Registered, string.Empty)
            : (HotkeyRegistrationStatus.Failed, match.Error);
    }

    /// <summary>
    /// Accurate behavior description for each capture route, phrased in the
    /// Capture-pipeline terminology from CONTEXT.md. Keep in sync with the routes
    /// the hotkeys actually trigger (see MainWindow.DispatchHotkeyRoute).
    /// </summary>
    private static string BehaviorFor(HotkeyRoute route) => route switch
    {
        HotkeyRoute.CurrentMonitor => "Captures the monitor the cursor is on.",
        HotkeyRoute.ActiveWindow => "Captures the active (foreground) window.",
        HotkeyRoute.FullDesktop => "Captures the full virtual desktop across all monitors.",
        HotkeyRoute.RectangularRegion => "Opens the Selection overlay to draw a region on the screen and capture it.",
        _ => string.Empty,
    };

    // ── Validation and gating ───────────────────────────────────────────

    /// <summary>
    /// Always valid: per-hotkey enabled states are booleans with no invalid value.
    /// </summary>
    public bool IsValid => true;

    /// <summary>Whether Apply may run (always, since enabled states are always valid).</summary>
    public bool CanApply => IsValid;

    /// <summary>Whether OK may run (always, since enabled states are always valid).</summary>
    public bool CanConfirm => IsValid;

    /// <summary>True when any working enabled state differs from its last applied baseline.</summary>
    public bool IsDirty
    {
        get
        {
            foreach (var spec in HotkeyRouteMap.AllSpecs)
            {
                if (_working.IsHotkeyEnabled(spec.Id) != _baseline.IsHotkeyEnabled(spec.Id))
                    return true;
            }
            return false;
        }
    }

    // ── Session actions ─────────────────────────────────────────────────

    /// <summary>
    /// Persists the working enabled states via the save delegate, reconciles
    /// runtime registration to match, and keeps the window open. Updates the
    /// baseline on success so Cancel no longer reverts the change. Never reports
    /// success when the save failed, and never reconciles runtime registration on
    /// a failed save.
    /// </summary>
    public GeneralSettingsActionResult Apply() => Persist(shouldCloseOnSuccess: false);

    /// <summary>
    /// Persists the working enabled states, reconciles runtime registration, and
    /// signals the window to close — but only after a successful save. On failure
    /// the window stays open with an inline message and runtime registration is
    /// left unchanged.
    /// </summary>
    public GeneralSettingsActionResult Confirm() => Persist(shouldCloseOnSuccess: true);

    /// <summary>
    /// Discards edits made since the last successful Apply (or since open if Apply
    /// has not run), reverting the working enabled states to the baseline. Never
    /// persists or reconciles runtime registration.
    /// </summary>
    public void Cancel()
    {
        _working = _baseline.Normalized();
    }

    private GeneralSettingsActionResult Persist(bool shouldCloseOnSuccess)
    {
        var toSave = BuildPersistSettings();
        var result = _save(toSave);

        if (!result.Success)
        {
            return new GeneralSettingsActionResult
            {
                Success = false,
                Message = SettingsWindowCoordinator.FormatSaveFailureMessage(result),
                ShouldClose = false,
            };
        }

        _baseline = _working.Normalized();

        // Reconcile runtime registration to match the persisted enabled states.
        // The adapter never throws for expected conflicts; failures surface as
        // Failed registration results that the next GetRows() call will display.
        _registrationResults = _hotkeys.ApplyEnabledStates(HotkeyRouteMap.EnabledHotkeyIds(_working));

        int failedRegistrations = _registrationResults.Count(r => !r.Succeeded);
        return new GeneralSettingsActionResult
        {
            Success = true,
            // The save itself succeeded; surface any runtime registration conflicts
            // in the status so a failed binding is visible to the user, not just in
            // its row. Never falsely reports the binding as active.
            Message = failedRegistrations > 0
                ? $"{SettingsWindowCoordinator.SavedMessage} {failedRegistrations} global hotkey{(failedRegistrations == 1 ? "" : "s")} failed to register — see below."
                : SettingsWindowCoordinator.SavedMessage,
            ShouldClose = shouldCloseOnSuccess,
        };
    }

    /// <summary>
    /// Builds the settings to persist: a normalized copy of the working state with
    /// an all-enabled map collapsed back to null, so persisted JSON stays clean
    /// (the hotkey field is omitted entirely when no hotkey is disabled).
    /// </summary>
    private AppSettings BuildPersistSettings()
    {
        var toSave = _working.Normalized();
        toSave.HotkeyEnabledStates = NormalizeForPersistence(toSave.HotkeyEnabledStates);
        return toSave;
    }

    private static Dictionary<int, bool>? NormalizeForPersistence(Dictionary<int, bool>? states)
    {
        if (states is null)
            return null;

        bool allEnabled = HotkeyRouteMap.AllSpecs.All(s => states.TryGetValue(s.Id, out var v) && v);
        return allEnabled ? null : states;
    }
}
