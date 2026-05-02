// MainWindow.xaml.cs — Button handlers, global hotkey routing, and UI state wiring
// for the capture preview window.
//
// Handles button clicks by disabling controls, running capture asynchronously via
// CapturePreviewService (optionally after a configurable delay countdown), then
// updating the preview image, status, and timing text.
// Export buttons (Save, Save As…, Copy) are wired to the export service methods
// and disabled until a capture succeeds and while export/capture/countdown is running.
// Global hotkeys (Print Screen, Win+Print, Shift+Print, Win+Shift+Print) are registered
// on startup via RegisterHotKey and routed through WM_HOTKEY to the same capture workflows.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Respectacle.Capture;
using WinRT.Interop;

namespace Respectacle.UI;

/// <summary>
/// Main window for the Respectacle capture preview application.
/// Provides Full Desktop, Current Monitor, Active Window, Window Under Cursor,
/// and Rectangular Region capture buttons with optional delayed capture countdown.
/// Export buttons (Save, Save As…, Copy to Clipboard) are enabled after a
/// successful capture and disabled during capture/export/countdown operations.
/// </summary>
public sealed partial class MainWindow : Window
{
    // ── Configuration fields ─────────────────────────────────────────

    /// <summary>
    /// Persisted application settings for save location and filename template.
    /// Initialized from ConfigurationService.Load() during startup.
    /// </summary>
    private readonly AppSettings _settings;

    /// <summary>
    /// Configuration service for persisting settings changes (e.g., after Save As).
    /// Null in design-time/test scenarios where persistence is not needed.
    /// </summary>
    private readonly ConfigurationService? _configService;

    /// <summary>
    /// The configuration load result from startup, exposing load warnings and backup paths.
    /// Null in design-time/test scenarios.
    /// </summary>
    private readonly ConfigurationLoadResult? _loadResult;

    private readonly CapturePreviewService _captureService = new();

    /// <summary>
    /// Tracks whether a capture has succeeded and the cached bitmap is available for export.
    /// Export buttons are only enabled when this is true and no operation is running.
    /// </summary>
    private bool _hasCapture;

    /// <summary>
    /// Tracks whether an export or capture/countdown operation is currently running,
    /// used to disable buttons during work to prevent overlapping operations.
    /// </summary>
    private bool _isOperationRunning;

    /// <summary>
    /// Active cancellation token source for the current delayed capture operation.
    /// One per operation — disposed in finally.
    /// </summary>
    private CancellationTokenSource? _activeCts;

    // ── Hotkey fields ─────────────────────────────────────────────────

    /// <summary>
    /// Manages registration and cleanup of all four global capture hotkeys.
    /// Null before initialization or after disposal.
    /// </summary>
    private HotkeyManager? _hotkeyManager;

    /// <summary>
    /// Original window procedure before subclassing. Used to restore on cleanup.
    /// </summary>
    private IntPtr _originalWndProc;

    /// <summary>
    /// Window handle used for hotkey registration and subclassing.
    /// </summary>
    private IntPtr _hwnd;

    /// <summary>
    /// Guards against double-cleanup of hotkey resources (UnregisterAll + subclass removal).
    /// </summary>
    private bool _hotkeyCleanedUp;

    // ── P/Invoke for window subclassing ───────────────────────────────

    /// <summary>
    /// Standard Win32 window procedure delegate (4 params, no ref bool).
    /// </summary>
    private delegate IntPtr NativeWndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "CallWindowProc")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const int GWLP_WNDPROC = -4;

    /// <summary>
    /// Keeps the managed delegate alive (prevents GC collection while subclassed).
    /// </summary>
    private NativeWndProcDelegate? _wndProcDelegate;

    /// <summary>
    /// Parameterless constructor for design-time and test scenarios.
    /// Uses default AppSettings without configuration persistence.
    /// </summary>
    public MainWindow() : this(AppSettings.WithDefaults(), null, null)
    {
    }

    /// <summary>
    /// Production constructor that receives loaded settings and configuration service.
    /// Falls back to defaults safely if settings are null.
    /// </summary>
    /// <param name="settings">Loaded application settings (may be defaults).</param>
    /// <param name="configService">Configuration service for persisting settings changes.</param>
    /// <param name="loadResult">Configuration load result with optional warnings.</param>
    public MainWindow(AppSettings settings, ConfigurationService? configService, ConfigurationLoadResult? loadResult)
    {
        _settings = settings ?? AppSettings.WithDefaults();
        _configService = configService;
        _loadResult = loadResult;

        InitializeComponent();
        Title = "Respectacle — Screen Capture Preview";

        // Set a reasonable default window size
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 700));

        // Initialize hotkeys after the window has an HWND.
        // In WinUI 3, the HWND is available immediately after construction.
        InitializeHotkeys();

        // Ensure cleanup on window close
        this.Closed += OnWindowClosed;
    }

    // ── Hotkey initialization and cleanup ─────────────────────────────

    /// <summary>
    /// Registers all four global hotkeys and installs a WndProc subclass for WM_HOTKEY.
    /// Reports partial registration conflicts in status text without throwing.
    /// </summary>
    private void InitializeHotkeys()
    {
        try
        {
            // Show configuration warning if settings load had issues
            if (_loadResult != null && _loadResult.UsedDefaults && _loadResult.ErrorMessage != null)
            {
                StatusText.Text = $"Settings: {_loadResult.ErrorMessage}";
            }

            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (_hwnd == IntPtr.Zero)
            {
                StatusText.Text = "Hotkeys: window handle unavailable — keyboard shortcuts disabled.";
                return;
            }

            _hotkeyManager = new HotkeyManager(new WindowsHotkeyRegistrar());
            var results = _hotkeyManager.RegisterAll(_hwnd);

            // Install WndProc subclass to intercept WM_HOTKEY messages.
            // Keep the delegate alive to prevent GC collection while subclassed.
            _wndProcDelegate = new NativeWndProcDelegate(SubclassedWndProc);
            _originalWndProc = SetWindowLongPtr64(_hwnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

            // Report registration status
            StatusText.Text = _hotkeyManager.GetRegistrationSummary();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Hotkeys: setup failed — {SanitizeException(ex)}";
        }
    }

    /// <summary>
    /// Subclassed window procedure that intercepts WM_HOTKEY and delegates all other
    /// messages to the original window proc. Unknown hotkey ids are silently ignored.
    /// </summary>
    private IntPtr SubclassedWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == HotkeyRouteMap.WM_HOTKEY && _hotkeyManager != null)
        {
            int hotkeyId = wParam.ToInt32();
            if (_hotkeyManager.TryResolveRoute(hotkeyId, out HotkeyRoute route))
            {
                // Dispatch on the UI thread via DispatcherQueue
                DispatcherQueue.TryEnqueue(() => DispatchHotkeyRoute(route));
            }
            // Unknown hotkey ids are silently ignored — not an error.
            // Still call original WndProc for WM_HOTKEY to maintain default handling.
        }

        return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Dispatches a hotkey route to the appropriate capture workflow.
    /// Respects the _isOperationRunning guard to prevent overlapping captures.
    /// </summary>
    private void DispatchHotkeyRoute(HotkeyRoute route)
    {
        if (_isOperationRunning)
        {
            // Overlapping triggers during an active capture/export are ignored.
            // This prevents the 10x breakpoint from repeated Print Screen presses.
            return;
        }

        switch (route)
        {
            case HotkeyRoute.CurrentMonitor:
                _ = RunDelayedCaptureAsync(_captureService.CaptureCurrentMonitorAsync);
                break;
            case HotkeyRoute.ActiveWindow:
                _ = RunDelayedCaptureAsync(_captureService.CaptureActiveWindowAsync);
                break;
            case HotkeyRoute.FullDesktop:
                _ = RunDelayedCaptureAsync(_captureService.CaptureFullDesktopAsync);
                break;
            case HotkeyRoute.RectangularRegion:
                _ = RunDelayedRegionCaptureAsync();
                break;
        }
    }

    /// <summary>
    /// Cleans up hotkey registrations and removes the WndProc subclass.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    private void CleanupHotkeys()
    {
        if (_hotkeyCleanedUp) return;
        _hotkeyCleanedUp = true;

        try
        {
            // Restore original window proc before unregistering hotkeys
            if (_hwnd != IntPtr.Zero && _originalWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr64(_hwnd, GWLP_WNDPROC, _originalWndProc);
                _originalWndProc = IntPtr.Zero;
            }

            _wndProcDelegate = null;
        }
        catch
        {
            // Subclass removal failure is non-fatal; the subclass will be released
            // when the window is destroyed.
        }

        try
        {
            _hotkeyManager?.UnregisterAll();
        }
        catch
        {
            // Unregister failure is non-fatal; hotkeys are released when
            // the process exits.
        }
    }

    /// <summary>
    /// Handles window close event — ensures hotkeys are unregistered exactly once.
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        CleanupHotkeys();
        this.Closed -= OnWindowClosed;
    }

    // ── Capture button click handlers ────────────────────────────────

    /// <summary>Handles "Full Desktop" click.</summary>
    private async void FullDesktop_Click(object sender, RoutedEventArgs e)
    {
        await RunDelayedCaptureAsync(_captureService.CaptureFullDesktopAsync);
    }

    /// <summary>Handles "Current Monitor" click.</summary>
    private async void CurrentMonitor_Click(object sender, RoutedEventArgs e)
    {
        await RunDelayedCaptureAsync(_captureService.CaptureCurrentMonitorAsync);
    }

    /// <summary>Handles "Active Window" click.</summary>
    private async void ActiveWindow_Click(object sender, RoutedEventArgs e)
    {
        await RunDelayedCaptureAsync(_captureService.CaptureActiveWindowAsync);
    }

    /// <summary>Handles "Window Under Cursor" click.</summary>
    private async void WindowUnderCursor_Click(object sender, RoutedEventArgs e)
    {
        await RunDelayedCaptureAsync(_captureService.CaptureWindowUnderCursorAsync);
    }

    /// <summary>Handles "Rectangular Region" click.</summary>
    private async void RegionCapture_Click(object sender, RoutedEventArgs e)
    {
        await RunDelayedRegionCaptureAsync();
    }

    /// <summary>Handles "Cancel" button click during countdown.</summary>
    private void CancelDelay_Click(object sender, RoutedEventArgs e)
    {
        _activeCts?.Cancel();
    }

    // ── Export button click handlers ─────────────────────────────────

    /// <summary>
    /// Handles "Save" click — saves to the configured save location using the
    /// configured filename template. Falls back to defaults if not configured.
    /// Disables all controls during save.
    /// </summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_isOperationRunning || !_hasCapture)
        {
            StatusText.Text = ExportStatusFormatter.FormatNoCapture();
            return;
        }

        EnterExportState();
        StatusText.Text = "Saving…";

        try
        {
            var result = await Task.Run(() =>
                _captureService.SaveLastCaptureWithSettingsAsync(_settings));
            StatusText.Text = ExportStatusFormatter.FormatStatus(result);
            TimingText.Text = FormatTimingWithExport(result, null);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {SanitizeException(ex)}";
            TimingText.Text = "";
        }
        finally
        {
            ExitExportState();
        }
    }

    /// <summary>
    /// Handles "Save As…" click — opens a FileSavePicker initialized with
    /// the configured filename template, then saves to the selected path.
    /// After successful save, persists the selected directory as the new
    /// default save location. Does not persist on cancel or failure.
    /// Uses WinRT.Interop.InitializeWithWindow for desktop HWND initialization.
    /// </summary>
    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_isOperationRunning || !_hasCapture)
        {
            StatusText.Text = ExportStatusFormatter.FormatNoCapture();
            return;
        }

        EnterExportState();
        StatusText.Text = "Choose save location…";

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();

            // Initialize the picker with the window's HWND for WinUI desktop
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add("PNG Image", new List<string> { ".png" });
            picker.DefaultFileExtension = ".png";
            picker.SuggestedFileName = ExportFilenameTemplate.Expand(
                _settings.EffectiveFilenameTemplate, DateTime.Now);

            var file = await picker.PickSaveFileAsync();

            if (file == null)
            {
                // User cancelled the picker — not an error, do NOT persist settings
                StatusText.Text = ExportStatusFormatter.FormatPickerCancelled();
                TimingText.Text = "";
                return;
            }

            StatusText.Text = "Saving…";
            var result = await Task.Run(() =>
                _captureService.SaveLastCaptureToFileAsync(file.Path));

            StatusText.Text = ExportStatusFormatter.FormatStatus(result);
            TimingText.Text = FormatTimingWithExport(result, null);

            // Persist the selected directory after successful save only
            if (result.Success && _configService != null)
            {
                PersistSaveAsDirectory(Path.GetDirectoryName(file.Path));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {SanitizeException(ex)}";
            TimingText.Text = "";
        }
        finally
        {
            ExitExportState();
        }
    }

    /// <summary>
    /// Persists the Save As directory as the new default save location.
    /// Failures are non-fatal — the export already succeeded.
    /// </summary>
    private void PersistSaveAsDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || _configService == null)
            return;

        try
        {
            var updatedSettings = new AppSettings
            {
                SaveLocation = directory,
                FilenameTemplate = _settings.FilenameTemplate,
            };

            var saveResult = _configService.Save(updatedSettings);

            if (saveResult.Success)
            {
                // Update in-memory settings to match what was persisted
                _settings.SaveLocation = directory;
            }
            // Save failure is non-fatal — the export already succeeded.
            // The user can still see the exported file in the status text.
        }
        catch
        {
            // Configuration save failure must not crash the app or
            // prevent the user from seeing their successful export.
        }
    }

    /// <summary>
    /// Handles "Copy" click — copies the last capture to the clipboard.
    /// Uses the ClipboardExportService wired to the capture service cache.
    /// </summary>
    private async void CopyToClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (_isOperationRunning || !_hasCapture)
        {
            StatusText.Text = ExportStatusFormatter.FormatNoCapture();
            return;
        }

        EnterExportState();
        StatusText.Text = "Copying to clipboard…";

        try
        {
            var clipboardAdapter = new WindowsClipboardAdapter();
            var exporter = _captureService.CreateClipboardExporter(clipboardAdapter);

            // ClipboardExportService.CopyToClipboard is synchronous — offload to avoid UI jank
            var result = await Task.Run(() => exporter.CopyToClipboard());

            StatusText.Text = ExportStatusFormatter.FormatStatus(result);
            TimingText.Text = FormatTimingWithExport(null, result);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Clipboard failed: {SanitizeException(ex)}";
            TimingText.Text = "";
        }
        finally
        {
            ExitExportState();
        }
    }

    // ── Delayed capture orchestration ────────────────────────────────

    /// <summary>
    /// Runs a standard capture with optional delay countdown.
    /// Delay 0 bypasses countdown UI and calls capture immediately.
    /// Delay > 0 shows countdown, then calls capture after completion.
    /// All paths restore controls in finally.
    /// </summary>
    private async Task RunDelayedCaptureAsync(Func<Task<CapturePreviewResult>> captureFunc)
    {
        int delaySeconds = GetNormalizedDelaySeconds();

        EnterCaptureState();
        _activeCts = new CancellationTokenSource();

        try
        {
            // Countdown phase
            if (delaySeconds > 0)
            {
                await RunCountdownPhaseAsync(delaySeconds, _activeCts.Token);
            }

            // Capture phase
            StatusText.Text = "Capturing…";
            CaptureProgress.Visibility = Visibility.Visible;

            var result = await captureFunc();
            ApplyCaptureResult(result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Countdown cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            TimingText.Text = "";
        }
        finally
        {
            ExitCaptureState();
        }
    }

    /// <summary>
    /// Runs the region capture flow with optional delay countdown.
    /// Delay completes before the overlay opens; overlay behavior is unchanged.
    /// </summary>
    private async Task RunDelayedRegionCaptureAsync()
    {
        int delaySeconds = GetNormalizedDelaySeconds();

        EnterCaptureState();
        _activeCts = new CancellationTokenSource();

        try
        {
            // Countdown phase (same as standard captures)
            if (delaySeconds > 0)
            {
                await RunCountdownPhaseAsync(delaySeconds, _activeCts.Token);
            }

            // Region selector overlay phase
            StatusText.Text = "Select a region on screen…";
            CaptureProgress.Visibility = Visibility.Visible;

            var overlay = new RegionOverlayWindow();
            Rect? result = await overlay.ShowAndWaitAsync();

            if (result.HasValue)
            {
                // Confirmed selection — run region capture
                StatusText.Text = "Capturing region…";
                var captureResult = await _captureService.CaptureRegionAsync(result.Value);
                ApplyCaptureResult(captureResult);
            }
            else
            {
                // Cancelled / null selection
                StatusText.Text = RegionSelectionStatusFormatter.FormatCancelled();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Countdown cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = RegionSelectionStatusFormatter.FormatError(ex);
        }
        finally
        {
            ExitCaptureState();
        }
    }

    // ── Countdown phase ──────────────────────────────────────────────

    /// <summary>
    /// Runs the countdown, updating title, status, and progress each tick.
    /// Throws OperationCanceledException if cancelled.
    /// </summary>
    private async Task RunCountdownPhaseAsync(int delaySeconds, CancellationToken ct)
    {
        CancelDelayButton.Visibility = Visibility.Visible;
        CountdownProgress.Visibility = Visibility.Visible;

        var countdown = new DelayedCaptureCountdown(delaySeconds);

        await foreach (var snapshot in countdown.RunAsync(ct))
        {
            Title = snapshot.TitleText;
            StatusText.Text = snapshot.StatusText;
            CountdownProgress.Value = snapshot.ProgressPercent;
        }
    }

    // ── UI state helpers ─────────────────────────────────────────────

    /// <summary>
    /// Normalizes the NumberBox value to an integer clamped 0–60.
    /// Handles NaN, negative, and out-of-range values.
    /// </summary>
    private int GetNormalizedDelaySeconds()
    {
        double raw = DelaySecondsInput.Value;
        if (double.IsNaN(raw))
            return 0;
        return Math.Clamp((int)Math.Round(raw), 0, 60);
    }

    /// <summary>
    /// Disables all capture/selector/export buttons, hides delay input,
    /// resets progress and title for a new capture operation.
    /// </summary>
    private void EnterCaptureState()
    {
        _isOperationRunning = true;
        SetButtonsEnabled(false);
        SetExportButtonsEnabled(false);
        DelaySecondsInput.IsEnabled = false;
        CountdownProgress.Visibility = Visibility.Collapsed;
        CountdownProgress.Value = 0;
        CancelDelayButton.Visibility = Visibility.Collapsed;
        CaptureProgress.Visibility = Visibility.Collapsed;
        TimingText.Text = "";
        StatusText.Text = "Starting…";
    }

    /// <summary>
    /// Re-enables all controls, hides countdown UI, resets title and progress.
    /// Called in finally blocks to prevent stuck states.
    /// </summary>
    private void ExitCaptureState()
    {
        _isOperationRunning = false;
        SetButtonsEnabled(true);
        SetExportButtonsEnabled(_hasCapture);
        DelaySecondsInput.IsEnabled = true;
        CountdownProgress.Visibility = Visibility.Collapsed;
        CountdownProgress.Value = 0;
        CancelDelayButton.Visibility = Visibility.Collapsed;
        CaptureProgress.Visibility = Visibility.Collapsed;

        Title = "Respectacle — Screen Capture Preview";

        _activeCts?.Dispose();
        _activeCts = null;
    }

    /// <summary>
    /// Disables all buttons during export operations.
    /// </summary>
    private void EnterExportState()
    {
        _isOperationRunning = true;
        SetButtonsEnabled(false);
        SetExportButtonsEnabled(false);
        CaptureProgress.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Restores button states after export completes.
    /// </summary>
    private void ExitExportState()
    {
        _isOperationRunning = false;
        SetButtonsEnabled(true);
        SetExportButtonsEnabled(_hasCapture);
        CaptureProgress.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Enables or disables all capture and selector buttons.
    /// </summary>
    private void SetButtonsEnabled(bool enabled)
    {
        FullDesktopButton.IsEnabled = enabled;
        CurrentMonitorButton.IsEnabled = enabled;
        ActiveWindowButton.IsEnabled = enabled;
        WindowUnderCursorButton.IsEnabled = enabled;
        RegionCaptureButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Enables or disables the export buttons (Save, Save As…, Copy).
    /// </summary>
    private void SetExportButtonsEnabled(bool enabled)
    {
        SaveButton.IsEnabled = enabled;
        SaveAsButton.IsEnabled = enabled;
        CopyToClipboardButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Applies a capture result to the UI (preview image, status, timing).
    /// Enables export buttons on successful capture.
    /// </summary>
    private void ApplyCaptureResult(CapturePreviewResult result)
    {
        if (result.IsSuccess && result.ImageSource != null)
        {
            PreviewImage.Source = null;
            PreviewImage.Source = result.ImageSource;
            StatusText.Text = $"{result.Mode} — {result.Dimensions}";
            _hasCapture = true;
        }
        else
        {
            StatusText.Text = result.Error ?? "Capture failed.";
            // Failed capture does NOT overwrite the last successful cache,
            // so _hasCapture retains its prior value.
        }

        TimingText.Text = FormatTiming(result);
    }

    /// <summary>
    /// Formats timing information into a compact, tabular-friendly string.
    /// </summary>
    private static string FormatTiming(CapturePreviewResult result)
    {
        if (result.CaptureMs == 0 && result.DisplayMs == 0)
            return "";

        var parts = new List<string>();
        if (result.CaptureMs > 0)
            parts.Add($"capture {result.CaptureMs:F1}ms");
        if (result.DisplayMs > 0)
            parts.Add($"display {result.DisplayMs:F1}ms");
        if (result.TotalMs > 0)
            parts.Add($"total {result.TotalMs:F1}ms");

        return string.Join(" │ ", parts);
    }

    /// <summary>
    /// Formats timing from either an export result or clipboard result.
    /// Pass null for either one to format only the non-null result.
    /// </summary>
    private static string FormatTimingWithExport(ExportResult? exportResult,
        ClipboardExportResult? clipboardResult)
    {
        var parts = new List<string>();

        if (exportResult != null && exportResult.Elapsed > TimeSpan.Zero)
            parts.Add($"export {exportResult.Elapsed.TotalMilliseconds:F1}ms");

        if (clipboardResult != null && clipboardResult.Elapsed > TimeSpan.Zero)
            parts.Add($"clipboard {clipboardResult.Elapsed.TotalMilliseconds:F1}ms");

        return string.Join(" │ ", parts);
    }

    /// <summary>
    /// Sanitizes an exception message for user-facing display.
    /// Strips stack traces and internal details.
    /// </summary>
    private static string SanitizeException(Exception ex)
    {
        var msg = ex.Message;
        // Truncate at first newline (stack traces, inner exceptions)
        int newline = msg.IndexOf('\n');
        if (newline > 0)
            msg = msg[..newline];
        return msg;
    }
}

/// <summary>
/// Production clipboard adapter that copies PNG data to the Windows clipboard
/// via WinRT DataPackage. Used by MainWindow for the Copy button.
/// </summary>
public sealed class WindowsClipboardAdapter : IClipboardAdapter
{
    public bool SetPngImage(byte[] pngBytes)
    {
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using var dataWriter = new Windows.Storage.Streams.DataWriter(stream);
            dataWriter.WriteBytes(pngBytes);
            stream.WriteAsync(dataWriter.DetachBuffer()).AsTask().Wait();
            stream.Seek(0);

            var content = new Windows.ApplicationModel.DataTransfer.DataPackage();
            content.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));

            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
