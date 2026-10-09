// CaptureOptions.cs — The three flags that flow from Capture Settings / Target
// Selection overrides into the workflow and the managed/native Capture boundary.
//
// Carries IncludePointer, IncludeDecorations, and IncludeShadow (spec user stories
// 27–30). The decoration/shadow dependency — window shadow is available only when
// decorations are included (spec #30) — is enforced through Normalized(), the
// single source of truth so the Settings tab, the session override holder, and the
// workflow composer cannot disagree about what counts as a consistent value.
//
// CaptureOptions is an immutable record so values can be compared (override vs.
// default) by equality; use the `With` constructor or build a fresh value to
// change a field.

using System;

namespace captcho.Capture;

/// <summary>
/// The three Capture-option flags that the user can set as persistent defaults
/// (Settings) or override temporarily for a Capture (Target Selection). All
/// three pass through the workflow into the managed/native Capture contract.
/// </summary>
/// <param name="IncludePointer">
/// When true, the mouse pointer is composited into the captured Frame.
/// </param>
/// <param name="IncludeDecorations">
/// When true, window Captures include the window's title bar and frame.
/// </param>
/// <param name="IncludeShadow">
/// When true, window Captures include the window's drop shadow. Dependent on
/// <paramref name="IncludeDecorations"/>: shadow is only meaningful when the
/// decorations are also included (spec user story #30). Normalized() enforces
/// the invariant.
/// </param>
public sealed record CaptureOptions(
    bool IncludePointer,
    bool IncludeDecorations,
    bool IncludeShadow)
{
    /// <summary>
    /// Production defaults for the three Capture-option flags: pointer off (a clean
    /// capture by default), decorations on (the conventional "what you see is what
    /// you capture" expectation), and shadow on (visually linked with decorations).
    /// This is the single source every Settings default reads from.
    /// </summary>
    public static CaptureOptions WithDefaults() => new(
        IncludePointer: false,
        IncludeDecorations: true,
        IncludeShadow: true);

    /// <summary>
    /// Returns a copy with the decoration/shadow dependency reconciled: if
    /// decorations are excluded, shadow is forced off. Other fields pass through
    /// unchanged. Never throws — pure normalization. The source is never mutated.
    /// </summary>
    public CaptureOptions Normalized() => this with
    {
        IncludeShadow = IncludeDecorations && IncludeShadow,
    };
}
