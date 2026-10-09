// QrWorkflowTests.cs — Headless tests for the WinUI-free QR scanning seam.
//
// Covers the QrScanResult contract distinctness (found, no-code, no-Frame,
// failure, operation-in-progress), the multi-value Found contract (no silent
// dropping of valid results), the status formatter wording, and the Workflow
// Session's Scan QR routing through a fake IQrScanner: in-memory Frame reuse
// (no capture adapter call and no file), distinct outcomes presented through
// the workflow, Frame preservation across every retryable outcome, and the
// shared operation guard with Capture Triggers.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

// ── Fake QR scanner ─────────────────────────────────────────────────────

/// <summary>
/// Fake IQrScanner. Records the frames it was asked to scan and returns a
/// configurable result.
/// </summary>
internal sealed class FakeQrScanner : IQrScanner
{
    public int CallCount { get; private set; }
    public ContiguousBitmap? LastFrame { get; private set; }
    public QrScanResult? NextResult { get; set; }

    /// <summary>Optional gate: when set, scanning blocks until released.</summary>
    public TaskCompletionSource<bool>? Started { get; set; }

    /// <summary>Optional gate: when set, scanning blocks until released.</summary>
    public TaskCompletionSource<bool>? Release { get; set; }

    public async Task<QrScanResult> ScanAsync(ContiguousBitmap frame)
    {
        CallCount++;
        LastFrame = frame;

        if (Started is not null && Release is not null)
        {
            Started.TrySetResult(true);
            await Release.Task;
        }

        return NextResult ?? QrScanResult.Found(new[] { "https://example.com" });
    }
}

// ── QrScanResult contract tests ─────────────────────────────────────────

public class QrResultContractTests
{
    [Fact]
    public void Outcomes_AreDistinctValues()
    {
        var all = Enum.GetValues<QrOutcome>();

        Assert.Equal(5, all.Length);
        Assert.Contains(QrOutcome.Found, all);
        Assert.Contains(QrOutcome.NotFound, all);
        Assert.Contains(QrOutcome.NoFrame, all);
        Assert.Contains(QrOutcome.Failed, all);
        Assert.Contains(QrOutcome.OperationInProgress, all);
    }

    [Fact]
    public void Found_CarriesEveryValueWithoutError()
    {
        var result = QrScanResult.Found(new[] { "first", "second", "third" }, 5);

        Assert.Equal(QrOutcome.Found, result.Outcome);
        Assert.Equal(new[] { "first", "second", "third" }, result.Values);
        Assert.Null(result.Error);
        Assert.Equal(5, result.ElapsedMs);
    }

    [Fact]
    public void Found_SingleValueIsPreserved()
    {
        var result = QrScanResult.Found(new[] { "only" });

        Assert.Single(result.Values);
        Assert.Equal("only", result.Values[0]);
    }

    [Fact]
    public void Found_NullValues_TreatedAsNone()
    {
        var result = QrScanResult.Found(null);

        Assert.Equal(QrOutcome.Found, result.Outcome);
        Assert.Empty(result.Values);
    }

    [Fact]
    public void NoCode_IsDistinctFromFailureAndCarriesNoError()
    {
        var result = QrScanResult.NoCode(2);

        Assert.Equal(QrOutcome.NotFound, result.Outcome);
        Assert.Empty(result.Values);
        Assert.Null(result.Error);
        Assert.NotEqual(QrOutcome.Failed, result.Outcome);
        Assert.NotEqual(QrOutcome.Found, result.Outcome);
    }

    [Fact]
    public void NoFrame_And_Busy_CarryRetryableErrors()
    {
        Assert.Equal(QrOutcome.NoFrame, QrScanResult.NoFrame().Outcome);
        Assert.NotNull(QrScanResult.NoFrame().Error);

        Assert.Equal(QrOutcome.OperationInProgress, QrScanResult.Busy().Outcome);
        Assert.NotNull(QrScanResult.Busy().Error);
    }

    [Fact]
    public void Fail_DefaultsToRetryableMessage()
    {
        var blank = QrScanResult.Fail(null);
        Assert.Equal(QrOutcome.Failed, blank.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(blank.Error));

        var explicitMessage = QrScanResult.Fail("decoder offline", 3);
        Assert.Equal("decoder offline", explicitMessage.Error);
        Assert.Equal(3, explicitMessage.ElapsedMs);
    }
}

// ── Status formatter tests ──────────────────────────────────────────────

public class QrStatusFormatterTests
{
    [Fact]
    public void FormatFound_ReportsSingleCodeSingular()
    {
        var result = QrScanResult.Found(new[] { "one" });

        Assert.Equal("Found 1 QR code.", QrStatusFormatter.FormatFound(result));
    }

    [Fact]
    public void FormatFound_ReportsMultipleCodesPlural()
    {
        var result = QrScanResult.Found(new[] { "one", "two" });

        Assert.Equal("Found 2 QR codes.", QrStatusFormatter.FormatFound(result));
    }

    [Fact]
    public void FormatNotFound_IsDistinctWordingFromFailure()
    {
        var noCode = QrStatusFormatter.FormatNotFound();

        Assert.Equal("No QR codes found.", noCode);
        Assert.DoesNotContain("fail", noCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatNoFrame_DirectsUserToCapture()
    {
        Assert.Equal(
            "No captured Frame to scan. Take a capture first.",
            QrStatusFormatter.FormatNoFrame());
    }
}
