// RegionOverlayWindow.xaml.cs — Full-desktop overlay for rectangular region selection.
//
// Captures the desktop at the moment the user initiates region selection, displays
// the screenshot with a light dim scrim, and lets the user drag-select a region.
// This is the same approach used by Windows Snipping Tool, Greenshot, and ShareX.
//
// Accepts a pre-captured ContiguousBitmap of the full desktop. The screenshot is
// displayed as the background, and cropping happens from this bitmap on confirm.
//
// Exposes ShowAndWaitAsync() which returns a RegionSelectionResult:
//   - Confirmed selection → ContiguousBitmap cropped from the pre-captured image
//   - Cancelled or invalid → null

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Respectacle.Capture;

namespace Respectacle.UI;

/// <summary>
/// Result of a region selection: the cropped bitmap and its virtual-desktop coordinates.
/// </summary>
public sealed class RegionSelectionResult
{
    /// <summary>Cropped bitmap in virtual-desktop coordinates.</summary>
    public ContiguousBitmap Bitmap { get; init; } = null!;
    /// <summary>The region rectangle in virtual-desktop pixels.</summary>
    public Rect Region { get; init; }
}

/// <summary>
/// Full-desktop overlay window for rectangular region selection.
/// Displays a pre-captured screenshot with a dim scrim, returns a cropped
/// bitmap from the screenshot on confirm, or null on cancellation.
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
    private readonly TaskCompletionSource<RegionSelectionResult?> _tcs = new();

    // ── Pre-captured screenshot ──────────────────────────────────────
    private readonly ContiguousBitmap _desktopCapture;

    // ── Selection state ──────────────────────────────────────────────
    private RegionSelection? _currentSelection;
    private bool _isDragging;
    private int _dragStartVirtualX;
    private int _dragStartVirtualY;

    // ── Virtual desktop bounds in physical pixels ────────────────────
    private int _virtualDesktopX;
    private int _virtualDesktopY;
    private int _virtualDesktopWidth;
    private int _virtualDesktopHeight;

    // ── DPI scale factor (physical pixels per DIP) ───────────────────
    private double _dpiScale = 1.0;

    // ── Constants ────────────────────────────────────────────────────
    private const int CoarseNudge = 10;
    private const int FineNudge = 1;

    /// <summary>
    /// Creates the overlay with a pre-captured screenshot of the virtual desktop.
    /// The screenshot is displayed as the background so the user can see what
    /// they're selecting.
    /// </summary>
    /// <param name="desktopCapture">Full virtual desktop capture (all monitors stitched).</param>
    public RegionOverlayWindow(ContiguousBitmap desktopCapture)
    {
        _desktopCapture = desktopCapture ?? throw new ArgumentNullException(nameof(desktopCapture));
        InitializeComponent();
    }

    /// <summary>
    /// Shows the overlay covering the entire virtual desktop and waits
    /// for the user to confirm or cancel a rectangular selection.
    /// Returns a cropped bitmap from the pre-captured screenshot, or null.
    /// </summary>
    public Task<RegionSelectionResult?> ShowAndWaitAsync()
    {
        InitializeOverlay();
        return _tcs.Task;
    }

    private void InitializeOverlay()
    {
        // Compute virtual-desktop bounds via Win32
        _virtualDesktopX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _virtualDesktopY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _virtualDesktopWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _virtualDesktopHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (_virtualDesktopWidth <= 0 || _virtualDesktopHeight <= 0)
        {
            _tcs.TrySetResult(null);
            return;
        }

        var appWindow = this.AppWindow;

        // Borderless, always-on-top presenter
        var presenter = appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        // Position to cover the entire virtual desktop
        appWindow.Move(new PointInt32(_virtualDesktopX, _virtualDesktopY));
        appWindow.Resize(new SizeInt32(_virtualDesktopWidth, _virtualDesktopHeight));

        _dpiScale = 1.0;

        // Hook input events
        RootCanvas.PointerPressed += OnPointerPressed;
        RootCanvas.PointerMoved += OnPointerMoved;
        RootCanvas.PointerReleased += OnPointerReleased;
        RootCanvas.DoubleTapped += OnDoubleTapped;
        RootCanvas.KeyDown += OnKeyDown;

        RootCanvas.IsTabStop = true;
        RootCanvas.AllowFocusOnInteraction = true;

        this.AppWindow.Changed += OnAppWindowChanged;
        RootCanvas.Loaded += OnRootLoaded;
        this.Closed += OnClosed;

        this.Activate();
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        double canvasWidth = RootCanvas.ActualWidth;
        if (canvasWidth > 0)
        {
            _dpiScale = _virtualDesktopWidth / canvasWidth;
        }

        double dipWidth = RootCanvas.ActualWidth > 0 ? RootCanvas.ActualWidth : _virtualDesktopWidth / _dpiScale;
        double dipHeight = RootCanvas.ActualHeight > 0 ? RootCanvas.ActualHeight : _virtualDesktopHeight / _dpiScale;

        // Display the pre-captured screenshot as the overlay background
        BackgroundImage.Source = await SoftwareBitmapConverter.ToWriteableBitmapAsync(_desktopCapture);
        BackgroundImage.Width = dipWidth;
        BackgroundImage.Height = dipHeight;

        // Size the scrim to fill the canvas
        ScrimRect.Width = dipWidth;
        ScrimRect.Height = dipHeight;

        PositionOverlayTexts();
        RootCanvas.Focus(FocusState.Programmatic);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange && sender.IsVisible)
        {
            PositionOverlayTexts();
            sender.Changed -= OnAppWindowChanged;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _tcs.TrySetResult(null);
        Cleanup();
    }

    // ── Pointer input ────────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootCanvas).Properties.IsLeftButtonPressed)
        {
            var position = e.GetCurrentPoint(RootCanvas).Position;
            var (px, py) = DipToPhysical(position.X, position.Y);
            var (vx, vy) = CoordinateHelper.OverlayToVirtual(px, py, _virtualDesktopX, _virtualDesktopY);

            _dragStartVirtualX = vx;
            _dragStartVirtualY = vy;
            _isDragging = true;
            _currentSelection = null;

            SelectionRect.Visibility = Visibility.Collapsed;

            e.Handled = true;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;

        var position = e.GetCurrentPoint(RootCanvas).Position;
        var (px, py) = DipToPhysical(position.X, position.Y);
        var (vx, vy) = CoordinateHelper.OverlayToVirtual(px, py, _virtualDesktopX, _virtualDesktopY);

        var raw = RegionSelection.FromDragPoints(_dragStartVirtualX, _dragStartVirtualY, vx, vy);
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
        int dx = 0, dy = 0;
        if (key == Windows.System.VirtualKey.Left) dx = -delta;
        if (key == Windows.System.VirtualKey.Right) dx = delta;
        if (key == Windows.System.VirtualKey.Up) dy = -delta;
        if (key == Windows.System.VirtualKey.Down) dy = delta;

        if (hasAlt)
        {
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
            UpdateStatusText("No valid selection — drag a region first, then press Enter");
            return;
        }

        // Crop from pre-captured bitmap
        int cropX = _currentSelection.X - _virtualDesktopX;
        int cropY = _currentSelection.Y - _virtualDesktopY;
        int cropW = _currentSelection.Width;
        int cropH = _currentSelection.Height;

        cropX = Math.Max(0, Math.Min(cropX, _desktopCapture.Width - 1));
        cropY = Math.Max(0, Math.Min(cropY, _desktopCapture.Height - 1));
        cropW = Math.Min(cropW, _desktopCapture.Width - cropX);
        cropH = Math.Min(cropH, _desktopCapture.Height - cropY);

        if (cropW <= 0 || cropH <= 0)
        {
            _tcs.TrySetResult(null);
            CloseOverlay();
            return;
        }

        var croppedBitmap = CropBitmap(_desktopCapture, cropX, cropY, cropW, cropH);

        var result = new RegionSelectionResult
        {
            Bitmap = croppedBitmap,
            Region = new Rect(_currentSelection.X, _currentSelection.Y, cropW, cropH),
        };

        _tcs.TrySetResult(result);
        CloseOverlay();
    }

    private void CancelSelection()
    {
        _tcs.TrySetResult(null);
        CloseOverlay();
    }

    // ── Bitmap cropping ──────────────────────────────────────────────

    private static ContiguousBitmap CropBitmap(ContiguousBitmap source, int x, int y, int width, int height)
    {
        int srcStride = source.Stride;
        int dstStride = width * 4;
        byte[] croppedPixels = new byte[dstStride * height];

        for (int row = 0; row < height; row++)
        {
            int srcOffset = (y + row) * srcStride + x * 4;
            int dstOffset = row * dstStride;
            Array.Copy(source.Pixels, srcOffset, croppedPixels, dstOffset, dstStride);
        }

        return new ContiguousBitmap(width, height, dstStride, croppedPixels);
    }

    // ── Visual updates ───────────────────────────────────────────────

    private void UpdateSelectionVisual()
    {
        if (_currentSelection == null)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        var (ox, oy) = CoordinateHelper.VirtualToOverlay(
            _currentSelection.X, _currentSelection.Y,
            _virtualDesktopX, _virtualDesktopY);

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
        double canvasWidth = RootCanvas.ActualWidth > 0
            ? RootCanvas.ActualWidth
            : _virtualDesktopWidth / _dpiScale;
        double canvasHeight = RootCanvas.ActualHeight > 0
            ? RootCanvas.ActualHeight
            : _virtualDesktopHeight / _dpiScale;

        double instructionWidth = InstructionBorder.ActualWidth > 0
            ? InstructionBorder.ActualWidth
            : 700;
        Canvas.SetLeft(InstructionBorder, (canvasWidth - instructionWidth) / 2.0);
        Canvas.SetTop(InstructionBorder, 20);

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

    private (int X, int Y) DipToPhysical(double dipX, double dipY)
    {
        return (
            (int)Math.Round(dipX * _dpiScale, MidpointRounding.AwayFromZero),
            (int)Math.Round(dipY * _dpiScale, MidpointRounding.AwayFromZero)
        );
    }

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
