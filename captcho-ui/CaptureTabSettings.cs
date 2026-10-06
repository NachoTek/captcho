// CaptureTabSettings.cs — Pure C# editing seam for the Capture settings tab.
//
// Owns the editable mouse-pointer, window-decoration, and window-shadow defaults
// (spec user stories 27–30). The decoration/shadow dependency — window shadow is
// available only when window decorations are included (spec #30) — is enforced
// at edit time so the UI can never offer an impossible combination, even before
// Apply. The shared snapshot/apply/cancel/reset plumbing lives in
// EditableTabSession (written once); this tab declares only its own Capture
// slice — what to edit, merge via WriteInto, and restore via ApplyDefaults.
// Never persists — it writes its working slice into a merged AppSettings via
// WriteInto and advances its baseline via Commit at the SettingsSession's
// direction, so the session can persist all tabs atomically.
//
// CaptureOptions has no persisted validation rule. The decoration/shadow rule is
// enforced on read (CaptureOptions.Normalized) and on edit (EditIncludeDecorations
// reconciles shadow immediately), so this tab is always valid and never
// contributes to the composed Apply/OK gate.

using System;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Pure C# editing seam for the Capture settings tab. Holds a working snapshot
/// of the persisted CaptureOptions defaults, exposes the three flags for
/// editing, and enforces the decoration/shadow dependency at edit time.
/// Never persists or mutates the active/persisted Settings on open or edit; the
/// <see cref="SettingsSession"/> orchestrates persistence across all tabs.
/// </summary>
internal sealed class CaptureTabSettings : EditableTabSession
{
    /// <summary>
    /// Snapshots the persisted settings into independent working and baseline copies via
    /// <see cref="EditableTabSession"/>'s constructor. The <paramref name="persisted"/>
    /// object is never mutated by this instance.
    /// </summary>
    public CaptureTabSettings(AppSettings persisted)
        : base(persisted)
    {
    }

    // ── Working editable state ──────────────────────────────────────────

    /// <summary>
    /// Whether the mouse pointer is captured by default. Editing updates the working
    /// snapshot without touching the source.
    /// </summary>
    public bool IncludePointer => Working.CaptureOptions.IncludePointer;

    /// <summary>
    /// Whether window Captures include decorations (title bar and frame) by default.
    /// Editing updates the working snapshot without touching the source.
    /// </summary>
    public bool IncludeDecorations => Working.CaptureOptions.IncludeDecorations;

    /// <summary>
    /// Whether window Captures include the window shadow by default. Dependent on
    /// <see cref="IncludeDecorations"/>: shadow is only meaningful when decorations
    /// are also included (spec #30). The dependency is enforced on edit through
    /// <see cref="EditIncludeDecorations"/>.
    /// </summary>
    public bool IncludeShadow => Working.CaptureOptions.IncludeShadow;

    /// <summary>How long confirmed Selection geometry is retained.</summary>
    public RememberSelectionLifetime RememberSelection => Working.EffectiveRememberSelection;

    /// <summary>Whether captured Frames open in Annotation before Export.</summary>
    public bool AnnotationEnabled => Working.AnnotationEnabled;

    /// <summary>
    /// Sets the working mouse-pointer default from user input. Never persists or
    /// mutates the original persisted settings.
    /// </summary>
    public void EditIncludePointer(bool value)
    {
        Working.CaptureOptions = Working.CaptureOptions with { IncludePointer = value };
    }

    /// <summary>
    /// Sets the working decorations default from user input. Reconciles shadow
    /// immediately through <see cref="CaptureOptions.Normalized"/>: turning
    /// decorations off forces shadow off so the working snapshot never carries an
    /// impossible combination, even before Apply. The reconciliation is symmetric —
    /// turning decorations off remembers whether shadow was on, and turning them
    /// back on restores the remembered value, so a decorations round-trip never
    /// silently discards the user's shadow choice. Never persists or mutates the
    /// original persisted settings.
    /// </summary>
    public void EditIncludeDecorations(bool value)
    {
        var current = Working.CaptureOptions;
        var candidate = current with { IncludeDecorations = value };
        if (!value && current.IncludeShadow)
        {
            // Remember the shadow-on choice across the decorations-off span so
            // re-enabling decorations can restore it symmetrically.
            _rememberedShadow = true;
        }
        Working.CaptureOptions = candidate.Normalized();
        if (value && _rememberedShadow)
        {
            Working.CaptureOptions = Working.CaptureOptions with { IncludeShadow = true };
            _rememberedShadow = false;
        }
    }

    // Whether shadow was on at the moment decorations were turned off — restored
    // when decorations are turned back on, then cleared. Not a persisted field:
    // it exists only to make the edit-time reconciliation symmetric within one
    // editing session.
    private bool _rememberedShadow;

    /// <summary>
    /// Sets the working shadow default from user input. Silently refused when
    /// decorations are off — shadow is only meaningful when decorations are also
    /// included (spec #30). Never persists or mutates the original persisted
    /// settings.
    /// </summary>
    public void EditIncludeShadow(bool value)
    {
        if (!Working.CaptureOptions.IncludeDecorations && value)
        {
            // Refuse: decorations are off, so shadow cannot be turned on. Stays off
            // so the working snapshot never carries an inconsistent combination.
            return;
        }
        // An explicit shadow edit supersedes any remembered round-trip value.
        _rememberedShadow = false;
        Working.CaptureOptions = Working.CaptureOptions with { IncludeShadow = value };
    }

    /// <summary>Sets the working remembered-Selection lifetime.</summary>
    public void EditRememberSelection(RememberSelectionLifetime value)
    {
        Working.RememberSelection = Enum.IsDefined(value)
            ? value
            : RememberSelectionLifetime.Never;
    }

    /// <summary>Sets whether the post-capture Annotation gate is enabled.</summary>
    public void EditAnnotationEnabled(bool value) => Working.AnnotationEnabled = value;

    // ── Validation ──────────────────────────────────────────────────────

    /// <summary>
    /// Always true. CaptureOptions has no persisted validation rule — the
    /// decoration/shadow dependency is enforced at edit time and reconciled on
    /// read — so this tab never contributes to the composed Apply/OK gate.
    /// </summary>
    public override bool IsValid => true;

    /// <summary>
    /// Always null. See <see cref="IsValid"/>: this tab never surfaces a validation
    /// error.
    /// </summary>
    public override string? FirstError => null;

    /// <summary>
    /// True when any working Capture-options flag differs from its baseline.
    /// </summary>
    public override bool IsDirty =>
        Working.CaptureOptions != Baseline.CaptureOptions
        || Working.EffectiveRememberSelection != Baseline.EffectiveRememberSelection
        || Working.AnnotationEnabled != Baseline.AnnotationEnabled;

    // ── Capture slice: merge, defaults ──────────────────────────────────

    /// <summary>
    /// Writes this tab's working CaptureOptions slice into <paramref name="target"/>.
    /// Other tabs' slices are left untouched so the session can merge every tab into
    /// one AppSettings and persist it once.
    /// </summary>
    public override void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.CaptureOptions = Working.CaptureOptions;
        target.RememberSelection = Working.EffectiveRememberSelection;
        target.AnnotationEnabled = Working.AnnotationEnabled;
        if (Working.EffectiveRememberSelection != RememberSelectionLifetime.Always)
            target.RememberedSelection = null;
    }

    /// <summary>
    /// Restores this tab's CaptureOptions to its defaults, read from
    /// <see cref="CaptureOptions.WithDefaults"/> (the single default source shared by
    /// every collaborator). Only this tab's slice is touched; the baseline is left
    /// untouched by <see cref="EditableTabSession.Reset"/>, so Reset alone never
    /// persists and a later Cancel still reverts to the settings that existed before
    /// Reset.
    /// </summary>
    protected override void ApplyDefaults(AppSettings working)
    {
        working.CaptureOptions = CaptureOptions.WithDefaults();
        working.RememberSelection = RememberSelectionLifetime.Never;
        working.RememberedSelection = null;
        working.AnnotationEnabled = true;
    }
}
