// ExportTabSettings.cs — Pure C# editing seam for the Export settings tab.
//
// Owns the editable export format (PNG or JPEG) and JPEG quality slice
// (issue #45): a working snapshot of the persisted settings, format-derived
// extension display, and inline validation for the inclusive 0–100 quality
// range. The shared snapshot/apply/cancel/reset plumbing lives in
// EditableTabSession (written once); this tab declares only its own Export
// slice — what to edit, validate, merge via WriteInto, and restore via
// ApplyDefaults. Never persists — it writes its working slice into a merged
// AppSettings via WriteInto and advances its baseline via Commit at the
// SettingsSession's direction, so the session can persist all tabs atomically.
//
// The Filename Template stays format-agnostic: the output extension is derived
// from the format at export time, and this tab surfaces that derived extension
// for display only. Quality remains editable (and validated) even while PNG is
// selected — the control is unavailable but the value is never silently
// discarded, so a format round-trip preserves the user's quality choice.

using System;
using System.Linq;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Pure C# editing seam for the Export settings tab. Holds a working snapshot
/// of the persisted export format and JPEG quality, exposes them for editing,
/// and derives the display extension from the working format. Never persists
/// or mutates the active/persisted Settings on open or edit; the
/// <see cref="SettingsSession"/> orchestrates persistence across all tabs.
/// </summary>
internal sealed class ExportTabSettings : EditableTabSession
{
    /// <summary>
    /// Snapshots the persisted settings into independent working and baseline copies via
    /// <see cref="EditableTabSession"/>'s constructor. The <paramref name="persisted"/>
    /// object is never mutated by this instance.
    /// </summary>
    public ExportTabSettings(AppSettings persisted)
        : base(persisted)
    {
    }

    // ── Working editable state ──────────────────────────────────────────

    /// <summary>
    /// The current working export format. Editing updates this without
    /// touching the source.
    /// </summary>
    public ExportImageFormat Format =>
        Enum.IsDefined(Working.ExportSettings!.Format)
            ? Working.ExportSettings.Format
            : ExportImageFormat.Png;

    /// <summary>
    /// The current working JPEG quality (inclusive 0–100). Applies only when
    /// the format is JPEG; the value is retained (not reset) while PNG is
    /// selected so a format round-trip never discards the user's choice.
    /// Exposed unclamped so out-of-range edits surface through validation
    /// instead of being silently rewritten.
    /// </summary>
    public int JpegQuality => Working.ExportSettings!.JpegQuality;

    /// <summary>
    /// The file extension (with a leading dot) derived from the working
    /// format — display-only; the export pipeline derives the real extension
    /// from the committed format at export time.
    /// </summary>
    public string FileExtension => "." + Format.Extension();

    /// <summary>
    /// Whether the JPEG quality control is available: only when the working
    /// format is JPEG. PNG is lossless and takes no quality.
    /// </summary>
    public bool JpegQualityAvailable => Format == ExportImageFormat.Jpeg;

    /// <summary>
    /// Sets the working export format from user input. Never persists or
    /// mutates the original persisted settings.
    /// </summary>
    public void EditFormat(ExportImageFormat value)
    {
        if (!Enum.IsDefined(value))
            return;
        Working.ExportSettings = Working.ExportSettings! with { Format = value };
    }

    /// <summary>
    /// Sets the working JPEG quality from user input. The value is stored
    /// unclamped so out-of-range edits surface as inline validation errors
    /// (and block Apply/OK) instead of being silently rewritten. Never
    /// persists or mutates the original persisted settings.
    /// </summary>
    public void EditJpegQuality(int value)
    {
        Working.ExportSettings = Working.ExportSettings! with { JpegQuality = value };
    }

    // ── Validation ──────────────────────────────────────────────────────

    /// <summary>
    /// True when the working export slice passes its validation rule. Derived
    /// from <see cref="AppSettings.Validate"/> (the source of truth) so this
    /// tab and the composed session gate never disagree about what counts as
    /// a valid export format or quality.
    /// </summary>
    public override bool IsValid =>
        !SliceIssues().Any(i => i.Field == SettingsField.ExportSettings);

    /// <summary>
    /// Inline validation message for the JPEG quality, sourced from
    /// <see cref="AppSettings.Validate"/>, or null when valid.
    /// </summary>
    public string? JpegQualityError =>
        SliceIssues().FirstOrDefault(i => i.Field == SettingsField.ExportSettings)?.Message;

    /// <summary>
    /// First validation error on this tab, or null when valid. Used by the
    /// session to build a status message when an invalid Apply/OK is
    /// attempted.
    /// </summary>
    public override string? FirstError =>
        SliceIssues().FirstOrDefault(i => i.Field == SettingsField.ExportSettings)?.Message;

    /// <summary>
    /// True when the working export slice differs from its last applied
    /// baseline.
    /// </summary>
    public override bool IsDirty =>
        Working.ExportSettings != Baseline.ExportSettings;

    // ── Export slice: merge, defaults ───────────────────────────────────

    /// <summary>
    /// Writes this tab's working export format and quality slice into
    /// <paramref name="target"/>. Other tabs' slices are left untouched so the
    /// session can merge every tab into one AppSettings and persist it once.
    /// </summary>
    public override void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ExportSettings = Working.ExportSettings;
    }

    /// <summary>
    /// Restores this tab's export format to its defaults, read from
    /// <see cref="ExportSettings.WithDefaults"/> (the single default source
    /// shared by every collaborator). Only this tab's slice is touched; the
    /// baseline is left untouched by <see cref="EditableTabSession.Reset"/>,
    /// so Reset alone never persists and a later Cancel still reverts to the
    /// settings that existed before Reset.
    /// </summary>
    protected override void ApplyDefaults(AppSettings working)
    {
        working.ExportSettings = ExportSettings.WithDefaults();
    }
}
