// WindowResolver.cs — Resolves window metadata for UI display labels.
//
// Provides managed, UI-friendly access to Win32 window metadata:
// active HWND, HWND under cursor, visibility/minimized checks, bounded title.
// All methods catch Win32 failures and return safe fallback values
// so they never crash the UI or headless tests.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Respectacle.UI;

/// <summary>
/// Resolves window metadata for capture mode labels and validation.
/// All methods are safe to call from any thread and never throw for Win32 errors.
/// </summary>
public static class WindowResolver
{
    #region Win32 P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    #endregion

    /// <summary>Maximum characters for a sanitized window title label.</summary>
    private const int MaxTitleLength = 60;

    /// <summary>
    /// Gets the HWND of the currently active (foreground) window.
    /// Returns IntPtr.Zero if no foreground window is available.
    /// </summary>
    public static IntPtr GetActiveWindowHandle()
    {
        try
        {
            return GetForegroundWindow();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets the HWND of the top-level window under the mouse cursor.
    /// Normalizes child controls to their root ancestor via GetAncestor(GA_ROOT).
    /// Returns IntPtr.Zero if resolution fails.
    /// </summary>
    public static IntPtr GetWindowUnderCursorHandle()
    {
        try
        {
            if (!GetCursorPos(out POINT pt))
                return IntPtr.Zero;

            IntPtr child = WindowFromPoint(pt);
            if (child == IntPtr.Zero)
                return IntPtr.Zero;

            IntPtr root = GetAncestor(child, GA_ROOT);
            return root == IntPtr.Zero ? child : root;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Gets a bounded, sanitized window title for display.
    /// Returns empty string if the handle is invalid or the title is empty.
    /// Truncates titles exceeding MaxTitleLength with an ellipsis.
    /// </summary>
    public static string GetTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "";

        try
        {
            var sb = new StringBuilder(MaxTitleLength + 1);
            int len = GetWindowText(hwnd, sb, MaxTitleLength + 1);
            if (len == 0)
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

    /// <summary>
    /// Returns true if the window is visible (not hidden).
    /// Returns false for invalid handles or on Win32 errors.
    /// </summary>
    public static bool IsVisible(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        try
        {
            return IsWindowVisible(hwnd);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the window is minimized.
    /// Returns false for invalid handles or on Win32 errors.
    /// </summary>
    public static bool IsMinimized(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        try
        {
            return IsIconic(hwnd);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Formats a display-safe handle string for UI labels (e.g., "0x1234AB").
    /// Returns empty string for zero handles.
    /// </summary>
    public static string FormatHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "";
        try
        {
            return $"0x{hwnd.ToInt64():X}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Builds a descriptive mode label for the active window capture.
    /// Includes the sanitized title and handle when available.
    /// Falls back to "Active Window" alone when metadata is unavailable.
    /// </summary>
    public static string BuildActiveWindowLabel()
    {
        IntPtr hwnd = GetActiveWindowHandle();
        string title = GetTitle(hwnd);
        string handle = FormatHandle(hwnd);

        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(handle))
            return "Active Window";

        if (string.IsNullOrEmpty(title))
            return $"Active Window [{handle}]";

        return $"Active Window — {title}";
    }

    /// <summary>
    /// Builds a descriptive mode label for the window-under-cursor capture.
    /// Includes the sanitized title and handle when available.
    /// Falls back to "Window Under Cursor" alone when metadata is unavailable.
    /// </summary>
    public static string BuildWindowUnderCursorLabel()
    {
        IntPtr hwnd = GetWindowUnderCursorHandle();
        string title = GetTitle(hwnd);
        string handle = FormatHandle(hwnd);

        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(handle))
            return "Window Under Cursor";

        if (string.IsNullOrEmpty(title))
            return $"Window Under Cursor [{handle}]";

        return $"Window Under Cursor — {title}";
    }
}
