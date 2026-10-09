// CaptureOptionsAcceptanceTests.cs — Cross-seam acceptance coverage for issue #35.
//
// The headline AC #5 ("Workflow and Settings tests distinguish committed defaults
// from session overrides across every Capture Mode") is exercised here as a
// walk across all five Capture Modes that asserts, end-to-end:
//
//   1. Committed defaults are observable through both seams when no overrides
//      are set (AC #1).
//   2. A session override takes precedence over the committed default for the
//      same mode and remains available for later Captures (AC #2).
//   3. A session override never mutates committed Configuration (AC #3).
//   4. Switching modes does not apply stale irrelevant values — overrides only
//      apply to the modes where their field is visible (AC #4).
//   5. The decoration/shadow dependency (spec #30) holds on both the committed
//      side (Settings tab) and the session-override side.
//
// The two seams under test are the SettingsSession (committed defaults, via
// CaptureTabSettings) and the new workflow session's SessionCaptureOptions
// holder. Both are WinUI-free pure C#; no native Capture, file system, or Win32
// dependency is exercised here.

using System.Collections.Generic;
using System.Linq;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class CaptureOptionsAcceptanceTests
{
    // All five Capture Modes from spec #24, walked exhaustively.
    private static readonly CaptureMode[] AllModes =
    {
        CaptureMode.FullDesktop,
        CaptureMode.ActiveWindow,
        CaptureMode.SelectedWindow,
        CaptureMode.SelectedMonitor,
        CaptureMode.Selection,
    };

    // The two window Capture Modes — the only modes where decorations and shadow
    // are visible (and overridable).
    private static readonly CaptureMode[] WindowModes =
    {
        CaptureMode.ActiveWindow,
        CaptureMode.SelectedWindow,
    };

    // The three non-window Capture Modes — pointer-only.
    private static readonly CaptureMode[] NonWindowModes =
    {
        CaptureMode.FullDesktop,
        CaptureMode.SelectedMonitor,
        CaptureMode.Selection,
    };

    // ── AC #1: Committed defaults flow through both seams ───────────────

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void NoOverridesSet_EffectiveOptionsMatchCommittedDefaults(CaptureMode mode)
    {
        // When no overrides exist, the workflow session's EffectiveFor equals the
        // committed (Settings-persisted) defaults. Both seams see the same value.
        var committed = new AppSettings
        {
            CaptureOptions = new CaptureOptions(
                IncludePointer: true,
                IncludeDecorations: true,
                IncludeShadow: true),
        };

        var session = new SessionCaptureOptions(committed);

        Assert.Equal(committed.CaptureOptions.Normalized(), session.EffectiveFor(mode));
    }

    // ── AC #2: A session override takes precedence for one mode only ─────

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void OverridePointerForOneMode_TakesPrecedenceOnlyForThatMode(CaptureMode mode)
    {
        // A pointer override applies to the chosen mode and does not affect any
        // other mode. This is the "remains available for later Captures in the
        // same app session" semantic of AC #2 in its scoped form.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        session.OverrideIncludePointer(mode, true);

        Assert.True(session.EffectiveFor(mode).IncludePointer);
        foreach (var other in AllModes.Where(m => m != mode))
        {
            // Other mode uses the committed default — override did not leak.
            Assert.Equal(
                committed.CaptureOptions.Normalized().IncludePointer,
                session.EffectiveFor(other).IncludePointer);
        }
    }

    // ── AC #3: Overrides never mutate committed Configuration ────────────

    [Fact]
    public void SessionOverrides_AcrossEveryMode_NeverMutateCommitted()
    {
        // The defining invariant of AC #3: overrides live in the session, not in
        // Configuration. Walk every mode, set every applicable override, and
        // verify the committed AppSettings instance is bit-for-bit unchanged.
        var committed = AppSettings.WithDefaults();
        var committedBefore = committed.CaptureOptions with { };

        var session = new SessionCaptureOptions(committed);
        foreach (var mode in AllModes)
        {
            session.OverrideIncludePointer(mode, true);
        }
        foreach (var mode in WindowModes)
        {
            session.OverrideIncludeDecorations(mode, false);
            session.OverrideIncludeShadow(mode, false);
        }

        Assert.Equal(committedBefore, committed.CaptureOptions);

        // And clearing the overrides also never touches committed Configuration.
        session.ClearAll();
        Assert.Equal(committedBefore, committed.CaptureOptions);
    }

    // ── AC #4: Switching modes does not apply stale irrelevant values ────

    [Fact]
    public void OverrideDecorationsInWindowMode_DoesNotLeakIntoAnyNonWindowMode()
    {
        // The classic AC #4 scenario: override decorations while in a window mode,
        // then capture in a non-window mode. The non-window mode's effective
        // options must NOT carry the decorations override — that field is not
        // visible for non-window modes, so the override cannot apply there.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        foreach (var windowMode in WindowModes)
        {
            session.OverrideIncludeDecorations(windowMode, false);
            session.OverrideIncludeShadow(windowMode, false);
        }

        foreach (var nonWindowMode in NonWindowModes)
        {
            var effective = session.EffectiveFor(nonWindowMode);
            // Non-window modes are not affected by the window-mode overrides.
            Assert.Equal(
                committed.CaptureOptions.Normalized().IncludePointer,
                effective.IncludePointer);
            Assert.Equal(
                committed.CaptureOptions.Normalized().IncludeDecorations,
                effective.IncludeDecorations);
            Assert.Equal(
                committed.CaptureOptions.Normalized().IncludeShadow,
                effective.IncludeShadow);
        }
    }

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void DecorationsAndShadow_NotVisibleForNonWindowModes(CaptureMode mode)
    {
        // The static visibility rule (AC #1) for non-window modes: only the pointer
        // control is visible. This is what makes the override-leak test above
        // possible — there is no surface for the override to apply through.
        Assert.False(SessionCaptureOptions.AreDecorationsVisible(mode));
        Assert.False(SessionCaptureOptions.IsShadowVisible(mode));
        Assert.True(SessionCaptureOptions.IsPointerVisible(mode));
    }

    [Theory]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    public void DecorationsAndShadow_VisibleForWindowModes(CaptureMode mode)
    {
        // The static visibility rule (AC #1) for window modes: all three controls
        // are visible, with shadow tracking decorations (spec #30).
        Assert.True(SessionCaptureOptions.AreDecorationsVisible(mode));
        Assert.True(SessionCaptureOptions.IsShadowVisible(mode));
        Assert.True(SessionCaptureOptions.IsPointerVisible(mode));
    }

    // ── Spec #30: decoration/shadow dependency on both seams ────────────

    [Fact]
    public void SettingsTab_TurningDecorationsOff_ReconcilesShadowImmediately()
    {
        // The committed-side seam: CaptureTabSettings enforces the decoration/shadow
        // dependency at edit time, so the Settings UI can never offer "shadow on
        // without decorations" even before Apply. The composed AppSettings written
        // via WriteInto reflects the reconciliation.
        var runtime = AppSettings.WithDefaults();
        var tab = new CaptureTabSettings(runtime);
        tab.EditIncludeDecorations(false);

        var merged = new AppSettings();
        tab.WriteInto(merged);

        Assert.False(merged.CaptureOptions.IncludeDecorations);
        Assert.False(merged.CaptureOptions.IncludeShadow);
    }

    [Fact]
    public void WorkflowSession_OverridingDecorationsOff_ReconcilesShadowImmediately()
    {
        // The session-side seam: SessionCaptureOptions enforces the same dependency
        // when an override is set. Both seams agree — there is no path where shadow
        // is on while decorations are off.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);

        var effective = session.EffectiveFor(CaptureMode.ActiveWindow);
        Assert.False(effective.IncludeDecorations);
        Assert.False(effective.IncludeShadow);
    }

    // ── AC #5: Workflow and Settings seams stay independent ─────────────

    [Fact]
    public void EditingCommittedDefaultInSettings_DoesNotInheritSessionOverrides()
    {
        // The two seams are independent: setting a session override does not change
        // what the Settings tab shows, and changing committed defaults in Settings
        // does not erase a session override (the override takes precedence until
        // cleared). This is the headline AC #5 invariant expressed at both seams.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);
        session.OverrideIncludePointer(CaptureMode.FullDesktop, true);

        // Opening Settings after the override was set shows committed defaults —
        // the override is not visible in the Settings tab.
        var settingsTab = new CaptureTabSettings(committed);
        Assert.Equal(committed.CaptureOptions.IncludePointer, settingsTab.IncludePointer);

        // Conversely, the workflow session still uses the override regardless of
        // Settings-tab activity (the Settings tab's working snapshot never writes
        // back until Apply; even after Apply the override still wins).
        Assert.True(session.EffectiveFor(CaptureMode.FullDesktop).IncludePointer);

        // Simulate the user applying a Settings change: the committed default moves,
        // but the session override still takes precedence for FullDesktop.
        settingsTab.EditIncludePointer(false);
        settingsTab.WriteInto(committed);
        Assert.True(session.EffectiveFor(CaptureMode.FullDesktop).IncludePointer);

        // Modes without an override reflect the new committed default immediately.
        Assert.False(session.EffectiveFor(CaptureMode.ActiveWindow).IncludePointer);
    }

    // ── AC #1: per-mode visibility with the decoration/shadow dependency ─

    [Fact]
    public void EffectiveFor_WindowMode_ReconcilesDecorationShadowDependency()
    {
        // For window modes, the decoration/shadow dependency holds at the
        // effective-options layer: an inconsistent combination of overrides and
        // committed defaults is reconciled through Normalized at read time.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        // Override decorations to false, then try to override shadow to true —
        // refused by OverrideIncludeShadow. Effective reflects the refusal.
        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);
        session.OverrideIncludeShadow(CaptureMode.ActiveWindow, true);

        var effective = session.EffectiveFor(CaptureMode.ActiveWindow);
        Assert.False(effective.IncludeDecorations);
        Assert.False(effective.IncludeShadow);
    }

    [Fact]
    public void EffectiveFor_EveryMode_NeverCarriesShadowOnWithoutDecorations()
    {
        // Defensive sweep: walk every mode and verify the decoration/shadow
        // dependency holds for the effective options, regardless of how overrides
        // and committed defaults combine. There is no path where the workflow
        // observes an impossible combination.
        var committed = new AppSettings
        {
            CaptureOptions = new CaptureOptions(
                IncludePointer: false,
                IncludeDecorations: false,
                IncludeShadow: true), // intentionally inconsistent — reconciled on read
        };
        var session = new SessionCaptureOptions(committed);

        // Override shadow to true in window modes — refused because committed
        // decorations are off (and not overridden).
        session.OverrideIncludeShadow(CaptureMode.ActiveWindow, true);
        session.OverrideIncludeShadow(CaptureMode.SelectedWindow, true);

        foreach (var mode in AllModes)
        {
            var effective = session.EffectiveFor(mode);
            if (!effective.IncludeDecorations)
            {
                Assert.False(effective.IncludeShadow);
            }
        }
    }
}
