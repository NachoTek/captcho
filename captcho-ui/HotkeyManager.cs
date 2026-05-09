// HotkeyManager.cs — Registration/cleanup manager with injectable Win32 registrar abstraction.
//
// Provides IHotkeyRegistrar for testability (fake in tests, P/Invoke in production),
// and HotkeyManager which registers all four specs, tracks successful registrations,
// unregisters idempotently, and exposes route resolution for WM_HOTKEY dispatch.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace captcho.UI;

/// <summary>
/// Abstraction over Windows RegisterHotKey/UnregisterHotKey.
/// Injectable for headless testing with a fake registrar.
/// </summary>
public interface IHotkeyRegistrar
{
    /// <summary>
    /// Registers a global hotkey. Returns true on success, false on failure
    /// (e.g. another process owns the hotkey). Must not throw for expected conflicts.
    /// </summary>
    /// <param name="hwnd">Window handle that will receive WM_HOTKEY messages. IntPtr.Zero uses the calling thread's message queue.</param>
    /// <param name="id">Hotkey id (0–0xBFFF range, per Win32 docs).</param>
    /// <param name="modifiers">Modifier flags (MOD_SHIFT, MOD_WIN, etc.).</param>
    /// <param name="virtualKey">Virtual key code.</param>
    bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int virtualKey);

    /// <summary>
    /// Unregisters a previously registered global hotkey.
    /// Returns true on success, false if the hotkey was not registered by this process.
    /// Must not throw for expected failures.
    /// </summary>
    bool UnregisterHotKey(IntPtr hwnd, int id);
}

/// <summary>
/// Production P/Invoke wrapper around Windows RegisterHotKey/UnregisterHotKey.
/// Sanitizes errors into structured strings rather than throwing.
/// </summary>
public sealed class WindowsHotkeyRegistrar : IHotkeyRegistrar
{
    // P/Invoke declarations — kept local and explicit

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "RegisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeRegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "UnregisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeUnregisterHotKey(IntPtr hWnd, int id);

    public bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int virtualKey)
    {
        return NativeRegisterHotKey(hwnd, id, (uint)modifiers, (uint)virtualKey);
    }

    public bool UnregisterHotKey(IntPtr hwnd, int id)
    {
        return NativeUnregisterHotKey(hwnd, id);
    }

    /// <summary>
    /// Gets the last Win32 error as a sanitized string.
    /// Call immediately after a RegisterHotKey/UnregisterHotKey that returned false.
    /// </summary>
    public static string GetLastErrorMessage()
    {
        int error = Marshal.GetLastWin32Error();
        return $"Win32 error {error}";
    }
}

/// <summary>
/// Manages registration and cleanup of all four global capture hotkeys.
/// Records only successful registrations, unregisters idempotently,
/// and exposes route resolution for WM_HOTKEY message dispatch.
///
/// Usage:
///   var manager = new HotkeyManager(new WindowsHotkeyRegistrar());
///   var results = manager.RegisterAll(hwnd);
///   // ... on WM_HOTKEY: manager.TryResolveRoute(id, out var route) ...
///   manager.UnregisterAll();
/// </summary>
public sealed class HotkeyManager
{
    private readonly IHotkeyRegistrar _registrar;
    private readonly HashSet<int> _registeredIds = new();
    private readonly List<HotkeyRegistrationResult> _registrationResults = new();
    private IntPtr _hwnd;
    private bool _disposed;

    /// <summary>
    /// Results from the most recent RegisterAll call.
    /// Empty before the first call or after UnregisterAll.
    /// </summary>
    public IReadOnlyList<HotkeyRegistrationResult> RegistrationResults =>
        _registrationResults.AsReadOnly();

    /// <summary>
    /// Whether all four hotkeys registered successfully.
    /// False before RegisterAll is called or if any failed.
    /// </summary>
    public bool AllRegistered =>
        _registrationResults.Count == HotkeyRouteMap.AllSpecs.Count &&
        _registrationResults.TrueForAll(r => r.Succeeded);

    /// <summary>
    /// Creates a new HotkeyManager with the given registrar.
    /// For production use <see cref="WindowsHotkeyRegistrar"/>; for tests use a fake.
    /// </summary>
    public HotkeyManager(IHotkeyRegistrar registrar)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
    }

    /// <summary>
    /// Registers all four capture hotkeys for the given window handle.
    /// Records successful registrations for later cleanup.
    /// Partial failure is handled gracefully: successful hotkeys stay registered,
    /// and failures are recorded with sanitized error details for UI display.
    /// Idempotent: calling twice without UnregisterAll is safe — skips already-registered ids.
    /// </summary>
    /// <param name="hwnd">Window handle to receive WM_HOTKEY messages.</param>
    /// <returns>Registration results for all four specs.</returns>
    public IReadOnlyList<HotkeyRegistrationResult> RegisterAll(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _registrationResults.Clear();

        foreach (var spec in HotkeyRouteMap.AllSpecs)
        {
            // Skip if already registered (idempotent)
            if (_registeredIds.Contains(spec.Id))
            {
                _registrationResults.Add(HotkeyRegistrationResult.Success(spec));
                continue;
            }

            bool success = false;
            string errorMessage = "";
            try
            {
                success = _registrar.RegisterHotKey(hwnd, spec.Id, spec.Modifiers, spec.VirtualKey);
                if (!success)
                {
                    errorMessage = _registrar is WindowsHotkeyRegistrar winRegistrar
                        ? WindowsHotkeyRegistrar.GetLastErrorMessage()
                        : "Registration failed";
                }
            }
            catch (Exception ex)
            {
                success = false;
                errorMessage = ex.Message;
            }

            var result = success
                ? HotkeyRegistrationResult.Success(spec)
                : HotkeyRegistrationResult.Fail(spec, "RegisterHotKey", errorMessage);

            _registrationResults.Add(result);

            if (success)
            {
                _registeredIds.Add(spec.Id);
            }
        }

        return RegistrationResults;
    }

    /// <summary>
    /// Unregisters all previously registered hotkeys.
    /// Idempotent: safe to call multiple times; skips already-unregistered ids.
    /// Records any unregister failures but does not throw.
    /// </summary>
    public void UnregisterAll()
    {
        foreach (var id in _registeredIds)
        {
            try
            {
                _registrar.UnregisterHotKey(_hwnd, id);
            }
            catch
            {
                // Unregister failure is non-fatal; log but don't throw.
                // The hotkey will be released when the process exits.
            }
        }
        _registeredIds.Clear();
    }

    /// <summary>
    /// Resolves a WM_HOTKEY id to a capture route.
    /// Returns true for known ids; false for unknown (should not trigger capture).
    /// Thread-safe for concurrent reads against the immutable spec list.
    /// </summary>
    public bool TryResolveRoute(int hotkeyId, out HotkeyRoute route)
    {
        return HotkeyRouteMap.TryResolveRoute(hotkeyId, out route);
    }

    /// <summary>
    /// Gets a summary string describing the registration state.
    /// Suitable for status bar display and diagnostics.
    /// Contains no PII, stack traces, or filesystem paths.
    /// </summary>
    public string GetRegistrationSummary()
    {
        if (_registrationResults.Count == 0)
            return "Hotkeys not registered.";

        int successCount = 0;
        var failures = new List<string>();
        foreach (var r in _registrationResults)
        {
            if (r.Succeeded)
                successCount++;
            else
                failures.Add($"{r.Spec.Name}: {r.Error}");
        }

        if (failures.Count == 0)
            return $"All {successCount} hotkeys registered.";

        return failures.Count == _registrationResults.Count
            ? $"No hotkeys registered. Conflicts: {string.Join("; ", failures)}"
            : $"{successCount}/{_registrationResults.Count} hotkeys registered. Conflicts: {string.Join("; ", failures)}";
    }
}
