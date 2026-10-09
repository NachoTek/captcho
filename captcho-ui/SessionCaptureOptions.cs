// SessionCaptureOptions.cs — Workflow-side Capture-options override holder for
// one application session.
//
// The two agreed top-level behavioral seams are SettingsSession (committed
// Configuration) and one new WinUI-free capture/post-capture workflow session.
// SessionCaptureOptions is the workflow session's Capture-options override
// state — distinct from committed Configuration so a temporary override can
// affect a Capture without ever mutating persistent Settings or Configuration
// (spec #35 AC #3). It is constructed from the runtime AppSettings reference
// (so later committed-default changes are observable) but never writes back to
// it.
//
// The override model is per-Capture-Mode and per-field:
//
//   - Each (mode, field) pair carries an optional override. A null override means
//     "use the committed default"; a non-null override pins the value for the
//     session, taking precedence over later committed-default changes.
//   - Each field is "visible" only for the modes it applies to (pointer for every
//     mode; decorations/shadow only for window modes). EffectiveFor(mode) only
//     considers overrides for fields that are visible for that mode — so an
//     override for a non-window field cannot leak into a non-window Capture
//     (spec #35 AC #4).
//   - The decoration/shadow dependency (spec #30) is enforced at override time
//     and at EffectiveFor read time, mirroring CaptureOptions.Normalized() and
//     CaptureTabSettings's edit behavior.
//
// This class is the pure-state seam the workflow session composes into its
// wider Capture routing. It does not touch WinUI, native Capture, or the file
// system, so every behavior is covered headlessly.

using System;
using System.Collections.Generic;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Workflow-side holder for per-Capture-Mode Capture-options overrides. Distinct
/// from committed Configuration — overrides never write back to the AppSettings
/// they were constructed from (spec #35 AC #3).
/// </summary>
public sealed class SessionCaptureOptions
{
    private readonly AppSettings _committed;

    // Per-mode override sets. Lazily allocated so a session that never overrides
    // anything carries no per-mode state. Each set is owned by exactly one mode.
    private readonly Dictionary<CaptureMode, OverrideSet> _overrides = new();

    /// <summary>
    /// Constructs a session holder for the given committed <paramref name="settings"/>.
    /// The reference is retained (so later committed-default changes are observable)
    /// but never written back to — overrides live only in this instance.
    /// </summary>
    public SessionCaptureOptions(AppSettings settings)
    {
        _committed = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    // ── Per-mode visibility (spec #35 AC #1, spec #30 dependency) ───────

    /// <summary>
    /// Whether the mouse-pointer option is visible for the given Capture Mode.
    /// True for every mode — a Frame always has potential pointer pixels.
    /// </summary>
    public static bool IsPointerVisible(CaptureMode mode) => true;

    /// <summary>
    /// Whether the window-decoration option is visible for the given Capture Mode.
    /// True only for window Capture Modes (ActiveWindow and SelectedWindow); other
    /// modes have no single window to decorate, so the control is hidden rather
    /// than disabled.
    /// </summary>
    public static bool AreDecorationsVisible(CaptureMode mode) =>
        mode is CaptureMode.ActiveWindow or CaptureMode.SelectedWindow;

    /// <summary>
    /// Whether the window-shadow option is visible for the given Capture Mode.
    /// Tracks <see cref="AreDecorationsVisible"/>: window shadow is only meaningful
    /// when window decorations are also visible (spec #30).
    /// </summary>
    public static bool IsShadowVisible(CaptureMode mode) => AreDecorationsVisible(mode);

    // ── Effective composition (committed defaults + session overrides) ──

    /// <summary>
    /// Resolves the effective CaptureOptions for the given <paramref name="mode"/>:
    /// the committed defaults overlaid with any per-mode overrides for fields that
    /// are visible for this mode. Fields that are not visible (e.g. decorations on
    /// a FullDesktop Capture) always reflect the committed default — overrides
    /// cannot leak into modes where the field does not apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is reconciled through <see cref="CaptureOptions.Normalized"/> so
    /// the decoration/shadow dependency holds even when overrides and committed
    /// defaults combine in a way that would otherwise produce an inconsistent
    /// combination.
    /// </para>
    /// <para>
    /// Non-visible fields are NOT forced to false here. Visibility is a UI concern
    /// (the Target Selection controls that do not apply to the active Capture Mode
    /// are hidden rather than disabled — spec #24); the native Capture engine
    /// receives the full flag set and consumes only the flags relevant to the
    /// Capture operation ("passed through as explicit flags on relevant Capture
    /// operations" — spec #24). Canonicalizing non-visible fields here would
    /// conflate the UI-visibility concern with the data-contract concern.
    /// </para>
    /// </remarks>
    public CaptureOptions EffectiveFor(CaptureMode mode)
    {
        var committed = _committed.EffectiveCaptureOptions;
        var (pointer, decorations, shadow) = GetOrCreate(mode);

        bool effectivePointer =
            IsPointerVisible(mode) && pointer.HasValue
                ? pointer.Value
                : committed.IncludePointer;

        bool effectiveDecorations =
            AreDecorationsVisible(mode) && decorations.HasValue
                ? decorations.Value
                : committed.IncludeDecorations;

        bool effectiveShadow =
            IsShadowVisible(mode) && shadow.HasValue
                ? shadow.Value
                : committed.IncludeShadow;

        return new CaptureOptions(effectivePointer, effectiveDecorations, effectiveShadow)
            .Normalized();
    }

    // ── Per-mode override edits (spec #35 AC #2) ────────────────────────

    /// <summary>
    /// Overrides the mouse-pointer default for the given Capture Mode for the
    /// remainder of the application session. Subsequent Captures in this mode use
    /// the overridden value. Never writes back to committed Configuration.
    /// </summary>
    public void OverrideIncludePointer(CaptureMode mode, bool value)
    {
        GetOrCreate(mode).Pointer = value;
    }

    /// <summary>
    /// Overrides the decorations default for the given Capture Mode. Reconciles
    /// shadow immediately through the decoration/shadow dependency: if decorations
    /// are turned off, any existing shadow override is replaced with false (spec
    /// #30). The reconciliation is symmetric — the on→off transition remembers
    /// whether the effective shadow value was on, and turning decorations back
    /// on restores the remembered value, so a decorations round-trip never
    /// silently discards the session's shadow choice. Never writes back to
    /// committed Configuration.
    /// </summary>
    public void OverrideIncludeDecorations(CaptureMode mode, bool value)
    {
        var set = GetOrCreate(mode);
        bool decorationsCurrentlyEffective =
            set.Decorations.HasValue
                ? set.Decorations.Value
                : _committed.EffectiveCaptureOptions.IncludeDecorations;
        if (!value)
        {
            // Remember the effective shadow-on state only at the on→off
            // transition, so a repeated decorations-off call cannot wipe the
            // memory an earlier transition recorded.
            if (decorationsCurrentlyEffective)
            {
                set.RememberedShadow = EffectiveShadowFor(mode, set);
            }
            // Mirror CaptureOptions.Normalized: turning decorations off forces shadow off.
            set.Shadow = false;
        }
        else if (set.RememberedShadow)
        {
            set.Shadow = true;
            set.RememberedShadow = false;
        }
        set.Decorations = value;
    }

    /// <summary>
    /// The effective shadow value for <paramref name="mode"/> under the override
    /// set <paramref name="set"/> (override if set, else the committed default),
    /// reconciled through the decoration/shadow dependency.
    /// </summary>
    private bool EffectiveShadowFor(CaptureMode mode, OverrideSet set)
    {
        bool effectiveDecorations =
            set.Decorations.HasValue
                ? set.Decorations.Value
                : _committed.EffectiveCaptureOptions.IncludeDecorations;

        bool effectiveShadow =
            IsShadowVisible(mode) && set.Shadow.HasValue
                ? set.Shadow.Value
                : _committed.EffectiveCaptureOptions.IncludeShadow;

        return effectiveDecorations && effectiveShadow;
    }

    /// <summary>
    /// Overrides the shadow default for the given Capture Mode. Silently refused
    /// when decorations are currently off in the override namespace — shadow is
    /// only meaningful when decorations are included (spec #30). Never writes
    /// back to committed Configuration.
    /// </summary>
    public void OverrideIncludeShadow(CaptureMode mode, bool value)
    {
        var set = GetOrCreate(mode);
        // Reconcile against the override namespace first: if decorations have been
        // overridden to false, shadow cannot be turned on. If decorations are not
        // overridden, fall back to the committed default for the dependency check.
        bool decorationsEffective =
            set.Decorations.HasValue
                ? set.Decorations.Value
                : _committed.EffectiveCaptureOptions.IncludeDecorations;

        if (!decorationsEffective && value)
        {
            // Refuse: decorations are off, so shadow cannot be turned on. Stays off.
            return;
        }
        // An explicit shadow override supersedes any remembered round-trip value.
        set.RememberedShadow = false;
        set.Shadow = value;
    }

    // ── Override visibility into the namespace (for UI display) ─────────

    /// <summary>
    /// Whether the mouse-pointer option for the given Capture Mode is currently
    /// overridden by the session (true) or carrying the committed default (false).
    /// Used by Target Selection to indicate a per-session value is in effect.
    /// </summary>
    public bool IsPointerOverridden(CaptureMode mode) =>
        _overrides.TryGetValue(mode, out var set) && set.Pointer.HasValue;

    /// <summary>
    /// Whether the decorations option for the given Capture Mode is currently
    /// overridden by the session (true) or carrying the committed default (false).
    /// </summary>
    public bool IsDecorationsOverridden(CaptureMode mode) =>
        _overrides.TryGetValue(mode, out var set) && set.Decorations.HasValue;

    /// <summary>
    /// Whether the shadow option for the given Capture Mode is currently
    /// overridden by the session (true) or carrying the committed default (false).
    /// </summary>
    public bool IsShadowOverridden(CaptureMode mode) =>
        _overrides.TryGetValue(mode, out var set) && set.Shadow.HasValue;

    // ── AC #3: cleared on application exit ──────────────────────────────

    /// <summary>
    /// Removes every override across every Capture Mode. The entry point the
    /// workflow calls when the application session is ending (or whenever a
    /// session-scoped reset is required). After ClearAll, every mode's effective
    /// options equal the committed defaults again. Never writes back to committed
    /// Configuration.
    /// </summary>
    public void ClearAll() => _overrides.Clear();

    // ── Per-mode override set ───────────────────────────────────────────

    private OverrideSet GetOrCreate(CaptureMode mode)
    {
        if (!_overrides.TryGetValue(mode, out var set))
        {
            set = new OverrideSet();
            _overrides[mode] = set;
        }
        return set;
    }

    /// <summary>
    /// Per-mode override namespace: each field is either null (use the committed
    /// default) or non-null (the value the user pinned for the session). Three
    /// independent fields rather than one nullable CaptureOptions so per-field
    /// override status is observable. <see cref="RememberedShadow"/> carries the
    /// shadow-on memory across a decorations-off span so the decoration/shadow
    /// reconciliation can be symmetric.
    /// </summary>
    private sealed class OverrideSet
    {
        public bool? Pointer;
        public bool? Decorations;
        public bool? Shadow;
        public bool RememberedShadow;

        public void Deconstruct(out bool? pointer, out bool? decorations, out bool? shadow)
        {
            pointer = Pointer;
            decorations = Decorations;
            shadow = Shadow;
        }
    }
}
