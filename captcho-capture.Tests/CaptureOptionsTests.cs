// CaptureOptionsTests.cs — Tests for the CaptureOptions model and the
// decoration/shadow dependency it owns.
//
// CaptureOptions carries the three Capture flags (pointer, decorations, shadow)
// that pass through the workflow into the native Capture engine. The decoration/
// shadow dependency (shadow can only be included when decorations are, per spec
// user story #30) is enforced here as the single source of truth, so the
// Settings tab and the runtime workflow cannot disagree about what counts as
// a consistent CaptureOptions value.

using System.Linq;
using Xunit;

namespace captcho.Capture.Tests;

public class CaptureOptionsTests
{
    // ── Defaults ────────────────────────────────────────────────────────

    [Fact]
    public void WithDefaults_IncludesPointer_False()
    {
        // Spec user story #27 calls for a persistent default; the value itself isn't
        // specified. A clean-capture default omits the mouse pointer so the resulting
        // Frame is not visually dominated by a stray cursor.
        var options = CaptureOptions.WithDefaults();

        Assert.False(options.IncludePointer);
    }

    [Fact]
    public void WithDefaults_IncludesDecorations_True()
    {
        // Window Captures include the window frame by default, matching the conventional
        // "what you see is what you capture" expectation.
        var options = CaptureOptions.WithDefaults();

        Assert.True(options.IncludeDecorations);
    }

    [Fact]
    public void WithDefaults_IncludesShadow_True()
    {
        // With decorations on by default, the window shadow is on by default too —
        // decorations and shadow are visually linked (spec #30).
        var options = CaptureOptions.WithDefaults();

        Assert.True(options.IncludeShadow);
    }

    // ── Decoration/shadow dependency (spec #30) ─────────────────────────

    [Fact]
    public void Normalized_ShadowTrueWithDecorationsFalse_ForcesShadowFalse()
    {
        // Spec #30: window shadow cannot be enabled without window decorations.
        // A Normalized() call enforces the invariant so any caller — Settings tab,
        // session override holder, or workflow composer — sees a consistent value.
        var inconsistent = new CaptureOptions(
            IncludePointer: false,
            IncludeDecorations: false,
            IncludeShadow: true);

        var normalized = inconsistent.Normalized();

        Assert.False(normalized.IncludeDecorations);
        Assert.False(normalized.IncludeShadow);
    }

    [Fact]
    public void Normalized_DependenciesConsistent_AreLeftUnchanged()
    {
        // The four self-consistent combinations pass through unchanged.
        var consistent = new CaptureOptions(
            IncludePointer: true,
            IncludeDecorations: true,
            IncludeShadow: false);

        var normalized = consistent.Normalized();

        Assert.Equal(consistent, normalized);
    }

    [Fact]
    public void Normalized_DoesNotMutateSource()
    {
        // Normalized returns an independent copy; the original keeps its (possibly
        // inconsistent) values so callers never observe mutation through it.
        var source = new CaptureOptions(
            IncludePointer: false,
            IncludeDecorations: false,
            IncludeShadow: true);

        _ = source.Normalized();

        Assert.True(source.IncludeShadow);
        Assert.False(source.IncludeDecorations);
    }

    [Fact]
    public void Normalized_IsIdempotent()
    {
        // Normalizing an already-normalized value yields an equal value (and a fresh copy).
        var source = CaptureOptions.WithDefaults();

        var once = source.Normalized();
        var twice = once.Normalized();

        Assert.Equal(once, twice);
    }

    // ── Equality (record value semantics) ───────────────────────────────

    [Fact]
    public void Equality_TwoEqualOptionSets_AreEqual()
    {
        var a = new CaptureOptions(true, true, true);
        var b = new CaptureOptions(true, true, true);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferingInOneField_AreNotEqual()
    {
        var a = new CaptureOptions(true, true, true);
        var b = new CaptureOptions(false, true, true);

        Assert.NotEqual(a, b);
    }
}
