// ExportSettings.cs — Persisted export format selection (issue #45).
//
// The export format (PNG or JPEG) and the JPEG quality (inclusive 0–100) are
// persisted Configuration. PNG remains the default; the Filename Template
// stays format-agnostic — the output extension is derived from the format at
// export time. The settings carry their own bounds constants so the UI slider,
// AppSettings.Validate, and the encoder share one source of truth. Quality is
// reconciled on read through Normalized, so a hand-edited settings file with
// an out-of-range value degrades to the default instead of blocking startup.

using System;

namespace captcho.Capture;

/// <summary>
/// The image format Captcho exports a Frame as. The extension (without a
/// leading dot) is derived from the format — never embedded in the Filename
/// Template — so the written extension always matches the encoded bytes.
/// </summary>
public enum ExportImageFormat
{
    /// <summary>
    /// Lossless PNG; preserves the Frame's alpha channel. The default.
    /// </summary>
    Png,

    /// <summary>
    /// Lossy JPEG; quality configurable 0–100. JPEG has no alpha channel —
    /// transparent pixels are flattened onto white by the encoder.
    /// </summary>
    Jpeg,
}

/// <summary>
/// Persisted export format and JPEG quality (issue #45). Quality applies only
/// to <see cref="ExportImageFormat.Jpeg"/>; the PNG encoder ignores it.
/// </summary>
public sealed record ExportSettings(
    ExportImageFormat Format,
    int JpegQuality)
{
    /// <summary>Inclusive lower bound for <see cref="JpegQuality"/>.</summary>
    public const int MinimumJpegQuality = 0;

    /// <summary>Inclusive upper bound for <see cref="JpegQuality"/>.</summary>
    public const int MaximumJpegQuality = 100;

    /// <summary>Default JPEG quality when the setting is absent or invalid.</summary>
    public const int DefaultJpegQuality = 90;

    /// <summary>Returns the PNG defaults — the format Captcho ships with.</summary>
    public static ExportSettings WithDefaults() => new(ExportImageFormat.Png, DefaultJpegQuality);

    /// <summary>
    /// Reconciles impossible values: an undefined format falls back to PNG and
    /// an out-of-range quality falls back to the default, so runtime consumers
    /// never observe state <see cref="AppSettings.Validate"/> would reject.
    /// </summary>
    public ExportSettings Normalized() => new(
        Enum.IsDefined(Format) ? Format : ExportImageFormat.Png,
        JpegQuality is >= MinimumJpegQuality and <= MaximumJpegQuality
            ? JpegQuality
            : DefaultJpegQuality);
}

/// <summary>
/// Extension methods over <see cref="ExportImageFormat"/>.
/// </summary>
public static class ExportImageFormatExtensions
{
    /// <summary>
    /// The file extension (without a leading dot) derived from the format:
    /// <c>png</c> for PNG, <c>jpg</c> for JPEG.
    /// </summary>
    public static string Extension(this ExportImageFormat format) => format switch
    {
        ExportImageFormat.Png => "png",
        ExportImageFormat.Jpeg => "jpg",
        _ => "png",
    };
}
