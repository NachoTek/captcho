// GlobalHotkeyBindingDefaults.cs — Default key combinations per capture route.
//
// The single source of the legacy hardcoded bindings (the combinations the app
// shipped with before remapping existed). Kept in the capture layer — keyed by
// GlobalHotkeyRoute, free of WinUI and Win32 hotkey ids — so Configuration
// resolves default bindings for pre-remap settings files (migration) and Reset
// without depending on the UI-layer GlobalHotkeyRouteMap.

using System.Collections.Generic;

namespace captcho.Capture;

/// <summary>
/// The default (legacy) <see cref="HotkeyBinding"/> for each
/// <see cref="GlobalHotkeyRoute"/>. A null bindings map (settings predating
/// remapping) or a missing entry resolves to these combinations, so existing
/// Configuration migrates to the legacy behavior without losing anything.
/// </summary>
public static class GlobalHotkeyBindingDefaults
{
    /// <summary>MOD_SHIFT — Win32 modifier flag.</summary>
    public const int MOD_SHIFT = 0x0004;
    /// <summary>MOD_WIN — Win32 modifier flag.</summary>
    public const int MOD_WIN = 0x0008;
    /// <summary>VK_SNAPSHOT (Print Screen) virtual key code.</summary>
    public const int VK_SNAPSHOT = 0x2C;

    /// <summary>
    /// Legacy combinations keyed by capture route. Values mirror the hardcoded
    /// specs the app shipped with (see the UI-layer GlobalHotkeyRouteMap, which
    /// builds its Win32 specs from the same combinations).
    /// </summary>
    public static readonly IReadOnlyDictionary<GlobalHotkeyRoute, HotkeyBinding> ByRoute =
        new Dictionary<GlobalHotkeyRoute, HotkeyBinding>
        {
            [GlobalHotkeyRoute.CurrentMonitor] = new(0, VK_SNAPSHOT),
            [GlobalHotkeyRoute.ActiveWindow] = new(MOD_WIN, VK_SNAPSHOT),
            [GlobalHotkeyRoute.FullDesktop] = new(MOD_SHIFT, VK_SNAPSHOT),
            [GlobalHotkeyRoute.RectangularRegion] = new(MOD_WIN | MOD_SHIFT, VK_SNAPSHOT),
        };

    /// <summary>
    /// Resolves the default binding for a route. Unknown routes resolve to the
    /// unmodified Print Screen combination (defensive; unreachable through the
    /// typed API).
    /// </summary>
    public static HotkeyBinding For(GlobalHotkeyRoute route) =>
        ByRoute.TryGetValue(route, out var binding)
            ? binding
            : new HotkeyBinding(0, VK_SNAPSHOT);
}
