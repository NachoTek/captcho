// GeneralTabSettings.cs — Pure C# editing seam for the General settings tab.
//
// Owns the editable Save Location and Filename Template: a working snapshot of the
// persisted settings, a live filename preview, and inline validation. The shared
// snapshot/apply/cancel/reset plumbing lives in EditableTabSession (written once); this
// tab declares only its own General slice — what to edit, validate, merge via WriteInto,
// and restore via ApplyDefaults. Never persists — it writes its working slice into a
// merged AppSettings via WriteInto and advances its baseline via Commit at the
// SettingsSession's direction, so the session can persist all tabs atomically.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// A supported Filename Template placeholder with a human description and example.
/// </summary>
public sealed record FilenameTemplatePlaceholder(string Token, string Description, string Example);

/// <summary>
/// Pure C# editing seam for the General settings tab. Holds a working snapshot of
/// the persisted Save Location and Filename Template, computes a representative
/// filename preview, lists the supported placeholders, and validates edits inline.
/// Never persists or mutates the active/persisted Settings on open or edit; the
/// <see cref="SettingsSession"/> orchestrates persistence across all tabs.
/// </summary>
internal sealed class GeneralTabSettings : EditableTabSession
{
    /// <summary>
    /// Snapshots the persisted settings into independent working and baseline copies via
    /// <see cref="EditableTabSession"/>'s constructor. The <paramref name="persisted"/>
    /// object is never mutated by this instance.
    /// </summary>
    public GeneralTabSettings(AppSettings persisted)
        : base(persisted)
    {
    }

    // ── Working editable state ──────────────────────────────────────────

    /// <summary>
    /// The current working Filename Template value (effective: the default when the
    /// persisted value was null/empty). Editing updates this without touching the source.
    /// </summary>
    public string FilenameTemplate => Working.FilenameTemplate!;

    /// <summary>
    /// Sets the working Filename Template from user input and recomputes the preview.
    /// Never persists or mutates the original persisted settings.
    /// </summary>
    public void EditFilenameTemplate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Working.FilenameTemplate = value;
    }

    /// <summary>
    /// A representative expanded filename for the current working template, using a
    /// fixed sample timestamp and title so the preview is deterministic. Empty when the
    /// template is blank (no representative filename can be produced).
    /// </summary>
    public string FilenameTemplatePreview =>
        string.IsNullOrWhiteSpace(Working.FilenameTemplate)
            ? string.Empty
            : ExportFilenameTemplate.Expand(Working.FilenameTemplate, SampleTimestamp, SampleTitle)
                + "." + ExportDefaults.DefaultExtension;

    // Fixed sample inputs so the preview is a stable, representative filename.
    private static readonly DateTime SampleTimestamp = new(2024, 1, 15, 14, 30, 45);
    private const string SampleTitle = "Screenshot";

    // ── Save Location working state ─────────────────────────────────────

    /// <summary>
    /// The current working Save Location value (effective: the default when the
    /// persisted value was null/empty). Editing updates this without touching the source.
    /// </summary>
    public string SaveLocation => Working.SaveLocation!;

    /// <summary>
    /// Sets the working Save Location from user input (a typed path or a folder-picker
    /// selection). Never persists or mutates the original persisted settings.
    /// </summary>
    public void EditSaveLocation(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Working.SaveLocation = value;
    }

    /// <summary>
    /// Applies a folder-picker outcome to the working Save Location. A null/empty path
    /// (picker cancelled) leaves the current edit unchanged; a real selection replaces
    /// the working value. Never persists. Lets picker outcomes be covered headlessly
    /// without invoking the WinUI picker.
    /// </summary>
    public void ApplyFolderPickerResult(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return;
        EditSaveLocation(folderPath);
    }

    // ── Validation ──────────────────────────────────────────────────────

    /// <summary>
    /// True when every working field passes its validation rule. Derived from
    /// <see cref="AppSettings.Validate"/> (the source of truth) so this tab and the
    /// composed session gate never disagree about what counts as a valid Save Location or
    /// Filename Template.
    /// </summary>
    public override bool IsValid => SliceIssues().Count == 0;

    /// <summary>
    /// Inline validation message for the Save Location, sourced from
    /// <see cref="AppSettings.Validate"/>, or null when valid.
    /// </summary>
    public string? SaveLocationError =>
        SliceIssues().FirstOrDefault(i => i.Field == SettingsField.SaveLocation)?.Message;

    /// <summary>
    /// Inline validation message for the Filename Template, sourced from
    /// <see cref="AppSettings.Validate"/>, or null when valid.
    /// </summary>
    public string? FilenameTemplateError =>
        SliceIssues().FirstOrDefault(i => i.Field == SettingsField.FilenameTemplate)?.Message;

    /// <summary>
    /// First validation error across this tab's fields, or null when the tab is valid.
    /// Sourced from <see cref="AppSettings.Validate"/> and used by the session to build a
    /// status message when an invalid Apply/OK is attempted.
    /// </summary>
    public override string? FirstError => SliceIssues().FirstOrDefault()?.Message;

    /// <summary>
    /// True when any working field differs from its last applied baseline.
    /// </summary>
    public override bool IsDirty =>
        !string.Equals(Working.FilenameTemplate, Baseline.FilenameTemplate, StringComparison.Ordinal)
        || !string.Equals(Working.SaveLocation, Baseline.SaveLocation, StringComparison.Ordinal);

    // ── General slice: merge, defaults ──────────────────────────────────

    /// <summary>
    /// Writes this tab's working Save Location and Filename Template slice into
    /// <paramref name="target"/>. Other tabs' slices are left untouched so the session
    /// can merge every tab into one AppSettings and persist it once.
    /// </summary>
    public override void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.SaveLocation = Working.SaveLocation;
        target.FilenameTemplate = Working.FilenameTemplate;
    }

    /// <summary>
    /// Restores this tab's Save Location and Filename Template to their defaults, read
    /// from <see cref="AppSettings.WithDefaults"/> (the single default source shared by
    /// every tab). Only this tab's slice is touched; the baseline is left untouched by
    /// <see cref="EditableTabSession.Reset"/>, so Reset alone never persists and a later
    /// Cancel still reverts to the settings that existed before Reset.
    /// </summary>
    protected override void ApplyDefaults(AppSettings working)
    {
        var defaults = AppSettings.WithDefaults();
        working.SaveLocation = defaults.SaveLocation;
        working.FilenameTemplate = defaults.FilenameTemplate;
    }

    // ── Placeholder reference ───────────────────────────────────────────

    /// <summary>
    /// The Filename Template placeholders a user can use, with descriptions and examples.
    /// Sourced from <see cref="ExportFilenameTemplate"/>'s documented placeholders.
    /// </summary>
    public static IReadOnlyList<FilenameTemplatePlaceholder> SupportedPlaceholders { get; } = new[]
    {
        new FilenameTemplatePlaceholder("<yyyy>", "4-digit year", "2024"),
        new FilenameTemplatePlaceholder("<yy>", "2-digit year", "24"),
        new FilenameTemplatePlaceholder("<MM>", "2-digit month", "01"),
        new FilenameTemplatePlaceholder("<dd>", "2-digit day", "15"),
        new FilenameTemplatePlaceholder("<hh>", "hour, 24-hour clock", "14"),
        new FilenameTemplatePlaceholder("<mm>", "minute", "30"),
        new FilenameTemplatePlaceholder("<ss>", "second", "45"),
        new FilenameTemplatePlaceholder("<title>", "sanitized window/capture title", "Screenshot"),
        new FilenameTemplatePlaceholder("<#>", "sequence number, zero-padded", "0001"),
    };
}
