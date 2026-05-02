// CliOptions.cs — Strongly-typed representation of parsed CLI arguments.

using System;

namespace Respectacle.Cli;

/// <summary>
/// Represents a fully resolved, validated set of CLI options.
/// Constructed by <see cref="CliParser"/> after validation passes.
/// All fields are non-null and validated — consumers never see raw strings.
/// </summary>
public sealed class CliOptions
{
    /// <summary>The capture mode selected by the user.</summary>
    public required CliCaptureMode Mode { get; init; }

    /// <summary>
    /// Monitor index for <see cref="CliCaptureMode.Monitor"/> mode.
    /// Null for all other modes.
    /// </summary>
    public int? MonitorIndex { get; init; }

    /// <summary>
    /// Region bounds for <see cref="CliCaptureMode.Region"/> mode.
    /// Null for all other modes.
    /// </summary>
    public CliRegion? Region { get; init; }

    /// <summary>
    /// Fully resolved output file path (directory + filename + .png extension).
    /// This is the final path where the PNG will be written.
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// Whether verbose diagnostics are enabled.
    /// </summary>
    public bool Verbose { get; init; }
}

/// <summary>
/// Represents a rectangular region of the virtual desktop.
/// All values are in pixels; width and height must be positive.
/// </summary>
public sealed class CliRegion
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public CliRegion(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Parses a region string in "x,y,width,height" format.
    /// Returns null with an error message if parsing fails.
    /// </summary>
    public static (CliRegion? region, string? error) Parse(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != 4)
            return (null, $"Region requires exactly 4 comma-separated values (x,y,width,height), got {parts.Length}.");

        if (!int.TryParse(parts[0], out int x))
            return (null, $"Region x must be an integer, got '{parts[0]}'.");

        if (!int.TryParse(parts[1], out int y))
            return (null, $"Region y must be an integer, got '{parts[1]}'.");

        if (!int.TryParse(parts[2], out int width))
            return (null, $"Region width must be an integer, got '{parts[2]}'.");

        if (!int.TryParse(parts[3], out int height))
            return (null, $"Region height must be an integer, got '{parts[3]}'.");

        if (width <= 0)
            return (null, $"Region width must be positive, got {width}.");

        if (height <= 0)
            return (null, $"Region height must be positive, got {height}.");

        return (new CliRegion(x, y, width, height), null);
    }
}
