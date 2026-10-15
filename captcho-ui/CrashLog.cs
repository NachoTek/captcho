// CrashLog.cs — Global crash logging (issue #81).
//
// A stowed-exception crash (0xc000027b, #80) leaves a WER record naming
// Microsoft.UI.Xaml.dll but no diagnostic trail: anything escaping an async
// void continuation, a fire-and-forget dispatch, or an unobserved background
// task dies with the process. CrashLog captures the underlying exception to
// disk before the process can die: Application.UnhandledException and
// TaskScheduler.UnobservedTaskException route every escape here at startup,
// and the writer records UTC timestamp, app version, source, exception
// type/message/HResult, and full ToString detail (inner exceptions included)
// to %LOCALAPPDATA%\captcho\logs\crash.log, rotating to a single crash.log.old
// at the size cap.
//
// Logging must never crash the crash handler: every write is wrapped in a
// top-level catch-all, and a broken filesystem state (invalid path, blocked
// directory, unwritable log) disables logging silently — the app keeps
// running.

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace captcho.UI;

/// <summary>
/// Append-only crash log writer. Entries are timestamped (UTC), tagged with
/// the app version and source, and carry the exception's type, message,
/// HResult, and full ToString detail — inner exceptions included. Rotation at
/// the size cap keeps at most two files: the current log and one .old.
/// Every member is guarded: nothing here may throw.
/// </summary>
internal sealed class CrashLogWriter
{
    internal const string LogFileName = "crash.log";

    private readonly string _directory;
    private readonly string _appVersion;
    private readonly long _maxLogBytes;
    private readonly object _gate = new();

    /// <summary>
    /// Creates a writer for <paramref name="directory"/>. The directory is
    /// created lazily on first write; construction never throws.
    /// </summary>
    public CrashLogWriter(string directory, string appVersion, long maxLogBytes)
    {
        _directory = directory;
        _appVersion = appVersion;
        _maxLogBytes = maxLogBytes;
    }

    /// <summary>Full path of the current log file.</summary>
    public string LogPath => Path.Combine(_directory, LogFileName);

    /// <summary>
    /// Appends one crash entry. Never throws: a write that cannot complete
    /// (missing directory, blocked path, IO error) is silently dropped.
    /// </summary>
    public void Write(string source, Exception? exception)
    {
        Write(ComposeEntry(source, exception, headline: null));
    }

    /// <summary>
    /// Appends the one-time startup notice naming the log location, so
    /// support can find the file. Never throws.
    /// </summary>
    public void WriteStartupNotice()
    {
        Write(ComposeEntry("Startup", exception: null, headline: $"Logging to: {LogPath}"));
    }

    private void Write(string entry)
    {
        try
        {
            lock (_gate)
            {
                EnsureDirectory();
                RotateIfNeeded(entry.Length);
                File.AppendAllText(LogPath, entry);
            }
        }
        catch
        {
            // The crash logger must never raise; a failed write is lost.
        }
    }

    private string ComposeEntry(string source, Exception? exception, string? headline)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var detail = exception?.ToString().Replace("\r\n", "\n").TrimEnd()
            ?? "(no exception detail available)";

        var lines = new StringBuilder();
        lines.Append(timestamp).Append(" captcho ").Append(_appVersion).Append('\n');
        lines.Append("Source: ").Append(source).Append('\n');
        if (headline is not null)
        {
            lines.Append(headline).Append('\n');
        }
        if (exception is not null)
        {
            lines.Append("Type: ").Append(exception.GetType().ToString()).Append('\n');
            lines.Append("Message: ").Append(exception.Message.Replace("\r\n", "\n")).Append('\n');
            lines.Append("HResult: 0x")
                .Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture))
                .Append('\n');
        }
        lines.Append("Detail:\n").Append(detail).Append('\n');
        lines.Append('\n');
        return lines.ToString();
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
    }

    private void RotateIfNeeded(long incomingBytes)
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length + incomingBytes <= _maxLogBytes)
        {
            return;
        }

        var oldPath = LogPath + ".old";
        try
        {
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
            File.Move(LogPath, oldPath);
        }
        catch (IOException)
        {
            // Rotation failed; the append may still succeed and exceed the
            // cap. Losing strict rotation is better than losing the entry.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Process-wide crash log facade. <see cref="Initialize"/> installs the log
/// once (first call wins) and announces its location; the handlers attached
/// at startup route Application.UnhandledException and
/// TaskScheduler.UnobservedTaskException here. All members never throw.
/// </summary>
public static class CrashLog
{
    private const long DefaultMaxLogBytes = 5 * 1024 * 1024; // 5 MB

    private static CrashLogWriter? _writer;
    private static bool _initialized;

    /// <summary>
    /// Installs the crash logger under %LOCALAPPDATA%\captcho\logs\ and
    /// writes the one-time "Logging to: &lt;path&gt;" notice. Only the first
    /// call takes effect. Never throws.
    /// </summary>
    public static void Initialize()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "captcho",
            "logs");
        Initialize(directory);
    }

    /// <summary>
    /// Installs the crash logger with an explicit log directory and size cap
    /// (test seam). Only the first call takes effect. Never throws.
    /// </summary>
    public static void Initialize(string directory, long maxLogBytes = DefaultMaxLogBytes)
    {
        try
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;
            _writer = new CrashLogWriter(directory, ResolveAppVersion(), maxLogBytes);
            _writer.WriteStartupNotice();
        }
        catch
        {
            // Installing logging must never crash the app.
        }
    }

    /// <summary>
    /// Appends a crash entry for <paramref name="exception"/> attributed to
    /// <paramref name="source"/>. Safe to call before Initialize and from
    /// any thread; never throws.
    /// </summary>
    public static void Write(string source, Exception? exception)
    {
        try
        {
            _writer?.Write(source, exception);
        }
        catch
        {
            // The crash logger must never raise.
        }
    }

    private static string ResolveAppVersion()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
