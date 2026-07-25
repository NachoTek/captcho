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
    /// Per-Global-Hotkey enabled states, keyed by the capture route each Global Hotkey
    /// triggers (<see cref="GlobalHotkeyRoute"/>). Null (or an absent route) means the
    /// Global Hotkey is enabled — the default, matching the pre-existing "every global
    /// hotkey on" behavior. When non-null, only routes mapped to <c>true</c> are enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keying by <see cref="GlobalHotkeyRoute"/> — a stable identity defined here in the
    /// capture layer — keeps this model free of UI and Win32 dependencies.
    /// The UI layer (<c>GlobalHotkeyRouteMap</c>) maps its Win32 hotkey ids to these
    /// routes; this layer never needs the UI map to interpret persisted state.
    /// </para>
    /// <para>
    /// The JSON property name is pinned to <c>hotkeyEnabledStates</c> so settings.json
    /// files written by earlier builds still load. On-disk keys are the route names
    /// (e.g. <c>currentMonitor</c>); settings written by the earlier int-id-keyed format
    /// (M002, stable ids 1–4) are migrated on read by the converter.
    /// </para>
    /// </remarks>
    [JsonPropertyName("hotkeyEnabledStates")]
    [JsonConverter(typeof(GlobalHotkeyEnabledStatesConverter))]
    public Dictionary<GlobalHotkeyRoute, bool>? GlobalHotkeyEnabledStates { get; set; }

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
    /// Resolves whether the Global Hotkey for the given capture route is enabled.
    /// A null states map (settings file predates per-Global-Hotkey toggles) or a missing
    /// entry means enabled, matching the pre-existing "every global hotkey on"
    /// behavior. An explicit <c>false</c> disables the Global Hotkey.
    /// </summary>
    public bool IsGlobalHotkeyEnabled(GlobalHotkeyRoute route) =>
        GlobalHotkeyEnabledStates is null
        || !GlobalHotkeyEnabledStates.TryGetValue(route, out bool enabled)
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
    /// The Global Hotkey enabled-states dictionary is deep-copied (or kept null) so the
    /// returned copy is fully independent of this instance.
    /// </summary>
    public AppSettings Normalized() => new()
    {
        SaveLocation = EffectiveSaveLocation,
        FilenameTemplate = EffectiveFilenameTemplate,
        GlobalHotkeyEnabledStates = GlobalHotkeyEnabledStates is null
            ? null
            : new Dictionary<GlobalHotkeyRoute, bool>(GlobalHotkeyEnabledStates),
    };
}
