// GlobalHotkeyTabSettings.cs — Pure C# editing seam for the Global Hotkeys settings tab.
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
// remapping and capture-mode routing are out of scope — only enable/disable per Global Hotkey.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Current registration status of a Global Hotkey as displayed on the Global Hotkeys tab.
/// Combines the user's enabled choice with the latest runtime registration result.
/// </summary>
public enum GlobalHotkeyRegistrationStatus
{
    /// <summary>The Global Hotkey is enabled and registered successfully.</summary>
    Registered,
    /// <summary>The Global Hotkey is enabled but registration failed (e.g. a conflict).</summary>
    Failed,
    /// <summary>The user has disabled the Global Hotkey; it is intentionally not registered.</summary>
    Disabled,
    /// <summary>No registration result is available (e.g. Global Hotkeys never initialized).</summary>
    Unknown,
}

/// <summary>
/// One row on the Global Hotkeys tab: the Global Hotkey binding, what it captures, whether it
/// is currently enabled, and its registration status with any sanitized detail.
/// </summary>
public sealed record GlobalHotkeyRow(
    int Id,
    string Binding,
    string Behavior,
    bool IsEnabled,
    GlobalHotkeyRegistrationStatus Status,
    string StatusDetail);

/// <summary>
/// Pure C# editing seam for the Global Hotkeys settings tab. Holds a working snapshot of
/// the persisted per-Global-Hotkey enabled states, exposes the four Global Hotkeys with
/// their bindings and behavior descriptions, and reflects the current runtime
/// registration status per global hotkey (read live from the adapter so a reconcile by the
/// <see cref="SettingsSession"/> is visible without rebuilding the tab). Never
/// persists or mutates the active/persisted Settings on open or edit; the session
/// orchestrates persistence and runtime registration across all tabs.
/// </summary>
public sealed class GlobalHotkeyTabSettings : IEditableSettingsTab
{
    private readonly IGlobalHotkeyAdapter _globalHotkeys;

    private AppSettings _working;
    private AppSettings _baseline;

    /// <summary>
    /// Snapshots the persisted settings into working and baseline copies, stores the
    /// Global Hotkey adapter (read for registration status display), and captures the
    /// current registration results. The <paramref name="persisted"/> object is never
    /// mutated by this instance.
    /// </summary>
    public GlobalHotkeyTabSettings(AppSettings persisted, IGlobalHotkeyAdapter globalHotkeys)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(globalHotkeys);

        _globalHotkeys = globalHotkeys;
        // Normalized() yields independent deep copies, so the active and persisted
        // settings are never mutated by opening or editing.
        _working = persisted.Normalized();
        _baseline = persisted.Normalized();
    }

    // ── Working editable state ──────────────────────────────────────────

    /// <summary>
    /// Whether the Global Hotkey with the given stable id is currently enabled in the
    /// working (editable) state. The id is the UI-layer Win32 hotkey identity (the row
    /// key); it is translated to the capture-layer route identity here.
    /// </summary>
    public bool IsEnabled(int globalHotkeyId) =>
        _working.IsGlobalHotkeyEnabled(SpecFor(globalHotkeyId).Route);

    /// <summary>
    /// Sets the working enabled state for a Global Hotkey from user input. Never persists
    /// or mutates the original persisted settings. Toggling a Global Hotkey off marks it
    /// disabled in the displayed rows immediately, before Apply.
    /// </summary>
    public void EditEnabled(int globalHotkeyId, bool enabled)
    {
        var route = SpecFor(globalHotkeyId).Route;
        _working.GlobalHotkeyEnabledStates ??= new Dictionary<GlobalHotkeyRoute, bool>();
        _working.GlobalHotkeyEnabledStates[route] = enabled;
    }

    /// <summary>
    /// Resolves the UI-layer spec for a Win32 hotkey id. Throws for unknown ids — the tab
    /// is an internal seam driven by row ids (1–4), so an unknown id is a programmer error.
    /// </summary>
    private static GlobalHotkeySpec SpecFor(int globalHotkeyId) =>
        GlobalHotkeyRouteMap.FindSpec(globalHotkeyId)
        ?? throw new ArgumentOutOfRangeException(nameof(globalHotkeyId), globalHotkeyId, "Unknown Global Hotkey id.");

    // ── Display rows ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the display rows for all four Global Hotkeys in stable-id order.
    /// Each row carries the Global Hotkey binding, the capture-mode behavior, the
    /// working enabled state, and the registration status derived live from the
    /// adapter's latest results. A disabled Global Hotkey always reads as Disabled regardless
    /// of its registration result.
    /// </summary>
    public IReadOnlyList<GlobalHotkeyRow> GetRows()
    {
        var results = _globalHotkeys.RegistrationResults;
        var rows = new List<GlobalHotkeyRow>(GlobalHotkeyRouteMap.AllSpecs.Count);
        foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
        {
            bool enabled = IsEnabled(spec.Id);
            var (status, detail) = StatusFor(spec, enabled, results);
            rows.Add(new GlobalHotkeyRow(
                Id: spec.Id,
                Binding: spec.Name,
                Behavior: BehaviorFor(spec.Route),
                IsEnabled: enabled,
                Status: status,
                StatusDetail: detail));
        }
        return rows;
    }

    private static (GlobalHotkeyRegistrationStatus status, string detail) StatusFor(
        GlobalHotkeySpec spec,
        bool enabled,
        IReadOnlyList<GlobalHotkeyRegistrationResult> results)
    {
        if (!enabled)
            return (GlobalHotkeyRegistrationStatus.Disabled, string.Empty);

        var match = results.FirstOrDefault(r => r.Spec.Id == spec.Id);
        if (match is null)
            return (GlobalHotkeyRegistrationStatus.Unknown, string.Empty);

        return match.Succeeded
            ? (GlobalHotkeyRegistrationStatus.Registered, string.Empty)
            : (GlobalHotkeyRegistrationStatus.Failed, match.Error);
    }

    /// <summary>
    /// Accurate behavior description for each capture route, phrased in the
    /// Capture-pipeline terminology from CONTEXT.md. Keep in sync with the routes
    /// the global hotkeys actually trigger (see MainWindow.DispatchGlobalHotkeyRoute).
    /// </summary>
    private static string BehaviorFor(GlobalHotkeyRoute route) => route switch
    {
        GlobalHotkeyRoute.CurrentMonitor => "Captures the monitor the cursor is on.",
        GlobalHotkeyRoute.ActiveWindow => "Captures the active (foreground) window.",
        GlobalHotkeyRoute.FullDesktop => "Captures the full virtual desktop across all monitors.",
        GlobalHotkeyRoute.RectangularRegion => "Opens the Selection overlay to draw a region on the screen and capture it.",
        _ => string.Empty,
    };

    // ── Validation, gating, dirty tracking ──────────────────────────────

    /// <summary>
    /// Always valid: per-Global-Hotkey enabled states are booleans with no invalid value.
    /// </summary>
    public bool IsValid => true;

    /// <summary>First error is always null — enabled states cannot be invalid.</summary>
    public string? FirstError => null;

    /// <summary>True when any working enabled state differs from its last applied baseline.</summary>
    public bool IsDirty
    {
        get
        {
            foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
            {
                if (_working.IsGlobalHotkeyEnabled(spec.Route) != _baseline.IsGlobalHotkeyEnabled(spec.Route))
                    return true;
            }
            return false;
        }
    }

    // ── IEditableSettingsTab: merge, commit, revert, reset ──────────────

    /// <summary>
    /// Writes this tab's working per-Global-Hotkey enabled states into <paramref name="target"/>,
    /// collapsing an all-enabled map back to null so persisted JSON stays clean (the
    /// Global Hotkey field is omitted entirely when no Global Hotkey is disabled), and deep-copying the
    /// map so the target never aliases this tab's working state (the session writes into
    /// the live runtime with the same call). Leaves other tabs' slices untouched so the
    /// session can merge every tab and persist once.
    /// </summary>
    public void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalized = NormalizeForPersistence(_working.GlobalHotkeyEnabledStates);
        target.GlobalHotkeyEnabledStates = normalized is null
            ? null
            : new Dictionary<GlobalHotkeyRoute, bool>(normalized);
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
        // default (and reset) state. Only this tab's slice is reset so a Global Hotkeys
        // Reset cannot leak into the General tab's fields in the working snapshot.
        _working.GlobalHotkeyEnabledStates = null;
    }

    private static Dictionary<GlobalHotkeyRoute, bool>? NormalizeForPersistence(Dictionary<GlobalHotkeyRoute, bool>? states)
    {
        if (states is null)
            return null;

        bool allEnabled = GlobalHotkeyRouteMap.AllSpecs.All(s => states.TryGetValue(s.Route, out var v) && v);
        return allEnabled ? null : states;
    }
}
