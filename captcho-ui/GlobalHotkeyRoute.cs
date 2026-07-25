// GlobalHotkeyRoute.cs — Pure global hotkey contract: route enum, Global Hotkey specs, mapping, and registration results.
//
// Defines the four Global Hotkeys with stable ids, modifier/VK constants,
// and the capture route each Global Hotkey triggers. Free of WinUI and Win32 dependencies
// so headless xUnit tests can verify the full mapping without a desktop session.

using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Capture workflow that a global hotkey triggers.
/// Maps one-to-one with the capture methods on <see cref="CapturePreviewService"/>.
/// </summary>
public enum GlobalHotkeyRoute
{
    /// <summary>Print Screen — captures the monitor the cursor is on.</summary>
    CurrentMonitor,
    /// <summary>Meta (Win) + Print Screen — captures the active (foreground) window.</summary>
    ActiveWindow,
    /// <summary>Shift + Print Screen — captures the full virtual desktop.</summary>
    FullDesktop,
    /// <summary>Meta (Win) + Shift + Print Screen — opens rectangular region selector.</summary>
    RectangularRegion,
}

/// <summary>
/// Immutable specification of a single global hotkey.
/// Includes stable id, human-readable name, Win32 modifier flags, virtual key code,
/// and the capture route it triggers.
/// </summary>
public sealed record GlobalHotkeySpec
{
    /// <summary>Stable numeric id used as the RegisterHotKey id and WM_HOTKEY wParam.</summary>
    public int Id { get; init; }
    /// <summary>Human-readable binding name (e.g. "Print Screen").</summary>
    public string Name { get; init; } = "";
    /// <summary>Win32 modifier flags (MOD_SHIFT, MOD_WIN, etc.). Zero for no modifiers.</summary>
    public int Modifiers { get; init; }
    /// <summary>Virtual key code (e.g. VK_SNAPSHOT = 0x2C).</summary>
    public int VirtualKey { get; init; }
    /// <summary>Capture workflow this Global Hotkey triggers.</summary>
    public GlobalHotkeyRoute Route { get; init; }
}

/// <summary>
/// Result of registering or attempting to register a single global hotkey.
/// Always returned (never thrown) — structured for UI display and diagnostics.
/// Error messages are sanitized and contain no stack traces or filesystem paths.
/// </summary>
public sealed record GlobalHotkeyRegistrationResult
{
    /// <summary>The spec that was registered (or failed to register).</summary>
    public required GlobalHotkeySpec Spec { get; init; }
    /// <summary>Whether registration succeeded.</summary>
    public bool Succeeded { get; init; }
    /// <summary>Sanitized error message on failure, or empty on success.</summary>
    public string Error { get; init; } = "";
    /// <summary>Phase that failed (e.g. "RegisterHotKey", "UnregisterHotKey"), or empty.</summary>
    public string Phase { get; init; } = "";

    /// <summary>Creates a success result.</summary>
    public static GlobalHotkeyRegistrationResult Success(GlobalHotkeySpec spec) => new() { Spec = spec, Succeeded = true };

    /// <summary>Creates a failure result with sanitized error info.</summary>
    public static GlobalHotkeyRegistrationResult Fail(GlobalHotkeySpec spec, string phase, string error) =>
        new() { Spec = spec, Succeeded = false, Phase = phase, Error = SanitizeErrorMessage(error) };

    /// <summary>
    /// Strips potential PII and stack traces from Win32 error messages.
    /// Keeps the error useful for diagnostics without exposing sensitive paths.
    /// </summary>
    private static string SanitizeErrorMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        // Remove anything that looks like a filesystem path (drive letter or UNC)
        string sanitized = System.Text.RegularExpressions.Regex.Replace(
            message,
            @"[A-Za-z]:\\[^\s""]*|\\\\[^\s""]*",
            "[path]");

        // Remove stack trace lines (starting with "   at ")
        int atIdx = sanitized.IndexOf("\n   at ", StringComparison.Ordinal);
        if (atIdx >= 0)
            sanitized = sanitized.Substring(0, atIdx);

        return sanitized.Trim();
    }
}

/// <summary>
/// Pure mapping between Global Hotkey ids/modifier+VK combinations and capture routes.
/// No Win32 or WinUI dependencies — fully testable headless.
/// </summary>
public static class GlobalHotkeyRouteMap
{
    // ── Win32 constants (kept here for mapping, P/Invoke in WindowsGlobalHotkeyRegistrar) ──

    /// <summary>WM_HOTKEY message id.</summary>
    public const int WM_HOTKEY = 0x0312;
    /// <summary>VK_SNAPSHOT (Print Screen) virtual key code.</summary>
    public const int VK_SNAPSHOT = 0x2C;
    /// <summary>MOD_SHIFT modifier flag.</summary>
    public const int MOD_SHIFT = 0x0004;
    /// <summary>MOD_WIN (Meta/Windows key) modifier flag.</summary>
    public const int MOD_WIN = 0x0008;

    // ── Stable Global Hotkey ids ──

    /// <summary>Print Screen → Current Monitor.</summary>
    public const int IdPrintScreen = 1;
    /// <summary>Win + Print Screen → Active Window.</summary>
    public const int IdWinPrintScreen = 2;
    /// <summary>Shift + Print Screen → Full Desktop.</summary>
    public const int IdShiftPrintScreen = 3;
    /// <summary>Win + Shift + Print Screen → Rectangular Region.</summary>
    public const int IdWinShiftPrintScreen = 4;

    /// <summary>
    /// All four Global Hotkey specifications. Stable ids 1–4, all using VK_SNAPSHOT
    /// with varying modifier combinations.
    /// </summary>
    public static IReadOnlyList<GlobalHotkeySpec> AllSpecs { get; } = Array.AsReadOnly(new[]
    {
        new GlobalHotkeySpec
        {
            Id = IdPrintScreen,
            Name = "Print Screen",
            Modifiers = 0,                // no modifiers
            VirtualKey = VK_SNAPSHOT,
            Route = GlobalHotkeyRoute.CurrentMonitor,
        },
        new GlobalHotkeySpec
        {
            Id = IdWinPrintScreen,
            Name = "Win + Print Screen",
            Modifiers = MOD_WIN,
            VirtualKey = VK_SNAPSHOT,
            Route = GlobalHotkeyRoute.ActiveWindow,
        },
        new GlobalHotkeySpec
        {
            Id = IdShiftPrintScreen,
            Name = "Shift + Print Screen",
            Modifiers = MOD_SHIFT,
            VirtualKey = VK_SNAPSHOT,
            Route = GlobalHotkeyRoute.FullDesktop,
        },
        new GlobalHotkeySpec
        {
            Id = IdWinShiftPrintScreen,
            Name = "Win + Shift + Print Screen",
            Modifiers = MOD_WIN | MOD_SHIFT,
            VirtualKey = VK_SNAPSHOT,
            Route = GlobalHotkeyRoute.RectangularRegion,
        },
    });

    /// <summary>
    /// Looks up the capture route for a given Global Hotkey id (from WM_HOTKEY wParam).
    /// Returns true and sets <paramref name="route"/> for known ids; false for unknown.
    /// </summary>
    public static bool TryResolveRoute(int globalHotkeyId, out GlobalHotkeyRoute route)
    {
        foreach (var spec in AllSpecs)
        {
            if (spec.Id == globalHotkeyId)
            {
                route = spec.Route;
                return true;
            }
        }
        route = default;
        return false;
    }

    /// <summary>
    /// Finds the spec for a given Global Hotkey id. Returns null for unknown ids.
    /// </summary>
    public static GlobalHotkeySpec? FindSpec(int globalHotkeyId)
    {
        foreach (var spec in AllSpecs)
        {
            if (spec.Id == globalHotkeyId)
                return spec;
        }
        return null;
    }

    /// <summary>
    /// Computes the set of enabled Global Hotkey ids from persisted settings. A null
    /// states map (settings predating per-Global-Hotkey toggles) or a missing entry means
    /// the global hotkey is enabled, matching <see cref="AppSettings.IsGlobalHotkeyEnabled"/>. The
    /// single source of truth for "which Global Hotkeys should be active right now", used by
    /// both startup registration and the Global Hotkeys tab reconcile.
    /// </summary>
    public static IReadOnlySet<int> EnabledGlobalHotkeyIds(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var ids = new HashSet<int>();
        foreach (var spec in AllSpecs)
        {
            if (settings.IsGlobalHotkeyEnabled(spec.Id))
                ids.Add(spec.Id);
        }
        return ids;
    }
}
