using System;
using captcho.Capture;
using captcho.UI;
using Xunit;

namespace captcho.UI.Tests;

public class AnnotationTabSettingsTests
{
    [Fact]
    public void Constructor_LoadsCommittedDefaultsWithoutMutatingSource()
    {
        var source = AppSettings.WithDefaults();
        source.AnnotationSettings = new AnnotationSettings(
            AnnotationTool.Pen,
            new AnnotationColor(10, 20, 30),
            7);

        var tab = new AnnotationTabSettings(source);

        Assert.Equal(AnnotationTool.Pen, tab.DefaultTool);
        Assert.Equal(new AnnotationColor(10, 20, 30), tab.PenColor);
        Assert.Equal(7, tab.StrokeWidth);
        Assert.Equal(7, source.AnnotationSettings.StrokeWidth);
    }

    [Fact]
    public void Edits_WriteOneAnnotationSliceAndLeaveOtherSettingsUntouched()
    {
        var tab = new AnnotationTabSettings(AppSettings.WithDefaults());
        var target = new AppSettings
        {
            SaveLocation = @"D:\Preserved",
            AnnotationEnabled = false,
        };

        tab.EditPenColor(new AnnotationColor(1, 2, 3, 200));
        tab.EditStrokeWidth(12);
        tab.WriteInto(target);

        Assert.Equal(new AnnotationColor(1, 2, 3, 200), target.AnnotationSettings.PenColor);
        Assert.Equal(12, target.AnnotationSettings.StrokeWidth);
        Assert.Equal(@"D:\Preserved", target.SaveLocation);
        Assert.False(target.AnnotationEnabled);
    }

    [Fact]
    public void InvalidStrokeWidth_DisablesTheTabAndSurfacesValidation()
    {
        var tab = new AnnotationTabSettings(AppSettings.WithDefaults());

        tab.EditStrokeWidth(0);

        Assert.False(tab.IsValid);
        Assert.Contains("stroke width", tab.FirstError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelAndResetFollowAtomicSettingsSemantics()
    {
        var source = AppSettings.WithDefaults();
        source.AnnotationSettings = new AnnotationSettings(
            AnnotationTool.Pen,
            new AnnotationColor(4, 5, 6),
            11);
        var tab = new AnnotationTabSettings(source);

        tab.EditStrokeWidth(2);
        tab.Cancel();
        Assert.Equal(11, tab.StrokeWidth);

        tab.Reset();
        Assert.Equal(AnnotationSettings.DefaultStrokeWidth, tab.StrokeWidth);
        tab.Cancel();
        Assert.Equal(11, tab.StrokeWidth);
    }
}
