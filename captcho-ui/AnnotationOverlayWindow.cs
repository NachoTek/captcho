using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Borderless layered Annotation surface over the Virtual Desktop. It displays the
/// frozen source Frame, maintains one toolbar state and document, and returns the
/// composed Frame on confirmation.
/// </summary>
public sealed class AnnotationOverlayWindow : IDisposable
{
    private const int WS_EX_LAYERED = unchecked((int)0x00080000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const int WM_CLOSE = 0x0010;
    private const int WM_DESTROY = 0x0002;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_CHAR = 0x0102;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_RETURN = 0x0D;
    private const int VK_CONTROL = 0x11;
    private const int VK_BACK = 0x08;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;
    private const int SW_SHOW = 5;
    private const int IDC_ARROW = 32512;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Cursor;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte Operation, Flags, ConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern ushort RegisterClassW(ref WindowClass windowClass);
    [DllImport("user32.dll")] private static extern IntPtr CreateWindowExW(int extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int cursorName);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref Point destination, ref Size size, IntPtr sourceDc, ref Point source, int colorKey, ref BlendFunction blend, int flags);
    [DllImport("user32.dll")] private static extern bool GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(IntPtr moduleName);

    private static readonly string ClassName = "captchoAnnotation_" + Guid.NewGuid().ToString("N");
    [ThreadStatic] private static AnnotationOverlayWindow? _current;

    private readonly ContiguousBitmap _sourceFrame;
    private readonly AnnotationSession _annotationSession;
    private readonly TaskCompletionSource<AnnotationPresentResult> _completion = new();
    private readonly Stopwatch _stopwatch = new();
    private IntPtr _window;
    private IntPtr _bitmap;
    private IntPtr _bitmapDc;
    private IntPtr _bits;
    private WindowProcedure? _windowProcedure;
    private AnnotationPresentResult? _pendingResult;
    private int _virtualX, _virtualY, _virtualWidth, _virtualHeight;
    private Rectangle _frameBounds;
    private bool _drawing;
    private readonly List<(Rectangle Bounds, AnnotationTool Tool)> _toolButtons = new();
    private Rectangle _colorButton;
    private Rectangle _fillButton;
    private Rectangle _widthDownButton;
    private Rectangle _widthUpButton;
    private Rectangle _undoButton;
    private Rectangle _redoButton;
    private Rectangle _confirmButton;
    private Rectangle _cancelButton;

    public AnnotationOverlayWindow(ContiguousBitmap sourceFrame)
        : this(sourceFrame, AnnotationToolState.WithDefaults())
    {
    }

    public AnnotationOverlayWindow(
        ContiguousBitmap sourceFrame,
        AnnotationToolState initialToolState)
    {
        _sourceFrame = sourceFrame ?? throw new ArgumentNullException(nameof(sourceFrame));
        _annotationSession = new AnnotationSession(sourceFrame, initialToolState);
    }

    public Task<AnnotationPresentResult> ShowAndWaitAsync()
    {
        _virtualX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _virtualY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (_virtualWidth <= 0 || _virtualHeight <= 0)
            return Task.FromResult(AnnotationPresentResult.Fail("Virtual Desktop dimensions are unavailable."));

        _stopwatch.Start();
        var thread = new Thread(RunMessageLoop) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return _completion.Task;
    }

    private void RunMessageLoop()
    {
        try
        {
            _current = this;
            CreateOverlay();
            if (_window == IntPtr.Zero)
            {
                Fail("Annotation could not be opened.");
                return;
            }

            ShowWindow(_window, SW_SHOW);
            SetForegroundWindow(_window);
            Render();

            while (GetMessage(out var message, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            Fail($"Annotation failed: {ex.Message}");
        }
        finally
        {
            Cleanup();
            _current = null;
            _completion.TrySetResult(
                _pendingResult
                ?? AnnotationPresentResult.Fail("Annotation ended unexpectedly.", ElapsedMilliseconds()));
        }
    }

    private void CreateOverlay()
    {
        _windowProcedure = StaticWindowProcedure;
        var windowClass = new WindowClass
        {
            Procedure = _windowProcedure,
            Cursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            Instance = GetModuleHandle(IntPtr.Zero),
            ClassName = ClassName,
        };
        RegisterClassW(ref windowClass);

        _window = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            ClassName,
            "captcho Annotation",
            WS_POPUP | WS_VISIBLE,
            _virtualX,
            _virtualY,
            _virtualWidth,
            _virtualHeight,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(IntPtr.Zero),
            IntPtr.Zero);
    }

    private static IntPtr StaticWindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) =>
        _current?.HandleWindowMessage(window, message, wParam, lParam)
        ?? DefWindowProcW(window, message, wParam, lParam);

    private IntPtr HandleWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        // While a text entry is open, keystrokes edit the entry instead of driving
        // the overlay: printable characters append, Backspace removes, Return commits,
        // and Escape cancels the entry (a second Escape cancels Annotation).
        if (IsTexting)
        {
            switch (message)
            {
                case WM_CHAR:
                    return HandleTextInput(wParam);
                case WM_KEYDOWN when wParam.ToInt32() == VK_RETURN:
                    CommitTextEntry();
                    return IntPtr.Zero;
                case WM_KEYDOWN when wParam.ToInt32() == VK_ESCAPE:
                    _annotationSession.CancelStroke();
                    Render();
                    return IntPtr.Zero;
                case WM_KEYDOWN when wParam.ToInt32() == VK_BACK:
                    BackspaceTextEntry();
                    return IntPtr.Zero;
                case WM_LBUTTONDOWN:
                    // Clicking away commits the open entry first, then the click
                    // proceeds through the normal toolbar/placement path.
                    CommitTextEntry();
                    HandleFrameClick(
                        window,
                        new System.Drawing.Point(SignedLowWord(lParam), SignedHighWord(lParam)));
                    return IntPtr.Zero;
                default:
                    return DefWindowProcW(window, message, wParam, lParam);
            }
        }

        switch (message)
        {
            case WM_KEYDOWN when wParam.ToInt32() == VK_RETURN:
                Confirm();
                return IntPtr.Zero;
            case WM_KEYDOWN when wParam.ToInt32() == VK_ESCAPE:
            case WM_CLOSE:
                Cancel();
                return IntPtr.Zero;
            case WM_KEYDOWN when wParam.ToInt32() is ('Z' or 'Y' or 'z' or 'y') && IsControlDown():
                int key = wParam.ToInt32();
                bool redo = key is 'Y' or 'y' || (key is 'Z' or 'z' && IsShiftDown());
                if (redo ? _annotationSession.Redo() : _annotationSession.Undo())
                    Render();
                return IntPtr.Zero;
            case WM_LBUTTONDOWN:
                var downPoint = new System.Drawing.Point(SignedLowWord(lParam), SignedHighWord(lParam));
                HandleFrameClick(window, downPoint);
                return IntPtr.Zero;
            case WM_MOUSEMOVE when _drawing:
                var movePoint = new System.Drawing.Point(SignedLowWord(lParam), SignedHighWord(lParam));
                var moveFramePoint = ToFramePoint(movePoint);
                // The document applies append vs replace by tool; each call is a no-op for
                // the other kind of stroke, keeping this adapter free of tool rules.
                _annotationSession.AppendStrokePoint(moveFramePoint);
                _annotationSession.UpdateStrokePoint(moveFramePoint);
                Render();
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (_drawing)
                {
                    _drawing = false;
                    ReleaseCapture();
                    var point = new System.Drawing.Point(SignedLowWord(lParam), SignedHighWord(lParam));
                    var framePoint = ToFramePoint(point);
                    _annotationSession.AppendStrokePoint(framePoint);
                    _annotationSession.UpdateStrokePoint(framePoint);
                    _annotationSession.CommitStroke();
                    Render();
                }
                else
                {
                    var point = new System.Drawing.Point(SignedLowWord(lParam), SignedHighWord(lParam));
                    if (_confirmButton.Contains(point)) Confirm();
                    else if (_cancelButton.Contains(point)) Cancel();
                }
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    private static bool IsControlDown()
    {
        const int VK_LCONTROL = 0xA2;
        const int VK_RCONTROL = 0xA3;
        return IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);
    }

    private static bool IsShiftDown()
    {
        const int VK_SHIFT = 0x10;
        return IsKeyDown(VK_SHIFT);
    }

    private static bool IsKeyDown(int virtualKey) =>
        (GetKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);

    private void Confirm()
    {
        Complete(AnnotationPresentResult.Confirmed(_annotationSession.Render(), ElapsedMilliseconds()));
    }

    private void Cancel()
    {
        Complete(AnnotationPresentResult.Cancelled(ElapsedMilliseconds()));
    }

    private void Fail(string error)
    {
        Complete(AnnotationPresentResult.Fail(error, ElapsedMilliseconds()));
    }

    private void Complete(AnnotationPresentResult result)
    {
        if (_pendingResult is not null)
            return;

        _pendingResult = result;
        if (_window != IntPtr.Zero)
            DestroyWindow(_window);
    }

    private double ElapsedMilliseconds()
    {
        _stopwatch.Stop();
        return _stopwatch.Elapsed.TotalMilliseconds;
    }

    private void Render()
    {
        ReleaseRenderSurface();
        var bitmapInfo = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = _virtualWidth,
            Height = -_virtualHeight,
            Planes = 1,
            BitCount = 32,
        };
        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            _bitmapDc = CreateCompatibleDC(screenDc);
            _bitmap = CreateDIBSection(_bitmapDc, ref bitmapInfo, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmapDc == IntPtr.Zero || _bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
                throw new InvalidOperationException("Annotation rendering surface could not be created.");
            SelectObject(_bitmapDc, _bitmap);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        using var surface = new Bitmap(_virtualWidth, _virtualHeight, _virtualWidth * 4, PixelFormat.Format32bppPArgb, _bits);
        using var graphics = Graphics.FromImage(surface);
        graphics.Clear(Color.FromArgb(255, 18, 18, 20));

        var composed = _annotationSession.Render();
        var composedPixels = GCHandle.Alloc(composed.Pixels, GCHandleType.Pinned);
        try
        {
            using var source = new Bitmap(
                composed.Width,
                composed.Height,
                composed.Stride,
                PixelFormat.Format32bppArgb,
                composedPixels.AddrOfPinnedObject());
            var destination = FitFrame(composed.Width, composed.Height, _virtualWidth, _virtualHeight);
            _frameBounds = destination;
            graphics.InterpolationMode = destination.Size == source.Size
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, destination);
        }
        finally
        {
            composedPixels.Free();
        }

        DrawControls(graphics);

        var destinationPoint = new Point { X = _virtualX, Y = _virtualY };
        var destinationSize = new Size { Width = _virtualWidth, Height = _virtualHeight };
        var sourcePoint = new Point();
        var blend = new BlendFunction
        {
            Operation = AC_SRC_OVER,
            ConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };
        if (!UpdateLayeredWindow(_window, IntPtr.Zero, ref destinationPoint, ref destinationSize,
                _bitmapDc, ref sourcePoint, 0, ref blend, ULW_ALPHA))
            throw new InvalidOperationException("Annotation could not be presented.");
    }

    private void DrawControls(Graphics graphics)
    {
        const int buttonWidth = 92;
        const int height = 42;
        const int gap = 8;
        const int toolWidth = 86;
        const int colorWidth = 54;
        const int fillWidth = 54;
        const int widthButton = 38;
        const int widthLabel = 86;
        const int undoWidth = 58;
        // Pen + four shapes + text + marker + color + fill + width stepper + undo/redo + confirm/cancel.
        int contentWidth = toolWidth * 7
            + colorWidth
            + fillWidth
            + widthButton * 2 + widthLabel
            + undoWidth * 2
            + buttonWidth * 2
            + gap * 13;
        int startX = Math.Max(8, (_virtualWidth - contentWidth) / 2);
        int y = 20;
        int x = startX;
        _toolButtons.Clear();
        foreach (var tool in new[] { AnnotationTool.Pen, AnnotationTool.Rectangle, AnnotationTool.Ellipse, AnnotationTool.Line, AnnotationTool.Arrow, AnnotationTool.Text, AnnotationTool.Marker })
        {
            _toolButtons.Add((new Rectangle(x, y, toolWidth, height), tool));
            x += toolWidth + gap;
        }
        _colorButton = new Rectangle(x, y, colorWidth, height);
        x += colorWidth + gap;
        _fillButton = new Rectangle(x, y, fillWidth, height);
        x += fillWidth + gap;
        _widthDownButton = new Rectangle(x, y, widthButton, height);
        x += widthButton + gap;
        var widthText = new Rectangle(x, y, widthLabel, height);
        x += widthLabel + gap;
        _widthUpButton = new Rectangle(x, y, widthButton, height);
        x += widthButton + gap * 2;
        _undoButton = new Rectangle(x, y, undoWidth, height);
        x += undoWidth + gap;
        _redoButton = new Rectangle(x, y, undoWidth, height);
        x += undoWidth + gap * 2;
        _confirmButton = new Rectangle(x, y, buttonWidth, height);
        x += buttonWidth + gap;
        _cancelButton = new Rectangle(x, y, buttonWidth, height);

        bool fillApplies = _annotationSession.ToolState.ToolSupportsFill;

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var panel = new SolidBrush(Color.FromArgb(220, 28, 30, 34));
        graphics.FillRectangle(panel, startX - 12, 8, contentWidth + 24, height + 24);
        using var accent = new SolidBrush(Color.FromArgb(255, 38, 139, 210));
        using var color = new SolidBrush(ToDrawingColor(_annotationSession.ToolState.PenColor));
        using var neutral = new SolidBrush(Color.FromArgb(255, 70, 72, 78));
        using var neutralDisabled = new SolidBrush(Color.FromArgb(255, 48, 50, 54));
        using var confirm = new SolidBrush(Color.FromArgb(255, 38, 139, 210));
        using var cancel = new SolidBrush(Color.FromArgb(255, 70, 72, 78));
        foreach (var (bounds, tool) in _toolButtons)
            graphics.FillRectangle(tool == _annotationSession.ToolState.Tool ? accent : neutral, bounds);
        graphics.FillRectangle(color, _colorButton);
        if (fillApplies)
        {
            // Fill applies only to the closed shape tools; for the others the control is
            // hidden outright rather than disabled.
            graphics.FillRectangle(
                _annotationSession.ToolState.Fill == AnnotationFillStyle.Solid ? accent : neutral,
                _fillButton);
        }
        graphics.FillRectangle(neutral, _widthDownButton);
        graphics.FillRectangle(neutral, widthText);
        graphics.FillRectangle(neutral, _widthUpButton);
        graphics.FillRectangle(_annotationSession.CanUndo ? neutral : neutralDisabled, _undoButton);
        graphics.FillRectangle(_annotationSession.CanRedo ? neutral : neutralDisabled, _redoButton);
        graphics.FillRectangle(confirm, _confirmButton);
        graphics.FillRectangle(cancel, _cancelButton);
        using var font = new Font("Segoe UI", 12, FontStyle.Bold);
        using var smallFont = new Font("Segoe UI", 10, FontStyle.Bold);
        using var text = new SolidBrush(Color.White);
        using var textDisabled = new SolidBrush(Color.FromArgb(255, 130, 132, 138));
        var toolLabels = new Dictionary<AnnotationTool, string>
        {
            [AnnotationTool.Pen] = "Pen",
            [AnnotationTool.Rectangle] = "Rectangle",
            [AnnotationTool.Ellipse] = "Ellipse",
            [AnnotationTool.Line] = "Line",
            [AnnotationTool.Arrow] = "Arrow",
            [AnnotationTool.Text] = "Text",
            [AnnotationTool.Marker] = "Marker",
        };
        foreach (var (bounds, tool) in _toolButtons)
            DrawCentered(graphics, toolLabels[tool], smallFont, text, bounds);
        DrawCentered(graphics, "Color", smallFont, text, _colorButton);
        if (fillApplies)
            DrawCentered(
                graphics,
                "Fill",
                smallFont,
                text,
                _fillButton);
        DrawCentered(graphics, "-", font, text, _widthDownButton);
        DrawCentered(graphics, $"Width {_annotationSession.ToolState.StrokeWidth}", smallFont, text, widthText);
        DrawCentered(graphics, "+", font, text, _widthUpButton);
        DrawCentered(graphics, "Undo", smallFont, _annotationSession.CanUndo ? text : textDisabled, _undoButton);
        DrawCentered(graphics, "Redo", smallFont, _annotationSession.CanRedo ? text : textDisabled, _redoButton);
        DrawCentered(graphics, "Confirm", smallFont, text, _confirmButton);
        DrawCentered(graphics, "Cancel", smallFont, text, _cancelButton);

        if (IsTexting)
        {
            // Editing affordance: a hint bar under the toolbar while a text entry is
            // open, spelling out commit (Return) and cancel (Escape) behavior.
            using var hintPanel = new SolidBrush(Color.FromArgb(220, 28, 30, 34));
            var hintBounds = new Rectangle(startX - 12, y + height + 12, contentWidth + 24, height);
            graphics.FillRectangle(hintPanel, hintBounds);
            DrawCentered(
                graphics,
                "Type to edit - Return commits, Escape cancels",
                smallFont,
                text,
                hintBounds);
        }
    }

    private bool HandleToolbarClick(System.Drawing.Point point)
    {
        foreach (var (bounds, tool) in _toolButtons)
        {
            if (bounds.Contains(point))
            {
                _annotationSession.SetTool(tool);
                Render();
                return true;
            }
        }

        if (_colorButton.Contains(point))
        {
            var colors = new[]
            {
                AnnotationColor.RedOpaque,
                AnnotationColor.BlueOpaque,
                AnnotationColor.BlackOpaque,
            };
            int current = Array.IndexOf(colors, _annotationSession.ToolState.PenColor);
            _annotationSession.SetPenColor(colors[(current + 1 + colors.Length) % colors.Length]);
            Render();
            return true;
        }

        if (_fillButton.Contains(point) && _annotationSession.ToolState.ToolSupportsFill)
        {
            _annotationSession.SetFill(
                _annotationSession.ToolState.Fill == AnnotationFillStyle.Solid
                    ? AnnotationFillStyle.None
                    : AnnotationFillStyle.Solid);
            Render();
            return true;
        }

        if (_widthDownButton.Contains(point))
        {
            _annotationSession.SetStrokeWidth(Math.Max(
                AnnotationSettings.MinimumStrokeWidth,
                _annotationSession.ToolState.StrokeWidth - 1));
            Render();
            return true;
        }

        if (_widthUpButton.Contains(point))
        {
            _annotationSession.SetStrokeWidth(Math.Min(
                AnnotationSettings.MaximumStrokeWidth,
                _annotationSession.ToolState.StrokeWidth + 1));
            Render();
            return true;
        }

        if (_undoButton.Contains(point))
        {
            UndoOrRedo(undo: true);
            return true;
        }

        if (_redoButton.Contains(point))
        {
            UndoOrRedo(undo: false);
            return true;
        }

        return false;
    }

    private void UndoOrRedo(bool undo)
    {
        if (undo ? _annotationSession.Undo() : _annotationSession.Redo())
            Render();
    }

    private bool IsTexting => _annotationSession.Document.InProgressText is not null;

    /// <summary>
    /// Non-texting left-click path: toolbar first, then frame placement. The text
    /// tool opens an editable entry; every other tool begins a drag stroke.
    /// </summary>
    private void HandleFrameClick(IntPtr window, System.Drawing.Point downPoint)
    {
        if (HandleToolbarClick(downPoint))
            return;
        if (!_frameBounds.Contains(downPoint))
            return;

        if (_annotationSession.ToolState.Tool == AnnotationTool.Text)
        {
            // The text tool places an editable entry; entry content is committed
            // as one annotation document entry on Return or click-away.
            _annotationSession.BeginText(ToFramePoint(downPoint));
            Render();
            return;
        }

        if (_annotationSession.ToolState.Tool == AnnotationTool.Marker)
        {
            // The marker tool places one numbered entry per click; the number is
            // assigned at placement and the entry commits immediately.
            _annotationSession.BeginMarker(ToFramePoint(downPoint));
            _annotationSession.CommitStroke();
            Render();
            return;
        }

        _drawing = true;
        SetCapture(window);
        _annotationSession.BeginStroke(ToFramePoint(downPoint));
        Render();
    }

    private IntPtr HandleTextInput(IntPtr wParam)
    {
        int character = wParam.ToInt32();
        if (character == VK_BACK || character == VK_RETURN || character == VK_ESCAPE)
            return IntPtr.Zero;
        if (char.IsControl((char)character) || (char.IsWhiteSpace((char)character) && character != ' '))
            return IntPtr.Zero;

        _annotationSession.EditInProgressText(
            _annotationSession.Document.InProgressText + (char)character);
        Render();
        return IntPtr.Zero;
    }

    private void BackspaceTextEntry()
    {
        var content = _annotationSession.Document.InProgressText;
        if (string.IsNullOrEmpty(content))
            return;

        _annotationSession.EditInProgressText(content[..^1]);
        Render();
    }

    private void CommitTextEntry()
    {
        // Empty or whitespace input commits nothing; the document drops the entry.
        _annotationSession.CommitStroke();
        Render();
    }

    private AnnotationPoint ToFramePoint(System.Drawing.Point point)
    {
        int x = Math.Clamp(
            (int)((point.X - _frameBounds.X) * (long)_sourceFrame.Width / _frameBounds.Width),
            0,
            _sourceFrame.Width - 1);
        int y = Math.Clamp(
            (int)((point.Y - _frameBounds.Y) * (long)_sourceFrame.Height / _frameBounds.Height),
            0,
            _sourceFrame.Height - 1);
        return new AnnotationPoint(x, y);
    }

    private static Color ToDrawingColor(AnnotationColor color) =>
        Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

    private static void DrawCentered(Graphics graphics, string value, Font font, Brush brush, Rectangle bounds)
    {
        var size = graphics.MeasureString(value, font);
        graphics.DrawString(value, font, brush,
            bounds.X + (bounds.Width - size.Width) / 2,
            bounds.Y + (bounds.Height - size.Height) / 2);
    }

    private static Rectangle FitFrame(int width, int height, int availableWidth, int availableHeight)
    {
        double scale = Math.Min(1, Math.Min((double)availableWidth / width, (double)availableHeight / height));
        int renderedWidth = Math.Max(1, (int)Math.Round(width * scale));
        int renderedHeight = Math.Max(1, (int)Math.Round(height * scale));
        return new Rectangle(
            (availableWidth - renderedWidth) / 2,
            (availableHeight - renderedHeight) / 2,
            renderedWidth,
            renderedHeight);
    }

    private static int SignedLowWord(IntPtr value) => (short)(value.ToInt64() & 0xffff);
    private static int SignedHighWord(IntPtr value) => (short)((value.ToInt64() >> 16) & 0xffff);

    private void Cleanup()
    {
        ReleaseRenderSurface();
        _bits = IntPtr.Zero;
        _window = IntPtr.Zero;
        _windowProcedure = null;
    }

    public void Dispose() => Cleanup();

    private void ReleaseRenderSurface()
    {
        if (_bitmapDc != IntPtr.Zero) { DeleteDC(_bitmapDc); _bitmapDc = IntPtr.Zero; }
        if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
    }
}
