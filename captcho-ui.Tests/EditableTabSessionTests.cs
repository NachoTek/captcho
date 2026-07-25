// EditableTabSessionTests.cs — Headless tests for the shared editable-tab base.
//
// Pins the snapshot/apply/cancel/reset mechanics that issue #11 hoisted out of the two
// editable tab classes (GeneralTabSettings, GlobalHotkeyTabSettings) into EditableTabSession,
// independently of any one tab's fields. A tiny test-only subclass exercises only the base
// behavior; the tab-specific surface (WriteInto / IsValid / IsDirty / FirstError) is stubbed
// because it is irrelevant to the shared plumbing, and is already covered per-tab.
//
// Defaults centralization is asserted here at the seam: Reset routes through ApplyDefaults,
// and the recording subclass reads its slice from AppSettings.WithDefaults() — the single
// default source every tab shares.

using System;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class EditableTabSessionTests
{
    // ── Construction snapshots persisted into independent working + baseline copies ──

    [Fact]
    public void Constructor_NullPersisted_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RecordingTab(null!));
    }

    [Fact]
    public void Constructor_SnapshotsPersistedIntoIndependentWorkingCopy()
    {
        var source = new AppSettings { SaveLocation = @"D:\Original" };

        var tab = new RecordingTab(source);

        Assert.Equal(@"D:\Original", tab.WorkingSaveLocation);
        // Mutating the source after construction does not leak into the working copy.
        source.SaveLocation = @"D:\Changed";
        Assert.Equal(@"D:\Original", tab.WorkingSaveLocation);
    }

    // ── Commit advances the shared baseline ─────────────────────────────

    [Fact]
    public void Commit_AdvancesBaselineSoCancelKeepsTheCommittedValue()
    {
        var tab = new RecordingTab(new AppSettings { SaveLocation = @"D:\Original" });

        tab.SetWorkingSaveLocation(@"D:\Committed");
        tab.Commit();
        tab.Cancel();

        Assert.Equal(@"D:\Committed", tab.WorkingSaveLocation);
    }

    // ── Cancel reverts to the shared baseline ───────────────────────────

    [Fact]
    public void Cancel_RevertsWorkingToBaseline()
    {
        var tab = new RecordingTab(new AppSettings { SaveLocation = @"D:\Original" });

        tab.SetWorkingSaveLocation(@"D:\ThrownAway");
        tab.Cancel();

        Assert.Equal(@"D:\Original", tab.WorkingSaveLocation);
    }

    // ── Reset routes through ApplyDefaults, reading from one default source ──

    [Fact]
    public void Reset_InvokesApplyDefaultsOnTheWorkingSnapshot()
    {
        var tab = new RecordingTab(new AppSettings { SaveLocation = @"D:\Custom" });

        tab.Reset();

        Assert.Equal(1, tab.ApplyDefaultsCallCount);
    }

    [Fact]
    public void Reset_RestoresTheDefaultSliceFromAppSettingsWithDefaults()
    {
        var tab = new RecordingTab(new AppSettings { SaveLocation = @"D:\Custom" });

        tab.Reset();

        Assert.Equal(AppSettings.WithDefaults().SaveLocation, tab.WorkingSaveLocation);
    }

    [Fact]
    public void Reset_IsIdempotent()
    {
        var tab = new RecordingTab(new AppSettings { SaveLocation = @"D:\Custom" });

        tab.Reset();
        tab.Reset();
        tab.Reset();

        Assert.Equal(3, tab.ApplyDefaultsCallCount);
        Assert.Equal(AppSettings.WithDefaults().SaveLocation, tab.WorkingSaveLocation);
    }

    /// <summary>
    /// Minimal test-only tab exercising only the shared base mechanics. Its tab-specific
    /// surface is stubbed; the SaveLocation hooks exercise the protected Working/Baseline
    /// accessors the base exposes, and ApplyDefaults reads from the single default source.
    /// </summary>
    private sealed class RecordingTab : EditableTabSession
    {
        public RecordingTab(AppSettings persisted) : base(persisted) { }

        public string WorkingSaveLocation => Working.SaveLocation!;
        public int ApplyDefaultsCallCount { get; private set; }

        public void SetWorkingSaveLocation(string value) => Working.SaveLocation = value;

        protected override void ApplyDefaults(AppSettings working)
        {
            ApplyDefaultsCallCount++;
            working.SaveLocation = AppSettings.WithDefaults().SaveLocation;
        }

        public override bool IsValid => true;
        public override bool IsDirty => false;
        public override string? FirstError => null;
        public override void WriteInto(AppSettings target) { }
    }
}
