// HotkeyBinding.cs — Persisted key combination for a Global Hotkey.
//
// The Configuration-level identity of a remappable binding: Win32 modifier flags
// and a virtual key code. Value type (record) so bindings compare by combination,
// independent of which Global Hotkey owns them. Lives in the capture layer next
// to Configuration so AppSettings can persist bindings without depending on the
// UI-layer GlobalHotkeyRouteMap (Win32 ids, binding names).

namespace captcho.Capture;

/// <summary>
/// A key combination for a Global Hotkey: Win32 modifier flags (MOD_ALT,
/// MOD_CONTROL, MOD_SHIFT, MOD_WIN, composable with |) and a virtual key code.
/// The persisted unit of Global Hotkey remapping.
/// </summary>
public readonly record struct HotkeyBinding(int Modifiers, int VirtualKey)
{
    /// <summary>Combination equality: modifiers and virtual key both match.</summary>
    public bool Equals(HotkeyBinding other) =>
        Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;

    /// <summary>Invariant for value equality of the record struct.</summary>
    public override int GetHashCode() => HashCode.Combine(Modifiers, VirtualKey);
}
