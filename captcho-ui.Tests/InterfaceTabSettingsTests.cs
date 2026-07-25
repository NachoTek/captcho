// InterfaceTabSettingsTests.cs — Headless tests for the read-only Interface tab seam.
//
// Verifies the pure C# InterfaceTabSettings coordinator clearly states that
// Interface settings are not yet available and describes what is planned, using
// readable prose rather than a "not implemented" development placeholder. The tab
// is read-only, so there is no editing, validation, or save session — only the
// displayed content.

using System;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class InterfaceTabSettingsTests
{
    // ── The tab clearly states Interface settings are not yet available ──

    [Fact]
    public void Heading_IsNonEmptyAndReadable()
    {
        var tab = new InterfaceTabSettings();

        Assert.False(string.IsNullOrWhiteSpace(tab.Heading));
        Assert.Contains("Interface", tab.Heading, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Message_StatesSettingsAreNotYetAvailable()
    {
        var tab = new InterfaceTabSettings();

        Assert.False(string.IsNullOrWhiteSpace(tab.Message));
        Assert.Contains("not yet available", tab.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Message_IsReadableProseNotADevPlaceholder()
    {
        var tab = new InterfaceTabSettings();

        // Readable explanatory text, not a "not implemented" development placeholder.
        Assert.DoesNotContain("not implemented", tab.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("placeholder", tab.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── The tab describes what interface settings are planned ───────────

    [Fact]
    public void PlannedSettingsNote_IsNonEmptyAndMentionsFutureRelease()
    {
        var tab = new InterfaceTabSettings();

        Assert.False(string.IsNullOrWhiteSpace(tab.PlannedSettingsNote));
        Assert.Contains("planned", tab.PlannedSettingsNote, StringComparison.OrdinalIgnoreCase);
    }

    // ── Every surfaced string is non-empty readable prose ──────────────

    [Fact]
    public void AllSurfacedText_IsNonEmptyReadableProse()
    {
        var tab = new InterfaceTabSettings();

        Assert.False(string.IsNullOrWhiteSpace(tab.Heading));
        Assert.False(string.IsNullOrWhiteSpace(tab.Message));
        Assert.False(string.IsNullOrWhiteSpace(tab.PlannedSettingsNote));
    }
}
