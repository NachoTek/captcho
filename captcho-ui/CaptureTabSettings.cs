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
    /// impossible combination, even before Apply. Turning decorations back on does
    /// not restore the old shadow value — the user must explicitly opt back in.
    /// Never persists or mutates the original persisted settings.
    /// </summary>
    public void EditIncludeDecorations(bool value)
    {
        var candidate = Working.CaptureOptions with { IncludeDecorations = value };
        Working.CaptureOptions = candidate.Normalized();
    }

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
        Working.CaptureOptions = Working.CaptureOptions with { IncludeShadow = value };
    }

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
        Working.CaptureOptions != Baseline.CaptureOptions;

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
    }
}
