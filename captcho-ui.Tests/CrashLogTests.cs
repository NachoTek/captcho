// CrashLogTests.cs — Headless tests for global crash logging (issue #81).
//
// CrashLogWriter is the seam: an append-only log whose entries carry the UTC
// timestamp, app version, source, exception type/message/HResult, and the
// full ToString detail (inner exceptions included), rotating to a single
// crash.log.old at the size cap, and never throwing regardless of filesystem
// state. The CrashLog facade test pins Initialize-once and location-once
// semantics; the delivery test proves an unobserved Task.Run fault reaches
// TaskScheduler.UnobservedTaskException and lands in the log.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

internal static class CrashLogTestHelpers
{
    internal static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = 0; (i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0; i += value.Length)
        {
            count++;
        }
        return count;
    }

    internal static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class CrashLogWriterTests : IDisposable
{
    private readonly string _tempDirectory;

    public CrashLogWriterTests()
    {
        _tempDirectory = CrashLogTestHelpers.CreateTempDirectory("captcho-crashlog-tests-");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // Test cleanup is best-effort.
        }
    }

    private string LogPath => Path.Combine(_tempDirectory, "crash.log");

    private static Exception CaptureException()
    {
        try
        {
            throw new InvalidOperationException("outer failure");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Write_RecordsTimestampVersionSourceTypeMessageHResultAndDetail()
    {
        var writer = new CrashLogWriter(_tempDirectory, "9.9.9", maxLogBytes: 1_000_000);

        writer.Write("UnhandledException", CaptureException());

        var log = File.ReadAllText(LogPath);
        Assert.Matches(new Regex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z"), log);
        Assert.Contains("captcho 9.9.9", log);
        Assert.Contains("Source: UnhandledException", log);
        Assert.Contains("Type: System.InvalidOperationException", log);
        Assert.Contains("Message: outer failure", log);
        Assert.Contains("HResult: 0x", log);
        // Full ToString detail (stack trace and inner chain) rides along.
        Assert.Contains("System.InvalidOperationException: outer failure", log);
    }

    [Fact]
    public void Write_IncludesInnerExceptionChain()
    {
        var writer = new CrashLogWriter(_tempDirectory, "9.9.9", maxLogBytes: 1_000_000);
        var exception = new InvalidOperationException(
            "outer failure",
            new FormatException("inner cause"));

        writer.Write("UnhandledException", exception);

        var log = File.ReadAllText(LogPath);
        Assert.Contains("System.FormatException: inner cause", log);
        Assert.Contains(" ---> ", log);
    }

    [Fact]
    public void Write_AppendsEntriesSequentially()
    {
        var writer = new CrashLogWriter(_tempDirectory, "9.9.9", maxLogBytes: 1_000_000);

        writer.Write("First", CaptureException());
        writer.Write("Second", CaptureException());

        var log = File.ReadAllText(LogPath);
        var first = log.IndexOf("Source: First", StringComparison.Ordinal);
        var second = log.IndexOf("Source: Second", StringComparison.Ordinal);
        Assert.True(first >= 0, "first entry missing");
        Assert.True(second >= 0, "second entry missing");
        Assert.True(first < second, "entries are out of order");
    }

    [Fact]
    public void Write_WithoutException_RecordsPlaceholderDetail()
    {
        var writer = new CrashLogWriter(_tempDirectory, "9.9.9", maxLogBytes: 1_000_000);

        writer.Write("Manual", null);

        var log = File.ReadAllText(LogPath);
        Assert.Contains("Source: Manual", log);
        Assert.Contains("(no exception detail available)", log);
    }

    [Fact]
    public void Write_WithNote_RecordsNoteBesideDetail()
    {
        var writer = new CrashLogWriter(_tempDirectory, "9.9.9", maxLogBytes: 1_000_000);

        writer.Write("UnhandledException", CaptureException(), note: "XAML event message");

        var log = File.ReadAllText(LogPath);
        Assert.Contains("Note: XAML event message", log);
        Assert.Contains("System.InvalidOperationException: outer failure", log);
    }

    [Fact]
    public void Write_BlockedLogDirectory_NeverThrows()
    {
        // A file occupies the name where the log directory should be — every
        // write fails inside the logger, and none of that may escape.
        var blocker = Path.Combine(_tempDirectory, "blocker");
        File.WriteAllText(blocker, "occupied");
        var writer = new CrashLogWriter(Path.Combine(blocker, "logs"), "1.0.0", maxLogBytes: 1_000_000);

        var exception = Record.Exception(() =>
        {
            writer.Write("UnhandledException", CaptureException());
            writer.Write("UnhandledException", CaptureException());
            writer.WriteStartupNotice();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void WriteStartupNotice_RecordsLogPathOnce()
    {
        var writer = new CrashLogWriter(_tempDirectory, "9.9.9", maxLogBytes: 1_000_000);

        writer.WriteStartupNotice();

        var log = File.ReadAllText(LogPath);
        Assert.Contains("Logging to: " + LogPath, log);
        Assert.Equal(1, CrashLogTestHelpers.CountOccurrences(log, "Logging to:"));
    }

    [Fact]
    public void Write_AfterSizeCap_RotatesToSingleOldFile()
    {
        // A cap smaller than any entry forces rotation on every follow-up
        // write, so the surviving files are deterministic: crash.log holds
        // only the newest entry, crash.log.old the one before it, and the
        // oldest entry is gone (no retention beyond one .old).
        var writer = new CrashLogWriter(_tempDirectory, "9.9.9", maxLogBytes: 10);

        writer.Write("First", CaptureException());
        writer.Write("Second", CaptureException());
        writer.Write("Third", CaptureException());

        var files = Directory.GetFiles(_tempDirectory);
        Assert.Equal(2, files.Length);
        var oldLog = File.ReadAllText(LogPath + ".old");
        var currentLog = File.ReadAllText(LogPath);
        Assert.Contains("Source: Second", oldLog);
        Assert.DoesNotContain("Source: First", oldLog);
        Assert.Contains("Source: Third", currentLog);
        Assert.DoesNotContain("Source: First", currentLog);
    }
}

    public sealed class CrashLogFacadeTests : IDisposable
    {
        private readonly string _firstDirectory;
        private readonly string _secondDirectory;

        public CrashLogFacadeTests()
        {
            var root = CrashLogTestHelpers.CreateTempDirectory("captcho-crashlog-facade-");
            _firstDirectory = Path.Combine(root, "first");
            _secondDirectory = Path.Combine(root, "second");
            Directory.CreateDirectory(_firstDirectory);
            Directory.CreateDirectory(_secondDirectory);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_firstDirectory, recursive: true);
                Directory.Delete(_secondDirectory, recursive: true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }

        [Fact]
        public void Initialize_OnceThenWrite_RoutesAllEntriesToFirstLog()
        {
            CrashLog.Initialize(_firstDirectory, maxLogBytes: 1_000_000);

            CrashLog.Write("Test", new InvalidOperationException("facade failure"));

            var firstLogPath = Path.Combine(_firstDirectory, "crash.log");
            Assert.True(File.Exists(firstLogPath), "log file was not created");
            var log = File.ReadAllText(firstLogPath);
            Assert.Contains("Logging to: " + firstLogPath, log);
            Assert.Equal(1, CrashLogTestHelpers.CountOccurrences(log, "Logging to:"));
            Assert.Contains("Source: Test", log);
            Assert.Contains("facade failure", log);

            // A second Initialize must not move the log or re-announce it.
            CrashLog.Initialize(_secondDirectory, maxLogBytes: 1_000_000);
            CrashLog.Write("Test2", new InvalidOperationException("still first"));

            log = File.ReadAllText(firstLogPath);
            Assert.Contains("Source: Test2", log);
            Assert.Equal(1, CrashLogTestHelpers.CountOccurrences(log, "Logging to:"));
            Assert.False(
                File.Exists(Path.Combine(_secondDirectory, "crash.log")),
                "second Initialize must not install a second log");
        }
    }

public sealed class UnobservedTaskExceptionDeliveryTests : IDisposable
{
    private readonly string _tempDirectory;

    public UnobservedTaskExceptionDeliveryTests()
    {
        _tempDirectory = CrashLogTestHelpers.CreateTempDirectory("captcho-crashlog-delivery-");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // Test cleanup is best-effort.
        }
    }

    [Fact]
    public async Task UnobservedTaskFault_IsDeliveredToHandler_AndLandsInLog()
    {
        var writer = new CrashLogWriter(_tempDirectory, "9.9.9", maxLogBytes: 1_000_000);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            writer.Write("UnobservedTaskException", e.Exception);
            delivered.TrySetResult();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            LeakFaultingTask();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!delivered.Task.IsCompleted && DateTime.UtcNow < deadline)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(50);
            }

            Assert.True(
                delivered.Task.IsCompleted,
                "TaskScheduler.UnobservedTaskException was not delivered within the timeout");
            var log = File.ReadAllText(Path.Combine(_tempDirectory, "crash.log"));
            Assert.Contains("Source: UnobservedTaskException", log);
            Assert.Contains("unobserved task fault", log);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeakFaultingTask()
    {
        _ = Task.Run(() => throw new InvalidOperationException("unobserved task fault"));
    }
}
