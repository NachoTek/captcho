// QrWorkflow.cs — WinUI-free QR scanning seam for post-capture decoding.
//
// The Workflow Session owns Scan QR as a post-capture workflow feature (not
// an Export): the session routes the in-memory Frame it already owns to a
// QR scanner adapter and surfaces distinct found, no-code, no-Frame, and
// failure outcomes so the WinUI layer can present each without a second
// Capture and without requiring a saved file. Found carries every decoded
// value — one or many — so valid results are never silently dropped. The
// engine seam is adapter-shaped on purpose, mirroring IOcrEngine: ZXing.Net
// is the production implementation here (research #15/#16 — pure C#,
// Apache-2.0, feeding it the contiguous BGRA Frame through an
// RGBLuminanceSource conversion), and any future engine slots in as another
// IQrScanner without touching the workflow or the UI.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Outcome of a QR scan attempt. Found and no-code are distinct successes
/// (no-code means the scanner ran and the Frame contains no QR code — not an
/// error); the remaining values are retryable conditions that preserve the
/// Frame.
/// </summary>
public enum QrOutcome
{
    /// <summary>
    /// The scanner decoded one or more QR codes. <see cref="QrScanResult.Values"/>
    /// carries every decoded value.
    /// </summary>
    Found,

    /// <summary>
    /// The scanner ran to completion but found no QR code in the Frame. Not
    /// an error — the Frame is preserved and the user can retry or deliver it.
    /// </summary>
    NotFound,

    /// <summary>
    /// No captured Frame is available to scan. The user must Capture first.
    /// </summary>
    NoFrame,

    /// <summary>
    /// The QR scanner failed (decode or conversion error). The Frame is
    /// preserved so the same Trigger can be retried or the Frame delivered.
    /// </summary>
    Failed,

    /// <summary>
    /// Another workflow operation was already in progress. No work was
    /// performed and the Frame is untouched.
    /// </summary>
    OperationInProgress,
}

/// <summary>
/// WinUI-free result of a QR scan attempt. Mirrors the never-throw contract
/// of <see cref="WorkflowResult{TImage}"/> and <see cref="OcrResult"/>:
/// expected failures surface through <see cref="QrOutcome"/> +
/// <see cref="Error"/>, never exceptions.
/// </summary>
public sealed class QrScanResult
{
    /// <summary>QR scan outcome.</summary>
    public QrOutcome Outcome { get; init; }

    /// <summary>
    /// Every decoded QR value, in decode order. Non-empty only when
    /// <see cref="QrOutcome.Found"/>; empty for every other outcome. A Frame
    /// with multiple QR codes carries all of them — valid results are never
    /// silently dropped.
    /// </summary>
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();

    /// <summary>
    /// User-visible error message. Null on Found and NotFound; set on every
    /// retryable condition.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Scan duration in milliseconds.</summary>
    public double ElapsedMs { get; init; }

    private QrScanResult(QrOutcome outcome, IReadOnlyList<string> values, string? error, double elapsedMs)
    {
        Outcome = outcome;
        Values = values;
        Error = error;
        ElapsedMs = elapsedMs;
    }

    /// <summary>Builds a Found result carrying every decoded value.</summary>
    public static QrScanResult Found(IEnumerable<string>? values, double elapsedMs = 0)
    {
        IReadOnlyList<string> list = values is null
            ? Array.Empty<string>()
            : new List<string>(values);
        return new QrScanResult(QrOutcome.Found, list, null, elapsedMs);
    }

    /// <summary>Builds a no-code result. Distinct from every failure.</summary>
    public static QrScanResult NoCode(double elapsedMs = 0) =>
        new(QrOutcome.NotFound, Array.Empty<string>(), null, elapsedMs);

    /// <summary>Builds the no-Frame result.</summary>
    public static QrScanResult NoFrame() =>
        new(QrOutcome.NoFrame, Array.Empty<string>(),
            "No captured Frame to scan. Take a capture first.", 0);

    /// <summary>Builds the operation-in-progress result.</summary>
    public static QrScanResult Busy() =>
        new(QrOutcome.OperationInProgress, Array.Empty<string>(),
            "A capture is already in progress.", 0);

    /// <summary>Builds a scanner-failure result.</summary>
    public static QrScanResult Fail(string? error, double elapsedMs = 0) =>
        new(QrOutcome.Failed, Array.Empty<string>(),
            string.IsNullOrWhiteSpace(error) ? "QR scanning failed. Try again." : error,
            elapsedMs);
}

/// <summary>
/// Narrow adapter over a QR decoding engine. Production implements this with
/// ZXing.Net (ZxingQrScanner). Implementations must not throw for expected
/// failures — surface them through <see cref="QrScanResult.Fail"/> so the
/// Frame stays retryable. Implementations may offload synchronous CPU-bound
/// decoding internally; the workflow invokes the adapter directly.
/// </summary>
public interface IQrScanner
{
    /// <summary>
    /// Scans the supplied in-memory Frame for QR codes, returning every
    /// decoded value. Implementations must not throw for expected failures.
    /// </summary>
    Task<QrScanResult> ScanAsync(ContiguousBitmap frame);
}
