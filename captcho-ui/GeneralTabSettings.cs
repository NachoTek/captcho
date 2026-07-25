// GeneralTabSettings.cs — Pure C# editing seam for the General settings tab.
//
// Owns the editable Save Location and Filename Template: a working snapshot of the
// persisted settings, a live filename preview, and inline validation. Construction
// snapshots the persisted settings so the active and persisted Settings are never
// mutated by opening or editing. This tab no longer persists — it writes its working
// slice into a merged AppSettings via WriteInto and advances its baseline via Commit
// at the SettingsSession's direction, so the session can persist all tabs atomically.

using System;
using System.Collections.Generic;
using System.IO;
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
public sealed class GeneralTabSettings : IEditableSettingsTab
{
    private AppSettings _working;
    private AppSettings _baseline;

    /// <summary>
    /// Snapshots the persisted settings into working and baseline copies. The
    /// <paramref name="persisted"/> object is never mutated by this instance.
    /// </summary>
    public GeneralTabSettings(AppSettings persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        // Normalized() yields independent copies carrying effective defaults, so the
        // active and persisted settings are never mutated by opening or editing.
        _working = persisted.Normalized();
        _baseline = persisted.Normalized();
    }

    // ── Working editable state ──────────────────────────────────────────

    /// <summary>
    /// The current working Filename Template value (effective: the default when the
    /// persisted value was null/empty). Editing updates this without touching the source.
    /// </summary>
    public string FilenameTemplate => _working.FilenameTemplate!;

    /// <summary>
    /// Sets the working Filename Template from user input and recomputes the preview.
    /// Never persists or mutates the original persisted settings.
    /// </summary>
    public void EditFilenameTemplate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _working.FilenameTemplate = value;
    }

    /// <summary>
    /// A representative expanded filename for the current working template, using a
    /// fixed sample timestamp and title so the preview is deterministic. Empty when the
    /// template is blank (no representative filename can be produced).
    /// </summary>
    public string FilenameTemplatePreview =>
        string.IsNullOrWhiteSpace(_working.FilenameTemplate)
            ? string.Empty
            : ExportFilenameTemplate.Expand(_working.FilenameTemplate, SampleTimestamp, SampleTitle)
                + "." + ExportDefaults.DefaultExtension;

    // Fixed sample inputs so the preview is a stable, representative filename.
    private static readonly DateTime SampleTimestamp = new(2024, 1, 15, 14, 30, 45);
    private const string SampleTitle = "Screenshot";

    // ── Save Location working state ─────────────────────────────────────

    /// <summary>
    /// The current working Save Location value (effective: the default when the
    /// persisted value was null/empty). Editing updates this without touching the source.
    /// </summary>
    public string SaveLocation => _working.SaveLocation!;

    /// <summary>
    /// Sets the working Save Location from user input (a typed path or a folder-picker
    /// selection). Never persists or mutates the original persisted settings.
    /// </summary>
    public void EditSaveLocation(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _working.SaveLocation = value;
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

    private const string EmptyTemplateError = "Filename template must not be empty.";
    private const string RelativeSaveLocationError = "Save location must be an absolute path.";

    /// <summary>
    /// True when the working Save Location, if provided, is an absolute path.
    /// Empty/whitespace is valid (it resolves to the default on save via Normalized),
    /// mirroring <see cref="AppSettings.Validate"/>.
    /// </summary>
    public bool IsSaveLocationValid =>
        string.IsNullOrWhiteSpace(_working.SaveLocation) || Path.IsPathRooted(_working.SaveLocation);

    /// <summary>True when every working field passes its validation rule.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(_working.FilenameTemplate) && IsSaveLocationValid;

    /// <summary>An inline validation message for the Filename Template, or null when valid.</summary>
    public string? FilenameTemplateError =>
        string.IsNullOrWhiteSpace(_working.FilenameTemplate) ? EmptyTemplateError : null;

    /// <summary>An inline validation message for the Save Location, or null when valid.</summary>
    public string? SaveLocationError => IsSaveLocationValid ? null : RelativeSaveLocationError;

    /// <summary>
    /// First validation error across this tab's fields, or null when the tab is valid.
    /// Used by the session to build a status message when an invalid Apply/OK is attempted.
    /// </summary>
    public string? FirstError => FilenameTemplateError ?? SaveLocationError;

    /// <summary>
    /// True when any working field differs from its last applied baseline.
    /// </summary>
    public bool IsDirty =>
        !string.Equals(_working.FilenameTemplate, _baseline.FilenameTemplate, StringComparison.Ordinal)
        || !string.Equals(_working.SaveLocation, _baseline.SaveLocation, StringComparison.Ordinal);

    // ── IEditableSettingsTab: merge, commit, revert, reset ──────────────

    /// <summary>
    /// Writes this tab's working Save Location and Filename Template slice into
    /// <paramref name="target"/>. Other tabs' slices are left untouched so the session
    /// can merge every tab into one AppSettings and persist it once.
    /// </summary>
    public void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.SaveLocation = _working.SaveLocation;
        target.FilenameTemplate = _working.FilenameTemplate;
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
    /// Discards all edits made since the last successful Apply (or since open if Apply
    /// has not run), reverting the working settings to the baseline. Never persists.
    /// </summary>
    public void Cancel()
    {
        _working = _baseline.Normalized();
    }

    /// <summary>
    /// Restores the working Save Location and Filename Template to their defaults in the
    /// current editing session so the user can inspect the defaults, preview, and
    /// validation results immediately. The baseline is left untouched, so Reset alone
    /// never persists or changes runtime state, and a later Cancel still reverts to the
    /// settings that existed before Reset. Apply or OK after Reset persists the defaults.
    /// </summary>
    public void Reset()
    {
        _working = AppSettings.WithDefaults();
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
