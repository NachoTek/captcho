// GlobalHotkeyTabSettings.cs — Pure C# editing seam for the Global Hotkeys settings tab.
//
// Owns the editable per-Global-Hotkey state: a working snapshot of the persisted
// settings, the display rows (binding, behavior description, registration
// status) for all four Global Hotkeys, and inline enable/disable editing plus
// key-combination recording for the Full Desktop row (issue #51; the remaining
// routes migrate in #52). The shared snapshot/apply/cancel/reset plumbing lives
// in EditableTabSession (written once); this tab declares only its own Global
// Hotkeys slice — what to edit, validate, merge via WriteInto, and restore via
// ApplyDefaults. Registration status is read live from the adapter so rows
// reflect the latest reconcile driven by the session. Recording detects
// suspected conflicts against the other rows' working combinations and surfaces
// them as warnings without rejecting the value; registration failure is shown
// as a Failed row status after Apply/OK reconciles.

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
/// is currently enabled, its registration status with any sanitized detail, and any suspected
/// conflict warning for the recorded combination.
/// </summary>
public sealed record GlobalHotkeyRow(
    int Id,
    string Binding,
    string Behavior,
    bool IsEnabled,
    GlobalHotkeyRegistrationStatus Status,
    string StatusDetail,
    string? BindingWarning = null);

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

    // ── Working editable state: key bindings ───────────────────────────

    /// <summary>
    /// The working (editable) key combination for the Global Hotkey with the given
    /// stable id: a recorded binding if one is set, otherwise the route's default
    /// combination from Configuration.
    /// </summary>
    public HotkeyBinding WorkingBinding(int globalHotkeyId) =>
        Working.EffectiveGlobalHotkeyBinding(SpecFor(globalHotkeyId).Route);

    /// <summary>
    /// Records a key combination for the Global Hotkey with the given stable id from
    /// inline recorder input. Never persists or mutates the original persisted
    /// settings. Detects suspected conflicts — the combination already being another
    /// enabled row's working combination — and returns a warning message without
    /// rejecting the value; returns null when no conflict is suspected.
    /// </summary>
    public string? RecordBinding(int globalHotkeyId, HotkeyBinding binding)
    {
        var spec = SpecFor(globalHotkeyId);
        Working.GlobalHotkeyBindings ??= new Dictionary<GlobalHotkeyRoute, HotkeyBinding>();
        Working.GlobalHotkeyBindings[spec.Route] = binding;
        return ConflictWarningFor(spec, binding);
    }

    /// <summary>
    /// The suspected-conflict warning for a row's current working combination, or
    /// null. Computed from the working state (never cached) so Cancel/Reset clear
    /// warnings automatically with the snapshot swap. Surfaced on the row so the
    /// recorder shows it inline.
    /// </summary>
    public string? BindingWarningFor(int globalHotkeyId) =>
        ConflictWarningFor(SpecFor(globalHotkeyId), WorkingBinding(globalHotkeyId));

    /// <summary>
    /// Computes the suspected-conflict warning for recording <paramref name="binding"/>
    /// on <paramref name="spec"/>: whether the same combination is another enabled
    /// row's working combination (its route differs, and the conflict would only
    /// surface at registration). Disabled rows cannot hold an active combination,
    /// so they never conflict.
    /// </summary>
    private string? ConflictWarningFor(GlobalHotkeySpec spec, HotkeyBinding binding)
    {
        foreach (var other in GlobalHotkeyRouteMap.AllSpecs)
        {
            if (other.Id == spec.Id || !IsEnabled(other.Id))
                continue;
            var otherBinding = Working.EffectiveGlobalHotkeyBinding(other.Route);
            if (otherBinding.Equals(binding))
                return $"This combination is already used by {DisplayName(otherBinding)} ({BehaviorFor(other.Route)})";
        }
        return null;
    }

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
            var binding = Working.EffectiveGlobalHotkeyBinding(spec.Route);
            rows.Add(new GlobalHotkeyRow(
                Id: spec.Id,
                Binding: DisplayName(binding),
                Behavior: BehaviorFor(spec.Route),
                IsEnabled: enabled,
                Status: status,
                StatusDetail: detail,
                BindingWarning: ConflictWarningFor(spec, binding)));
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

    /// <summary>
    /// Human-readable name for a key combination, in the same style as the legacy
    /// spec names ("Shift + Print Screen"). Modifier order is fixed (Win, Ctrl,
    /// Alt, Shift); the virtual key is spelled out for known keys and rendered as
    /// a VK code otherwise.
    /// </summary>
    internal static string DisplayName(HotkeyBinding binding)
    {
        const int MOD_ALT = 0x0001;
        const int MOD_CONTROL = 0x0002;
        var parts = new List<string>();
        if ((binding.Modifiers & GlobalHotkeyBindingDefaults.MOD_WIN) != 0) parts.Add("Win");
        if ((binding.Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((binding.Modifiers & GlobalHotkeyBindingDefaults.MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(VirtualKeyName(binding.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string VirtualKeyName(int virtualKey) => virtualKey switch
    {
        0x2C => "Print Screen",
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x70 + 1}",
        >= 0x30 and <= 0x39 => ((char)('0' + (virtualKey - 0x30))).ToString(),
        >= 0x41 and <= 0x5A => ((char)('A' + (virtualKey - 0x41))).ToString(),
        0x20 => "Space",
        0x1B => "Esc",
        0x09 => "Tab",
        0x0D => "Enter",
        _ => $"Key 0x{virtualKey:X2}",
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

    /// <summary>True when any working enabled state or binding differs from its last applied baseline.</summary>
    public override bool IsDirty
    {
        get
        {
            foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
            {
                if (Working.IsGlobalHotkeyEnabled(spec.Route) != Baseline.IsGlobalHotkeyEnabled(spec.Route))
                    return true;
                if (!Working.EffectiveGlobalHotkeyBinding(spec.Route)
                        .Equals(Baseline.EffectiveGlobalHotkeyBinding(spec.Route)))
                    return true;
            }
            return false;
        }
    }

    // ── Global Hotkeys slice: merge, defaults ───────────────────────────

    /// <summary>
    /// Writes this tab's working per-Global-Hotkey state into <paramref name="target"/>:
    /// enabled states (collapsing an all-enabled map back to null so persisted JSON stays
    /// clean) and key bindings (dropping entries that match their route default, collapsing
    /// an all-default map back to null), deep-copying both so the target never aliases this
    /// tab's working state. Leaves other tabs' slices untouched so the session can merge
    /// every tab and persist once.
    /// </summary>
    public override void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalized = NormalizeForPersistence(Working.GlobalHotkeyEnabledStates);
        target.GlobalHotkeyEnabledStates = normalized is null
            ? null
            : new Dictionary<GlobalHotkeyRoute, bool>(normalized);
        target.GlobalHotkeyBindings = NormalizeBindingsForPersistence(Working);
    }

    /// <summary>
    /// Restores this tab's slice to defaults: every Global Hotkey enabled with its
    /// default (legacy) key combination, read from <see cref="AppSettings.WithDefaults"/>
    /// (the single default source shared by every tab). Recorded conflict warnings are
    /// cleared — defaults never conflict with each other. Only this tab's slice is
    /// touched; the baseline is left untouched by <see cref="EditableTabSession.Reset"/>,
    /// so Reset alone never persists or reconciles runtime registration, and a later
    /// Cancel still reverts to the state that existed before Reset. Apply or OK after
    /// Reset persists the defaults and the session reconciles runtime.
    /// </summary>
    protected override void ApplyDefaults(AppSettings working)
    {
        working.GlobalHotkeyEnabledStates = AppSettings.WithDefaults().GlobalHotkeyEnabledStates;
        working.GlobalHotkeyBindings = null;
    }

    private static Dictionary<GlobalHotkeyRoute, bool>? NormalizeForPersistence(Dictionary<GlobalHotkeyRoute, bool>? states)
    {
        if (states is null)
            return null;

        bool allEnabled = GlobalHotkeyRouteMap.AllSpecs.All(s => states.TryGetValue(s.Route, out var v) && v);
        return allEnabled ? null : states;
    }

    /// <summary>
    /// Builds the persisted bindings map for the working settings: only routes whose
    /// working combination differs from its default are kept, so a map that is entirely
    /// default persists as null (clean JSON) and reverting a remap to the default drops
    /// its entry.
    /// </summary>
    private static Dictionary<GlobalHotkeyRoute, HotkeyBinding>? NormalizeBindingsForPersistence(AppSettings working)
    {
        Dictionary<GlobalHotkeyRoute, HotkeyBinding>? normalized = null;
        foreach (var spec in GlobalHotkeyRouteMap.AllSpecs)
        {
            var binding = working.EffectiveGlobalHotkeyBinding(spec.Route);
            if (binding.Equals(GlobalHotkeyBindingDefaults.For(spec.Route)))
                continue;
            normalized ??= new Dictionary<GlobalHotkeyRoute, HotkeyBinding>();
            normalized[spec.Route] = binding;
        }
        return normalized;
    }
}
