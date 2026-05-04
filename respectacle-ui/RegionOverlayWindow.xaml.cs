// RegionOverlayWindow.xaml.cs — Full-desktop overlay for rectangular region selection.
//
// Implements the spike overlay using geometry helpers from T01 (RegionSelection, CoordinateHelper).
// Exposes ShowAndWaitAsync() which returns a nullable Rect:
//   - Confirmed selection → Rect with virtual-desktop coordinates
//   - Cancelled or invalid → null
//
// Input contract:
//   - Pointer drag to draw selection rectangle
//   - Enter or double-click to confirm
//   - Escape to cancel (always available)
//   - Arrow keys: nudge selection 10px (1px with Shift held)
//   - Alt+Arrow: resize selection from bottom-right anchor
//
// The overlay positions itself to cover the entire virtual desktop,
// handling negative virtual origins (multi-monitor left/above primary).

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;

namespace Respectacle.UI;

/// <summary>
/// Full-desktop overlay window for rectangular region selection.
/// Returns sanitized virtual-desktop coordinates or null on cancellation.
/// </summary>
public sealed partial class RegionOverlayWindow : Window
{
    #region Win32 P/Invoke for virtual-desktop bounds

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    #endregion

    // ── Completion contract ──────────────────────────────────────────
    private readonly TaskCompletionSource<Rect?> _tcs = new();

    // ── Selection state ──────────────────────────────────────────────
    private RegionSelection? _currentSelection;
    private bool _isDragging;
    private int _dragStartVirtualX;
    private int _dragStartVirtualY;

    // ── Virtual desktop bounds in physical pixels (set during initialization) ──
    private int _virtualDesktopX;
    private int _virtualDesktopY;
    private int _virtualDesktopWidth;
    private int _virtualDesktopHeight;

    // ── DPI scale factor (physical pixels per DIP) ───────────────────
    // The overlay window is sized in physical pixels via AppWindow.Resize,
    // but WinUI XAML layout and pointer events use DIPs. This factor bridges
    // the two coordinate systems so that selection geometry is always computed
    // in physical (virtual-desktop) pixels while visual placement uses DIPs.
    private double _dpiScale = 1.0;

    // ── Constants ────────────────────────────────────────────────────
    private const int CoarseNudge = 10;
    private const int FineNudge = 1;

    public RegionOverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the overlay covering the entire virtual desktop and waits
    /// for the user to confirm or cancel a rectangular selection.
    /// Returns the selected region in virtual-desktop coordinates, or null.
    /// </summary>
    public Task<Rect?> ShowAndWaitAsync()
    {
        InitializeOverlay();
        return _tcs.Task;
    }

    /// <summary>
    /// Sets up the overlay window: computes virtual-desktop bounds,
    /// positions and styles the window, hooks input events.
    /// </summary>
    private void InitializeOverlay()
    {
        // Compute virtual-desktop bounds via Win32
        _virtualDesktopX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _virtualDesktopY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _virtualDesktopWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _virtualDesktopHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        // Safety: if we can't get valid bounds, cancel immediately
        if (_virtualDesktopWidth <= 0 || _virtualDesktopHeight <= 0)
        {
            _tcs.TrySetResult(null);
            return;
        }

        // Position the window to cover the entire virtual desktop
        var appWindow = this.AppWindow;

        // Use Presenter to go borderless, always-on-top, no taskbar
        var presenter = appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        // Position using virtual-desktop coordinates (handles negative origins)
        appWindow.Move(new PointInt32(_virtualDesktopX, _virtualDesktopY));
        appWindow.Resize(new SizeInt32(_virtualDesktopWidth, _virtualDesktopHeight));

        // Compute DPI scale factor: AppWindow uses physical pixels, but XAML
        // pointer events and layout use DIPs. On a 150% scaled monitor,
        // _dpiScale = 1.5, meaning 1 DIP = 1.5 physical pixels.
        // We derive this from the ratio of the physical window size to the
        // XAML canvas size, which avoids per-monitor DPI API complexity.
        _dpiScale = 1.0; // Will be updated in OnRootLoaded once canvas measures

        // Hook input events
        RootCanvas.PointerPressed += OnPointerPressed;
        RootCanvas.PointerMoved += OnPointerMoved;
        RootCanvas.PointerReleased += OnPointerReleased;
        RootCanvas.DoubleTapped += OnDoubleTapped;
        RootCanvas.KeyDown += OnKeyDown;

        // Ensure the canvas can receive keyboard focus
        RootCanvas.IsTabStop = true;
        RootCanvas.AllowFocusOnInteraction = true;

        // Position instruction and status text when the XAML tree loads
        this.AppWindow.Changed += OnAppWindowChanged;
        RootCanvas.Loaded += OnRootLoaded;
        this.Closed += OnClosed;

        // Show the overlay window. WinUI 3 windows are invisible until Activate()
        // is called — without this the overlay never appears on screen.
        this.Activate();
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // Compute DPI scale from physical window size vs DIP canvas size.
        // AppWindow.Size is in physical pixels; RootCanvas.ActualWidth is in DIPs.
        double canvasWidth = RootCanvas.ActualWidth;
        if (canvasWidth > 0)
        {
            _dpiScale = _virtualDesktopWidth / canvasWidth;
        }

        // Size the scrim to fill the entire canvas in DIPs
        ScrimRect.Width = RootCanvas.ActualWidth;
        ScrimRect.Height = RootCanvas.ActualHeight;

        PositionOverlayTexts();

        // Activate and focus for keyboard input
        RootCanvas.Focus(FocusState.Programmatic);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Position texts once the window is visible
        if (args.DidVisibilityChange && sender.IsVisible)
        {
            PositionOverlayTexts();
            sender.Changed -= OnAppWindowChanged;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Ensure the TCS is resolved if the window closes unexpectedly
        _tcs.TrySetResult(null);
        Cleanup();
    }

    // ── Pointer input ────────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootCanvas).Properties.IsLeftButtonPressed)
        {
            var position = e.GetCurrentPoint(RootCanvas).Position;
            // Convert DIPs to physical pixels before coordinate math
            var (px, py) = DipToPhysical(position.X, position.Y);
            var (vx, vy) = CoordinateHelper.OverlayToVirtual(px, py, _virtualDesktopX, _virtualDesktopY);

            _dragStartVirtualX = vx;
            _dragStartVirtualY = vy;
            _isDragging = true;
            _currentSelection = null;

            // Hide the selection rect until we have a minimum-size region
            SelectionRect.Visibility = Visibility.Collapsed;

            e.Handled = true;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;

        var position = e.GetCurrentPoint(RootCanvas).Position;
        // Convert DIPs to physical pixels before coordinate math
        var (px, py) = DipToPhysical(position.X, position.Y);
        var (vx, vy) = CoordinateHelper.OverlayToVirtual(px, py, _virtualDesktopX, _virtualDesktopY);

        // Use T01 helper to normalize drag points (handles reversed drags)
        var raw = RegionSelection.FromDragPoints(
            _dragStartVirtualX, _dragStartVirtualY, vx, vy);

        // Clamp to virtual-desktop bounds
        var clamped = raw.ClampToBounds(
            _virtualDesktopX, _virtualDesktopY,
            _virtualDesktopWidth, _virtualDesktopHeight);

        if (clamped != null && clamped.MeetsMinimumSize)
        {
            _currentSelection = clamped;
            UpdateSelectionVisual();
        }
        else
        {
            _currentSelection = null;
            SelectionRect.Visibility = Visibility.Collapsed;
        }

        UpdateStatusText();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;

        // Keep current selection visible if valid; otherwise hide
        if (_currentSelection == null || !_currentSelection.MeetsMinimumSize)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            UpdateStatusText("Selection too small — drag again or press Escape to cancel");
        }

        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ConfirmSelection();
    }

    // ── Keyboard input ───────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                CancelSelection();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
                ConfirmSelection();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Left:
            case Windows.System.VirtualKey.Right:
            case Windows.System.VirtualKey.Up:
            case Windows.System.VirtualKey.Down:
                HandleArrowKey(e.Key,
                    hasShift: InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down),
                    hasAlt: InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));
                e.Handled = true;
                break;
        }
    }

    private void HandleArrowKey(Windows.System.VirtualKey key, bool hasShift, bool hasAlt)
    {
        if (_currentSelection == null) return;

        int delta = hasShift ? FineNudge : CoarseNudge;

        // Compute direction deltas
        int dx = 0, dy = 0;
        if (key == Windows.System.VirtualKey.Left) dx = -delta;
        if (key == Windows.System.VirtualKey.Right) dx = delta;
        if (key == Windows.System.VirtualKey.Up) dy = -delta;
        if (key == Windows.System.VirtualKey.Down) dy = delta;

        if (hasAlt)
        {
            // Alt+Arrow: resize from bottom-right anchor
            var resized = _currentSelection.Resize(dx, dy, "tl",
                _virtualDesktopX, _virtualDesktopY,
                _virtualDesktopWidth, _virtualDesktopHeight);

            if (resized != null)
            {
                _currentSelection = resized;
                UpdateSelectionVisual();
                UpdateStatusText();
            }
        }
        else
        {
            // Arrow/Shift+Arrow: move selection
            var moved = _currentSelection.Move(dx, dy,
                _virtualDesktopX, _virtualDesktopY,
                _virtualDesktopWidth, _virtualDesktopHeight);

            if (moved != null)
            {
                _currentSelection = moved;
                UpdateSelectionVisual();
                UpdateStatusText();
            }
        }
    }

    // ── Confirm / Cancel ─────────────────────────────────────────────

    private void ConfirmSelection()
    {
        if (_currentSelection == null || !_currentSelection.MeetsMinimumSize)
        {
            // No valid selection — don't close, just update status
            UpdateStatusText("No valid selection — drag a region first, then press Enter");
            return;
        }

        var result = new Rect(
            _currentSelection.X,
            _currentSelection.Y,
            _currentSelection.Width,
            _currentSelection.Height);

        _tcs.TrySetResult(result);
        CloseOverlay();
    }

    private void CancelSelection()
    {
        _tcs.TrySetResult(null);
        CloseOverlay();
    }

    // ── Visual updates ───────────────────────────────────────────────

    private void UpdateSelectionVisual()
    {
        if (_currentSelection == null)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        // Convert virtual-desktop coordinates (physical pixels) to overlay-relative physical pixels
        var (ox, oy) = CoordinateHelper.VirtualToOverlay(
            _currentSelection.X, _currentSelection.Y,
            _virtualDesktopX, _virtualDesktopY);

        // Convert physical pixels to DIPs for XAML Canvas layout
        var (dipX, dipY) = PhysicalToDip(ox, oy);
        var (dipW, dipH) = PhysicalToDip(_currentSelection.Width, _currentSelection.Height);

        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, dipX);
        Canvas.SetTop(SelectionRect, dipY);
        SelectionRect.Width = dipW;
        SelectionRect.Height = dipH;
    }

    private void UpdateStatusText(string? overrideText = null)
    {
        if (_currentSelection != null && _currentSelection.MeetsMinimumSize)
        {
            StatusText.Text = $"{_currentSelection.Width}×{_currentSelection.Height} at ({_currentSelection.X}, {_currentSelection.Y})";
        }
        else if (overrideText != null)
        {
            StatusText.Text = overrideText;
        }
        else
        {
            StatusText.Text = "Ready";
        }
    }

    private void PositionOverlayTexts()
    {
        // Use DIP dimensions for positioning XAML elements
        double canvasWidth = RootCanvas.ActualWidth > 0
            ? RootCanvas.ActualWidth
            : _virtualDesktopWidth / _dpiScale;
        double canvasHeight = RootCanvas.ActualHeight > 0
            ? RootCanvas.ActualHeight
            : _virtualDesktopHeight / _dpiScale;

        // Center instruction text at the top
        double instructionWidth = InstructionBorder.ActualWidth > 0
            ? InstructionBorder.ActualWidth
            : 700;
        Canvas.SetLeft(InstructionBorder, (canvasWidth - instructionWidth) / 2.0);
        Canvas.SetTop(InstructionBorder, 20);

        // Center status text at the bottom
        double statusWidth = StatusBorder.ActualWidth > 0
            ? StatusBorder.ActualWidth
            : 300;
        double statusHeight = StatusBorder.ActualHeight > 0
            ? StatusBorder.ActualHeight
            : 30;
        Canvas.SetLeft(StatusBorder, (canvasWidth - statusWidth) / 2.0);
        Canvas.SetTop(StatusBorder, canvasHeight - statusHeight - 20);
    }

    // ── DPI coordinate helpers ───────────────────────────────────────

    /// <summary>
    /// Converts DIP (device-independent pixel) coordinates to physical pixels.
    /// Pointer events report positions in DIPs; virtual-desktop coordinates
    /// and RegionSelection math use physical pixels.
    /// </summary>
    private (int X, int Y) DipToPhysical(double dipX, double dipY)
    {
        return (
            (int)Math.Round(dipX * _dpiScale, MidpointRounding.AwayFromZero),
            (int)Math.Round(dipY * _dpiScale, MidpointRounding.AwayFromZero)
        );
    }

    /// <summary>
    /// Converts physical pixel dimensions to DIPs for XAML layout.
    /// Canvas.SetLeft/SetTop and Width/Height use DIPs.
    /// </summary>
    private (double X, double Y) PhysicalToDip(int physX, int physY)
    {
        return (physX / _dpiScale, physY / _dpiScale);
    }

    // ── Cleanup ──────────────────────────────────────────────────────

    private void CloseOverlay()
    {
        Cleanup();
        this.Close();
    }

    private void Cleanup()
    {
        RootCanvas.PointerPressed -= OnPointerPressed;
        RootCanvas.PointerMoved -= OnPointerMoved;
        RootCanvas.PointerReleased -= OnPointerReleased;
        RootCanvas.DoubleTapped -= OnDoubleTapped;
        RootCanvas.KeyDown -= OnKeyDown;
        RootCanvas.Loaded -= OnRootLoaded;
        this.AppWindow.Changed -= OnAppWindowChanged;
        this.Closed -= OnClosed;
    }
}
