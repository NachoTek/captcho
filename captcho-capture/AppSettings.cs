// AppSettings.cs — Persisted user configuration for captcho.
// Defaults are sourced from ExportDefaults to keep a single source of truth.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace captcho.Capture;

/// <summary>
/// Identifies which persisted setting a <see cref="SettingsIssue"/> belongs to. Used by
/// <see cref="AppSettings.Validate"/> to attribute each issue so editable tabs can surface
/// inline per-field errors and the composed session can build a cross-tab status, all from
/// the one validation result.
/// </summary>
public enum SettingsField
{
    /// <summary>The <see cref="AppSettings.SaveLocation"/> field.</summary>
    SaveLocation,
    /// <summary>The <see cref="AppSettings.FilenameTemplate"/> field.</summary>
    FilenameTemplate,
    /// <summary>The <see cref="AppSettings.GlobalHotkeyEnabledStates"/> field.</summary>
    GlobalHotkeyEnabledStates,
}

/// <summary>
/// One validation issue produced by <see cref="AppSettings.Validate"/>: which field it
/// concerns and a user-displayable message. Carried verbatim into inline tab errors and
/// the composed session status so the message text has exactly one source.
/// </summary>
public sealed record SettingsIssue(SettingsField Field, string Message);

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
    /// Validates the settings, returning a list of field-attributed issues found.
    /// An empty list means the settings are valid. This is the single source of truth
    /// for persisted-setting validity: every editable tab and the composed settings
    /// session gate Apply/OK enablement through this method, so a validation rule added
    /// here flows to both inline per-field errors and the cross-tab button gate without
    /// any caller-specific duplication.
    /// </summary>
    public IReadOnlyList<SettingsIssue> Validate()
    {
        var issues = new List<SettingsIssue>();

        // SaveLocation: if provided, must be an absolute path. Empty/whitespace is valid
        // (it resolves to the default on save via Normalized).
        if (!string.IsNullOrWhiteSpace(SaveLocation))
        {
            if (!Path.IsPathRooted(SaveLocation))
            {
                issues.Add(new SettingsIssue(
                    SettingsField.SaveLocation,
                    "Save location must be an absolute path."));
            }
        }

        // FilenameTemplate: if provided (non-null), must not be empty/whitespace after
        // trimming. A null template is valid (resolves to the default via Normalized);
        // an explicitly blank one is a user edit that cannot produce a filename.
        if (FilenameTemplate is not null && string.IsNullOrWhiteSpace(FilenameTemplate))
        {
            issues.Add(new SettingsIssue(
                SettingsField.FilenameTemplate,
                "Filename template must not be empty."));
        }

        // GlobalHotkeyEnabledStates: defensive structural check at the persistence
        // boundary. The typed API and JSON converter cannot introduce an undefined
        // route through normal use, so this rule is a no-op on well-formed state — but
        // it gives the field a real validation path (covered by AppSettings.Validate)
        // so the Global Hotkeys tab is never "valid by accident" and a future rule flows
        // straight through the composed Apply/OK gate.
        if (GlobalHotkeyEnabledStates is not null)
        {
            foreach (var route in GlobalHotkeyEnabledStates.Keys)
            {
                if (!Enum.IsDefined(typeof(GlobalHotkeyRoute), route))
                {
                    issues.Add(new SettingsIssue(
                        SettingsField.GlobalHotkeyEnabledStates,
                        "Global Hotkey enabled states contain an unknown route."));
                    break;
                }
            }
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
