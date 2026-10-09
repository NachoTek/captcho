using System;
using System.Collections.Generic;
using System.Drawing;
using captcho.Capture;

namespace captcho.UI;

/// <summary>Shared Capture Mode toolbar used by every Target Selection overlay.</summary>
public static class TargetSelectionModeControls
{
    private const int TopMargin = 18;
    private const int SideMargin = 12;
    private const int Gap = 6;
    private const int Height = 42;
    private const int PreferredWidth = 740;

    private static readonly (CaptureMode Mode, string Label)[] Modes =
    {
        (CaptureMode.FullDesktop, "Full Desktop"),
        (CaptureMode.ActiveWindow, "Active Window"),
        (CaptureMode.SelectedWindow, "Selected Window"),
        (CaptureMode.SelectedMonitor, "Selected Monitor"),
        (CaptureMode.Selection, "Selection"),
    };

    public static IReadOnlyList<TargetSelectionModeButton> GetButtons(Rectangle virtualDesktop)
    {
        int toolbarWidth = Math.Min(PreferredWidth, Math.Max(1, virtualDesktop.Width - SideMargin * 2));
        int buttonWidth = Math.Max(1, (toolbarWidth - Gap * (Modes.Length - 1)) / Modes.Length);
        int usedWidth = buttonWidth * Modes.Length + Gap * (Modes.Length - 1);
        int left = virtualDesktop.X + (virtualDesktop.Width - usedWidth) / 2;
        int top = virtualDesktop.Y + TopMargin;
        var buttons = new TargetSelectionModeButton[Modes.Length];

        for (int i = 0; i < Modes.Length; i++)
        {
            buttons[i] = new TargetSelectionModeButton(
                Modes[i].Mode,
                Modes[i].Label,
                new Rectangle(left + i * (buttonWidth + Gap), top, buttonWidth, Height));
        }

        return buttons;
    }

    public static CaptureMode? HitTest(int x, int y, Rectangle virtualDesktop)
    {
        foreach (var button in GetButtons(virtualDesktop))
        {
            if (button.Bounds.Contains(x, y))
            {
                return button.Mode;
            }
        }

        return null;
    }

    public static void Draw(
        Graphics graphics,
        CaptureMode activeMode,
        Rectangle virtualDesktop)
    {
        using var font = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var activeFill = new SolidBrush(Color.FromArgb(245, 79, 195, 247));
        using var inactiveFill = new SolidBrush(Color.FromArgb(225, 24, 24, 24));
        using var activeText = new SolidBrush(Color.FromArgb(255, 8, 32, 45));
        using var inactiveText = new SolidBrush(Color.White);
        using var border = new Pen(Color.FromArgb(230, 255, 255, 255), 1f);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        foreach (var button in GetButtons(virtualDesktop))
        {
            var bounds = new Rectangle(
                button.Bounds.X - virtualDesktop.X,
                button.Bounds.Y - virtualDesktop.Y,
                button.Bounds.Width,
                button.Bounds.Height);
            bool active = button.Mode == activeMode;
            graphics.FillRectangle(active ? activeFill : inactiveFill, bounds);
            graphics.DrawRectangle(border, bounds);
            graphics.DrawString(button.Label, font, active ? activeText : inactiveText, bounds, format);
        }
    }
}

public sealed record TargetSelectionModeButton(
    CaptureMode Mode,
    string Label,
    Rectangle Bounds);
