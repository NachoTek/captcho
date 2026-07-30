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
//   5. Return the confirmed geometry (or null for cancellation) to the caller
//
// Per spec #27, the overlay does NOT perform Capture and does NOT own any
// post-capture state. The runtime CaptureWorkflowSession owns Capture of the
// returned geometry, the resulting Frame, and the preview transition.
//
// Supports: drag to draw, arrow keys to nudge (10px coarse, 1px fine with Shift),
// Alt+Arrow to resize from top-left anchor, Enter/double-click confirm, Escape cancel.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace captcho.UI;

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
    private const int WM_SETCURSOR    = 0x0020;

    private const int HTCLIENT        = 1;

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

    private const int IDC_CROSS     = 32515;
    private const int IDC_SIZEALL   = 32646;
    private const int IDC_SIZENWSE  = 32642;
    private const int IDC_SIZENESW  = 32643;
    private const int IDC_SIZENS    = 32645;
    private const int IDC_SIZEWE    = 32644;

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
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    // ── Magnifier backdrop snapshot ────────────────────────────────────
    // The magnifier samples a frozen snapshot of the desktop taken when the
    // overlay opens, so the zoomed view shows clean desktop pixels instead of
    // this overlay's own dim scrim. Snapshotted before the first render so the
    // not-yet-composited layered window cannot appear in its own magnifier.
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
    private const uint SRCCOPY = 0x00CC0020;

    #endregion

    private readonly TaskCompletionSource<TargetSelectionResult<SelectionGeometry>?> _tcs = new();
    private IntPtr _hwnd;
    private WndProc? _wndProc; // prevent GC
    private static readonly string _className = "captchoRegion_" + Guid.NewGuid().ToString("N");

    // DIB section for per-pixel alpha rendering
    private IntPtr _hBitmap;
    private IntPtr _bitmapDC;
    private IntPtr _bits; // pointer to DIB pixel memory

    // Frozen desktop snapshot sampled by the adaptive magnifier. null when the
    // snapshot failed or the Virtual Desktop was empty, in which case the
    // magnifier is silently skipped and the rest of the overlay still works.
    private System.Drawing.Bitmap? _backdrop;

    private RegionSelection? _currentSelection;
    private readonly SelectionGeometry? _initialGeometry;
    private int _dragStartX, _dragStartY;
    private int _vdx, _vdy, _vdw, _vdh;

    // Pointer interaction state. A click resolves (via SelectionHandleResolver)
    // to one of: start a brand-new drag, grab a resize handle, or grab the body
    // to move. Keyboard nudging (Alt+Arrows) keeps using the delta-based Resize.
    private DragMode _dragMode = DragMode.None;
    private SelectionHandleKind _activeHandle = SelectionHandleKind.None;
    private int _moveStartCursorX, _moveStartCursorY;
    private RegionSelection? _moveStartSelection;
    // Window-wide DPI fallback; per-cursor-position DPI is resolved on demand so
    // forgiving hit zones track the monitor the pointer is actually on.
    private double _dpiFactor = 1.0;

    private enum DragMode { None, NewSelection, MoveBody, ResizeHandle }

    private const int CoarseNudge = 10;
    private const int FineNudge = 1;

    public RegionOverlayWindow(SelectionGeometry? initialGeometry = null)
    {
        _initialGeometry = initialGeometry;
        InitializeComponent();
    }

    public Task<TargetSelectionResult<SelectionGeometry>?> ShowAndWaitAsync()
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

        if (_initialGeometry is not null
            && _initialGeometry.Width <= int.MaxValue
            && _initialGeometry.Height <= int.MaxValue)
        {
            _currentSelection = new RegionSelection
            {
                X = _initialGeometry.X,
                Y = _initialGeometry.Y,
                Width = (int)_initialGeometry.Width,
                Height = (int)_initialGeometry.Height,
            };
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

            // Per-monitor DPI scales the forgiving handle hit zones so the
            // always-visible 8px handles stay practical on high-DPI monitors.
            _dpiFactor = Math.Max(1.0, GetDpiForWindow(_hwnd) / 96.0);

            // Snapshot the desktop before this layered window is composited so
            // the adaptive magnifier can zoom clean desktop pixels instead of
            // its own dim scrim. A failed snapshot leaves _backdrop null and the
            // overlay simply skips the magnifier.
            SnapshotBackdrop();

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
            hCursor = LoadCursor(IntPtr.Zero, IDC_CROSS),
            hInstance = GetModuleHandle(IntPtr.Zero),
            lpszClassName = _className,
        };
        RegisterClassW(ref wc);

        // WS_EX_TOPMOST ensures the overlay appears above all windows
        int exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        uint style = unchecked((uint)(WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS));

        _hwnd = CreateWindowExW(exStyle, _className, "captchoRegion",
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
            case WM_SETCURSOR:    return OnSetCursor(lParam);
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ── Input ────────────────────────────────────────────────────────
    //
    // A left-button down event is resolved against the existing selection
    // (if any) via the pure SelectionHandleResolver: grabbing a corner/edge
    // handle starts a pointer-driven resize, grabbing the body starts a move,
    // and anything else starts a brand-new drag-to-draw selection. While a
    // drag is in progress WM_MOUSEMOVE routes to the matching geometry update.

    private void OnMouseDown(IntPtr lParam)
    {
        int vx = LoWord(lParam) + _vdx;
        int vy = HiWord(lParam) + _vdy;
        var requestedMode = TargetSelectionModeControls.HitTest(
            vx, vy, new Rectangle(_vdx, _vdy, _vdw, _vdh));
        if (requestedMode is captcho.Capture.CaptureMode mode)
        {
            if (mode != captcho.Capture.CaptureMode.Selection)
            {
                DestroyWindow(_hwnd);
                _tcs.TrySetResult(TargetSelectionResult<SelectionGeometry>.RouteTo(mode));
            }
            return;
        }

        if (_currentSelection != null && _currentSelection.MeetsMinimumSize)
        {
            var handle = SelectionHandleResolver.Resolve(_currentSelection, vx, vy, DpiFactorForPoint(vx, vy));
            if (handle == SelectionHandleKind.Body)
            {
                _dragMode = DragMode.MoveBody;
                _moveStartCursorX = vx;
                _moveStartCursorY = vy;
                _moveStartSelection = _currentSelection;
                return;
            }
            if (handle != SelectionHandleKind.None)
            {
                _dragMode = DragMode.ResizeHandle;
                _activeHandle = handle;
                return;
            }
        }

        // No existing selection, or the click missed every forgiving handle —
        // discard the current selection and start a brand-new drag.
        _dragMode = DragMode.NewSelection;
        _dragStartX = vx;
        _dragStartY = vy;
        _currentSelection = null;
        Render();
    }

    private void OnMouseMove(IntPtr lParam)
    {
        int vx = LoWord(lParam) + _vdx;
        int vy = HiWord(lParam) + _vdy;

        switch (_dragMode)
        {
            case DragMode.NewSelection:
            {
                var raw = RegionSelection.FromDragPoints(_dragStartX, _dragStartY, vx, vy);
                var clamped = raw.ClampToBounds(_vdx, _vdy, _vdw, _vdh);
                _currentSelection = (clamped != null && clamped.MeetsMinimumSize) ? clamped : null;
                Render();
                break;
            }
            case DragMode.MoveBody:
            {
                if (_moveStartSelection == null) break;
                // Delta from the press position against the snapshot so the
                // rectangle follows the pointer without accumulating drift.
                int dx = vx - _moveStartCursorX;
                int dy = vy - _moveStartCursorY;
                var moved = _moveStartSelection.Move(dx, dy, _vdx, _vdy, _vdw, _vdh);
                if (moved != null) { _currentSelection = moved; Render(); }
                break;
            }
            case DragMode.ResizeHandle:
            {
                var resized = _currentSelection?.ResizeHandle(
                    _activeHandle, vx, vy, _vdx, _vdy, _vdw, _vdh);
                if (resized != null) { _currentSelection = resized; Render(); }
                break;
            }
        }
    }

    private void OnMouseUp() => _dragMode = DragMode.None;

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

    // ── Cursor ───────────────────────────────────────────────────────
    //
    // Directional resize cursors follow the hovered handle. The overlay covers
    // the entire Virtual Desktop, so screen coordinates returned by GetCursorPos
    // are already Virtual Desktop coordinates and need no translation.

    private IntPtr OnSetCursor(IntPtr lParam)
    {
        // Low word of lParam is the hit-test code; only set the cursor inside
        // the client area, otherwise defer to DefWindowProc.
        if ((lParam.ToInt32() & 0xFFFF) != HTCLIENT)
            return DefWindowProcW(_hwnd, WM_SETCURSOR, IntPtr.Zero, lParam);

        var kind = ResolveCursorKind();
        SetCursor(LoadCursor(IntPtr.Zero, Win32CursorId(kind)));
        return (IntPtr)1;
    }

    private SelectionCursorKind ResolveCursorKind()
    {
        if (_currentSelection == null || !_currentSelection.MeetsMinimumSize)
            return SelectionCursorKind.Cross;

        // Lock the cursor to the grabbed handle/body while a drag is in progress
        // so it does not flicker as the pointer crosses other handles' hit zones.
        SelectionHandleKind handle;
        if (_dragMode == DragMode.ResizeHandle)
            handle = _activeHandle;
        else if (_dragMode == DragMode.MoveBody)
            handle = SelectionHandleKind.Body;
        else
        {
            GetCursorPos(out POINT p);
            handle = SelectionHandleResolver.Resolve(_currentSelection, p.X, p.Y, DpiFactorForPoint(p.X, p.Y));
        }
        return SelectionHandleResolver.GetCursor(handle);
    }

    private static int Win32CursorId(SelectionCursorKind kind) => kind switch
    {
        SelectionCursorKind.SizeAll  => IDC_SIZEALL,
        SelectionCursorKind.SizeNwse => IDC_SIZENWSE,
        SelectionCursorKind.SizeNesw => IDC_SIZENESW,
        SelectionCursorKind.SizeNs   => IDC_SIZENS,
        SelectionCursorKind.SizeWe   => IDC_SIZEWE,
        _ => IDC_CROSS,
    };

    /// <summary>
    /// Resolves the DPI factor for the monitor containing the given Virtual
    /// Desktop point, so forgiving hit zones scale with the monitor the pointer
    /// is actually on in mixed-DPI multi-monitor layouts. Falls back to the
    /// cached window-wide DPI if the per-monitor query is unavailable.
    /// </summary>
    private double DpiFactorForPoint(int x, int y)
    {
        try
        {
            IntPtr hmon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (hmon != IntPtr.Zero
                && GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
                && dpiX > 0)
            {
                return Math.Max(1.0, dpiX / 96.0);
            }
        }
        catch
        {
            // shcore.dll / GetDpiForMonitor unavailable — fall back below.
        }
        return _dpiFactor;
    }

    // ── Confirm / Cancel ─────────────────────────────────────────────

    private void Confirm()
    {
        if (_currentSelection == null || !_currentSelection.MeetsMinimumSize) return;
        var sel = _currentSelection;
        DestroyWindow(_hwnd);

        // Per spec #27, the overlay returns confirmed geometry and does NOT
        // perform Capture. The runtime CaptureWorkflowSession owns Capture of
        // this geometry, the resulting Frame, and the preview transition.
        _tcs.TrySetResult(TargetSelectionResult<SelectionGeometry>.Confirmed(
            new SelectionGeometry
            {
                X = sel.X,
                Y = sel.Y,
                Width = (uint)sel.Width,
                Height = (uint)sel.Height,
            }));
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

            // Always-visible 8×8 resize handles on every edge and corner so the
            // user can see how to refine the selection. White fill with the
            // selection's cyan outline stays legible on light and dark desktops.
            DrawHandles(g, sx, sy, sw, sh);
        }
        else if (_dragMode != DragMode.NewSelection)
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

        // The adaptive magnifier serves precision boundary placement, so it is
        // shown only while the user is drawing a new selection or dragging a
        // resize handle — the two interactions that adjust a boundary. A body
        // move is coarse (the cursor is in the interior, not on a boundary), so
        // it needs no magnifier; an idle overlay is unobstructed.
        if (_dragMode == DragMode.NewSelection || _dragMode == DragMode.ResizeHandle)
            DrawMagnifier(g);

        TargetSelectionModeControls.Draw(
            g, captcho.Capture.CaptureMode.Selection, new Rectangle(_vdx, _vdy, _vdw, _vdh));

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
        var dstPt = new POINT { X = _vdx, Y = _vdy };
        var dstSz = new SIZE { cx = w, cy = h };
        var srcPt = new POINT { X = 0, Y = 0 };
        UpdateLayeredWindow(_hwnd, IntPtr.Zero,
            ref dstPt, ref dstSz,
            _bitmapDC, ref srcPt,
            0, ref blend, ULW_ALPHA);
    }

    /// <summary>
    /// Draws the always-visible 8×8 resize handles at the four corners and four
    /// edge midpoints of the selection (coordinates already in overlay space).
    /// </summary>
    private static void DrawHandles(System.Drawing.Graphics g, int sx, int sy, int sw, int sh)
    {
        int hs = SelectionHandleResolver.VisibleHandleSize;
        using var fill = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
        using var edge = new Pen(Color.FromArgb(255, 79, 195, 247), 1f);

        void Draw(float cx, float cy)
        {
            float hx = cx - hs / 2f;
            float hy = cy - hs / 2f;
            g.FillRectangle(fill, hx, hy, hs, hs);
            g.DrawRectangle(edge, hx, hy, hs, hs);
        }

        // Corners
        Draw(sx, sy);
        Draw(sx + sw, sy);
        Draw(sx, sy + sh);
        Draw(sx + sw, sy + sh);
        // Edge midpoints
        Draw(sx + sw / 2f, sy);
        Draw(sx + sw, sy + sh / 2f);
        Draw(sx + sw / 2f, sy + sh);
        Draw(sx, sy + sh / 2f);
    }

    // ── Adaptive magnifier ───────────────────────────────────────────
    //
    // A 200×200 viewport at 4× zoom that follows the active boundary point
    // (the cursor) while a drag is in progress. The pure
    // SelectionMagnifierPlacement resolver decides what to sample and where to
    // place the box; this method performs the backdrop read (DrawImage from the
    // frozen snapshot) and draws the border and focus crosshair. Sampling the
    // frozen backdrop — not the live, scrimmed overlay — keeps the zoomed pixels
    // truthful, and works across mixed-DPI, cross-monitor, and negative-
    // coordinate layouts because every coordinate stays in Virtual Desktop
    // space (the screen DC BitBlt covers the whole virtual screen).

    private void SnapshotBackdrop()
    {
        try
        {
            IntPtr screenDC = GetDC(IntPtr.Zero);
            if (screenDC == IntPtr.Zero) return;
            IntPtr compatDC = CreateCompatibleDC(screenDC);
            IntPtr hbm = CreateCompatibleBitmap(screenDC, _vdw, _vdh);
            if (hbm == IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDC);
                DeleteDC(compatDC);
                return;
            }
            SelectObject(compatDC, hbm);
            // Source origin is the signed Virtual Desktop origin so monitors at
            // negative coordinates are captured too.
            BitBlt(compatDC, 0, 0, _vdw, _vdh, screenDC, _vdx, _vdy, SRCCOPY);
            ReleaseDC(IntPtr.Zero, screenDC);
            _backdrop = (System.Drawing.Bitmap)Image.FromHbitmap(hbm);
            DeleteObject(hbm);
            DeleteDC(compatDC);
        }
        catch
        {
            // Backdrop unavailable — the overlay skips the magnifier and the
            // rest of the selection interaction is unaffected.
            _backdrop = null;
        }
    }

    private void DrawMagnifier(System.Drawing.Graphics g)
    {
        if (_backdrop == null) return;
        if (!GetCursorPos(out POINT p)) return;

        var placement = SelectionMagnifierPlacement.Resolve(p.X, p.Y, _vdx, _vdy, _vdw, _vdh);
        if (placement == null) return;

        // Both the destination viewport and the source sample are stored in
        // Virtual Desktop coordinates; the overlay DIB and the frozen backdrop
        // share the VD origin, so subtracting (_vdx, _vdy) lands in both.
        int dx = placement.DestinationX - _vdx;
        int dy = placement.DestinationY - _vdy;
        int sx = placement.SourceX - _vdx;
        int sy = placement.SourceY - _vdy;
        int size = SelectionMagnifierPlacement.Size;
        int srcSize = SelectionMagnifierPlacement.SourceSize;
        int zoom = SelectionMagnifierPlacement.Zoom;

        // Nearest-neighbour scaling keeps individual desktop pixels crisp so the
        // user can place a boundary on an exact pixel — bicubic would blur them.
        var prevInterp = g.InterpolationMode;
        var prevOffset = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_backdrop,
            new Rectangle(dx, dy, size, size),
            sx, sy, srcSize, srcSize,
            GraphicsUnit.Pixel);
        g.InterpolationMode = prevInterp;
        g.PixelOffsetMode = prevOffset;

        // Border in the selection's cyan so the magnifier reads as part of the
        // overlay's affordance set.
        using var border = new Pen(Color.FromArgb(255, 79, 195, 247), 2f);
        g.DrawRectangle(border, dx + 1, dy + 1, size - 2, size - 2);

        // Crosshair marking the active boundary point inside the magnified view.
        // Mapping the focus through the source rectangle (rather than assuming
        // centre) keeps the crosshair under the correct pixel when the source is
        // clamped at a Virtual Desktop edge; the focus offset within the 50px
        // source stays in [0,50], so the mapped position always lands inside the
        // 200px viewport.
        int fx = dx + (p.X - placement.SourceX) * zoom;
        int fy = dy + (p.Y - placement.SourceY) * zoom;
        using var shadow = new Pen(Color.FromArgb(200, 0, 0, 0), 3f);
        using var core = new Pen(Color.FromArgb(255, 255, 255, 255), 1f);
        g.DrawLine(shadow, fx, dy, fx, dy + size);
        g.DrawLine(shadow, dx, fy, dx + size, fy);
        g.DrawLine(core, fx, dy, fx, dy + size);
        g.DrawLine(core, dx, fy, dx + size, fy);
    }

    // ── Cleanup ──────────────────────────────────────────────────────

    private void Cleanup()
    {
        _backdrop?.Dispose();
        _backdrop = null;
        if (_hBitmap != IntPtr.Zero) { DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
        if (_bitmapDC != IntPtr.Zero) { DeleteDC(_bitmapDC); _bitmapDC = IntPtr.Zero; }
        _bits = IntPtr.Zero;
    }

    public void Dispose() => Cleanup();
}
