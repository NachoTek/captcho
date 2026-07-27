// WindowPickerOverlayWindow.cs — Transparent overlay for Selected Window
// Target Selection.
//
// Creates a raw Win32 layered window (WS_EX_LAYERED | WS_EX_TOPMOST) covering
// the Virtual Desktop, renders a 25% black scrim everywhere, highlights the
// hovered window's full bounds (which may cross monitor boundaries) with a
// bright border, and shows the window's title near the pointer. Hover and click
// resolution come from the pure, headless-testable WindowPickerTargetResolver;
// this window is the thin rendering and input adapter over that contract.
//
// Flow:
//   1. Create a transparent topmost Win32 window over the Virtual Desktop.
//   2. On mouse move, enumerate eligible top-level windows (z-order, filtered),
//      resolve the hovered window via the resolver, and re-render (scrim +
//      bright border + pointer-adjacent title).
//   3. On click: re-enumerate (fresh snapshot so a window that disappeared
//      between hover and click can never be confirmed as a stale target) and
//      resolve the click point. A window yields WindowConfirmed; empty desktop
//      (no eligible window under the click) yields EmptyDesktopFallback, which
//      the workflow routes to a Full Desktop capture including the taskbar.
//   4. On Escape: cancel and return null.
//
// Per spec #29, the overlay does NOT perform Capture and does NOT own any
// post-capture state. The runtime CaptureWorkflowSession owns Capture of the
// returned target (by handle) or the Full Desktop fallback, the resulting
// Frame, and the preview transition.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace captcho.UI;

/// <summary>
/// Transparent overlay for Selected Window Target Selection using a raw Win32
/// layered window. Returns a <see cref="WindowPickerResult"/> for a confirming
/// outcome (window or empty-desktop fallback), or null if the user cancelled.
/// Never throws — unexpected failures surface as a null result so the workflow
/// reports cancellation rather than crashing.
/// </summary>
public sealed class WindowPickerOverlayWindow : IDisposable
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

    private const int GWL_EXSTYLE = -20;

    private const int MaxTitleLength = 80;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

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
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

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
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

    #endregion

    private readonly TaskCompletionSource<WindowPickerResult?> _tcs = new();
    private IntPtr _hwnd;
    private WndProc? _wndProc; // prevent GC
    private static readonly string _className = "captchoWindowPicker_" + Guid.NewGuid().ToString("N");

    // DIB section for per-pixel alpha rendering.
    private IntPtr _hBitmap;
    private IntPtr _bitmapDC;
    private IntPtr _bits;

    private WindowTarget? _hovered;
    private int _cursorVx, _cursorVy;
    private int _vdx, _vdy, _vdw, _vdh;

    /// <summary>
    /// Shows the window picker overlay on the caller's thread (the UI thread in
    /// production, where the modal Win32 message loop must live) and returns the
    /// confirming outcome. Returns null if the user cancelled or if the overlay
    /// could not be shown.
    /// </summary>
    public Task<WindowPickerResult?> ShowAndWaitAsync()
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
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            hInstance = GetModuleHandle(IntPtr.Zero),
            lpszClassName = _className,
        };
        RegisterClassW(ref wc);

        // WS_EX_TOOLWINDOW also excludes this overlay from its own eligibility
        // filter, so the picker never picks itself.
        int exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        uint style = unchecked((uint)(WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS));

        _hwnd = CreateWindowExW(exStyle, _className, "captchoWindowPicker",
            style, _vdx, _vdy, _vdw, _vdh,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero);

        _wndProc = InstanceWndProc;
    }

    // ── WndProc ──────────────────────────────────────────────────────

    [ThreadStatic] private static WindowPickerOverlayWindow? _current;

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
        // coordinates for the resolver.
        _cursorVx = LoWord(lParam) + _vdx;
        _cursorVy = HiWord(lParam) + _vdy;

        // Re-enumerate on each move so the hovered highlight tracks live window
        // state — a window that closes mid-interaction drops out of the snapshot
        // and the resolver never returns a stale handle.
        var target = WindowPickerTargetResolver.Resolve(EnumerateEligibleWindows(), _cursorVx, _cursorVy);
        if (SameHover(target, _hovered))
        {
            return;
        }

        _hovered = target;
        Render();
    }

    private void OnClick(IntPtr lParam)
    {
        // Re-enumerate at click time and resolve the click point directly —
        // robust against clicks that arrive without a preceding mouse-move at
        // the same point, and against a window that disappeared between hover
        // and click (the fresh snapshot cannot contain a vanished handle, so a
        // stale target can never be confirmed).
        int clickVx = LoWord(lParam) + _vdx;
        int clickVy = HiWord(lParam) + _vdy;
        var resolved = WindowPickerTargetResolver.Resolve(EnumerateEligibleWindows(), clickVx, clickVy);

        DestroyWindow(_hwnd);

        if (resolved is null)
        {
            // Empty desktop — no eligible window under the click. The workflow
            // routes this to a Full Desktop capture including the taskbar.
            _tcs.TrySetResult(new WindowPickerResult
            {
                Outcome = WindowPickerOutcome.EmptyDesktopFallback,
            });
            return;
        }

        // Per spec #29, the overlay returns the confirmed window target and
        // does NOT perform Capture. The runtime CaptureWorkflowSession owns
        // capture by handle, the resulting Frame, and the preview.
        _tcs.TrySetResult(new WindowPickerResult
        {
            Outcome = WindowPickerOutcome.WindowConfirmed,
            Target = resolved,
        });
    }

    private void Cancel()
    {
        DestroyWindow(_hwnd);
        _tcs.TrySetResult(null);
    }

    private static int LoWord(IntPtr p) => (short)(p.ToInt32() & 0xFFFF);
    private static int HiWord(IntPtr p) => (short)((p.ToInt32() >> 16) & 0xFFFF);

    // ── Window enumeration ───────────────────────────────────────────

    /// <summary>
    /// Enumerates eligible top-level windows in top-to-bottom z-order (the
    /// order EnumWindows yields), filtered to the windows that are meaningful
    /// to pick. Filtering follows Windows conventions (the spec did not fix an
    /// exact set): visible, non-minimized, non-tool, non-Captcho-process, with
    /// non-empty bounds intersecting the Virtual Desktop, and excluding the
    /// shell desktop/taskbar classes so clicking empty desktop falls through to
    /// the Full Desktop fallback. This is the overlay adapter's internal
    /// detail — the externally observable hover/empty-desktop/disappearance
    /// contract is the pure resolver's, covered by headless tests.
    /// </summary>
    private List<PickableWindow> EnumerateEligibleWindows()
    {
        var windows = new List<PickableWindow>();
        uint ownPid = GetCurrentProcessId();

        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                    return true;

                // Exclude tool windows (tooltips, floating palettes) and, by
                // construction, this picker overlay itself (it is a tool window).
                IntPtr exStyle = GetWindowLongPtr64(hWnd, GWL_EXSTYLE);
                if ((exStyle.ToInt64() & WS_EX_TOOLWINDOW) != 0)
                    return true;

                // Exclude Captcho's own windows (main window, settings, this picker).
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == ownPid)
                    return true;

                if (!GetWindowRect(hWnd, out RECT rect))
                    return true;
                if (rect.right <= rect.left || rect.bottom <= rect.top)
                    return true;

                // Exclude windows fully outside the Virtual Desktop.
                if (rect.right <= _vdx || rect.left >= _vdx + _vdw ||
                    rect.bottom <= _vdy || rect.top >= _vdy + _vdh)
                    return true;

                // Exclude the shell desktop and taskbar so empty-desktop and
                // taskbar clicks fall through to the Full Desktop fallback.
                if (IsShellClass(hWnd))
                    return true;

                var title = GetWindowTitle(hWnd);
                windows.Add(new PickableWindow(
                    hWnd,
                    rect.left, rect.top, rect.right, rect.bottom,
                    title));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Enumeration failure leaves an empty snapshot; the resolver yields
            // null (empty desktop) and the workflow reports a fallback rather
            // than crashing.
        }

        return windows;
    }

    private static bool IsShellClass(IntPtr hWnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            int len = GetClassName(hWnd, sb, sb.Capacity);
            if (len <= 0)
                return false;
            string cls = sb.ToString();
            return cls is "Progman" or "WorkerW" or "Shell_TrayWnd";
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        try
        {
            var sb = new StringBuilder(MaxTitleLength + 1);
            int len = GetWindowText(hWnd, sb, MaxTitleLength + 1);
            if (len <= 0)
                return "";
            string title = sb.ToString();
            return len >= MaxTitleLength
                ? title.Substring(0, MaxTitleLength - 1) + "…"
                : title;
        }
        catch
        {
            return "";
        }
    }

    private static bool SameHover(WindowTarget? a, WindowTarget? b) =>
        a is null ? b is null : (b is not null && a.Handle == b.Handle);

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
            // Client-space rectangle for the hovered window's full bounds.
            int hx = _hovered.X - _vdx;
            int hy = _hovered.Y - _vdy;
            int hw = (int)_hovered.Width;
            int hh = (int)_hovered.Height;

            // Clear the scrim inside the hovered window so its live pixels show
            // through at full brightness, then frame it with a bright border.
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
            {
                g.FillRectangle(clear, hx, hy, hw, hh);
            }
            g.CompositingMode = CompositingMode.SourceOver;

            using var border = new Pen(Color.FromArgb(255, 79, 195, 247), 4);
            g.DrawRectangle(border, hx, hy, hw, hh);

            // Pointer-adjacent window title: offset above-right of the cursor so
            // it follows the pointer without sitting under it, then clamp inside
            // the Virtual Desktop so it stays on-screen near edges or at
            // negative coordinates.
            string label = string.IsNullOrEmpty(_hovered.Title) ? "(untitled window)" : _hovered.Title;
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
            // No eligible window hovered (cursor over empty desktop). Hint that
            // clicking here captures Full Desktop.
            string hint = "Click a window to capture · Click desktop for Full Desktop · Esc cancels";
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
