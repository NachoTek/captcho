// HotkeysTabSettings.cs — Pure C# editing seam for the Hotkeys settings tab.
//
// Owns the editable per-Global-Hotkey enabled states: a working snapshot of the
// persisted settings, the display rows (binding, behavior description, registration
// status) for all four Global Hotkeys, and inline enable/disable editing. Construction
// snapshots the persisted settings so the active and persisted Settings are never
// mutated by opening or editing. This tab no longer persists or reconciles runtime
// registration — it writes its working slice into a merged AppSettings via WriteInto
// and advances its baseline via Commit at the SettingsSession's direction, which also
// owns the runtime reconcile through the Global Hotkey adapter. Registration status is
// read live from the adapter so rows reflect the latest reconcile. Global Hotkey
// remapping and capture-mode routing are out of scope — only enable/disable per hotkey.

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
/// Pure C# editing seam for the Hotkeys settings tab. Holds a working snapshot of
/// the persisted per-hotkey enabled states, exposes the four Global Hotkeys with
/// their bindings and behavior descriptions, and reflects the current runtime
/// registration status per hotkey (read live from the adapter so a reconcile by the
/// <see cref="SettingsSession"/> is visible without rebuilding the tab). Never
/// persists or mutates the active/persisted Settings on open or edit; the session
/// orchestrates persistence and runtime registration across all tabs.
/// </summary>
public sealed class HotkeysTabSettings : IEditableSettingsTab
{
    private readonly IGlobalHotkeyAdapter _hotkeys;

    private AppSettings _working;
    private AppSettings _baseline;

    /// <summary>
    /// Snapshots the persisted settings into working and baseline copies, stores the
    /// Global Hotkey adapter (read for registration status display), and captures the
    /// current registration results. The <paramref name="persisted"/> object is never
    /// mutated by this instance.
    /// </summary>
    public HotkeysTabSettings(AppSettings persisted, IGlobalHotkeyAdapter hotkeys)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(hotkeys);

        _hotkeys = hotkeys;
        // Normalized() yields independent deep copies, so the active and persisted
        // settings are never mutated by opening or editing.
        _working = persisted.Normalized();
        _baseline = persisted.Normalized();
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
    /// working enabled state, and the registration status derived live from the
    /// adapter's latest results. A disabled hotkey always reads as Disabled regardless
    /// of its registration result.
    /// </summary>
    public IReadOnlyList<HotkeyRow> GetRows()
    {
        var results = _hotkeys.RegistrationResults;
        var rows = new List<HotkeyRow>(HotkeyRouteMap.AllSpecs.Count);
        foreach (var spec in HotkeyRouteMap.AllSpecs)
        {
            bool enabled = IsEnabled(spec.Id);
            var (status, detail) = StatusFor(spec, enabled, results);
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

    // ── Validation, gating, dirty tracking ──────────────────────────────

    /// <summary>
    /// Always valid: per-hotkey enabled states are booleans with no invalid value.
    /// </summary>
    public bool IsValid => true;

    /// <summary>First error is always null — enabled states cannot be invalid.</summary>
    public string? FirstError => null;

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

    // ── IEditableSettingsTab: merge, commit, revert, reset ──────────────

    /// <summary>
    /// Writes this tab's working per-hotkey enabled states into <paramref name="target"/>,
    /// collapsing an all-enabled map back to null so persisted JSON stays clean (the
    /// hotkey field is omitted entirely when no hotkey is disabled), and deep-copying the
    /// map so the target never aliases this tab's working state (the session writes into
    /// the live runtime with the same call). Leaves other tabs' slices untouched so the
    /// session can merge every tab and persist once.
    /// </summary>
    public void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalized = NormalizeForPersistence(_working.HotkeyEnabledStates);
        target.HotkeyEnabledStates = normalized is null
            ? null
            : new Dictionary<int, bool>(normalized);
    }

    /// <summary>
    /// Advances the baseline to the current working state. Called by the session only
    /// after a successful persist, so Cancel no longer reverts the committed change.
    /// </summary>
    public void Commit()
    {
        _baseline = _working.Normalized();
    }

    /// <summary>
    /// Discards edits made since the last successful Apply (or since open if Apply
    /// has not run), reverting the working enabled states to the baseline. Never
    /// persists or reconciles runtime registration.
    /// </summary>
    public void Cancel()
    {
        _working = _baseline.Normalized();
    }

    /// <summary>
    /// Restores every Global Hotkey to enabled (the default) in the current editing
    /// session, so the user can inspect the default enabled state immediately. The
    /// baseline is left untouched and runtime registration is never reconciled, so
    /// Reset alone does not persist or change runtime Global Hotkey registrations,
    /// and a later Cancel still reverts to the states that existed before Reset.
    /// Apply or OK after Reset persists all-enabled and the session reconciles runtime.
    /// </summary>
    public void Reset()
    {
        // A null states map means every Global Hotkey is enabled — exactly the
        // default (and reset) state. Only this tab's slice is reset so a Hotkeys
        // Reset cannot leak into the General tab's fields in the working snapshot.
        _working.HotkeyEnabledStates = null;
    }

    private static Dictionary<int, bool>? NormalizeForPersistence(Dictionary<int, bool>? states)
    {
        if (states is null)
            return null;

        bool allEnabled = HotkeyRouteMap.AllSpecs.All(s => states.TryGetValue(s.Id, out var v) && v);
        return allEnabled ? null : states;
    }
}
