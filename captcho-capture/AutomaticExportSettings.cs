// AutomaticExportSettings.cs — Persisted automatic Export settings.
//
// The Behavior slice of Settings: three independent toggles controlling which
// Export actions run automatically after Annotation confirmation (issue #47).
// Every action is disabled by default, matching the pre-existing
// manual-only delivery workflow; settings files written before this field
// shipped therefore retain manual-only behavior on load.

using System.Text.Json.Serialization;

namespace captcho.Capture;

/// <summary>
/// Persisted defaults for the configured automatic delivery actions. Each
/// toggle is independent — any combination is meaningful — and the dependency
/// order (save, then Copy Frame, then Copy Path) is owned by the Workflow
/// Session that runs them, not by this model.
/// </summary>
public sealed record AutomaticExportSettings(
    bool AutoSave,
    bool AutoCopyFrame,
    bool AutoCopyPath)
{
    /// <summary>
    /// The disabled defaults: no automatic delivery. A Capture is delivered
    /// only through the manual Export actions until the user opts in.
    /// </summary>
    public static AutomaticExportSettings WithDefaults() => new(false, false, false);

    /// <summary>
    /// True when at least one automatic action is enabled — the session's
    /// cheap check for whether any automatic delivery should run at all.
    /// </summary>
    [JsonIgnore]
    public bool AnyEnabled => AutoSave || AutoCopyFrame || AutoCopyPath;

    /// <summary>
    /// Returns this configuration unchanged (the record is already immutable
    /// and every value is valid); kept for symmetry with the other persisted
    /// records whose effective-value resolution flows through Normalized().
    /// </summary>
    public AutomaticExportSettings Normalized() => this;
}
