// WindowPlacement.cs — Pure C# seam for settings window placement persistence.
//
// Encapsulates the parse/serialize logic for the settings window's saved
// position and size, which is persisted to LocalSettings as a comma-separated
// "x,y,width,height" string. This is the pure, headlessly-testable core of the
// placement logic; SettingsWindow owns the WinUI AppWindow / LocalSettings I/O
// and delegates parsing and formatting to this seam.

namespace captcho.UI;

/// <summary>
/// Pure, headlessly-testable seam for settings window placement (position and
/// size). Owns the parse/serialize format and the minimum-size clamping applied
/// on load. The WinUI AppWindow and LocalSettings I/O lives in SettingsWindow
/// and delegates here.
/// </summary>
public readonly record struct WindowPlacement(int X, int Y, int Width, int Height)
{
    /// <summary>Comma separator used in the persisted placement string.</summary>
    public const char Separator = ',';

    /// <summary>
    /// Minimum restored width. A persisted width below this is clamped up so a
    /// window cannot be restored too narrow to use.
    /// </summary>
    public const int MinWidth = 400;

    /// <summary>
    /// Minimum restored height. A persisted height below this is clamped up so a
    /// window cannot be restored too short to use.
    /// </summary>
    public const int MinHeight = 300;

    /// <summary>
    /// Parses a persisted placement string ("x,y,width,height") into a
    /// placement, applying <see cref="MinWidth"/>/<see cref="MinHeight"/> clamping
    /// to the restored size. Returns null when the string is missing or malformed.
    /// </summary>
    public static WindowPlacement? TryParse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        var parts = raw.Split(Separator);
        if (parts.Length != 4)
            return null;

        if (int.TryParse(parts[0], out int x) &&
            int.TryParse(parts[1], out int y) &&
            int.TryParse(parts[2], out int width) &&
            int.TryParse(parts[3], out int height))
        {
            return new WindowPlacement(
                x,
                y,
                Math.Max(width, MinWidth),
                Math.Max(height, MinHeight));
        }

        return null;
    }

    /// <summary>
    /// Serializes this placement to the persisted "x,y,width,height" string
    /// format, the inverse of <see cref="TryParse"/>.
    /// </summary>
    public string Serialize() =>
        $"{X}{Separator}{Y}{Separator}{Width}{Separator}{Height}";
}
