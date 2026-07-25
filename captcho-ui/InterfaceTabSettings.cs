// InterfaceTabSettings.cs — Pure C# read-only display seam for the Interface settings tab.
//
// Surfaces read-only content for the Interface tab of the Settings window.
// Interface settings (theme, window behavior, annotation preferences) are not yet
// available, so this coordinator exposes a clear heading, a plain-language message
// stating that the settings are not yet available, and a note describing what is
// planned. The tab is read-only: no editing, validation, or save session flows
// through this seam — only the displayed content, keeping the placeholder honest
// and user-facing rather than a "not implemented" development stub.

namespace captcho.UI;

/// <summary>
/// Pure C# read-only coordinator for the Interface settings tab. Exposes the
/// heading, not-yet-available message, and planned-settings note. Read-only: no
/// editing, validation, or persistence flows through this seam.
/// </summary>
public sealed class InterfaceTabSettings
{
    /// <summary>
    /// Accessible heading for the Interface tab.
    /// </summary>
    public string Heading => "Interface settings";

    /// <summary>
    /// A clear, user-facing message stating that Interface settings are not yet
    /// available. Readable prose, not a development placeholder.
    /// </summary>
    public string Message =>
        "Interface settings are not yet available in this version of Captcho.";

    /// <summary>
    /// A note describing the interface settings that are planned for a future release.
    /// </summary>
    public string PlannedSettingsNote =>
        "Interface options such as theme, window behavior, and annotation " +
        "preferences are planned for a future release.";
}
