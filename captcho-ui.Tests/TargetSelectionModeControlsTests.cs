using System.Drawing;
using System.Linq;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public sealed class TargetSelectionModeControlsTests
{
    [Fact]
    public void Buttons_ExposeAllCaptureModesWithProductTerminology()
    {
        var buttons = TargetSelectionModeControls.GetButtons(new Rectangle(-1920, -200, 3840, 1280));

        Assert.Equal(
            new[]
            {
                (CaptureMode.FullDesktop, "Full Desktop"),
                (CaptureMode.ActiveWindow, "Active Window"),
                (CaptureMode.SelectedWindow, "Selected Window"),
                (CaptureMode.SelectedMonitor, "Selected Monitor"),
                (CaptureMode.Selection, "Selection"),
            },
            buttons.Select(button => (button.Mode, button.Label)));
    }

    [Fact]
    public void HitTest_ResolvesEveryVisibleControlInVirtualDesktopCoordinates()
    {
        var virtualDesktop = new Rectangle(-1920, -200, 3840, 1280);
        var buttons = TargetSelectionModeControls.GetButtons(virtualDesktop);

        foreach (var button in buttons)
        {
            var mode = TargetSelectionModeControls.HitTest(
                button.Bounds.Left + button.Bounds.Width / 2,
                button.Bounds.Top + button.Bounds.Height / 2,
                virtualDesktop);

            Assert.Equal(button.Mode, mode);
        }
    }
}
