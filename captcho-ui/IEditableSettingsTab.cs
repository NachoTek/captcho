// IEditableSettingsTab.cs — Internal seam between SettingsSession and each editable tab.
//
// This is an INTERNAL contract shared by SettingsSession and the editable tab
// collaborators (GeneralTabSettings, GlobalHotkeyTabSettings). It is deliberately not a
// public ISettingsTab: per the #8 design, promoting it to a public tab-port is exactly
// what issue #11 will decide on its own merits (only if a real second use appears).
// One adapter is a hypothetical seam — keep it internal.

using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// The slice of tab behavior the composed <see cref="SettingsSession"/> needs: validity
/// for composed gating, dirty tracking, merging the working slice into one persisted
/// AppSettings, advancing the baseline after a successful persist, and reverting /
/// resetting the working state. Persistence itself lives in the session, not the tab.
/// </summary>
internal interface IEditableSettingsTab
{
    /// <summary>True when every working field on this tab passes its validation rules.</summary>
    bool IsValid { get; }

    /// <summary>True when any working field differs from this tab's last applied baseline.</summary>
    bool IsDirty { get; }

    /// <summary>
    /// First validation error on this tab, or null when valid. Used by the session to
    /// build an inline status message when an Apply/OK is attempted while invalid.
    /// </summary>
    string? FirstError { get; }

    /// <summary>
    /// Writes this tab's working slice into <paramref name="target"/>. Leaves other
    /// tabs' slices untouched so the session can merge every tab and persist once.
    /// </summary>
    void WriteInto(AppSettings target);

    /// <summary>
    /// Advances this tab's baseline to the current working state. Called by the session
    /// only after a successful persist.
    /// </summary>
    void Commit();

    /// <summary>
    /// Reverts this tab's working state to its baseline. Never persists.
    /// </summary>
    void Cancel();

    /// <summary>
    /// Restores this tab's working state to defaults; baseline is left untouched so a
    /// later Cancel still reverts the reset.
    /// </summary>
    void Reset();
}
