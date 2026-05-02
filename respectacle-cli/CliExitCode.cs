// CliExitCode.cs — Deterministic exit codes for the Respectacle CLI.
//
// Exit codes are designed so automation scripts can classify failure type:
//   0   = success
//   1   = invalid arguments (usage error)
//   2   = capture failure (native/graphics)
//   3   = export failure (filesystem/encoding)
//   4   = unexpected internal error

namespace Respectacle.Cli;

/// <summary>
/// Well-defined exit codes for the Respectacle CLI.
/// These are the only values returned from Main — all paths converge to one of these.
/// </summary>
public enum CliExitCode
{
    /// <summary>Capture succeeded and output was written.</summary>
    Success = 0,

    /// <summary>Invalid arguments: missing mode, multiple modes, malformed values, etc.</summary>
    InvalidArguments = 1,

    /// <summary>Capture failed: native status was non-OK, graphics pipeline error, etc.</summary>
    CaptureFailed = 2,

    /// <summary>Export failed: disk write error, encoding failure, path not accessible, etc.</summary>
    ExportFailed = 3,

    /// <summary>Unexpected internal error (unhandled exception, shouldn't happen).</summary>
    InternalError = 4,
}
