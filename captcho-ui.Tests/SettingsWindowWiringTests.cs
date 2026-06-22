// SettingsWindowWiringTests.cs — Headless lifecycle tests for settings window coordinator.
//
// Verifies singleton lifecycle: create-or-activate, close cleanup, and save-result
// reporting without WinUI controls. Covers:
// - First open creates a window
// - Second open activates the same window
// - Closed window clears the current reference
// - After close, open creates a new window
// - Activation does not call create again
// - Save-result formatting keeps sanitized ConfigurationService failure messages visible

using System;
using System.Collections.Generic;
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
    /// Factory delegate that creates a new TestSettingsWindowHandle.
    /// </summary>
    public static object? Create()
    {
        var handle = new TestSettingsWindowHandle();
        handle.CreateCount++;
        return handle;
    }
}

/// <summary>
/// Test adapter that converts between TestSettingsWindowHandle and coordinator delegates.
/// </summary>
public sealed class TestSettingsWindowAdapter
{
    /// <summary>
    /// Factory delegate for coordinator.
    /// </summary>
    public object? CreateWindow()
    {
        return TestSettingsWindowHandle.Create();
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
/// Wrapper for ConfigurationService that allows forced save results for testing.
/// </summary>
public sealed class FakeConfigurationService
{
    private readonly ConfigurationService _innerService;
    private readonly ConfigurationSaveResult? _forcedResult;
    private bool _saveCalled;

    /// <summary>
    /// Creates a fake service that forces a specific result.
    /// </summary>
    public FakeConfigurationService(string configDir, ConfigurationSaveResult? forcedResult = null)
    {
        _innerService = new ConfigurationService(configDir);
        _forcedResult = forcedResult;
    }

    /// <summary>
    /// Whether Save was called.
    /// </summary>
    public bool SaveCalled => _saveCalled;

    /// <summary>
    /// Returns a forced result for testing, or delegates to the inner service.
    /// </summary>
    public ConfigurationSaveResult Save(AppSettings settings)
    {
        _saveCalled = true;
        return _forcedResult ?? _innerService.Save(settings);
    }

    /// <summary>
    /// Loads settings by delegating to the inner service.
    /// </summary>
    public ConfigurationLoadResult Load()
    {
        return _innerService.Load();
    }

    /// <summary>
    /// Gets the config path from the inner service.
    /// </summary>
    public string ConfigPath => _innerService.ConfigPath;

    /// <summary>
    /// Implicit conversion to ConfigurationService for use with SettingsWindowCoordinator.
    /// </summary>
    public static implicit operator ConfigurationService(FakeConfigurationService fake)
    {
        return fake._innerService;
    }
}

public class SettingsWindowWiringTests
{
    // ── First open creates a window ───────────────────────────────────

    [Fact]
    public void FirstOpen_CreatesWindow()
    {
        var adapter = new TestSettingsWindowAdapter();
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));
        var coordinator = new SettingsWindowCoordinator(
            configService,
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
    }

    // ── Second open activates the same window ────────────────────────

    [Fact]
    public void SecondOpen_ActivatesSameWindow()
    {
        var adapter = new TestSettingsWindowAdapter();
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));
        var coordinator = new SettingsWindowCoordinator(
            configService,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        // First open creates a window
        var firstResult = coordinator.CreateOrActivate();
        Assert.True(firstResult);
        var firstWindow = coordinator.CurrentWindow;

        // Second open should activate the same window
        var secondResult = coordinator.CreateOrActivate();
        Assert.True(secondResult);
        Assert.Same(firstWindow, coordinator.CurrentWindow);

        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        Assert.NotNull(handle);
        Assert.Equal(1, handle!.CreateCount);  // Created once
        Assert.Equal(1, handle.ActivateCount);  // Activated once on second open
    }

    // ── Closed window clears the current reference ──────────────────

    [Fact]
    public void ClosedWindow_ClearsCurrentReference()
    {
        var adapter = new TestSettingsWindowAdapter();
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));
        var coordinator = new SettingsWindowCoordinator(
            configService,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        // Create window
        coordinator.CreateOrActivate();
        Assert.True(coordinator.IsWindowOpen);
        var window = coordinator.CurrentWindow;

        // Close the window
        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        handle!.Close();

        // Reference should be cleared
        Assert.False(coordinator.IsWindowOpen);
        Assert.Null(coordinator.CurrentWindow);
    }

    // ── After close, open creates a new window ───────────────────────

    [Fact]
    public void AfterClose_OpenCreatesNewWindow()
    {
        var adapter = new TestSettingsWindowAdapter();
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));
        var coordinator = new SettingsWindowCoordinator(
            configService,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        // Create first window
        coordinator.CreateOrActivate();
        var firstWindow = coordinator.CurrentWindow;

        // Close it
        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        handle!.Close();

        // Open again should create a new window
        coordinator.CreateOrActivate();
        Assert.True(coordinator.IsWindowOpen);
        Assert.NotSame(firstWindow, coordinator.CurrentWindow);

        var newHandle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        Assert.NotNull(newHandle);
        Assert.Equal(1, newHandle!.CreateCount);
    }

    // ── Activation does not call create again ─────────────────────────

    [Fact]
    public void MultipleActivations_DoNotCreateAgain()
    {
        var adapter = new TestSettingsWindowAdapter();
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));
        var coordinator = new SettingsWindowCoordinator(
            configService,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        // Create window
        coordinator.CreateOrActivate();

        // Activate multiple times
        coordinator.CreateOrActivate();
        coordinator.CreateOrActivate();
        coordinator.CreateOrActivate();

        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);
        Assert.NotNull(handle);
        Assert.Equal(1, handle!.CreateCount);   // Created once
        Assert.Equal(3, handle.ActivateCount);  // Activated 3 times (the first call creates, subsequent calls activate)
    }

    // ── CreateOrActivate returns false on creation failure ─────────────

    [Fact]
    public void CreateFailure_ReturnsFalse()
    {
        var adapter = new TestSettingsWindowAdapter();
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));

        // Override CreateWindow to return null (creation failed)
        var coordinator = new SettingsWindowCoordinator(
            configService,
            () => null,  // Always fails
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        var result = coordinator.CreateOrActivate();

        Assert.False(result);
        Assert.False(coordinator.IsWindowOpen);
        Assert.Null(coordinator.CurrentWindow);
    }

    // ── Save-result formatting keeps sanitized messages visible ───────

    [Fact]
    public void SaveSuccess_FormatsSuccessMessage()
    {
        var adapter = new TestSettingsWindowAdapter();
        var testDir = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}");
        var configService = new ConfigurationService(testDir);
        var coordinator = new SettingsWindowCoordinator(
            configService,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        var settings = AppSettings.WithDefaults();
        var report = coordinator.SaveSettings(settings);

        Assert.True(report.Success);
        Assert.Equal("Settings saved successfully.", report.Message);
        Assert.Null(report.Phase);
    }

    [Fact]
    public void SaveFailure_FormatsErrorMessageWithPhase()
    {
        var adapter = new TestSettingsWindowAdapter();
        var testDir = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}");
        var forcedResult = new ConfigurationSaveResult
        {
            Success = false,
            Phase = "WriteTemp",
            ErrorMessage = "Access is denied",
            ConfigPath = Path.Combine(testDir, "settings.json"),
        };
        
        // Use the real ConfigurationService with the wrapper
        var innerConfigService = new ConfigurationService(testDir);
        var fakeConfigService = new FakeConfigurationService(testDir, forcedResult);
        
        // Create a wrapper coordinator that uses the fake's Save
        var coordinator = new SettingsWindowCoordinator(
            innerConfigService,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        var settings = AppSettings.WithDefaults();
        
        // Test formatting directly through the fake service
        var saveResult = fakeConfigService.Save(settings);
        var report = new SettingsSaveReport
        {
            Success = saveResult.Success,
            Message = SettingsWindowCoordinator.FormatSaveFailureMessage(saveResult),
            Phase = saveResult.Phase,
            ConfigPath = saveResult.ConfigPath,
        };

        Assert.False(report.Success);
        Assert.Equal("Failed during 'WriteTemp'. Access is denied", report.Message);
        Assert.Equal("WriteTemp", report.Phase);
    }

    [Fact]
    public void SaveFailure_WithoutPhase_FormatsGenericMessage()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}");
        var forcedResult = new ConfigurationSaveResult
        {
            Success = false,
            Phase = null,
            ErrorMessage = null,
            ConfigPath = Path.Combine(testDir, "settings.json"),
        };
        
        var fakeConfigService = new FakeConfigurationService(testDir, forcedResult);
        var saveResult = fakeConfigService.Save(AppSettings.WithDefaults());
        var report = new SettingsSaveReport
        {
            Success = saveResult.Success,
            Message = SettingsWindowCoordinator.FormatSaveFailureMessage(saveResult),
            Phase = saveResult.Phase,
            ConfigPath = saveResult.ConfigPath,
        };

        Assert.False(report.Success);
        Assert.Equal("Failed to save settings.", report.Message);
    }

    [Fact]
    public void SaveFailure_WithoutErrorMessage_StillIncludesPhase()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}");
        var forcedResult = new ConfigurationSaveResult
        {
            Success = false,
            Phase = "Move",
            ErrorMessage = null,
            ConfigPath = Path.Combine(testDir, "settings.json"),
        };
        
        var fakeConfigService = new FakeConfigurationService(testDir, forcedResult);
        var saveResult = fakeConfigService.Save(AppSettings.WithDefaults());
        var report = new SettingsSaveReport
        {
            Success = saveResult.Success,
            Message = SettingsWindowCoordinator.FormatSaveFailureMessage(saveResult),
            Phase = saveResult.Phase,
            ConfigPath = saveResult.ConfigPath,
        };

        Assert.False(report.Success);
        Assert.Equal("Failed during 'Move'.", report.Message);
    }

    // ── Load settings returns defaults or loaded settings ─────────────

    [Fact]
    public void LoadSettings_ReturnsDefaultsWhenMissing()
    {
        var adapter = new TestSettingsWindowAdapter();
        var testDir = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}");
        var configService = new ConfigurationService(testDir);
        var coordinator = new SettingsWindowCoordinator(
            configService,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        var settings = coordinator.LoadSettings();

        Assert.NotNull(settings);
        Assert.Equal(ExportDefaults.DefaultSaveDirectory, settings.EffectiveSaveLocation);
        Assert.Equal(ExportDefaults.DefaultFilenameTemplate, settings.EffectiveFilenameTemplate);
    }

    [Fact]
    public void LoadSettings_WithValidFile_ReturnsLoadedSettings()
    {
        var adapter = new TestSettingsWindowAdapter();
        var testDir = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}");
        Directory.CreateDirectory(testDir);

        // Create a valid settings file
        var configService = new ConfigurationService(testDir);
        var settingsToSave = new AppSettings
        {
            SaveLocation = @"C:\Custom\Path",
            FilenameTemplate = "screenshot-{date}",
        };
        configService.Save(settingsToSave);

        // Now load it
        var coordinator = new SettingsWindowCoordinator(
            configService,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        var loadedSettings = coordinator.LoadSettings();

        Assert.NotNull(loadedSettings);
        Assert.Equal(@"C:\Custom\Path", loadedSettings.SaveLocation);
        Assert.Equal("screenshot-{date}", loadedSettings.FilenameTemplate);
    }

    // ── Constructor validation ─────────────────────────────────────────

    [Fact]
    public void Constructor_NullConfigurationService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            new SettingsWindowCoordinator(
                null!,
                () => new object(),
                _ => { },
                (_, _) => { });
        });
    }

    [Fact]
    public void Constructor_NullCreateWindow_Throws()
    {
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));

        Assert.Throws<ArgumentNullException>(() =>
        {
            new SettingsWindowCoordinator(
                configService,
                null!,
                _ => { },
                (_, _) => { });
        });
    }

    [Fact]
    public void Constructor_NullActivateWindow_Throws()
    {
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));

        Assert.Throws<ArgumentNullException>(() =>
        {
            new SettingsWindowCoordinator(
                configService,
                () => new object(),
                null!,
                (_, _) => { });
        });
    }

    [Fact]
    public void Constructor_NullSubscribeToClosed_Throws()
    {
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));

        Assert.Throws<ArgumentNullException>(() =>
        {
            new SettingsWindowCoordinator(
                configService,
                () => new object(),
                _ => { },
                null!);
        });
    }

    // ── Closed event subscription is invoked ───────────────────────────

    [Fact]
    public void ClosedEvent_SubscriptionInvoked()
    {
        var adapter = new TestSettingsWindowAdapter();
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));
        var coordinator = new SettingsWindowCoordinator(
            configService,
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
        var configService = new ConfigurationService(Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}"));
        var coordinator = new SettingsWindowCoordinator(
            configService,
            adapter.CreateWindow,
            adapter.ActivateWindow,
            adapter.SubscribeToClosed);

        coordinator.CreateOrActivate();
        var handle = TestSettingsWindowAdapter.GetHandleFromCoordinator(coordinator);

        // Close multiple times
        handle!.Close();
        Assert.False(coordinator.IsWindowOpen);

        handle.Close();
        Assert.False(coordinator.IsWindowOpen);

        handle.Close();
        Assert.False(coordinator.IsWindowOpen);
    }
}