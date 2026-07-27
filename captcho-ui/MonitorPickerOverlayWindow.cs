// MonitorPickerOverlayWindow.cs — Transparent overlay for Selected Monitor
// Target Selection.
//
// Creates a raw Win32 layered window (WS_EX_LAYERED | WS_EX_TOPMOST) covering
// the Virtual Desktop, renders a 25% black scrim everywhere, highlights the
// hovered monitor with a bright border, and shows a pointer-adjacent monitor
// label. Hover resolution and gap rejection come from the pure, headless-
// testable MonitorPickerTargetResolver; this window is the thin rendering and
// input adapter over that contract.
//
// Flow:
//   1. Enumerate monitors and create a transparent topmost Win32 window over
//      the Virtual Desktop.
//   2. On mouse move, resolve the hovered monitor and re-render (scrim +
//      bright border + label near the pointer).
//   3. On click: confirm the hovered monitor (if any) and return its Virtual
//      Desktop bounds as a MonitorTarget; clicks in monitor-layout gaps are
//      ignored so the picker stays open.
//   4. On Escape: cancel and return null.
//
// Per spec #28, the overlay does NOT perform Capture and does NOT own any
// post-capture state. The runtime CaptureWorkflowSession owns Capture of the
// returned bounds, the resulting Frame, and the preview transition.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// Transparent overlay for Selected Monitor Target Selection using a raw Win32
/// layered window. Returns the confirmed monitor as a <see cref="MonitorTarget"/>,
/// or null if the user cancelled. Never throws — unexpected failures surface as
/// a null result so the workflow reports cancellation rather than crashing.
/// </summary>
public sealed class MonitorPickerOverlayWindow : IDisposable
{
    #region Win32 P/Invoke

    private const int WS_EX_LAYERED     = unchecked((int)0x00080000);
    private const int WS_EX_TOOLWINDOW  = unchecked((int)0x00000080);
    private const int WS_EX_TOPMOST     = unchecked((int)0x00000008);
    private const int WS_POPUP          = unchecked((int)0x80000000);
    private const int WS_VISIBLE        = unchecked((int)0x10000000);
    private const int WS_CLIPCHILDREN   = unchecked((int)0x02000000);
    private const int WS_CLIPSIBLINGS   = unchecked((int)0x04000000);

    private const int CS_HREDRAW = 0x0002;
    private const int CS_VREDRAW = 0x0001;

    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_LBUTTONUP   = 0x0202;
    private const int WM_MOUSEMOVE   = 0x0200;

    private const int VK_ESCAPE = 0x1B;

    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int ULW_ALPHA    = 0x02;
    private const int AC_SRC_OVER  = 0x00;
    private const int AC_SRC_ALPHA = 0x01;
    private const int SW_SHOW      = 5;

    private const int IDC_ARROW = 32512;

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
    [DllImport("user32.dll")] private static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")] private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
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

    #endregion

    private readonly TaskCompletionSource<MonitorTarget?> _tcs = new();
    private IntPtr _hwnd;
    private WndProc? _wndProc; // prevent GC
    private static readonly string _className = "captchoMonitorPicker_" + Guid.NewGuid().ToString("N");

    // DIB section for per-pixel alpha rendering.
    private IntPtr _hBitmap;
    private IntPtr _bitmapDC;
    private IntPtr _bits;

    private IReadOnlyList<MonitorRect> _monitors = Array.Empty<MonitorRect>();
    private MonitorTarget? _hovered;
    private int _cursorVx, _cursorVy;
    private int _vdx, _vdy, _vdw, _vdh;

    /// <summary>
    /// Shows the monitor picker overlay on the caller's thread (the UI thread
    /// in production, where the modal Win32 message loop must live) and returns
    /// the confirmed monitor target. Returns null if the user cancelled or if
    /// the overlay could not be shown.
    /// </summary>
    public Task<MonitorTarget?> ShowAndWaitAsync()
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

        // Enumerate monitors up front so the hover resolver works over a stable
        // snapshot of the Virtual Desktop layout for the whole interaction.
        try
        {
            _monitors = MonitorInterop.CreateNative().EnumerateMonitors();
        }
        catch
        {
            _monitors = Array.Empty<MonitorRect>();
        }

        if (_monitors.Count == 0)
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
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            hInstance = GetModuleHandle(IntPtr.Zero),
            lpszClassName = _className,
        };
        RegisterClassW(ref wc);

        int exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        uint style = unchecked((uint)(WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS));

        _hwnd = CreateWindowExW(exStyle, _className, "captchoMonitorPicker",
            style, _vdx, _vdy, _vdw, _vdh,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero);

        _wndProc = InstanceWndProc;
    }

    // ── WndProc ──────────────────────────────────────────────────────

    [ThreadStatic] private static MonitorPickerOverlayWindow? _current;

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => _current?.InstanceWndProc(hWnd, msg, wParam, lParam)
           ?? DefWindowProcW(hWnd, msg, wParam, lParam);

    private IntPtr InstanceWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEMOVE: OnMouseMove(lParam); return IntPtr.Zero;
            case WM_LBUTTONUP: OnClick(lParam);     return IntPtr.Zero;
            case WM_KEYDOWN:   if (wParam.ToInt32() == VK_ESCAPE) Cancel(); return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ── Input ────────────────────────────────────────────────────────

    private void OnMouseMove(IntPtr lParam)
    {
        // lParam carries client coordinates (0-based at the window origin,
        // which is the Virtual Desktop origin). Convert to Virtual Desktop
        // coordinates for the hover resolver.
        _cursorVx = LoWord(lParam) + _vdx;
        _cursorVy = HiWord(lParam) + _vdy;

        var hovered = MonitorPickerTargetResolver.Resolve(_monitors, _cursorVx, _cursorVy);
        if (hovered == _hovered)
        {
            return;
        }

        _hovered = hovered;
        Render();
    }

    private void OnClick(IntPtr lParam)
    {
        // Hit-test the click point directly rather than trusting the last
        // hover — robust against clicks that arrive without a preceding
        // mouse-move at the same point. The resolver returns null for a
        // monitor-layout gap, and gap clicks are ignored so the picker stays
        // open. Only a confirmed monitor ends the interaction.
        int clickVx = LoWord(lParam) + _vdx;
        int clickVy = HiWord(lParam) + _vdy;
        var target = MonitorPickerTargetResolver.Resolve(_monitors, clickVx, clickVy);
        if (target is null)
        {
            return;
        }

        DestroyWindow(_hwnd);

        // Per spec #28, the overlay returns the confirmed monitor bounds and
        // does NOT perform Capture. The runtime CaptureWorkflowSession owns
        // Capture of these bounds, the resulting Frame, and the preview.
        _tcs.TrySetResult(target);
    }

    private void Cancel()
    {
        DestroyWindow(_hwnd);
        _tcs.TrySetResult(null);
    }

    private static int LoWord(IntPtr p) => (short)(p.ToInt32() & 0xFFFF);
    private static int HiWord(IntPtr p) => (short)((p.ToInt32() >> 16) & 0xFFFF);

    // ── Rendering ────────────────────────────────────────────────────

    private void Render()
    {
        int w = _vdw, h = _vdh;

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

        int stride = w * 4;
        using var bmp = new System.Drawing.Bitmap(w, h, stride, PixelFormat.Format32bppPArgb, _bits);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 25% black scrim over the whole Virtual Desktop — the live desktop
        // shows through the transparent pixels around and within highlights.
        g.Clear(Color.Transparent);
        using (var scrim = new SolidBrush(Color.FromArgb(64, 0, 0, 0)))
        {
            g.FillRectangle(scrim, 0, 0, w, h);
        }

        if (_hovered is not null)
        {
            // Client-space rectangle for the hovered monitor.
            int hx = _hovered.X - _vdx;
            int hy = _hovered.Y - _vdy;
            int hw = (int)_hovered.Width;
            int hh = (int)_hovered.Height;

            // Clear the scrim inside the hovered monitor so its live pixels
            // show through at full brightness, then frame it with a bright
            // border.
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
            {
                g.FillRectangle(clear, hx, hy, hw, hh);
            }
            g.CompositingMode = CompositingMode.SourceOver;

            using var border = new Pen(Color.FromArgb(255, 79, 195, 247), 4);
            g.DrawRectangle(border, hx, hy, hw, hh);

            // Pointer-adjacent monitor label: offset above-right of the cursor
            // so it follows the pointer without sitting under it, then clamp
            // inside the Virtual Desktop so it stays on-screen for monitors at
            // negative coordinates or along the edges.
            string label = _hovered.Label;
            using var font = new Font("Segoe UI", 14f, FontStyle.Bold);
            using var textBrush = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            var sz = g.MeasureString(label, font);
            float lx = (_cursorVx - _vdx) + 16;
            float ly = (_cursorVy - _vdy) - sz.Height - 12;
            lx = Math.Max(4, Math.Min(lx, w - sz.Width - 4));
            ly = Math.Max(4, Math.Min(ly, h - sz.Height - 4));

            using var labelBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            g.FillRectangle(labelBg, lx - 8, ly - 4, sz.Width + 16, sz.Height + 8);
            g.DrawString(label, font, textBrush, lx, ly);
        }
        else
        {
            // No monitor hovered (cursor in a gap). Show a hint so the
            // interaction is discoverable.
            string hint = "Click a monitor to capture · Esc cancels";
            using var font = new Font("Segoe UI", 13f);
            using var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            using var hintBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            var sz = g.MeasureString(hint, font);
            float cx = (w - sz.Width) / 2f;
            g.FillRectangle(hintBg, cx - 12, 18, sz.Width + 24, sz.Height + 12);
            g.DrawString(hint, font, brush, cx, 24);
        }

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

    // ── Cleanup ──────────────────────────────────────────────────────

    private void Cleanup()
    {
        if (_hBitmap != IntPtr.Zero) { DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
        if (_bitmapDC != IntPtr.Zero) { DeleteDC(_bitmapDC); _bitmapDC = IntPtr.Zero; }
        _bits = IntPtr.Zero;
    }

    public void Dispose() => Cleanup();
}
