// AppSettings.cs — Persisted user configuration for captcho.
// Defaults are sourced from ExportDefaults to keep a single source of truth.

using System;
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
    /// </summary>
    public AppSettings Normalized() => new()
    {
        SaveLocation = EffectiveSaveLocation,
        FilenameTemplate = EffectiveFilenameTemplate,
    };
}
