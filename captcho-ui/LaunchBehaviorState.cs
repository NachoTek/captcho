// LaunchBehaviorState.cs — Runtime recording of the last Capture Mode for
// the launch behavior (issue #53).
//
// Mirrors RememberedSelectionState's shape: owns one workflow session's
// last-Capture-Mode recording, persists through a narrow
// ILaunchBehaviorPersistence seam, and records only while the configured
// launch action needs the value (Last Capture Mode). Do nothing and
// Configured Capture Mode never write — "remember the last mode only when
// required".

using System;
using captcho.Capture;

namespace captcho.UI;

/// <summary>Persists changes to the recorded last Capture Mode.</summary>
public interface ILaunchBehaviorPersistence
{
    bool Save(CaptureMode? mode);
}

/// <summary>Records the last Capture Mode used by a workflow session.</summary>
public interface ILaunchBehaviorRecorder
{
    void Record(CaptureMode mode);
}

/// <summary>
/// Owns the last Capture Mode recording for one workflow session. Reads the
/// live runtime settings per capture so committed Settings changes apply to
/// the next capture without rebuilding the state, and persists through the
/// injected seam only while <see cref="LaunchAction.LastCaptureMode"/> is the
/// configured action. Persistence failures are non-fatal: the in-memory
/// settings object is only advanced on success, and capture never fails
/// because its recording could not be written.
/// </summary>
public sealed class LaunchBehaviorState : ILaunchBehaviorRecorder
{
    private readonly AppSettings _settings;
    private readonly ILaunchBehaviorPersistence? _persistence;

    public LaunchBehaviorState(
        AppSettings settings,
        ILaunchBehaviorPersistence? persistence = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _persistence = persistence;
    }

    /// <summary>
    /// Records <paramref name="mode"/> as the last Capture Mode when the
    /// configured launch action needs it; otherwise a no-op. An out-of-range
    /// mode (defensive) is rejected without writing.
    /// </summary>
    public void Record(CaptureMode mode)
    {
        if (!Enum.IsDefined(mode))
            return;

        var behavior = _settings.EffectiveLaunchBehavior;
        if (behavior.Action != LaunchAction.LastCaptureMode)
            return;

        if (_persistence is null)
        {
            _settings.LastCaptureMode = mode;
            return;
        }

        if (_persistence.Save(mode))
            _settings.LastCaptureMode = mode;
    }
}
