// AppSettings.cs — Persisted user configuration for captcho.
// Defaults are sourced from ExportDefaults to keep a single source of truth.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace captcho.Capture;

/// <summary>
/// User-configurable settings persisted to %LOCALAPPDATA%\captcho\settings.json.
/// Unknown properties from future versions are preserved during round-trip.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Directory where screenshots are saved by default.
    /// When null or empty, falls back to <see cref="ExportDefaults.DefaultSaveDirectory"/>.
    /// </summary>
    public string? SaveLocation { get; set; }

    /// <summary>
    /// Filename template supporting date/time, title, and sequence placeholders.
    /// When null or empty/whitespace, falls back to <see cref="ExportDefaults.DefaultFilenameTemplate"/>.
    /// </summary>
    public string? FilenameTemplate { get; set; }

    /// <summary>
    /// Per-Global-Hotkey enabled states, keyed by the hotkey's stable id (1–4).
    /// Null (or an absent id) means the hotkey is enabled — the default, matching
    /// the pre-existing "every global hotkey on" behavior. When non-null, only ids
    /// mapped to <c>true</c> are enabled. The ids match <c>HotkeyRouteMap</c> but
    /// are kept here as plain integers so this persistence model stays free of UI
    /// and Win32 dependencies.
    /// </summary>
    public Dictionary<int, bool>? HotkeyEnabledStates { get; set; }

    // Future properties can be added here. System.Text.Json will ignore
    // unknown properties on read and only serialize declared ones.

    /// <summary>
    /// Returns an <see cref="AppSettings"/> instance populated with production defaults
    /// from <see cref="ExportDefaults"/>.
    /// </summary>
    public static AppSettings WithDefaults() => new()
    {
        SaveLocation = ExportDefaults.DefaultSaveDirectory,
        FilenameTemplate = ExportDefaults.DefaultFilenameTemplate,
    };

    /// <summary>
    /// Resolves the effective save location, falling back to the default if null/empty.
    /// </summary>
    [JsonIgnore]
    public string EffectiveSaveLocation =>
        string.IsNullOrWhiteSpace(SaveLocation)
            ? ExportDefaults.DefaultSaveDirectory
            : SaveLocation;

    /// <summary>
    /// Resolves the effective filename template, falling back to the default if null/empty/whitespace.
    /// </summary>
    [JsonIgnore]
    public string EffectiveFilenameTemplate =>
        string.IsNullOrWhiteSpace(FilenameTemplate)
            ? ExportDefaults.DefaultFilenameTemplate
            : FilenameTemplate;

    /// <summary>
    /// Resolves whether the global hotkey with the given stable id is enabled.
    /// A null states map (settings file predates per-hotkey toggles) or a missing
    /// entry means enabled, matching the pre-existing "every global hotkey on"
    /// behavior. An explicit <c>false</c> disables the hotkey.
    /// </summary>
    public bool IsHotkeyEnabled(int hotkeyId) =>
        HotkeyEnabledStates is null
        || !HotkeyEnabledStates.TryGetValue(hotkeyId, out bool enabled)
        || enabled;

    /// <summary>
    /// Validates the settings, returning a list of issues found.
    /// An empty list means the settings are valid.
    /// </summary>
    public List<string> Validate()
    {
        var issues = new List<string>();

        // SaveLocation: if provided, must be an absolute path
        if (!string.IsNullOrWhiteSpace(SaveLocation))
        {
            if (!Path.IsPathRooted(SaveLocation))
            {
                issues.Add("SaveLocation must be an absolute path.");
            }
        }

        // FilenameTemplate: if provided, must not be empty/whitespace after trimming
        // (the ExportFilenameTemplate.Expand method will validate at expansion time,
        //  but we can pre-check for obviously invalid values)
        if (FilenameTemplate is not null && string.IsNullOrWhiteSpace(FilenameTemplate))
        {
            issues.Add("FilenameTemplate must not be empty or whitespace when specified.");
        }

        return issues;
    }

    /// <summary>
    /// Returns a sanitized copy with null/empty/whitespace fields replaced by defaults.
    /// The hotkey enabled-states dictionary is deep-copied (or kept null) so the
    /// returned copy is fully independent of this instance.
    /// </summary>
    public AppSettings Normalized() => new()
    {
        SaveLocation = EffectiveSaveLocation,
        FilenameTemplate = EffectiveFilenameTemplate,
        HotkeyEnabledStates = HotkeyEnabledStates is null
            ? null
            : new Dictionary<int, bool>(HotkeyEnabledStates),
    };
}
