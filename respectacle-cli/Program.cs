// Program.cs — Respectacle CLI entry point.
//
// Delegates to CliParser for argument parsing, then either prints help,
// reports parse errors, or executes the capture workflow via CliCaptureService.
// Structured diagnostics are written to stdout; errors to stderr.
// The CLI references respectacle-capture only, never respectacle-ui.

using System;
using Respectacle.Capture;
using Respectacle.Cli;

namespace Respectacle.Cli;

public class Program
{
    public static int Main(string[] args)
    {
        var result = CliParser.Parse(args);

        // ── Help ─────────────────────────────────────────────────────────
        if (result.HelpText is not null)
        {
            Console.WriteLine(result.HelpText);
            return (int)CliExitCode.Success;
        }

        // ── Parse error ──────────────────────────────────────────────────
        if (result.ErrorCode is not null)
        {
            Console.Error.WriteLine(result.ErrorMessage);
            return (int)result.ErrorCode.Value;
        }

        // ── Capture workflow ─────────────────────────────────────────────
        if (result.Options is not null)
        {
            var adapter = new NativeCliCaptureAdapter();
            var service = new CliCaptureService(adapter);
            var (exitCode, diagnostics) = service.Execute(result.Options);

            // Write structured diagnostics to stdout
            string output = CliDiagnostics.Format(diagnostics);
            Console.WriteLine(output);

            // Write error details to stderr on failure
            if (exitCode != CliExitCode.Success)
            {
                string errorLine = CliDiagnostics.FormatError(diagnostics);
                if (!string.IsNullOrEmpty(errorLine))
                    Console.Error.WriteLine(errorLine);
            }

            return (int)exitCode;
        }

        // ── Should not reach here ────────────────────────────────────────
        Console.Error.WriteLine("Internal parser error: result has no outcome.");
        return (int)CliExitCode.InternalError;
    }

    /// <summary>
    /// Internal entry point for testing — accepts a pre-built adapter.
    /// Not called from production Main (which creates NativeCliCaptureAdapter).
    /// </summary>
    internal static int ExecuteWithAdapter(string[] args, ICliCaptureAdapter adapter)
    {
        var result = CliParser.Parse(args);

        if (result.HelpText is not null)
        {
            Console.WriteLine(result.HelpText);
            return (int)CliExitCode.Success;
        }

        if (result.ErrorCode is not null)
        {
            Console.Error.WriteLine(result.ErrorMessage);
            return (int)result.ErrorCode.Value;
        }

        if (result.Options is not null)
        {
            var service = new CliCaptureService(adapter);
            var (exitCode, diagnostics) = service.Execute(result.Options);

            string output = CliDiagnostics.Format(diagnostics);
            Console.WriteLine(output);

            if (exitCode != CliExitCode.Success)
            {
                string errorLine = CliDiagnostics.FormatError(diagnostics);
                if (!string.IsNullOrEmpty(errorLine))
                    Console.Error.WriteLine(errorLine);
            }

            return (int)exitCode;
        }

        Console.Error.WriteLine("Internal parser error: result has no outcome.");
        return (int)CliExitCode.InternalError;
    }
}
