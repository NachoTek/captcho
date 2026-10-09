// LaunchBehaviorSettings.cs — Persisted launch behavior Settings (issue #53).
//
// The launch slice of Settings: which Capture Mode (if any) a Trigger fires
// on startup. Do nothing is the safe default, so settings files written
// before this field shipped never capture unexpectedly on load. The last
// Capture Mode is recorded as separate persisted state and is only read when
// the chosen action needs it.

using System.Text.Json.Serialization;

namespace captcho.Capture;

/// <summary>
/// The configured launch action: what captcho does when it starts.
/// Its meaning is defined here, in the capture layer, free of WinUI and
/// Win32 dependencies.
/// </summary>
public enum LaunchAction
{
    /// <summary>
    /// Startup stays idle — no Capture is triggered. The safe default.
    /// </summary>
    DoNothing,

    /// <summary>
    /// Startup triggers the last Capture Mode used in a previous run. When
    /// none is recorded (or the recorded value is invalid), startup stays
    /// idle rather than guessing.
    /// </summary>
    LastCaptureMode,

    /// <summary>
    /// Startup triggers the specifically configured Capture Mode.
    /// </summary>
    ConfiguredCaptureMode,
}

/// <summary>
/// Persisted launch behavior: the chosen <see cref="LaunchAction"/> plus the
/// configured Capture Mode for <see cref="LaunchAction.ConfiguredCaptureMode"/>.
/// The configured mode is meaningful only for that action — the editing tab
/// clears it when another action is chosen, and a null mode with the
/// Configured action is invalid at the persistence boundary.
/// </summary>
public sealed record LaunchBehaviorSettings(
    LaunchAction Action,
    CaptureMode? ConfiguredMode)
{
    /// <summary>
    /// The safe defaults: startup does nothing, no Capture Mode configured.
    /// Settings files written before this field shipped load with these
    /// defaults, so existing Configuration never starts capturing on launch.
    /// </summary>
    public static LaunchBehaviorSettings WithDefaults() => new(LaunchAction.DoNothing, null);

    /// <summary>
    /// Resolves the effective configured Capture Mode, falling back to null
    /// for an out-of-range enum value (a hand-edited settings file) so an
    /// invalid persisted value degrades to "not configured" instead of
    /// capturing something unintended.
    /// </summary>
    [JsonIgnore]
    public CaptureMode? EffectiveConfiguredMode =>
        ConfiguredMode is not null && Enum.IsDefined(ConfiguredMode.Value)
            ? ConfiguredMode
            : null;

    /// <summary>
    /// True when this configuration is structurally valid: the action is a
    /// known value, and the Configured Capture Mode action carries a real
    /// Capture Mode (the other actions carry none).
    /// </summary>
    [JsonIgnore]
    public bool IsValid =>
        Enum.IsDefined(Action)
        && (Action != LaunchAction.ConfiguredCaptureMode || EffectiveConfiguredMode is not null);
}

public static class LaunchBehaviorSettingsExtensions
{
    /// <summary>
    /// Resolves the Capture Mode a startup Trigger fires, or null when
    /// startup must stay idle. The single resolution owner for the launch
    /// behavior: Do nothing and a missing/invalid source mode stay idle;
    /// Last Capture Mode reads the separately persisted last-used mode;
    /// Configured Capture Mode reads the configured selection. An invalid
    /// action degrades to Do nothing.
    /// </summary>
    public static CaptureMode? ResolveStartupMode(this AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var behavior = settings.EffectiveLaunchBehavior;
        return behavior.Action switch
        {
            LaunchAction.LastCaptureMode => settings.EffectiveLastCaptureMode,
            LaunchAction.ConfiguredCaptureMode when behavior.IsValid =>
                behavior.EffectiveConfiguredMode,
            _ => null,
        };
    }
}
