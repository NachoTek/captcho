// ExportTabSettings.cs — Pure C# read-only display seam for the Export settings tab.
//
// Surfaces the current export format as read-only content for the Settings window.
// Captcho currently exports captures exclusively as PNG; this coordinator exposes
// the format name, file extension, a readable description of why PNG is the
// default, and a generic note that additional formats are planned. It never
// presents JPEG controls or implies that JPEG export is available. The tab is
// read-only, so there is no editing, validation, or save session here — only the
// displayed content, sourced from ExportDefaults (the single source of truth for
// the current format) so the UI cannot drift from what the export pipeline writes.

using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Pure C# read-only coordinator for the Export settings tab. Exposes the current
/// export format (PNG) and supporting display text. Read-only: no editing,
/// validation, or persistence flows through this seam.
/// </summary>
public sealed class ExportTabSettings
{
    /// <summary>
    /// Accessible heading for the Export tab.
    /// </summary>
    public string Heading => "Export format";

    /// <summary>
    /// The current export format name, upper-cased from
    /// <see cref="ExportDefaults.DefaultExtension"/> so the displayed name cannot
    /// drift from the extension the export pipeline writes. Captcho exports as PNG.
    /// </summary>
    public string FormatName => ExportDefaults.DefaultExtension.ToUpperInvariant();

    /// <summary>
    /// The current export file extension, including the leading dot (e.g. ".png").
    /// Derived from <see cref="ExportDefaults.DefaultExtension"/> so the displayed
    /// extension always matches what the export pipeline writes.
    /// </summary>
    public string FileExtension => "." + ExportDefaults.DefaultExtension;

    /// <summary>
    /// Readable explanation of PNG as Captcho's current export format and why it is
    /// the default. User-facing prose, not a development placeholder.
    /// </summary>
    public string FormatDescription =>
        "Captcho saves every capture as a PNG image. PNG is a lossless format, so " +
        "your screenshots keep full pixel fidelity without the quality loss or " +
        "artifacts introduced by compressed formats. That makes it the best default " +
        "for screenshots, which often contain sharp text and fine detail.";

    /// <summary>
    /// A generic note that additional export formats are planned for a future release.
    /// Deliberately does not name a specific format (such as JPEG) so it cannot imply
    /// that any other format is currently available.
    /// </summary>
    public string PlannedFormatsNote =>
        "Additional export formats are planned for a future release.";
}
