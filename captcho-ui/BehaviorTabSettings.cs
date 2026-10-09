// BehaviorTabSettings.cs — Pure C# editing seam for the Behavior settings tab.
//
// Owns the editable automatic delivery defaults (issue #47): automatic save,
// Copy Frame, and Copy Path, each an independent toggle configured on the
// Behavior tab. The shared snapshot/apply/cancel/reset plumbing lives in
// EditableTabSession (written once); this tab declares only its own Behavior
// slice — what to edit, merge via WriteInto, and restore via ApplyDefaults.
// Never persists — it writes its working slice into a merged AppSettings via
// WriteInto and advances its baseline via Commit at the SettingsSession's
// direction, so the session can persist all tabs atomically.
//
// AutomaticExportSettings has no persisted validation rule (three booleans
// have no invalid value), so this tab is always valid and never contributes
// to the composed Apply/OK gate.

using System;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Pure C# editing seam for the Behavior settings tab. Holds a working
/// snapshot of the persisted automatic Export defaults and exposes the three
/// independent toggles for editing. Never persists or mutates the
/// active/persisted Settings on open or edit; the <see cref="SettingsSession"/>
/// orchestrates persistence across all tabs.
/// </summary>
internal sealed class BehaviorTabSettings : EditableTabSession
{
    /// <summary>
    /// Snapshots the persisted settings into independent working and baseline
    /// copies via <see cref="EditableTabSession"/>'s constructor. The
    /// <paramref name="persisted"/> object is never mutated by this instance.
    /// </summary>
    public BehaviorTabSettings(AppSettings persisted)
        : base(persisted)
    {
    }

    // ── Working editable state ──────────────────────────────────────────

    /// <summary>Whether the composed Frame is saved automatically after Annotation confirmation.</summary>
    public bool AutoSave => Working.EffectiveAutomaticExport.AutoSave;

    /// <summary>Whether Copy Frame runs automatically after Annotation confirmation.</summary>
    public bool AutoCopyFrame => Working.EffectiveAutomaticExport.AutoCopyFrame;

    /// <summary>Whether Copy Path runs automatically after Annotation confirmation.</summary>
    public bool AutoCopyPath => Working.EffectiveAutomaticExport.AutoCopyPath;

    /// <summary>Sets the working automatic save toggle from user input.</summary>
    public void EditAutoSave(bool value) =>
        Working.AutomaticExport = Working.EffectiveAutomaticExport with { AutoSave = value };

    /// <summary>Sets the working automatic Copy Frame toggle from user input.</summary>
    public void EditAutoCopyFrame(bool value) =>
        Working.AutomaticExport = Working.EffectiveAutomaticExport with { AutoCopyFrame = value };

    /// <summary>Sets the working automatic Copy Path toggle from user input.</summary>
    public void EditAutoCopyPath(bool value) =>
        Working.AutomaticExport = Working.EffectiveAutomaticExport with { AutoCopyPath = value };

    // ── Validation ──────────────────────────────────────────────────────

    /// <summary>
    /// Always true. Three booleans have no invalid value, so this tab never
    /// contributes to the composed Apply/OK gate.
    /// </summary>
    public override bool IsValid => true;

    /// <summary>Always null. See <see cref="IsValid"/>.</summary>
    public override string? FirstError => null;

    /// <summary>
    /// True when any working Behavior toggle differs from its baseline.
    /// </summary>
    public override bool IsDirty =>
        Working.EffectiveAutomaticExport != Baseline.EffectiveAutomaticExport;

    // ── Behavior slice: merge, defaults ─────────────────────────────────

    /// <summary>
    /// Writes this tab's working Behavior slice into <paramref name="target"/>.
    /// Other tabs' slices are left untouched so the session can merge every
    /// tab into one AppSettings and persist it once.
    /// </summary>
    public override void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.AutomaticExport = Working.EffectiveAutomaticExport;
    }

    /// <summary>
    /// Restores this tab's automatic Export settings to the disabled defaults,
    /// read from <see cref="AutomaticExportSettings.WithDefaults"/> (the single
    /// default source shared by every collaborator). Only this tab's slice is
    /// touched; the baseline is left untouched by
    /// <see cref="EditableTabSession.Reset"/>, so Reset alone never persists
    /// and a later Cancel still reverts to the settings that existed before
    /// Reset.
    /// </summary>
    protected override void ApplyDefaults(AppSettings working) =>
        working.AutomaticExport = AutomaticExportSettings.WithDefaults();
}
