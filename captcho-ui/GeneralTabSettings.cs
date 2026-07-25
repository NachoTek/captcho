// GeneralTabSettings.cs — Pure C# editing seam for the General settings tab.
//
// Owns the editable Filename Template and the settings session used by the
// Settings window: a working snapshot of the persisted settings, a live preview,
// inline validation, and Apply/OK/Cancel actions backed by an injected save
// delegate. Construction snapshots the persisted settings so the active and
// persisted Settings are never mutated by opening or editing.

using System;
using System.Collections.Generic;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Save delegate injected so the session is testable without a filesystem.
/// Mirrors <see cref="ConfigurationService.Save"/>.
/// </summary>
public delegate ConfigurationSaveResult SaveSettingsDelegate(AppSettings settings);

/// <summary>
/// A supported Filename Template placeholder with a human description and example.
/// </summary>
public sealed record FilenameTemplatePlaceholder(string Token, string Description, string Example);

/// <summary>
/// Outcome of a session Apply or OK action, carrying an inline message and
/// whether the window should close (OK closes only on a successful save).
/// </summary>
public sealed class GeneralSettingsActionResult
{
    /// <summary>Whether the settings were persisted.</summary>
    public bool Success { get; init; }

    /// <summary>Inline message to display (sanitized; never reports success on failure).</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>True only when the caller should close the window (OK on success).</summary>
    public bool ShouldClose { get; init; }
}

/// <summary>
/// Pure C# coordinator for the General settings tab. Holds a working snapshot of
/// the persisted Filename Template, computes a representative preview, lists the
/// supported placeholders, validates edits inline, and drives the Apply/OK/Cancel
/// session. Never mutates the active or persisted Settings on open or edit.
/// </summary>
public sealed class GeneralTabSettings
{
    private readonly SaveSettingsDelegate _save;

    private AppSettings _working;
    private AppSettings _baseline;

    /// <summary>
    /// Snapshots the persisted settings into working and baseline copies and
    /// stores the save delegate. The <paramref name="persisted"/> object is never
    /// mutated by this instance.
    /// </summary>
    public GeneralTabSettings(AppSettings persisted, SaveSettingsDelegate save)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(save);

        _save = save;
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

    // ── Validation ──────────────────────────────────────────────────────

    private const string EmptyTemplateError = "Filename template must not be empty.";

    /// <summary>True when the working template is a non-blank value.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(_working.FilenameTemplate);

    /// <summary>An inline validation message, or null when the template is valid.</summary>
    public string? FilenameTemplateError => IsValid ? null : EmptyTemplateError;

    /// <summary>Whether Apply may run (requires a valid template).</summary>
    public bool CanApply => IsValid;

    /// <summary>Whether OK may run (requires a valid template).</summary>
    public bool CanConfirm => IsValid;

    /// <summary>
    /// True when the working template differs from the last applied baseline.
    /// </summary>
    public bool IsDirty =>
        !string.Equals(_working.FilenameTemplate, _baseline.FilenameTemplate, StringComparison.Ordinal);

    // ── Session actions ─────────────────────────────────────────────────

    /// <summary>
    /// Persists the valid working template via the save delegate and keeps the window
    /// open. Updates the baseline on success so Cancel no longer reverts the change.
    /// Never reports success when the save failed.
    /// </summary>
    public GeneralSettingsActionResult Apply() => Persist(shouldCloseOnSuccess: false);

    /// <summary>
    /// Persists the valid working template and signals the window to close, but only
    /// after a successful save. On failure the window stays open with an inline message.
    /// </summary>
    public GeneralSettingsActionResult Confirm() => Persist(shouldCloseOnSuccess: true);

    /// <summary>
    /// Discards all edits made since the last successful Apply (or since open if Apply
    /// has not run), reverting the working template to the baseline. Never persists.
    /// </summary>
    public void Cancel()
    {
        _working = _baseline.Normalized();
    }

    private GeneralSettingsActionResult Persist(bool shouldCloseOnSuccess)
    {
        if (!IsValid)
        {
            return new GeneralSettingsActionResult
            {
                Success = false,
                Message = FilenameTemplateError ?? "Cannot apply while the filename template is invalid.",
                ShouldClose = false,
            };
        }

        var result = _save(_working.Normalized());

        if (result.Success)
        {
            _baseline = _working.Normalized();
            return new GeneralSettingsActionResult
            {
                Success = true,
                Message = SettingsWindowCoordinator.SavedMessage,
                ShouldClose = shouldCloseOnSuccess,
            };
        }

        return new GeneralSettingsActionResult
        {
            Success = false,
            Message = SettingsWindowCoordinator.FormatSaveFailureMessage(result),
            ShouldClose = false,
        };
    }

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
