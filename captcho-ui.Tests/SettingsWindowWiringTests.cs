// SettingsWindowWiringTests.cs — Headless lifecycle tests for the settings window
// coordinator.
//
// Verifies singleton lifecycle: create-or-activate, close cleanup, and that the
// coordinator constructs one session per window-open and hands it to the window
// factory. Does not exercise WinUI controls. Covers:
// - First open creates a window (and a session)
// - Second open activates the same window (no second session)
// - Closed window clears the current reference
// - After close, open creates a new window
// - Activation does not call create again
// - CreateOrActivate returns false on creation failure

using System;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

/// <summary>
/// Testable window handle that tracks lifecycle events for assertions.
/// Simulates a WinUI window without requiring WinUI runtime.
/// </summary>
public sealed class TestSettingsWindowHandle
{
    // Lifecycle tracking
    public bool Activated { get; private set; }
    public bool Closed { get; private set; }
    public int ActivateCount { get; private set; }
    public int CreateCount { get; private set; }
    public SettingsSession? Session { get; private set; }
    private Action? _onClosedCallback;

    /// <summary>
    /// Simulates window activation.
    /// </summary>
    public void Activate()
    {
        Activated = true;
        ActivateCount++;
    }

    /// <summary>
    /// Simulates window closing and invokes the closed callback.
    /// </summary>
    public void Close()
    {
        if (Closed)
            return; // Already closed

        Closed = true;
        _onClosedCallback?.Invoke();
    }

    /// <summary>
    /// Subscribes to the closed event.
    /// </summary>
    public void SubscribeToClosed(Action onClosed)
    {
        _onClosedCallback = onClosed;
    }

    /// <summary>
    /// Factory delegate that creates a new TestSettingsWindowHandle bound to a session.
    /// </summary>
    public static object? Create(SettingsSession session)
    {
        var handle = new TestSettingsWindowHandle();
        handle.CreateCount++;
        handle.Session = session;
        return handle;
    }
}

/// <summary>
/// Test adapter that converts between TestSettingsWindowHandle and coordinator delegates.
/// </summary>
public sealed class TestSettingsWindowAdapter
{
    public int SessionCreateCount { get; private set; }

    /// <summary>
    /// Session factory for the coordinator: returns a real session bound to an
    /// in-memory runtime and a temp-dir ConfigurationService.
    /// </summary>
    public SettingsSession CreateSession()
    {
        SessionCreateCount++;
        return new SettingsSession(
            new AppSettings(),
            new ConfigurationService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test_{Guid.NewGuid()}")),
            new NullGlobalHotkeyAdapterForTests());
    }

    /// <summary>
    /// Factory delegate for coordinator: creates a test window bound to the session.
    /// </summary>
    public object? CreateWindow(SettingsSession session)
    {
        return TestSettingsWindowHandle.Create(session);
    }

    /// <summary>
    /// Activation delegate for coordinator.
    /// </summary>
    public void ActivateWindow(object windowHandle)
    {
        if (windowHandle is TestSettingsWindowHandle handle)
        {
            handle.Activate();
        }
        else
        {
            throw new ArgumentException("Expected TestSettingsWindowHandle", nameof(windowHandle));
        }
    }

    /// <summary>
    /// Closed subscription delegate for coordinator.
    /// </summary>
    public void SubscribeToClosed(object windowHandle, Action onClosed)
    {
        if (windowHandle is TestSettingsWindowHandle handle)
        {
            handle.SubscribeToClosed(onClosed);
        }
        else
        {
            throw new ArgumentException("Expected TestSettingsWindowHandle", nameof(windowHandle));
        }
    }

    /// <summary>
    /// Helper to extract the test handle from a coordinator's CurrentWindow.
    /// </summary>
    public static TestSettingsWindowHandle? GetHandleFromCoordinator(SettingsWindowCoordinator coordinator)
    {
        return coordinator.CurrentWindow as TestSettingsWindowHandle;
    }
}

/// <summary>
/// No-op Global Hotkey adapter for wiring tests (the real NullGlobalHotkeyAdapter is
/// internal). Reports no registrations and no-op reconciles.
/// </summary>
internal sealed class NullGlobalHotkeyAdapterForTests : IGlobalHotkeyAdapter
{
    private static readonly IReadOnlyList<GlobalHotkeyRegistrationResult> Empty =
        Array.Empty<GlobalHotkeyRegistrationResult>();

    public IReadOnlyList<GlobalHotkeyRegistrationResult> RegistrationResults => Empty;

    public IReadOnlyList<GlobalHotkeyRegistrationResult> ApplyEnabledStates(IReadOnlySet<int> enabledIds)
        => Empty;
}

public class SettingsWindowWiringTests
{
    // ── First open creates a window and constructs one session ─────────

    [Fact]
    public void FirstOpen_CreatesWindowAndSession()
    {
        var adapter = new TestSettingsWindowAdapter();
        var coordinator = new SettingsWindowCoordinator(
            adapter.CreateSession,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        var result = coordinator.CreateOrActivate();

        Assert.True(result);
        Assert.True(coordinator.IsWindowOpen);
        Assert.NotNull(coordinator.CurrentWindow);

        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        Assert.NotNull(handle);
        Assert.Equal(1, handle!.CreateCount);
        Assert.Equal(0, handle.ActivateCount);
        Assert.NotNull(handle.Session);
        Assert.Equal(1, adapter.SessionCreateCount);
    }

    // ── Second open activates the same window without a new session ────

    [Fact]
    public void SecondOpen_ActivatesSameWindow_WithoutNewSession()
    {
        var adapter = new TestSettingsWindowAdapter();
        var coordinator = new SettingsWindowCoordinator(
            adapter.CreateSession,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        coordinator.CreateOrActivate();
        var firstWindow = coordinator.CurrentWindow;

        coordinator.CreateOrActivate();

        Assert.Same(firstWindow, coordinator.CurrentWindow);
        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        Assert.NotNull(handle);
        Assert.Equal(1, handle!.CreateCount);
        Assert.Equal(1, handle.ActivateCount);
        Assert.Equal(1, adapter.SessionCreateCount); // no second session on activate
    }

    // ── Closed window clears the current reference ─────────────────────

    [Fact]
    public void ClosedWindow_ClearsCurrentReference()
    {
        var adapter = new TestSettingsWindowAdapter();
        var coordinator = new SettingsWindowCoordinator(
            adapter.CreateSession,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        coordinator.CreateOrActivate();
        Assert.True(coordinator.IsWindowOpen);

        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        handle!.Close();

        Assert.False(coordinator.IsWindowOpen);
        Assert.Null(coordinator.CurrentWindow);
    }

    // ── After close, open creates a new window and session ─────────────

    [Fact]
    public void AfterClose_OpenCreatesNewWindowAndSession()
    {
        var adapter = new TestSettingsWindowAdapter();
        var coordinator = new SettingsWindowCoordinator(
            adapter.CreateSession,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        coordinator.CreateOrActivate();
        var firstWindow = coordinator.CurrentWindow;

        var firstHandle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        firstHandle!.Close();

        coordinator.CreateOrActivate();
        Assert.True(coordinator.IsWindowOpen);
        Assert.NotSame(firstWindow, coordinator.CurrentWindow);
        Assert.Equal(2, adapter.SessionCreateCount); // a fresh session per open
    }

    // ── Activation does not call create again ──────────────────────────

    [Fact]
    public void MultipleActivations_DoNotCreateAgain()
    {
        var adapter = new TestSettingsWindowAdapter();
        var coordinator = new SettingsWindowCoordinator(
            adapter.CreateSession,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        coordinator.CreateOrActivate();
        coordinator.CreateOrActivate();
        coordinator.CreateOrActivate();
        coordinator.CreateOrActivate();

        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        Assert.NotNull(handle);
        Assert.Equal(1, handle!.CreateCount);
        Assert.Equal(3, handle.ActivateCount);
        Assert.Equal(1, adapter.SessionCreateCount);
    }

    // ── CreateOrActivate returns false on creation failure ─────────────

    [Fact]
    public void CreateFailure_ReturnsFalse()
    {
        var adapter = new TestSettingsWindowAdapter();
        var coordinator = new SettingsWindowCoordinator(
            adapter.CreateSession,
            _ => null,  // window creation always fails
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        var result = coordinator.CreateOrActivate();

        Assert.False(result);
        Assert.False(coordinator.IsWindowOpen);
        Assert.Null(coordinator.CurrentWindow);
    }

    // ── Closed event subscription is invoked ────────────────────────────

    [Fact]
    public void ClosedEvent_SubscriptionInvoked()
    {
        var adapter = new TestSettingsWindowAdapter();
        var coordinator = new SettingsWindowCoordinator(
            adapter.CreateSession,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        coordinator.CreateOrActivate();
        Assert.True(coordinator.IsWindowOpen);

        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        handle!.Close();

        Assert.False(coordinator.IsWindowOpen);
        Assert.Null(coordinator.CurrentWindow);
    }

    // ── Multiple close invocations are safe ─────────────────────────────

    [Fact]
    public void MultipleCloseInvocations_AreSafe()
    {
        var adapter = new TestSettingsWindowAdapter();
        var coordinator = new SettingsWindowCoordinator(
            adapter.CreateSession,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        coordinator.CreateOrActivate();
        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);

        handle!.Close();
        Assert.False(coordinator.IsWindowOpen);

        handle.Close();
        Assert.False(coordinator.IsWindowOpen);

        handle.Close();
        Assert.False(coordinator.IsWindowOpen);
    }

    // ── Constructor validation ─────────────────────────────────────────

    [Fact]
    public void Constructor_NullCreateSession_Throws()
    {
        var adapter = new TestSettingsWindowAdapter();
        Assert.Throws<ArgumentNullException>(() =>
            new SettingsWindowCoordinator(
                null!,
                adapter.CreateWindow,
                adapter.ActivateWindow,
                adapter.SubscribeToClosed));
    }

    [Fact]
    public void Constructor_NullCreateWindow_Throws()
    {
        var adapter = new TestSettingsWindowAdapter();
        Assert.Throws<ArgumentNullException>(() =>
            new SettingsWindowCoordinator(
                adapter.CreateSession,
                null!,
                adapter.ActivateWindow,
                adapter.SubscribeToClosed));
    }

    [Fact]
    public void Constructor_NullActivateWindow_Throws()
    {
        var adapter = new TestSettingsWindowAdapter();
        Assert.Throws<ArgumentNullException>(() =>
            new SettingsWindowCoordinator(
                adapter.CreateSession,
                adapter.CreateWindow,
                null!,
                adapter.SubscribeToClosed));
    }

    [Fact]
    public void Constructor_NullSubscribeToClosed_Throws()
    {
        var adapter = new TestSettingsWindowAdapter();
        Assert.Throws<ArgumentNullException>(() =>
            new SettingsWindowCoordinator(
                adapter.CreateSession,
                adapter.CreateWindow,
                adapter.ActivateWindow,
                null!));
    }
}
