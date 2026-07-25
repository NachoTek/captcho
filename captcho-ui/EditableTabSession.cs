// EditableTabSession.cs — Internal seam shared by SettingsSession and each editable tab.
//
// Holds the snapshot/apply/cancel/reset mechanics that every editable tab needs and
// that were previously duplicated field-for-field across GeneralTabSettings and
// GlobalHotkeyTabSettings: independent Working/Baseline snapshots of the persisted
// AppSettings, a Commit that advances the baseline after a successful persist, a Cancel
// that reverts the working snapshot to the baseline, and a Reset that restores the tab's
// slice of defaults through the ApplyDefaults template method. It also owns the
// slice-validation plumbing (SliceIssues: seed a defaults probe, WriteInto it, run
// AppSettings.Validate) — identical for every tab, so written once here. The validation
// *rules* themselves stay in AppSettings.Validate (the single source of truth); each
// concrete tab declares only its own slice (WriteInto, IsValid, IsDirty, FirstError,
// ApplyDefaults) and reads SliceIssues for IsValid/FirstError/inline errors. Issue #13
// routed the rules through AppSettings.Validate so the composed Apply/OK gate and each
// tab's inline errors can never disagree.
//
// Internal: this is the private seam SettingsSession depends on (it holds an
// EditableTabSession[] and iterates it for Apply/OK/Cancel/Reset). Issue #11 reviewed
// whether to promote it to a public port and decided against it: with only two
// collaborators and no external caller, a public tab-port would be a one-adapter
// (hypothetical) seam. The duplication the base removes is real; the public surface is
// not — so the contract stays internal, and there is no separate IEditableSettingsTab
// (one abstraction, not two). Revisit only if a real third editable tab appears.

using System;
using System.Collections.Generic;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Shared snapshot/apply/cancel/reset mechanics for an editable settings tab. Construction
/// snapshots <paramref name="persisted"/> into independent working and baseline copies so
/// the active and persisted settings are never mutated by opening or editing.
/// <see cref="Commit"/> and <see cref="Cancel"/> are identical for every tab and live here,
/// as does <see cref="SliceIssues"/> (the slice-validation plumbing that delegates to
/// <see cref="AppSettings.Validate"/>). <see cref="WriteInto"/>, dirty tracking, and
/// <see cref="ApplyDefaults"/> are tab-specific and left abstract; <see cref="IsValid"/> and
/// <see cref="FirstError"/> are tab-specific reads over <see cref="SliceIssues"/>. This is
/// the contract <see cref="SettingsSession"/> holds a collection of; concrete tabs extend it.
/// </summary>
internal abstract class EditableTabSession
{
    /// <summary>The tab's editable working snapshot. Never aliases the persisted source.</summary>
    protected AppSettings Working { get; private set; }

    /// <summary>The last applied baseline. <see cref="Cancel"/> reverts Working here;
    /// <see cref="Commit"/> advances this to Working.</summary>
    protected AppSettings Baseline { get; private set; }

    /// <summary>
    /// Snapshots <paramref name="persisted"/> into independent working and baseline copies.
    /// The <paramref name="persisted"/> object is never mutated by this instance.
    /// </summary>
    protected EditableTabSession(AppSettings persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        // Normalized() yields independent deep copies carrying effective defaults, so the
        // active and persisted settings are never mutated by opening or editing.
        Working = persisted.Normalized();
        Baseline = persisted.Normalized();
    }

    /// <summary>
    /// Advances the baseline to the current working state. Called by the session only
    /// after a successful persist, so a subsequent Cancel no longer reverts the committed
    /// change. Identical for every tab.
    /// </summary>
    public void Commit() => Baseline = Working.Normalized();

    /// <summary>
    /// Reverts the working snapshot to the baseline. Never persists. Identical for every tab.
    /// </summary>
    public void Cancel() => Working = Baseline.Normalized();

    /// <summary>
    /// Restores this tab's slice of defaults in the working snapshot by delegating to
    /// <see cref="ApplyDefaults"/>. Defaults come from <see cref="AppSettings.WithDefaults"/>
    /// (the single source every tab reads from); the baseline is left untouched so a later
    /// Cancel still reverts the reset. Never persists.
    /// </summary>
    public void Reset() => ApplyDefaults(Working);

    /// <summary>
    /// Restores this tab's own slice of defaults into <paramref name="working"/>. Each
    /// concrete tab reads its slice from <see cref="AppSettings.WithDefaults"/> so every
    /// tab shares one default source and touches only the fields it owns.
    /// </summary>
    protected abstract void ApplyDefaults(AppSettings working);

    /// <summary>True when every working field on this tab passes its validation rules.</summary>
    public abstract bool IsValid { get; }

    /// <summary>
    /// First validation error on this tab, or null when valid. Used by the session to build
    /// an inline status message when an Apply/OK is attempted while invalid.
    /// </summary>
    public abstract string? FirstError { get; }

    /// <summary>True when any working field differs from this tab's last applied baseline.</summary>
    public abstract bool IsDirty { get; }

    /// <summary>
    /// Writes this tab's working slice into <paramref name="target"/>. Leaves other tabs'
    /// slices untouched so the session can merge every tab and persist once.
    /// </summary>
    public abstract void WriteInto(AppSettings target);

    /// <summary>
    /// The issues <see cref="AppSettings.Validate"/> reports for this tab's working slice —
    /// the single source of truth for persisted-setting validity. Writes the working slice
    /// into a defaults-populated probe so other tabs' fields cannot contribute issues, then
    /// returns every issue Validate reports for it. Because the probe is seeded with
    /// defaults, the returned issues belong only to the fields this tab owns; concrete tabs
    /// read them for <see cref="IsValid"/>, <see cref="FirstError"/>, and inline per-field
    /// errors, and the composed session gates Apply/OK on the AND of every tab's IsValid.
    /// </summary>
    protected IReadOnlyList<SettingsIssue> SliceIssues()
    {
        var probe = AppSettings.WithDefaults();
        WriteInto(probe);
        return probe.Validate();
    }
}
