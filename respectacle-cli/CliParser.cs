// CliParser.cs — Parses, validates, and resolves CLI arguments into CliOptions.
//
// This parser is deterministic and does NOT call native capture or WinUI code.
// It produces either a valid CliOptions (ready for workflow consumption),
// a help request, or a parse error with a specific exit code and message.

using System;
using System.IO;
using System.Text;
using Respectacle.Capture;

namespace Respectacle.Cli;

/// <summary>
/// Result of parsing CLI arguments. Exactly one field will be non-null.
/// </summary>
public sealed class CliParseResult
{
    /// <summary>Parsed successfully — options are ready for workflow.</summary>
    public CliOptions? Options { get; }

    /// <summary>User requested --help.</summary>
    public string? HelpText { get; }

    /// <summary>Parse/validation error with exit code and message.</summary>
    public CliExitCode? ErrorCode { get; }

    /// <summary>Human-readable error message (null unless ErrorCode is set).</summary>
    public string? ErrorMessage { get; }

    private CliParseResult(CliOptions? options, string? helpText,
        CliExitCode? errorCode, string? errorMessage)
    {
        Options = options;
        HelpText = helpText;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public static CliParseResult Ok(CliOptions options) =>
        new(options, null, null, null);

    public static CliParseResult Help(string helpText) =>
        new(null, helpText, null, null);

    public static CliParseResult Error(CliExitCode code, string message) =>
        new(null, null, code, message);
}

/// <summary>
/// Parses command-line arguments into validated CliOptions.
/// Handles: --help, mode selection, --output, --filename, --verbose.
/// Does NOT perform any capture or filesystem I/O (path resolution is string-only).
/// </summary>
public static class CliParser
{
    /// <summary>
    /// Parses the given arguments and returns a CliParseResult.
    /// </summary>
    public static CliParseResult Parse(string[] args)
    {
        if (args is null || args.Length == 0)
            return CliParseResult.Error(CliExitCode.InvalidArguments,
                "No arguments provided. Use --help for usage information.");

        // Check for help first (can appear alone or with other args ignored)
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
                return CliParseResult.Help(BuildHelpText());
        }

        // Collect parsed state
        CliCaptureMode? mode = null;
        int? monitorIndex = null;
        CliRegion? region = null;
        string? outputArg = null;
        string? filenameArg = null;
        bool verbose = false;

        // Track which mode flag was seen for error reporting
        string? modeFlag = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "--full":
                    if (mode is not null)
                        return MultipleModesError(modeFlag!, "--full");
                    mode = CliCaptureMode.Full;
                    modeFlag = "--full";
                    break;

                case "--monitor":
                    if (mode is not null)
                        return MultipleModesError(modeFlag!, "--monitor");
                    mode = CliCaptureMode.Monitor;
                    modeFlag = "--monitor";
                    // Optional index — if next arg exists and looks like a value (not a known flag)
                    if (i + 1 < args.Length && IsValue(args[i + 1]))
                    {
                        i++;
                        if (!int.TryParse(args[i], out int mi) || mi < 0)
                            return CliParseResult.Error(CliExitCode.InvalidArguments,
                                $"Monitor index must be a non-negative integer, got '{args[i]}'.");
                        monitorIndex = mi;
                    }
                    else
                    {
                        // --monitor without index = current/default monitor
                        monitorIndex = null;
                    }
                    break;

                case "--window-active":
                    if (mode is not null)
                        return MultipleModesError(modeFlag!, "--window-active");
                    mode = CliCaptureMode.WindowActive;
                    modeFlag = "--window-active";
                    break;

                case "--window-cursor":
                    if (mode is not null)
                        return MultipleModesError(modeFlag!, "--window-cursor");
                    mode = CliCaptureMode.WindowCursor;
                    modeFlag = "--window-cursor";
                    break;

                case "--region":
                    if (mode is not null)
                        return MultipleModesError(modeFlag!, "--region");
                    mode = CliCaptureMode.Region;
                    modeFlag = "--region";
                    // Region value is always required and starts with a digit, minus, or is a comma-separated tuple
                    if (i + 1 >= args.Length || IsKnownFlag(args[i + 1]))
                        return CliParseResult.Error(CliExitCode.InvalidArguments,
                            "--region requires a value in the form x,y,width,height.");
                    i++;
                    var (parsedRegion, regionError) = CliRegion.Parse(args[i]);
                    if (regionError is not null)
                        return CliParseResult.Error(CliExitCode.InvalidArguments, regionError);
                    region = parsedRegion;
                    break;

                case "--output":
                    if (i + 1 >= args.Length)
                        return CliParseResult.Error(CliExitCode.InvalidArguments,
                            "--output requires a path value.");
                    i++;
                    outputArg = args[i];
                    if (string.IsNullOrWhiteSpace(outputArg))
                        return CliParseResult.Error(CliExitCode.InvalidArguments,
                            "--output path must not be empty or whitespace.");
                    break;

                case "--filename":
                    if (i + 1 >= args.Length)
                        return CliParseResult.Error(CliExitCode.InvalidArguments,
                            "--filename requires a template value.");
                    i++;
                    filenameArg = args[i];
                    if (string.IsNullOrWhiteSpace(filenameArg))
                        return CliParseResult.Error(CliExitCode.InvalidArguments,
                            "--filename template must not be empty or whitespace.");
                    break;

                case "--verbose":
                case "-v":
                    verbose = true;
                    break;

                default:
                    return CliParseResult.Error(CliExitCode.InvalidArguments,
                        $"Unknown option: '{arg}'. Use --help for usage information.");
            }
        }

        // Validate: exactly one mode required
        if (mode is null)
            return CliParseResult.Error(CliExitCode.InvalidArguments,
                "No capture mode specified. Use one of: --full, --monitor, --window-active, --window-cursor, --region.");

        // Validate: monitor mode with explicit index requires MonitorIndex
        // (already handled in parsing — mode=Monitor with null index is "current monitor")

        // Validate: region requires region data
        if (mode == CliCaptureMode.Region && region is null)
            return CliParseResult.Error(CliExitCode.InvalidArguments,
                "Region bounds could not be parsed."); // Should not reach here, but defensive

        // Resolve output path
        string outputPath = ResolveOutputPath(outputArg, filenameArg);

        return CliParseResult.Ok(new CliOptions
        {
            Mode = mode.Value,
            MonitorIndex = monitorIndex,
            Region = region,
            OutputPath = outputPath,
            Verbose = verbose,
        });
    }

    /// <summary>
    /// Resolves the output file path from --output and --filename arguments.
    /// Does NOT perform any filesystem I/O — only string manipulation.
    /// </summary>
    /// <param name="outputArg">Value from --output (path or directory).</param>
    /// <param name="filenameArg">Value from --filename (template).</param>
    /// <returns>Fully resolved file path with .png extension.</returns>
    internal static string ResolveOutputPath(string? outputArg, string? filenameArg)
    {
        // Determine the filename component
        string filename;
        if (!string.IsNullOrWhiteSpace(filenameArg))
        {
            filename = ExportFilenameTemplate.Expand(
                filenameArg, DateTime.Now, sequenceNumber: 1);
            filename = ExportFilenameTemplate.SanitizeForFileName(filename);
        }
        else
        {
            filename = ExportFilenameTemplate.Expand(
                ExportDefaults.DefaultFilenameTemplate, DateTime.Now, sequenceNumber: 1);
            filename = ExportFilenameTemplate.SanitizeForFileName(filename);
        }

        // Ensure .png extension
        if (!filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            filename += ".png";

        // Determine the directory / full path
        if (string.IsNullOrWhiteSpace(outputArg))
        {
            // No --output: use default save directory
            return Path.Combine(ExportDefaults.DefaultSaveDirectory, filename);
        }

        // If --output ends with .png, treat as full file path
        if (outputArg.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return outputArg;

        // Otherwise treat --output as a directory
        return Path.Combine(outputArg, filename);
    }

    private static CliParseResult MultipleModesError(string first, string second) =>
        CliParseResult.Error(CliExitCode.InvalidArguments,
            $"Multiple capture modes specified: '{first}' and '{second}'. Exactly one mode is required.");

    /// <summary>
    /// Determines whether an argument is a value (not a flag).
    /// Flags start with '-' and are not negative numbers.
    /// This handles the case where negative numbers like "-1" should be treated as values.
    /// </summary>
    private static bool IsValue(string arg)
    {
        if (!arg.StartsWith('-'))
            return true;
        // Could be a negative number
        if (arg.Length > 1 && char.IsDigit(arg[1]))
            return true;
        return false;
    }

    /// <summary>
    /// Determines whether an argument is a known CLI flag (not a value).
    /// Returns true for --flag or -x style args, false for negative numbers or plain values.
    /// </summary>
    private static bool IsKnownFlag(string arg)
    {
        if (!arg.StartsWith('-'))
            return false;
        // Negative number (e.g., "-1")
        if (arg.Length > 1 && char.IsDigit(arg[1]))
            return false;
        return true;
    }

    /// <summary>
    /// Builds the full help/usage text including examples and exit codes.
    /// </summary>
    public static string BuildHelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Respectacle CLI — Headless screen capture for Windows");
        sb.AppendLine();
        sb.AppendLine("Usage: respectacle-cli <mode> [options]");
        sb.AppendLine();
        sb.AppendLine("Capture Modes (exactly one required):");
        sb.AppendLine("  --full                         Capture all monitors (full virtual desktop).");
        sb.AppendLine("  --monitor [index]              Capture a monitor by 0-based index.");
        sb.AppendLine("                                 Without index, captures the current monitor.");
        sb.AppendLine("  --window-active                Capture the currently active (foreground) window.");
        sb.AppendLine("  --window-cursor                Capture the top-level window under the cursor.");
        sb.AppendLine("  --region <x,y,width,height>    Capture a rectangular region of the virtual desktop.");
        sb.AppendLine();
        sb.AppendLine("Output Options:");
        sb.AppendLine("  --output <path>                Output file path or directory.");
        sb.AppendLine("                                 If path ends in .png, treated as full file path.");
        sb.AppendLine("                                 Otherwise treated as a directory.");
        sb.AppendLine("                                 Default: %USERPROFILE%\\Pictures\\Respectacle\\");
        sb.AppendLine("  --filename <template>          Filename template with placeholders:");
        sb.AppendLine("                                 <yyyy> <MM> <dd> <hh> <mm> <ss> <#>");
        sb.AppendLine("                                 Default: Respectacle_<yyyy>-<MM>-<dd>_<hh><mm><ss>.png");
        sb.AppendLine();
        sb.AppendLine("General Options:");
        sb.AppendLine("  --verbose, -v                  Print detailed diagnostics (timings, dimensions).");
        sb.AppendLine("  --help, -h                     Show this help message.");
        sb.AppendLine();
        sb.AppendLine("Examples:");
        sb.AppendLine("  respectacle-cli --full");
        sb.AppendLine("      Capture all monitors, save to default directory with timestamped name.");
        sb.AppendLine();
        sb.AppendLine("  respectacle-cli --monitor 1 --output C:\\captures");
        sb.AppendLine("      Capture second monitor, save to C:\\captures\\ with default filename.");
        sb.AppendLine();
        sb.AppendLine("  respectacle-cli --window-active --output screenshot.png");
        sb.AppendLine("      Capture active window, save as screenshot.png in current directory.");
        sb.AppendLine();
        sb.AppendLine("  respectacle-cli --region 100,200,800,600 --output C:\\out --verbose");
        sb.AppendLine("      Capture 800x600 region at (100,200) with verbose diagnostics.");
        sb.AppendLine();
        sb.AppendLine("  respectacle-cli --monitor --filename my_<yyyy><MM><dd>.png");
        sb.AppendLine("      Capture current monitor with custom filename template.");
        sb.AppendLine();
        sb.AppendLine("Exit Codes:");
        sb.AppendLine("  0  Success — capture completed and PNG written.");
        sb.AppendLine("  1  Invalid arguments — bad mode, malformed values, missing required options.");
        sb.AppendLine("  2  Capture failed — native capture returned non-OK status.");
        sb.AppendLine("  3  Export failed — filesystem write or PNG encoding error.");
        sb.AppendLine("  4  Internal error — unexpected exception.");
        sb.AppendLine();
        sb.AppendLine("Diagnostics:");
        sb.AppendLine("  All output is printed as key=value pairs to stdout.");
        sb.AppendLine("  Errors are printed to stderr with the offending option identified.");
        sb.AppendLine("  Use --verbose for capture/bitmap/export/total timing breakdown.");

        return sb.ToString();
    }
}
