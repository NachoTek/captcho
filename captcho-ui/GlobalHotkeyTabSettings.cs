// GlobalHotkeyTabSettings.cs — Pure C# editing seam for the Global Hotkeys settings tab.
//
// Owns the editable per-Global-Hotkey enabled states: a working snapshot of the
// persisted settings, the display rows (binding, behavior description, registration
// status) for all four Global Hotkeys, and inline enable/disable editing. The shared
// snapshot/apply/cancel/reset plumbing lives in EditableTabSession (written once); this
// tab declares only its own Global Hotkeys slice — what to edit, validate, merge via
// WriteInto, and restore via ApplyDefaults. Registration status is read live from the
// adapter so rows reflect the latest reconcile driven by the session. Global Hotkey
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
internal sealed class GlobalHotkeyTabSettings : EditableTabSession
{
    private readonly IGlobalHotkeyAdapter _globalHotkeys;

    /// <summary>
    /// Snapshots the persisted settings into independent working and baseline copies via
    /// <see cref="EditableTabSession"/>'s constructor, stores the Global Hotkey adapter
    /// (read for registration status display), and captures the current registration
    /// results. The <paramref name="persisted"/> object is never mutated by this instance.
    /// </summary>
    public GlobalHotkeyTabSettings(AppSettings persisted, IGlobalHotkeyAdapter globalHotkeys)
        : base(persisted)
    {
        ArgumentNullException.ThrowIfNull(globalHotkeys);
        _globalHotkeys = globalHotkeys;
    }

    // ── Working editable state ──────────────────────────────────────────

    /// <summary>
    /// Whether the Global Hotkey with the given stable id is currently enabled in the
    /// working (editable) state. The id is the UI-layer Win32 hotkey identity (the row
    /// key); it is translated to the capture-layer route identity here.
    /// </summary>
    public bool IsEnabled(int globalHotkeyId) =>
        Working.IsGlobalHotkeyEnabled(SpecFor(globalHotkeyId).Route);

    /// <summary>
    /// Sets the working enabled state for a Global Hotkey from user input. Never persists
    /// or mutates the original persisted settings. Toggling a Global Hotkey off marks it
    /// disabled in the displayed rows immediately, before Apply.
    /// </summary>
    public void EditEnabled(int globalHotkeyId, bool enabled)
    {
        var route = SpecFor(globalHotkeyId).Route;
        Working.GlobalHotkeyEnabledStates ??= new Dictionary<GlobalHotkeyRoute, bool>();
        Working.GlobalHotkeyEnabledStates[route] = enabled;
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
    /// True when the working per-Global-Hotkey enabled states pass validation. Derived
    /// from <see cref="AppSettings.Validate"/> (the source of truth) so this tab is never
    /// "valid by accident": today only an out-of-range route key — unreachable through
    /// normal editing — can fail, but the rule flows straight through the composed gate if
    /// one is ever added. See <see cref="AppSettings.Validate"/>.
    /// </summary>
    public override bool IsValid => SliceIssues().Count == 0;

    /// <summary>
    /// First validation error for this tab's enabled states, or null when valid. Sourced
    /// from <see cref="AppSettings.Validate"/>.
    /// </summary>
    public override string? FirstError => SliceIssues().FirstOrDefault()?.Message;

    /// <summary>True when any working enabled state differs from its last applied baseline.</summary>
    public override bool IsDirty
    {
        get
        {
            foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
            {
                if (Working.IsGlobalHotkeyEnabled(spec.Route) != Baseline.IsGlobalHotkeyEnabled(spec.Route))
                    return true;
            }
            return false;
        }
    }

    // ── Global Hotkeys slice: merge, defaults ───────────────────────────

    /// <summary>
    /// Writes this tab's working per-Global-Hotkey enabled states into <paramref name="target"/>,
    /// collapsing an all-enabled map back to null so persisted JSON stays clean (the
    /// Global Hotkey field is omitted entirely when no Global Hotkey is disabled), and deep-copying the
    /// map so the target never aliases this tab's working state (the session writes into
    /// the live runtime with the same call). Leaves other tabs' slices untouched so the
    /// session can merge every tab and persist once.
    /// </summary>
    public override void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalized = NormalizeForPersistence(Working.GlobalHotkeyEnabledStates);
        target.GlobalHotkeyEnabledStates = normalized is null
            ? null
            : new Dictionary<GlobalHotkeyRoute, bool>(normalized);
    }

    /// <summary>
    /// Restores this tab's per-Global-Hotkey enabled states to their default (every
    /// Global Hotkey enabled), read from <see cref="AppSettings.WithDefaults"/> (the
    /// single default source shared by every tab). Only this tab's slice is touched; the
    /// baseline is left untouched by <see cref="EditableTabSession.Reset"/>, so Reset alone
    /// never persists or reconciles runtime registration, and a later Cancel still reverts
    /// to the states that existed before Reset. Apply or OK after Reset persists
    /// all-enabled and the session reconciles runtime.
    /// </summary>
    protected override void ApplyDefaults(AppSettings working)
    {
        working.GlobalHotkeyEnabledStates = AppSettings.WithDefaults().GlobalHotkeyEnabledStates;
    }

    private static Dictionary<GlobalHotkeyRoute, bool>? NormalizeForPersistence(Dictionary<GlobalHotkeyRoute, bool>? states)
    {
        if (states is null)
            return null;

        bool allEnabled = GlobalHotkeyRouteMap.AllSpecs.All(s => states.TryGetValue(s.Route, out var v) && v);
        return allEnabled ? null : states;
    }
}
