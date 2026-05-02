// ExportFilenameTemplate.cs — Expands filename templates with placeholders
// for date/time, title, and auto-incrementing sequence numbers.

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Respectacle.Capture;

/// <summary>
/// Named constants for default export configuration, usable by both UI and CLI.
/// </summary>
public static class ExportDefaults
{
    /// <summary>
    /// Default filename template supporting date/time and sequence placeholders.
    /// </summary>
    public const string DefaultFilenameTemplate = "Respectacle_<yyyy>-<MM>-<dd>_<hh><mm><ss>";

    /// <summary>
    /// Default save directory name (under user's Pictures folder).
    /// </summary>
    public const string DefaultFolderName = "Respectacle";

    /// <summary>
    /// Returns the full path to the default save directory:
    /// %USERPROFILE%\Pictures\Respectacle
    /// </summary>
    public static string DefaultSaveDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), DefaultFolderName);

    /// <summary>
    /// Default file extension (without leading dot).
    /// </summary>
    public const string DefaultExtension = "png";
}

/// <summary>
/// Expands filename templates containing placeholders:
///   &lt;yyyy&gt; 4-digit year
///   &lt;yy&gt;   2-digit year
///   &lt;MM&gt;   2-digit month
///   &lt;dd&gt;   2-digit day
///   &lt;hh&gt;   2-digit hour (24h)
///   &lt;mm&gt;   2-digit minute
///   &lt;ss&gt;   2-digit second
///   &lt;title&gt; sanitized window/capture title
///   &lt;#&gt;    auto-incrementing sequence number (4-digit, zero-padded)
/// Unknown placeholders are preserved literally unless they would create invalid path characters.
/// </summary>
public static class ExportFilenameTemplate
{
    // Characters invalid in Windows file names
    private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars());

    /// <summary>
    /// Expands a filename template using the given timestamp, optional title,
    /// and optional sequence number.
    /// </summary>
    /// <param name="template">Filename template with placeholders.</param>
    /// <param name="timestamp">Timestamp for date/time placeholders.</param>
    /// <param name="title">Optional title for the &lt;title&gt; placeholder.</param>
    /// <param name="sequenceNumber">Optional sequence number for &lt;#&gt; placeholder.</param>
    /// <returns>Expanded filename (not a full path — no directory component).</returns>
    /// <exception cref="ArgumentNullException">template is null.</exception>
    /// <exception cref="ArgumentException">template is empty or whitespace.</exception>
    public static string Expand(string template, DateTime timestamp,
        string? title = null, int? sequenceNumber = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(template))
            throw new ArgumentException("Template must not be empty or whitespace.", nameof(template));

        var sb = new StringBuilder(template.Length + 16);

        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '<')
            {
                int close = template.IndexOf('>', i + 1);
                if (close < 0)
                {
                    // No closing '>' — append the '<' literally and continue
                    sb.Append(template[i]);
                    i++;
                    continue;
                }

                string placeholder = template.Substring(i + 1, close - i - 1);
                string? expanded = ExpandPlaceholder(placeholder, timestamp, title, sequenceNumber);

                if (expanded != null)
                {
                    sb.Append(expanded);
                }
                else
                {
                    // Unknown placeholder — preserve literally but sanitize for path safety
                    string literal = template[i..(close + 1)];
                    sb.Append(SanitizeForFileName(literal));
                }

                i = close + 1;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }

        // Final sanitization pass on the whole result
        return SanitizeForFileName(sb.ToString());
    }

    /// <summary>
    /// Generates the full export path using the provided settings for directory and template.
    /// Falls back to <see cref="ExportDefaults"/> when settings properties are null/empty.
    /// Creates the target directory if it doesn't exist.
    /// </summary>
    /// <param name="settings">Application settings providing save location and filename template.</param>
    /// <param name="timestamp">Timestamp for date/time placeholders.</param>
    /// <param name="title">Optional capture title.</param>
    /// <param name="sequenceNumber">Optional sequence number.</param>
    /// <returns>Full path to the export file (collision NOT resolved — call <see cref="ResolveCollision"/> separately if needed).</returns>
    public static string GetExportPath(AppSettings settings, DateTime timestamp,
        string? title = null, int? sequenceNumber = null)
    {
        string template = settings.EffectiveFilenameTemplate;
        string filename = Expand(template, timestamp, title, sequenceNumber);
        string sanitized = SanitizeForFileName(filename);
        if (!sanitized.EndsWith($".{ExportDefaults.DefaultExtension}", StringComparison.OrdinalIgnoreCase))
            sanitized += $".{ExportDefaults.DefaultExtension}";

        string directory = settings.EffectiveSaveLocation;
        Directory.CreateDirectory(directory);

        return Path.Combine(directory, sanitized);
    }

    /// <summary>
    /// Generates the full default export path by expanding the default template,
    /// appending sequence number if needed, and combining with the default directory.
    /// Creates the default directory if it doesn't exist.
    /// </summary>
    /// <param name="timestamp">Timestamp for date/time placeholders.</param>
    /// <param name="title">Optional capture title.</param>
    /// <param name="sequenceNumber">Optional sequence number.</param>
    /// <returns>Full path to the export file.</returns>
    public static string GetDefaultExportPath(DateTime timestamp,
        string? title = null, int? sequenceNumber = null)
    {
        return GetExportPath(AppSettings.WithDefaults(), timestamp, title, sequenceNumber);
    }

    /// <summary>
    /// Resolves a collision by incrementing the sequence number or appending one.
    /// </summary>
    /// <param name="basePath">The desired file path.</param>
    /// <param name="maxAttempts">Maximum sequence numbers to try.</param>
    /// <returns>A path that does not currently exist, or the original if no collision.</returns>
    public static string ResolveCollision(string basePath, int maxAttempts = 9999)
    {
        if (!File.Exists(basePath))
            return basePath;

        string dir = Path.GetDirectoryName(basePath)!;
        string nameNoExt = Path.GetFileNameWithoutExtension(basePath);
        string ext = Path.GetExtension(basePath);

        for (int seq = 2; seq <= maxAttempts; seq++)
        {
            string candidate = Path.Combine(dir, $"{nameNoExt}_{seq:D4}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        // Fallback: return original (caller handles the overwrite/exception)
        return basePath;
    }

    /// <summary>
    /// Sanitizes a string for use as a Windows file name by replacing
    /// invalid characters with underscores. Also strips leading/trailing dots and spaces.
    /// </summary>
    public static string SanitizeForFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "untitled";

        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(InvalidFileNameChars.Contains(c) ? '_' : c);
        }

        string result = sb.ToString();

        // Strip leading/trailing dots, spaces, and underscores (Windows doesn't allow these in certain positions)
        result = result.Trim('.', ' ', '_');

        return string.IsNullOrEmpty(result) ? "untitled" : result;
    }

    /// <summary>
    /// Sanitizes a title string for safe embedding in a filename.
    /// Removes path separators, colons, and other characters that are
    /// invalid in Windows file names. Also prevents path traversal.
    /// </summary>
    public static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        // Prevent path traversal
        string sanitized = title.Replace("..", "").Replace("/", "").Replace("\\", "");

        // Replace invalid filename characters with underscores
        var sb = new StringBuilder(sanitized.Length);
        foreach (char c in sanitized)
        {
            if (InvalidFileNameChars.Contains(c) || c == ':' || c == '*')
                sb.Append('_');
            else
                sb.Append(c);
        }

        // Collapse multiple underscores, trim
        string result = Regex.Replace(sb.ToString(), "_{2,}", "_").Trim('_', ' ');

        return result;
    }

    private static string? ExpandPlaceholder(string placeholder, DateTime timestamp,
        string? title, int? sequenceNumber)
    {
        return placeholder switch
        {
            "yyyy" => timestamp.ToString("yyyy"),
            "yy" => timestamp.ToString("yy"),
            "MM" => timestamp.ToString("MM"),
            "dd" => timestamp.ToString("dd"),
            "hh" => timestamp.ToString("HH"), // 24-hour
            "mm" => timestamp.ToString("mm"),
            "ss" => timestamp.ToString("ss"),
            "title" => SanitizeTitle(title),
            "#" when sequenceNumber.HasValue => sequenceNumber.Value.ToString("D4"),
            "#" => "0001", // Default sequence when not provided
            _ => null // Unknown placeholder
        };
    }
}
