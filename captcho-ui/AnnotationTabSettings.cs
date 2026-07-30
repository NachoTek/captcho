using System;
using System.Linq;
using captcho.Capture;

namespace captcho.UI;

/// <summary>
/// WinUI-free editing seam for the persisted Annotation defaults. It owns only the
/// Annotation slice; SettingsSession supplies atomic persistence with the other tabs.
/// </summary>
internal sealed class AnnotationTabSettings : EditableTabSession
{
    public AnnotationTabSettings(AppSettings persisted)
        : base(persisted)
    {
    }

    public AnnotationTool DefaultTool => Working.EffectiveAnnotationSettings.DefaultTool;
    public AnnotationColor PenColor => Working.EffectiveAnnotationSettings.PenColor;
    public int StrokeWidth => Working.AnnotationSettings?.StrokeWidth ?? AnnotationSettings.DefaultStrokeWidth;

    public void EditDefaultTool(AnnotationTool value) =>
        Working.AnnotationSettings = Working.EffectiveAnnotationSettings with { DefaultTool = value };

    public void EditPenColor(AnnotationColor value) =>
        Working.AnnotationSettings = Working.EffectiveAnnotationSettings with { PenColor = value };

    public void EditStrokeWidth(int value) =>
        Working.AnnotationSettings = Working.EffectiveAnnotationSettings with { StrokeWidth = value };

    public override bool IsValid => !SliceIssues().Any(i => i.Field == SettingsField.AnnotationSettings);

    public override string? FirstError =>
        SliceIssues().FirstOrDefault(i => i.Field == SettingsField.AnnotationSettings)?.Message;

    public override bool IsDirty => Working.AnnotationSettings != Baseline.AnnotationSettings;

    public override void WriteInto(AppSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.AnnotationSettings = Working.AnnotationSettings;
    }

    protected override void ApplyDefaults(AppSettings working) =>
        working.AnnotationSettings = AnnotationSettings.WithDefaults();
}
