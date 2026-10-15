// App.xaml.cs — Application startup. Loads persisted configuration, creates
// and activates the main window with resolved settings. Never crashes solely
// because settings are missing or corrupt — falls back to defaults with a warning.

using Microsoft.UI.Xaml;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// WinUI 3 application entry point.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// The loaded application settings, available after OnLaunched.
    /// Null before startup completes.
    /// </summary>
    internal AppSettings? LoadedSettings { get; private set; }

    /// <summary>
    /// The configuration load result from startup, available after OnLaunched.
    /// Null before startup completes. Exposes load warnings and backup paths.
    /// </summary>
    internal ConfigurationLoadResult? LoadResult { get; private set; }

    public App()
    {
        InitializeComponent();

        // Crash logging (issue #81): XAML-unhandled exceptions (async void
        // continuations, stowed exceptions rethrown on the dispatcher) land
        // here. e.Exception carries the full detail the truncated WER record
        // lacks; e.Message sometimes holds extra detail when the exception
        // itself is truncated (known XAML behavior) — record both, then let
        // the default teardown proceed. The handler itself never throws
        // (CrashLog swallows everything).
        UnhandledException += (sender, eventArgs) =>
            CrashLog.Write("UnhandledException", eventArgs.Exception, eventArgs.Message);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Load configuration — falls back to defaults on missing/corrupt file.
        // Never crashes the app for configuration issues.
        var configurationService = new ConfigurationService();
        var loadResult = configurationService.Load();

        LoadedSettings = loadResult.Settings;
        LoadResult = loadResult;

        _window = new MainWindow(loadResult.Settings, configurationService, loadResult);
        _window.Closed += OnWindowClosed;
        _window.Activate();
    }

    /// <summary>
    /// When the main window closes, terminate the application.
    /// WinUI 3 does not auto-exit when the last window closes — the dispatcher
    /// loop keeps the process alive indefinitely unless Application.Exit() is called.
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _window = null;
        Application.Current.Exit();
    }
}
