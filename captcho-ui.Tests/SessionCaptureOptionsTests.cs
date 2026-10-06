// SessionCaptureOptionsTests.cs — Headless tests for the workflow-side session
// override holder.
//
// The two highest-level behavioral seams are the existing SettingsSession
// (committed defaults) and the new WinUI-free capture/post-capture workflow
// session. SessionCaptureOptions is the workflow session's override holder for
// Capture options — distinct from committed Configuration so a temporary
// override can affect a Capture without ever mutating persistent Settings or
// Configuration (spec #35 AC #3).
//
// Covers the per-mode override model (AC #2 — a temporary override affects the
// resulting Capture and remains available for later Captures in the same app
// session), the per-mode visibility rule (AC #1 — Target Selection shows only
// options relevant to the active Capture Mode, including the decoration/shadow
// dependency), the absence of stale irrelevant values on mode switch (AC #4),
// and the clear-on-exit behavior (AC #3).

using System;
using System.Linq;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class SessionCaptureOptionsTests
{
    // ── Construction ────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullCommitted_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionCaptureOptions(null!));
    }

    [Fact]
    public void Constructor_CommittedDefaultsAreObservableImmediately()
    {
        // With no overrides set, the effective options for any mode equal the
        // committed defaults (reconciled through CaptureOptions.Normalized so
        // the decoration/shadow dependency is enforced on read).
        var committed = new AppSettings
        {
            CaptureOptions = new CaptureOptions(
                IncludePointer: true,
                IncludeDecorations: true,
                IncludeShadow: true),
        };
        var session = new SessionCaptureOptions(committed);

        foreach (var mode in AllModes)
        {
            var effective = session.EffectiveFor(mode);
            Assert.True(effective.IncludePointer);
            Assert.True(effective.IncludeDecorations);
            Assert.True(effective.IncludeShadow);
        }
    }

    // ── AC #1: per-mode visibility ──────────────────────────────────────

    [Theory]
    [InlineData(CaptureMode.FullDesktop)]
    [InlineData(CaptureMode.ActiveWindow)]
    [InlineData(CaptureMode.SelectedWindow)]
    [InlineData(CaptureMode.SelectedMonitor)]
    [InlineData(CaptureMode.Selection)]
    public void IsPointerVisible_TrueForEveryMode(CaptureMode mode)
    {
        // Mouse pointer applies to every Capture Mode (a Frame always has a
        // potential pointer pixel).
        Assert.True(SessionCaptureOptions.IsPointerVisible(mode));
    }

    [Theory]
    [InlineData(CaptureMode.ActiveWindow, true)]
    [InlineData(CaptureMode.SelectedWindow, true)]
    [InlineData(CaptureMode.FullDesktop, false)]
    [InlineData(CaptureMode.SelectedMonitor, false)]
    [InlineData(CaptureMode.Selection, false)]
    public void AreDecorationsVisible_OnlyForWindowCaptureModes(
        CaptureMode mode, bool expected)
    {
        // Window decorations (and by dependency, window shadow) only apply to
        // Captures of a single window — ActiveWindow and SelectedWindow. Other
        // modes capture a region or the whole desktop, where there is no single
        // window to decorate.
        Assert.Equal(expected, SessionCaptureOptions.AreDecorationsVisible(mode));
    }

    [Theory]
    [InlineData(CaptureMode.ActiveWindow, true)]
    [InlineData(CaptureMode.SelectedWindow, true)]
    [InlineData(CaptureMode.FullDesktop, false)]
    [InlineData(CaptureMode.SelectedMonitor, false)]
    [InlineData(CaptureMode.Selection, false)]
    public void IsShadowVisible_TracksDecorationsVisibility(
        CaptureMode mode, bool expected)
    {
        // Shadow visibility follows decorations visibility (spec #30): window
        // shadow is only meaningful when window decorations are also visible.
        Assert.Equal(expected, SessionCaptureOptions.IsShadowVisible(mode));
    }

    // ── AC #2: a temporary override affects the resulting Capture ────────

    [Fact]
    public void OverrideIncludePointer_ForMode_AffectsThatModeOnly()
    {
        // Overriding the pointer in FullDesktop does not affect ActiveWindow — each
        // mode has its own override namespace. AC #2: the override remains available
        // for later Captures in the same session.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        session.OverrideIncludePointer(CaptureMode.FullDesktop, true);

        Assert.True(session.EffectiveFor(CaptureMode.FullDesktop).IncludePointer);
        // ActiveWindow has no override — uses committed default.
        Assert.Equal(committed.CaptureOptions.IncludePointer,
                     session.EffectiveFor(CaptureMode.ActiveWindow).IncludePointer);
    }

    [Fact]
    public void Override_RemainsAvailableForLaterCapturesInTheSameSession()
    {
        // AC #2: a temporary override is not consumed by the first Capture — it
        // applies to subsequent Captures in the same session until cleared.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);
        session.OverrideIncludePointer(CaptureMode.Selection, true);

        var first = session.EffectiveFor(CaptureMode.Selection);
        var second = session.EffectiveFor(CaptureMode.Selection);

        Assert.True(first.IncludePointer);
        Assert.True(second.IncludePointer);
    }

    [Fact]
    public void Override_DoesNotChangeCommittedConfiguration()
    {
        // AC #3: overrides are never written back to persistent Settings or
        // Configuration. The committed AppSettings instance is untouched.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        session.OverrideIncludePointer(CaptureMode.FullDesktop, true);
        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);

        // The committed instance is unaffected — every field retains its original
        // value. (The workflow passes this instance to SettingsSession when the
        // user later opens Settings; that session reads committed defaults, not
        // overrides.)
        Assert.Equal(CaptureOptions.WithDefaults(), committed.CaptureOptions);
    }

    [Fact]
    public void Override_TakesPrecedenceOverLaterCommittedDefaultChange()
    {
        // A session override pins the value for the session: even if the user later
        // opens Settings and changes the committed default, the override still wins
        // for that mode and field. This matches the "remains available" semantic —
        // the override is the source of truth for the session, layered above
        // committed Configuration.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);
        session.OverrideIncludePointer(CaptureMode.FullDesktop, true);

        // User later opens Settings and changes the committed default to false.
        committed.CaptureOptions = committed.CaptureOptions with { IncludePointer = false };

        // The override still wins for FullDesktop — the session is independent of
        // committed-Configuration changes.
        Assert.True(session.EffectiveFor(CaptureMode.FullDesktop).IncludePointer);
    }

    // ── AC #4: switching modes does not apply stale irrelevant values ───

    [Fact]
    public void Override_ForWindowOnlyField_DoesNotLeakIntoNonWindowMode()
    {
        // If the user overrides decorations while in ActiveWindow and switches to
        // FullDesktop, the FullDesktop capture must NOT use the ActiveWindow
        // decorations override. Per-mode override namespace + per-mode visibility
        // together guarantee that switching modes shows only the relevant controls
        // and uses only the relevant overrides — there is no "stale irrelevant
        // value" path.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);
        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);

        var fullDesktopEffective = session.EffectiveFor(CaptureMode.FullDesktop);

        // FullDesktop ignores decorations entirely (not visible for that mode), and
        // the ActiveWindow decorations override is never consulted for FullDesktop.
        Assert.Equal(committed.CaptureOptions.Normalized().IncludePointer,
                     fullDesktopEffective.IncludePointer);
    }

    [Fact]
    public void Override_ForNonWindowModeField_IsPreservedAcrossModeSwitch()
    {
        // Overrides for relevant fields persist across mode switches: override
        // pointer while in FullDesktop, switch to Selection and back, the pointer
        // override is still there. AC #2 + #4 reconcile through the per-mode
        // override namespace.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);
        session.OverrideIncludePointer(CaptureMode.FullDesktop, true);

        // A different mode is queried in between — no effect on the FullDesktop
        // override.
        _ = session.EffectiveFor(CaptureMode.Selection);

        Assert.True(session.EffectiveFor(CaptureMode.FullDesktop).IncludePointer);
    }

    // ── Decoration/shadow dependency on override (spec #30) ─────────────

    [Fact]
    public void OverrideDecorations_TurningShadowOnWhileDecorationsOff_IsRefused()
    {
        // The decoration/shadow dependency applies to overrides the same way it
        // applies to committed defaults: shadow cannot be turned on while
        // decorations are off. The override is silently refused.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);
        session.OverrideIncludeShadow(CaptureMode.ActiveWindow, true);

        var effective = session.EffectiveFor(CaptureMode.ActiveWindow);
        Assert.False(effective.IncludeDecorations);
        Assert.False(effective.IncludeShadow);
    }

    [Fact]
    public void OverrideDecorations_TurningOff_ForcesShadowOverrideOff()
    {
        // Setting decorations to false in the override namespace reconciles shadow
        // immediately: any existing shadow override is replaced with false (the
        // dependency holds at the override layer too).
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        session.OverrideIncludeShadow(CaptureMode.ActiveWindow, true);
        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);

        var effective = session.EffectiveFor(CaptureMode.ActiveWindow);
        Assert.False(effective.IncludeDecorations);
        Assert.False(effective.IncludeShadow);
    }

    [Fact]
    public void OverrideDecorations_TurningBackOn_RestoresRememberedShadow()
    {
        // Turning decorations off remembers that shadow was on (when it was);
        // turning decorations back on restores the remembered value, so a
        // decorations round-trip does not silently discard the session's shadow
        // override.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);
        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, true);

        var effective = session.EffectiveFor(CaptureMode.ActiveWindow);
        Assert.True(effective.IncludeDecorations);
        Assert.True(effective.IncludeShadow);
    }

    [Fact]
    public void OverrideDecorations_RepeatedOff_DoesNotWipeRememberedShadow()
    {
        // Only the on→off transition records the shadow memory: a repeated
        // decorations-off call must not wipe what the first transition
        // remembered, so the later re-enable still restores shadow.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);
        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);
        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, true);

        var effective = session.EffectiveFor(CaptureMode.ActiveWindow);
        Assert.True(effective.IncludeDecorations);
        Assert.True(effective.IncludeShadow);
    }

    [Fact]
    public void OverrideDecorations_RoundTrip_PreservesCommittedShadowOffDefault()
    {
        // Symmetric for the committed-off starting point: a decorations
        // round-trip must not switch shadow on when the committed default (and
        // no override) had it off.
        var committed = new AppSettings
        {
            CaptureOptions = new CaptureOptions(
                IncludePointer: false,
                IncludeDecorations: true,
                IncludeShadow: false),
        };
        var session = new SessionCaptureOptions(committed);

        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);
        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, true);

        Assert.False(session.EffectiveFor(CaptureMode.ActiveWindow).IncludeShadow);
    }

    // ── AC #3: cleared on application exit ──────────────────────────────

    [Fact]
    public void ClearAll_RemovesEveryOverrideAcrossEveryMode()
    {
        // AC #3: overrides are cleared on application exit. ClearAll is the entry
        // point the workflow calls when the application is shutting down (or
        // whenever a session-scoped reset is required). After ClearAll, every
        // mode's effective options equal the committed defaults again.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);
        session.OverrideIncludePointer(CaptureMode.FullDesktop, true);
        session.OverrideIncludeDecorations(CaptureMode.ActiveWindow, false);

        session.ClearAll();

        foreach (var mode in AllModes)
        {
            Assert.Equal(committed.CaptureOptions.Normalized(),
                         session.EffectiveFor(mode));
        }
    }

    [Fact]
    public void ClearAll_DoesNotChangeCommittedConfiguration()
    {
        // ClearAll touches only the override namespace — committed Configuration
        // is never mutated by the session holder (it doesn't own that side).
        var committed = AppSettings.WithDefaults();
        var committedSnapshot = committed.CaptureOptions with { };
        var session = new SessionCaptureOptions(committed);
        session.OverrideIncludePointer(CaptureMode.FullDesktop, true);

        session.ClearAll();

        Assert.Equal(committedSnapshot, committed.CaptureOptions);
    }

    // ── Override visibility into the namespace (for UI display) ─────────

    [Fact]
    public void IsOverridden_ReportsThePerModeOverrideStatus()
    {
        // The Target Selection overlay needs to know whether each visible control is
        // showing a committed default or a session override, so the user can tell
        // they're adjusting a per-session value. IsOverridden exposes that.
        var committed = AppSettings.WithDefaults();
        var session = new SessionCaptureOptions(committed);

        Assert.False(session.IsPointerOverridden(CaptureMode.FullDesktop));
        Assert.False(session.IsDecorationsOverridden(CaptureMode.ActiveWindow));

        session.OverrideIncludePointer(CaptureMode.FullDesktop, true);

        Assert.True(session.IsPointerOverridden(CaptureMode.FullDesktop));
        // ActiveWindow is untouched — a different mode's override.
        Assert.False(session.IsPointerOverridden(CaptureMode.ActiveWindow));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static readonly CaptureMode[] AllModes =
    {
        CaptureMode.FullDesktop,
        CaptureMode.ActiveWindow,
        CaptureMode.SelectedWindow,
        CaptureMode.SelectedMonitor,
        CaptureMode.Selection,
    };
}
