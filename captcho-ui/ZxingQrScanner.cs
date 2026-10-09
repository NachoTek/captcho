// ZxingQrScanner.cs — Production IQrScanner over ZXing.Net.
//
// Implements the adapter seam the Workflow Session routes Scan QR through
// (spec #48): converts the session-owned in-memory Frame (contiguous BGRA)
// into ZXing's RGBLuminanceSource — the Frame→luminance conversion is the
// adapter seam the research (#15/#16) identified, avoiding any WinRT
// SoftwareBitmap round-trip — and runs a multi-code decode (TryHarder +
// TryInverted) so one Frame yields every QR value without dropping valid
// results. Never throws for expected failures: conversion and decode
// failures surface through QrScanResult.Fail so the Frame stays retryable.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using captcho.Capture;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace captcho.UI;

/// <summary>
/// Production <see cref="IQrScanner"/> backed by ZXing.Net (pure C#,
/// Apache-2.0). Decodes QR codes from the in-memory Frame's contiguous BGRA
/// pixels — no file is written and no SoftwareBitmap conversion is needed
/// because RGBLuminanceSource accepts a BGRA32 buffer directly.
/// </summary>
public sealed class ZxingQrScanner : IQrScanner
{
    /// <summary>
    /// Scans the supplied in-memory Frame for QR codes. Every decoded value
    /// is returned in decode order; a Frame with no QR code yields the
    /// distinct no-code outcome. Never throws for expected failures —
    /// malformed input and decode failures surface as
    /// <see cref="QrOutcome.Failed"/> with a retryable message. The
    /// synchronous CPU-bound decode runs on the thread pool so the workflow
    /// caller's (UI) thread stays free.
    /// </summary>
    public Task<QrScanResult> ScanAsync(ContiguousBitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var source = ToLuminanceSource(frame);
                var reader = new ZXing.Multi.GenericMultipleBarcodeReader(new QRCodeReader());

                var results = reader.decodeMultiple(
                    new BinaryBitmap(new HybridBinarizer(source)),
                    DecodeHints);
                sw.Stop();

                var values = ExtractValues(results);
                return values.Count > 0
                    ? QrScanResult.Found(values, sw.Elapsed.TotalMilliseconds)
                    : QrScanResult.NoCode(sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return QrScanResult.Fail($"QR scanning failed: {ex.Message}", sw.Elapsed.TotalMilliseconds);
            }
        });
    }

    /// <summary>
    /// Per-decode hints: restrict decoding to QR codes (skip every other
    /// symbology's detector cost) and enable TryHarder (accurate mode for
    /// static Frames) plus TryInverted (white-on-dark codes).
    /// </summary>
    private static readonly Dictionary<DecodeHintType, object> DecodeHints = new()
    {
        [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
        [DecodeHintType.TRY_HARDER] = true,
        [DecodeHintType.ALSO_INVERTED] = true,
    };

    /// <summary>
    /// Converts the contiguous BGRA Frame into ZXing's luminance source —
    /// the Frame→luminance adapter seam. Performs the same dimension/buffer
    /// validation as the OCR engine's SoftwareBitmap conversion; BGRA32 is
    /// consumed directly with no intermediate copy.
    /// </summary>
    private static LuminanceSource ToLuminanceSource(ContiguousBitmap frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException($"Frame dimensions must be positive ({frame.Width}×{frame.Height}).", nameof(frame));

        int expectedLen = frame.Stride * frame.Height;
        if (frame.Pixels.Length < expectedLen)
            throw new ArgumentException(
                $"Pixel buffer ({frame.Pixels.Length} bytes) is shorter than stride×height ({expectedLen}).",
                nameof(frame));

        return new RGBLuminanceSource(frame.Pixels, frame.Width, frame.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
    }

    /// <summary>
    /// Extracts the decoded values from the raw results, preserving decode
    /// order and skipping null/empty entries without dropping valid ones.
    /// </summary>
    private static List<string> ExtractValues(Result[]? results)
    {
        var values = new List<string>();
        if (results is null)
            return values;

        foreach (var result in results)
        {
            if (result?.Text is { Length: > 0 } text)
                values.Add(text);
        }

        return values;
    }
}
