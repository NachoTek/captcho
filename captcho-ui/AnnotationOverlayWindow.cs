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
/// Borderless layered Annotation surface over the Virtual Desktop. This gate
/// displays the frozen source Frame and returns it unchanged on confirmation;
/// subsequent Annotation-tool tickets will compose edits on this same surface.
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
    private const int WM_LBUTTONUP = 0x0202;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_RETURN = 0x0D;
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
    private readonly TaskCompletionSource<AnnotationPresentResult> _completion = new();
    private readonly Stopwatch _stopwatch = new();
    private IntPtr _window;
    private IntPtr _bitmap;
    private IntPtr _bitmapDc;
    private IntPtr _bits;
    private WindowProcedure? _windowProcedure;
    private AnnotationPresentResult? _pendingResult;
    private int _virtualX, _virtualY, _virtualWidth, _virtualHeight;
    private Rectangle _confirmButton;
    private Rectangle _cancelButton;

    public AnnotationOverlayWindow(ContiguousBitmap sourceFrame) =>
        _sourceFrame = sourceFrame ?? throw new ArgumentNullException(nameof(sourceFrame));

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
        switch (message)
        {
            case WM_KEYDOWN when wParam.ToInt32() == VK_RETURN:
                Confirm();
                return IntPtr.Zero;
            case WM_KEYDOWN when wParam.ToInt32() == VK_ESCAPE:
            case WM_CLOSE:
                Cancel();
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                var point = new System.Drawing.Point(SignedLowWord(lParam), SignedHighWord(lParam));
                if (_confirmButton.Contains(point)) Confirm();
                else if (_cancelButton.Contains(point)) Cancel();
                return IntPtr.Zero;
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    private void Confirm()
    {
        Complete(AnnotationPresentResult.Confirmed(_sourceFrame, ElapsedMilliseconds()));
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

        var pixels = GCHandle.Alloc(_sourceFrame.Pixels, GCHandleType.Pinned);
        try
        {
            using var source = new Bitmap(
                _sourceFrame.Width,
                _sourceFrame.Height,
                _sourceFrame.Stride,
                PixelFormat.Format32bppArgb,
                pixels.AddrOfPinnedObject());
            var destination = FitFrame(_sourceFrame.Width, _sourceFrame.Height, _virtualWidth, _virtualHeight);
            graphics.InterpolationMode = destination.Size == source.Size
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, destination);
        }
        finally
        {
            pixels.Free();
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
        const int width = 138;
        const int height = 42;
        const int gap = 12;
        int startX = (_virtualWidth - (width * 2 + gap)) / 2;
        _confirmButton = new Rectangle(startX, 20, width, height);
        _cancelButton = new Rectangle(startX + width + gap, 20, width, height);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var panel = new SolidBrush(Color.FromArgb(220, 28, 30, 34));
        graphics.FillRectangle(panel, startX - 12, 8, width * 2 + gap + 24, height + 24);
        using var confirm = new SolidBrush(Color.FromArgb(255, 38, 139, 210));
        using var cancel = new SolidBrush(Color.FromArgb(255, 70, 72, 78));
        graphics.FillRectangle(confirm, _confirmButton);
        graphics.FillRectangle(cancel, _cancelButton);
        using var font = new Font("Segoe UI", 12, FontStyle.Bold);
        using var text = new SolidBrush(Color.White);
        DrawCentered(graphics, "Confirm  Enter", font, text, _confirmButton);
        DrawCentered(graphics, "Cancel  Esc", font, text, _cancelButton);
    }

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
        if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
        if (_bitmapDc != IntPtr.Zero) { DeleteDC(_bitmapDc); _bitmapDc = IntPtr.Zero; }
        _bits = IntPtr.Zero;
        _window = IntPtr.Zero;
        _windowProcedure = null;
    }

    public void Dispose() => Cleanup();
}
