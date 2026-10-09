// BehaviorTabSettings.cs — Pure C# editing seam for the Behavior settings tab.
//
// Owns the editable automatic delivery defaults (issue #47) — automatic save,
// Copy Frame, and Copy Path, each an independent toggle — and the configured
// launch behavior (issue #53): Do nothing, Last Capture Mode, or a configured
// Capture Mode. The shared snapshot/apply/cancel/reset plumbing lives in
// EditableTabSession (written once); this tab declares only its own Behavior
// slice — what to edit, merge via WriteInto, and restore via ApplyDefaults.
// Never persists — it writes its working slice into a merged AppSettings via
// WriteInto and advances its baseline via Commit at the SettingsSession's
// direction, so the session can persist all tabs atomically.
//
// The automatic delivery toggles have no persisted validation rule (three
// booleans have no invalid value); the launch behavior does (a configured
// launch must select a Capture Mode), so this tab contributes to the composed
// Apply/OK gate through AppSettings.Validate.

using System;
using System.Linq;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Pure C# editing seam for the Behavior settings tab. Holds a working
/// snapshot of the persisted automatic Export defaults and launch behavior
/// and exposes them for editing. Never persists or mutates the
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

    // ── Working editable state: launch behavior ────────────────────────

    /// <summary>The working launch action.</summary>
    public LaunchAction LaunchAction => Working.EffectiveLaunchBehavior.Action;

    /// <summary>
    /// The working configured Capture Mode. Null unless the working launch
    /// action is <see cref="captcho.Capture.LaunchAction.ConfiguredCaptureMode"/>.
    /// </summary>
    public CaptureMode? LaunchConfiguredMode =>
        Working.EffectiveLaunchBehavior.Action == captcho.Capture.LaunchAction.ConfiguredCaptureMode
            ? Working.EffectiveLaunchBehavior.EffectiveConfiguredMode
            : null;

    /// <summary>
    /// Sets the working launch action. Switching away from
    /// <see cref="captcho.Capture.LaunchAction.ConfiguredCaptureMode"/> clears
    /// the configured Capture Mode — it is meaningful only for that action.
    /// </summary>
    public void EditLaunchAction(LaunchAction value)
    {
        var current = Working.EffectiveLaunchBehavior;
        var configured = value == captcho.Capture.LaunchAction.ConfiguredCaptureMode
            ? current.EffectiveConfiguredMode
            : null;
        Working.LaunchBehavior = new LaunchBehaviorSettings(value, configured);
    }

    /// <summary>
    /// Sets the working configured Capture Mode for the
    /// <see cref="captcho.Capture.LaunchAction.ConfiguredCaptureMode"/> launch
    /// action. The selection is retained in the working snapshot even while
    /// another action is chosen, so switching back and forth does not lose it.
    /// </summary>
    public void EditLaunchBehavior(CaptureMode mode)
    {
        if (!Enum.IsDefined(mode))
            return;
        var current = Working.EffectiveLaunchBehavior;
        Working.LaunchBehavior = new LaunchBehaviorSettings(current.Action, mode);
    }

    // ── Validation ──────────────────────────────────────────────────────

    /// <summary>
    /// True when the working Behavior slice is valid. The automatic delivery
    /// toggles have no invalid value; the launch behavior contributes through
    /// <see cref="AppSettings.Validate"/> (a configured launch must select a
    /// Capture Mode).
    /// </summary>
    public override bool IsValid =>
        !SliceIssues().Any(i => i.Field == SettingsField.LaunchBehavior);

    /// <summary>First launch behavior validation error, or null when valid.</summary>
    public override string? FirstError =>
        SliceIssues().FirstOrDefault(i => i.Field == SettingsField.LaunchBehavior)?.Message;

    /// <summary>
    /// True when any working Behavior value — an automatic delivery toggle or
    /// the launch behavior — differs from its baseline.
    /// </summary>
    public override bool IsDirty =>
        Working.EffectiveAutomaticExport != Baseline.EffectiveAutomaticExport
        || Working.EffectiveLaunchBehavior != Baseline.EffectiveLaunchBehavior;

    // ── Behavior slice: merge, defaults ─────────────────────────────────

    /// <summary>
    /// Writes this tab's working Behavior slice into <paramref name="target"/>.
    /// Other tabs' slices — including the recorded last Capture Mode, which is
    /// runtime-recorded state no tab owns — are left untouched so the session
    /// can merge every tab into one AppSettings and persist it once.
    /// </summary>
    public override void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.AutomaticExport = Working.EffectiveAutomaticExport;
        target.LaunchBehavior = Working.EffectiveLaunchBehavior;
    }

    /// <summary>
    /// Restores this tab's slice to the defaults — disabled automatic Export
    /// and the Do-nothing launch behavior — read from each model's
    /// WithDefaults (the single default source shared by every collaborator).
    /// Only this tab's slice is touched; the baseline is left untouched by
    /// <see cref="EditableTabSession.Reset"/>, so Reset alone never persists
    /// and a later Cancel still reverts to the settings that existed before
    /// Reset.
    /// </summary>
    protected override void ApplyDefaults(AppSettings working)
    {
        working.AutomaticExport = AutomaticExportSettings.WithDefaults();
        working.LaunchBehavior = LaunchBehaviorSettings.WithDefaults();
    }
}
