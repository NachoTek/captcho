// RegionOverlayWindow.xaml.cs — Transparent overlay for rectangular region selection.
//
// Creates a raw Win32 layered window (WS_EX_LAYERED | WS_EX_TOPMOST) that is truly
// transparent, showing the live desktop behind it. Rendering uses a 32-bit ARGB DIB
// section with per-pixel alpha. A System.Drawing.Bitmap wraps the DIB memory directly
// (via the scan0 pointer from CreateDIBSection) so GDI+ drawing goes straight into the
// buffer that UpdateLayeredWindow composites onto the desktop.
//
// Flow:
//   1. Create transparent topmost Win32 window covering the virtual desktop
//   2. Draw selection rectangle (dim scrim everywhere, clear inside selection)
//   3. User draws/adjusts/moves selection, presses Enter to confirm
//   4. Destroy the overlay window
//   5. Call native capture_region to capture the live desktop at those coordinates
//
// Supports: drag to draw, arrow keys to nudge (10px coarse, 1px fine with Shift),
// Alt+Arrow to resize from top-left anchor, Enter/double-click confirm, Escape cancel.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Respectacle.Capture;

namespace Respectacle.UI;

/// <summary>
/// Result of a region selection: the captured bitmap and its virtual-desktop coordinates.
/// </summary>
public sealed class RegionSelectionResult
{
    public ContiguousBitmap Bitmap { get; init; } = null!;
    public Rect Region { get; init; }
}

/// <summary>
/// Transparent overlay for rectangular region selection using a raw Win32 layered window.
/// </summary>
public sealed partial class RegionOverlayWindow : IDisposable
{
    #region Win32 P/Invoke

    private const int WS_EX_LAYERED   = unchecked((int)0x00080000);
    private const int WS_EX_TOOLWINDOW = unchecked((int)0x00000080);
    private const int WS_EX_TOPMOST   = unchecked((int)0x00000008);
    private const int WS_POPUP        = unchecked((int)0x80000000);
    private const int WS_VISIBLE      = unchecked((int)0x10000000);
    private const int WS_CLIPCHILDREN = unchecked((int)0x02000000);
    private const int WS_CLIPSIBLINGS = unchecked((int)0x04000000);

    private const int CS_HREDRAW = 0x0002;
    private const int CS_VREDRAW = 0x0001;

    private const int WM_KEYDOWN      = 0x0100;
    private const int WM_LBUTTONDOWN  = 0x0201;
    private const int WM_LBUTTONUP    = 0x0202;
    private const int WM_MOUSEMOVE    = 0x0200;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private const int VK_ESCAPE = 0x1B;
    private const int VK_RETURN = 0x0D;
    private const int VK_LEFT   = 0x25;
    private const int VK_UP     = 0x26;
    private const int VK_RIGHT  = 0x27;
    private const int VK_DOWN   = 0x28;
    private const int VK_SHIFT  = 0x10;
    private const int VK_MENU   = 0x12;

    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int ULW_ALPHA     = 0x02;
    private const int AC_SRC_OVER   = 0x00;
    private const int AC_SRC_ALPHA  = 0x01;
    private const int SW_SHOW       = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")] private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, POINT? pptDst, SIZE? psize, IntPtr hdcSrc, POINT pptSrc, int crKey, BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    #endregion

    private readonly TaskCompletionSource<RegionSelectionResult?> _tcs = new();
    private IntPtr _hwnd;
    private WndProc? _wndProc; // prevent GC
    private static readonly string _className = "RespectacleRegion_" + Guid.NewGuid().ToString("N");

    // DIB section for per-pixel alpha rendering
    private IntPtr _hBitmap;
    private IntPtr _bitmapDC;
    private IntPtr _bits; // pointer to DIB pixel memory

    private RegionSelection? _currentSelection;
    private bool _isDragging;
    private int _dragStartX, _dragStartY;
    private int _vdx, _vdy, _vdw, _vdh;

    private const int CoarseNudge = 10;
    private const int FineNudge = 1;

    public RegionOverlayWindow() { InitializeComponent(); }

    public Task<RegionSelectionResult?> ShowAndWaitAsync()
    {
        _vdx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _vdy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _vdw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _vdh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (_vdw <= 0 || _vdh <= 0)
        {
            _tcs.TrySetResult(null);
            return _tcs.Task;
        }

        var thread = new System.Threading.Thread(RunMessageLoop);
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return _tcs.Task;
    }

    private void RunMessageLoop()
    {
        try
        {
            _current = this;
            CreateOverlay();

            if (_hwnd == IntPtr.Zero)
            {
                _tcs.TrySetResult(null);
                return;
            }

            ShowWindow(_hwnd, SW_SHOW);
            SetForegroundWindow(_hwnd);
            Render();

            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            Cleanup();
        }
        catch
        {
            Cleanup();
            _tcs.TrySetResult(null);
        }
    }

    private void CreateOverlay()
    {
        var wc = new WNDCLASS
        {
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = StaticWndProc,
            hInstance = GetModuleHandle(IntPtr.Zero),
            lpszClassName = _className,
        };
        RegisterClassW(ref wc);

        // WS_EX_TOPMOST ensures the overlay appears above all windows
        int exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        uint style = unchecked((uint)(WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS));

        _hwnd = CreateWindowExW(exStyle, _className, "RespectacleRegion",
            style, _vdx, _vdy, _vdw, _vdh,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero);

        _wndProc = InstanceWndProc;
    }

    // ── WndProc ──────────────────────────────────────────────────────

    [ThreadStatic] private static RegionOverlayWindow? _current;

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => _current?.InstanceWndProc(hWnd, msg, wParam, lParam)
           ?? DefWindowProcW(hWnd, msg, wParam, lParam);

    private IntPtr InstanceWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN:  OnMouseDown(lParam); return IntPtr.Zero;
            case WM_MOUSEMOVE:    OnMouseMove(lParam); return IntPtr.Zero;
            case WM_LBUTTONUP:    OnMouseUp();         return IntPtr.Zero;
            case WM_LBUTTONDBLCLK: Confirm();          return IntPtr.Zero;
            case WM_KEYDOWN:      OnKey(wParam);       return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ── Input ────────────────────────────────────────────────────────

    private void OnMouseDown(IntPtr lParam)
    {
        _dragStartX = LoWord(lParam) + _vdx;
        _dragStartY = HiWord(lParam) + _vdy;
        _isDragging = true;
        _currentSelection = null;
        Render();
    }

    private void OnMouseMove(IntPtr lParam)
    {
        if (!_isDragging) return;
        int vx = LoWord(lParam) + _vdx;
        int vy = HiWord(lParam) + _vdy;

        var raw = RegionSelection.FromDragPoints(_dragStartX, _dragStartY, vx, vy);
        var clamped = raw.ClampToBounds(_vdx, _vdy, _vdw, _vdh);
        _currentSelection = (clamped != null && clamped.MeetsMinimumSize) ? clamped : null;
        Render();
    }

    private void OnMouseUp() => _isDragging = false;

    private void OnKey(IntPtr wParam)
    {
        switch (wParam.ToInt32())
        {
            case VK_ESCAPE: Cancel(); break;
            case VK_RETURN: Confirm(); break;
            case VK_LEFT: case VK_RIGHT: case VK_UP: case VK_DOWN: Nudge(wParam.ToInt32()); break;
        }
    }

    private void Nudge(int vk)
    {
        if (_currentSelection == null) return;
        bool shift = KeyDown(VK_SHIFT), alt = KeyDown(VK_MENU);
        int d = shift ? FineNudge : CoarseNudge;
        int dx = 0, dy = 0;
        if (vk == VK_LEFT) dx = -d; if (vk == VK_RIGHT) dx = d;
        if (vk == VK_UP) dy = -d;   if (vk == VK_DOWN) dy = d;

        var result = alt
            ? _currentSelection.Resize(dx, dy, "tl", _vdx, _vdy, _vdw, _vdh)
            : _currentSelection.Move(dx, dy, _vdx, _vdy, _vdw, _vdh);
        if (result != null) { _currentSelection = result; Render(); }
    }

    private static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    private static int LoWord(IntPtr p) => (short)(p.ToInt32() & 0xFFFF);
    private static int HiWord(IntPtr p) => (short)((p.ToInt32() >> 16) & 0xFFFF);

    // ── Confirm / Cancel ─────────────────────────────────────────────

    private void Confirm()
    {
        if (_currentSelection == null || !_currentSelection.MeetsMinimumSize) return;
        var sel = _currentSelection;
        DestroyWindow(_hwnd);

        try
        {
            using var r = SafeCaptureResult.CaptureRegion(sel.X, sel.Y, (uint)sel.Width, (uint)sel.Height);
            if (r.IsSuccess)
            {
                var bmp = BitmapBufferConverter.StripPadding(r.Pixels!, (int)r.Width, (int)r.Height, (int)r.Stride);
                _tcs.TrySetResult(new RegionSelectionResult
                {
                    Bitmap = bmp,
                    Region = new Rect(sel.X, sel.Y, sel.Width, sel.Height),
                });
                return;
            }
        }
        catch { /* fall through to null */ }
        _tcs.TrySetResult(null);
    }

    private void Cancel()
    {
        DestroyWindow(_hwnd);
        _tcs.TrySetResult(null);
    }

    // ── Rendering ────────────────────────────────────────────────────

    private void Render()
    {
        int w = _vdw, h = _vdh;

        // Create DIB section on first render
        if (_hBitmap == IntPtr.Zero)
        {
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
            };

            IntPtr screenDC = GetDC(IntPtr.Zero);
            _bitmapDC = CreateCompatibleDC(screenDC);
            _hBitmap = CreateDIBSection(_bitmapDC, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            SelectObject(_bitmapDC, _hBitmap);
            ReleaseDC(IntPtr.Zero, screenDC);
        }

        // Wrap the DIB memory directly — drawing goes straight into the buffer
        // that UpdateLayeredWindow reads. No copy, no disposal issue.
        int stride = w * 4;
        using var bmp = new System.Drawing.Bitmap(w, h, stride, PixelFormat.Format32bppPArgb, _bits);
        using var g = System.Drawing.Graphics.FromImage(bmp);

        // 25% dim scrim everywhere — desktop shows through transparent pixels
        g.Clear(Color.Transparent);
        g.FillRectangle(new SolidBrush(Color.FromArgb(64, 0, 0, 0)), 0, 0, w, h);

        if (_currentSelection != null && _currentSelection.MeetsMinimumSize)
        {
            int sx = _currentSelection.X - _vdx;
            int sy = _currentSelection.Y - _vdy;
            int sw = _currentSelection.Width;
            int sh = _currentSelection.Height;

            // Clear scrim inside selection — live desktop shows through
            g.CompositingMode = CompositingMode.SourceCopy;
            g.FillRectangle(new SolidBrush(Color.Transparent), sx, sy, sw, sh);
            g.CompositingMode = CompositingMode.SourceOver;

            // Dashed blue border
            using var pen = new Pen(Color.FromArgb(255, 79, 195, 247), 2)
            {
                DashStyle = DashStyle.Custom,
                DashPattern = new float[] { 6, 3 }
            };
            g.DrawRectangle(pen, sx, sy, sw, sh);

            // Dimension + position label
            string label = $"{sw}×{sh} at ({_currentSelection.X}, {_currentSelection.Y})";
            using var font = new Font("Consolas", 11f);
            using var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            var sz = g.MeasureString(label, font);
            float lx = sx + (sw - sz.Width) / 2f;
            float ly = sy + sh + 6;
            if (ly + sz.Height > h) ly = sy - sz.Height - 6;
            if (lx < 4) lx = 4;

            g.FillRectangle(new SolidBrush(Color.FromArgb(180, 0, 0, 0)),
                lx - 6, ly - 2, sz.Width + 12, sz.Height + 4);
            g.DrawString(label, font, brush, lx, ly);
        }
        else if (!_isDragging)
        {
            string hint = "Drag to select · Enter confirm · Esc cancel · Arrows adjust";
            using var font = new Font("Segoe UI", 13f);
            using var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            var sz = g.MeasureString(hint, font);
            float cx = (w - sz.Width) / 2f;
            g.FillRectangle(new SolidBrush(Color.FromArgb(180, 0, 0, 0)),
                cx - 12, 18, sz.Width + 24, sz.Height + 12);
            g.DrawString(hint, font, brush, cx, 24);
        }

        // Do NOT dispose bmp — it wraps _bits, not an owned HBITMAP.
        // Disposing would try to free memory we don't own.
        // The using statement is harmless for Bitmap(IntPtr) wrapper though.

        // Composite the DIB onto the desktop via the layered window
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };
        UpdateLayeredWindow(_hwnd, IntPtr.Zero,
            new POINT { X = _vdx, Y = _vdy },
            new SIZE { cx = w, cy = h },
            _bitmapDC,
            new POINT { X = 0, Y = 0 },
            0, blend, ULW_ALPHA);
    }

    // ── Cleanup ──────────────────────────────────────────────────────

    private void Cleanup()
    {
        if (_hBitmap != IntPtr.Zero) { DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
        if (_bitmapDC != IntPtr.Zero) { DeleteDC(_bitmapDC); _bitmapDC = IntPtr.Zero; }
        _bits = IntPtr.Zero;
    }

    public void Dispose() => Cleanup();
}
