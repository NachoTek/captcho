using System;
using System.Collections.Generic;
using System.Linq;

namespace captcho.Capture;

/// <summary>Annotation tools currently available in the post-capture editor.</summary>
public enum AnnotationTool
{
    Pen,
}

/// <summary>RGBA color used by annotation strokes.</summary>
public readonly record struct AnnotationColor(
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha = 255)
{
    public static AnnotationColor RedOpaque => new(255, 0, 0);
    public static AnnotationColor BlueOpaque => new(0, 90, 255);
    public static AnnotationColor BlackOpaque => new(0, 0, 0);
}

/// <summary>Integer pixel coordinate in the source Frame.</summary>
public readonly record struct AnnotationPoint(int X, int Y);

/// <summary>
/// One global state object for the active Annotation toolbar. A stroke snapshots this
/// state when it begins, so changing the toolbar never rewrites existing strokes.
/// </summary>
public sealed record AnnotationToolState(
    AnnotationTool Tool,
    AnnotationColor PenColor,
    int StrokeWidth)
{
    public static AnnotationToolState WithDefaults() => AnnotationSettings.WithDefaults().ToToolState();

    public AnnotationToolState Normalized() => new(
        Enum.IsDefined(Tool) ? Tool : AnnotationTool.Pen,
        PenColor,
        StrokeWidth is >= AnnotationSettings.MinimumStrokeWidth and <= AnnotationSettings.MaximumStrokeWidth
            ? StrokeWidth
            : AnnotationSettings.DefaultStrokeWidth);
}

/// <summary>
/// Persisted Annotation defaults. This is intentionally separate from the live
/// <see cref="AnnotationToolState"/> so an editing session can change its toolbar without
/// changing Configuration.
/// </summary>
public sealed record AnnotationSettings(
    AnnotationTool DefaultTool,
    AnnotationColor PenColor,
    int StrokeWidth)
{
    public const int MinimumStrokeWidth = 1;
    public const int MaximumStrokeWidth = 64;
    public const int DefaultStrokeWidth = 4;
    public static readonly AnnotationColor DefaultPenColor = AnnotationColor.RedOpaque;

    public static AnnotationSettings WithDefaults() => new(
        AnnotationTool.Pen,
        DefaultPenColor,
        DefaultStrokeWidth);

    public AnnotationSettings Normalized() => new(
        Enum.IsDefined(DefaultTool) ? DefaultTool : AnnotationTool.Pen,
        PenColor,
        StrokeWidth is >= MinimumStrokeWidth and <= MaximumStrokeWidth
            ? StrokeWidth
            : DefaultStrokeWidth);

    public AnnotationToolState ToToolState() =>
        new AnnotationToolState(DefaultTool, PenColor, StrokeWidth).Normalized();
}

/// <summary>Immutable committed or in-progress freehand pen stroke.</summary>
public sealed class AnnotationStroke
{
    public AnnotationTool Tool { get; }
    public AnnotationColor Color { get; }
    public int StrokeWidth { get; }
    public IReadOnlyList<AnnotationPoint> Points { get; }

    public AnnotationStroke(
        AnnotationTool tool,
        AnnotationColor color,
        int strokeWidth,
        IReadOnlyList<AnnotationPoint> points)
    {
        if (!Enum.IsDefined(tool))
            throw new ArgumentOutOfRangeException(nameof(tool));
        if (strokeWidth is < AnnotationSettings.MinimumStrokeWidth or > AnnotationSettings.MaximumStrokeWidth)
            throw new ArgumentOutOfRangeException(nameof(strokeWidth));
        ArgumentNullException.ThrowIfNull(points);

        Tool = tool;
        Color = color;
        StrokeWidth = strokeWidth;
        Points = Array.AsReadOnly(points.ToArray());
    }
}

/// <summary>
/// Source-plus-strokes Annotation document. The source Frame is never mutated; every
/// render creates a fresh composite from the source, committed strokes, and any current
/// in-progress stroke.
/// </summary>
public sealed class AnnotationDocument
{
    private readonly List<AnnotationStroke> _strokes = new();
    private readonly List<AnnotationStroke> _redoStrokes = new();
    private List<AnnotationPoint>? _inProgressPoints;
    private AnnotationToolState? _inProgressState;

    public AnnotationDocument(ContiguousBitmap sourceFrame) =>
        SourceFrame = sourceFrame ?? throw new ArgumentNullException(nameof(sourceFrame));

    public ContiguousBitmap SourceFrame { get; }
    public IReadOnlyList<AnnotationStroke> Strokes => _strokes.AsReadOnly();
    public bool CanUndo => _strokes.Count > 0 && _inProgressPoints is null;
    public bool CanRedo => _redoStrokes.Count > 0 && _inProgressPoints is null;

    public AnnotationStroke? InProgressStroke =>
        _inProgressPoints is null || _inProgressState is null
            ? null
            : new AnnotationStroke(
                _inProgressState.Tool,
                _inProgressState.PenColor,
                _inProgressState.StrokeWidth,
                _inProgressPoints);

    public void BeginStroke(AnnotationPoint point, AnnotationToolState toolState)
    {
        ArgumentNullException.ThrowIfNull(toolState);
        var state = toolState.Normalized();
        if (state.Tool != AnnotationTool.Pen)
            throw new ArgumentOutOfRangeException(nameof(toolState), "Only the pen tool is available.");

        _inProgressState = state;
        _inProgressPoints = new List<AnnotationPoint> { point };
    }

    public void AppendStrokePoint(AnnotationPoint point)
    {
        _inProgressPoints?.Add(point);
    }

    public bool CommitStroke()
    {
        var stroke = InProgressStroke;
        if (stroke is null)
            return false;

        _strokes.Add(stroke);
        if (_redoStrokes.Count > 0)
            _redoStrokes.Clear();
        CancelStroke();
        return true;
    }

    public void CancelStroke()
    {
        _inProgressPoints = null;
        _inProgressState = null;
    }

    /// <summary>Undo removes the latest committed stroke and updates the composed Frame on the next render.</summary>
    public bool Undo()
    {
        if (_inProgressPoints is not null)
            return false;
        if (_strokes.Count == 0)
            return false;

        _redoStrokes.Add(_strokes[^1]);
        _strokes.RemoveAt(_strokes.Count - 1);
        return true;
    }

    /// <summary>Redo restores the most recently undone stroke in its original order and style.</summary>
    public bool Redo()
    {
        if (_inProgressPoints is not null)
            return false;
        if (_redoStrokes.Count == 0)
            return false;

        _strokes.Add(_redoStrokes[^1]);
        _redoStrokes.RemoveAt(_redoStrokes.Count - 1);
        return true;
    }

    public ContiguousBitmap Render() => AnnotationRenderer.Render(SourceFrame, _strokes, InProgressStroke);
}

/// <summary>
/// Runtime Annotation seam combining one global toolbar state with one document.
/// </summary>
public sealed class AnnotationSession
{
    public AnnotationSession(
        ContiguousBitmap sourceFrame,
        AnnotationToolState? initialToolState = null)
    {
        Document = new AnnotationDocument(sourceFrame);
        ToolState = (initialToolState ?? AnnotationToolState.WithDefaults()).Normalized();
    }

    public AnnotationDocument Document { get; }
    public AnnotationToolState ToolState { get; private set; }

    public void SetToolState(AnnotationToolState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ToolState = state.Normalized();
    }

    public void SetTool(AnnotationTool tool) =>
        SetToolState(ToolState with { Tool = tool });

    public void SetPenColor(AnnotationColor color) =>
        SetToolState(ToolState with { PenColor = color });

    public void SetStrokeWidth(int width) =>
        SetToolState(ToolState with { StrokeWidth = width });

    public void BeginStroke(AnnotationPoint point) => Document.BeginStroke(point, ToolState);
    public void AppendStrokePoint(AnnotationPoint point) => Document.AppendStrokePoint(point);
    public bool CommitStroke() => Document.CommitStroke();
    public void CancelStroke() => Document.CancelStroke();
    public bool Undo() => Document.Undo();
    public bool Redo() => Document.Redo();
    public bool CanUndo => Document.CanUndo;
    public bool CanRedo => Document.CanRedo;
    public ContiguousBitmap Render() => Document.Render();
}

/// <summary>Software compositor for BGRA Frames and pen strokes.</summary>
public static class AnnotationRenderer
{
    public static ContiguousBitmap Render(
        ContiguousBitmap sourceFrame,
        IEnumerable<AnnotationStroke> committedStrokes,
        AnnotationStroke? inProgressStroke = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        ArgumentNullException.ThrowIfNull(committedStrokes);

        var pixels = sourceFrame.Pixels.ToArray();
        foreach (var stroke in committedStrokes)
            DrawStroke(sourceFrame.Width, sourceFrame.Height, sourceFrame.Stride, pixels, stroke);
        if (inProgressStroke is not null)
            DrawStroke(sourceFrame.Width, sourceFrame.Height, sourceFrame.Stride, pixels, inProgressStroke);

        return new ContiguousBitmap(sourceFrame.Width, sourceFrame.Height, sourceFrame.Stride, pixels);
    }

    private static void DrawStroke(int width, int height, int stride, byte[] pixels, AnnotationStroke stroke)
    {
        if (stroke.Tool != AnnotationTool.Pen || stroke.Points.Count == 0)
            return;

        var points = stroke.Points;
        Stamp(width, height, stride, pixels, points[0], stroke);
        for (int i = 1; i < points.Count; i++)
            DrawSegment(width, height, stride, pixels, points[i - 1], points[i], stroke);
    }

    private static void DrawSegment(
        int width,
        int height,
        int stride,
        byte[] pixels,
        AnnotationPoint start,
        AnnotationPoint end,
        AnnotationStroke stroke)
    {
        int dx = end.X - start.X;
        int dy = end.Y - start.Y;
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (steps == 0)
        {
            Stamp(width, height, stride, pixels, start, stroke);
            return;
        }

        for (int step = 1; step <= steps; step++)
        {
            var point = new AnnotationPoint(
                (int)Math.Round(start.X + dx * (double)step / steps, MidpointRounding.AwayFromZero),
                (int)Math.Round(start.Y + dy * (double)step / steps, MidpointRounding.AwayFromZero));
            Stamp(width, height, stride, pixels, point, stroke);
        }
    }

    private static void Stamp(
        int width,
        int height,
        int stride,
        byte[] pixels,
        AnnotationPoint center,
        AnnotationStroke stroke)
    {
        double radius = Math.Max(0.5, stroke.StrokeWidth / 2.0);
        int minX = Math.Max(0, (int)Math.Floor(center.X - radius));
        int maxX = Math.Min(width - 1, (int)Math.Ceiling(center.X + radius));
        int minY = Math.Max(0, (int)Math.Floor(center.Y - radius));
        int maxY = Math.Min(height - 1, (int)Math.Ceiling(center.Y + radius));
        double radiusSquared = radius * radius;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                double distanceX = x - center.X;
                double distanceY = y - center.Y;
                if (distanceX * distanceX + distanceY * distanceY <= radiusSquared)
                    BlendPixel(pixels, y * stride + x * 4, stroke.Color);
            }
        }
    }

    private static void BlendPixel(byte[] pixels, int offset, AnnotationColor color)
    {
        int sourceAlpha = color.Alpha;
        if (sourceAlpha == 0)
            return;
        if (sourceAlpha == 255)
        {
            pixels[offset] = color.Blue;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Red;
            pixels[offset + 3] = 255;
            return;
        }

        int destinationAlpha = pixels[offset + 3];
        int outputAlpha = sourceAlpha + (destinationAlpha * (255 - sourceAlpha) + 127) / 255;
        if (outputAlpha == 0)
            return;

        pixels[offset] = CompositeChannel(color.Blue, pixels[offset], color.Alpha, destinationAlpha, outputAlpha);
        pixels[offset + 1] = CompositeChannel(color.Green, pixels[offset + 1], color.Alpha, destinationAlpha, outputAlpha);
        pixels[offset + 2] = CompositeChannel(color.Red, pixels[offset + 2], color.Alpha, destinationAlpha, outputAlpha);
        pixels[offset + 3] = (byte)outputAlpha;
    }

    private static byte CompositeChannel(
        byte source,
        byte destination,
        int sourceAlpha,
        int destinationAlpha,
        int outputAlpha) =>
        (byte)Math.Clamp(
            (source * sourceAlpha + destination * destinationAlpha * (255 - sourceAlpha) / 255 + outputAlpha / 2)
                / outputAlpha,
            0,
            255);
}
