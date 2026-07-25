// GlobalHotkeyAdapter.cs — Headless seam between the Hotkeys settings tab and the
// runtime hotkey registration.
//
// The HotkeysTabSettings coordinator depends on this interface (never on the Win32
// hotkey manager directly) so that persistence and runtime-registration behavior can
// be covered by headless tests with an injected fake. The production implementation
// delegates to HotkeyManager; a null adapter is used when hotkeys could not be
// initialized.

using System;
using System.Collections.Generic;

namespace captcho.UI;

/// <summary>
/// Reads current registration status for display and reconciles runtime
/// registration to match the user's per-hotkey enabled choices. Injectable so the
/// Hotkeys settings coordinator is testable without a Win32 hotkey manager.
/// </summary>
public interface IGlobalHotkeyAdapter
{
    /// <summary>
    /// Registration results for the most recent registration or reconcile. Empty
    /// before anything has been registered or after everything has been disabled.
    /// </summary>
    IReadOnlyList<HotkeyRegistrationResult> RegistrationResults { get; }

    /// <summary>
    /// Reconciles runtime registration so exactly the hotkeys in
    /// <paramref name="enabledIds"/> are registered: registers any enabled id that
    /// is not yet active and unregisters any disabled id. Returns the registration
    /// results for the enabled hotkeys (disabled hotkeys are intentionally omitted).
    /// Must not throw for expected registration conflicts.
    /// </summary>
    IReadOnlyList<HotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds);
}

/// <summary>
/// Production adapter that delegates to a <see cref="HotkeyManager"/> bound to a
/// specific window handle. The handle is captured at construction (when the main
/// window is available) so later reconcile calls re-register against the same
/// window without the settings UI needing to know about HWNDs.
/// </summary>
internal sealed class HotkeyManagerAdapter : IGlobalHotkeyAdapter
{
    private readonly HotkeyManager _manager;
    private readonly IntPtr _hwnd;

    public HotkeyManagerAdapter(HotkeyManager manager, IntPtr hwnd)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _hwnd = hwnd;
    }

    public IReadOnlyList<HotkeyRegistrationResult> RegistrationResults => _manager.RegistrationResults;

    public IReadOnlyList<HotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds)
        => _manager.Reconcile(_hwnd, enabledIds);
}

/// <summary>
/// No-op adapter used when global hotkeys could not be initialized (for example,
/// the main window handle was unavailable at startup). Reports no registrations and
/// makes reconcile a safe no-op so the settings UI still renders without crashing.
/// </summary>
internal sealed class NullGlobalHotkeyAdapter : IGlobalHotkeyAdapter
{
    private static readonly IReadOnlyList<HotkeyRegistrationResult> Empty =
        Array.Empty<HotkeyRegistrationResult>();

    public IReadOnlyList<HotkeyRegistrationResult> RegistrationResults => Empty;

    public IReadOnlyList<HotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds)
        => Empty;
}
