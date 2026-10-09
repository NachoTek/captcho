using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;

namespace captcho.Capture;

/// <summary>Annotation tools currently available in the post-capture editor.</summary>
public enum AnnotationTool
{
    Pen,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Text,
    Marker,
}

/// <summary>
/// Fill treatment for the closed shape tools. Shapes that support filling snapshot
/// this alongside the shared stroke color and width.
/// </summary>
public enum AnnotationFillStyle
{
    /// <summary>No fill; only the shape outline is drawn.</summary>
    None,
    /// <summary>The shape interior is filled with the shared color, drawn under the outline.</summary>
    Solid,
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
    int StrokeWidth,
    AnnotationFillStyle Fill = AnnotationFillStyle.None)
{
    public static AnnotationToolState WithDefaults() => AnnotationSettings.WithDefaults().ToToolState();

    public AnnotationToolState Normalized() => new(
        Enum.IsDefined(Tool) ? Tool : AnnotationTool.Pen,
        PenColor,
        StrokeWidth is >= AnnotationSettings.MinimumStrokeWidth and <= AnnotationSettings.MaximumStrokeWidth
            ? StrokeWidth
            : AnnotationSettings.DefaultStrokeWidth,
        Enum.IsDefined(Fill) ? Fill : AnnotationFillStyle.None);

    /// <summary>Whether the tool draws a filled interior under its outline.</summary>
    public bool ToolSupportsFill => Tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse;
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

/// <summary>Immutable committed or in-progress annotation stroke or shape.</summary>
public sealed class AnnotationStroke
{
    public AnnotationTool Tool { get; }
    public AnnotationColor Color { get; }
    public int StrokeWidth { get; }
    public AnnotationFillStyle Fill { get; }
    public IReadOnlyList<AnnotationPoint> Points { get; }

    /// <summary>
    /// Text content for the text tool; null for stroke/shape tools. A text
    /// entry carries its anchor in <see cref="Points"/>[0] and never more than
    /// one point.
    /// </summary>
    public string? Text { get; }

    /// <summary>
    /// Sequence number for the marker tool; null for every other tool. Numbers
    /// follow commit order: the next marker takes one past the highest number
    /// still committed, so undo never renumbers the survivors and redo restores
    /// the original number.
    /// </summary>
    public int? MarkerNumber { get; }

    public AnnotationStroke(
        AnnotationTool tool,
        AnnotationColor color,
        int strokeWidth,
        IReadOnlyList<AnnotationPoint> points,
        AnnotationFillStyle fill = AnnotationFillStyle.None,
        string? text = null,
        int? markerNumber = null)
    {
        if (!Enum.IsDefined(tool))
            throw new ArgumentOutOfRangeException(nameof(tool));
        if (strokeWidth is < AnnotationSettings.MinimumStrokeWidth or > AnnotationSettings.MaximumStrokeWidth)
            throw new ArgumentOutOfRangeException(nameof(strokeWidth));
        ArgumentNullException.ThrowIfNull(points);

        Tool = tool;
        Color = color;
        StrokeWidth = strokeWidth;
        Fill = Enum.IsDefined(fill) ? fill : AnnotationFillStyle.None;
        Points = Array.AsReadOnly(points.ToArray());
        Text = tool == AnnotationTool.Text ? text ?? string.Empty : null;
        MarkerNumber = tool == AnnotationTool.Marker
            ? markerNumber is null or < 1 ? 1 : markerNumber
            : null;
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
    private string? _inProgressText;
    private int? _inProgressMarkerNumber;

    public AnnotationDocument(ContiguousBitmap sourceFrame) =>
        SourceFrame = sourceFrame ?? throw new ArgumentNullException(nameof(sourceFrame));

    public ContiguousBitmap SourceFrame { get; }
    public IReadOnlyList<AnnotationStroke> Strokes => _strokes.AsReadOnly();
    public bool CanUndo => _strokes.Count > 0 && _inProgressPoints is null;
    public bool CanRedo => _redoStrokes.Count > 0 && _inProgressPoints is null;

    /// <summary>Content of the in-progress text entry; null when no text entry is open.</summary>
    public string? InProgressText => _inProgressText;

    public AnnotationStroke? InProgressStroke
    {
        get
        {
            if (_inProgressPoints is null || _inProgressState is null)
                return null;
            // An empty or whitespace text entry has nothing legible to show yet.
            if (_inProgressText is not null)
                return string.IsNullOrWhiteSpace(_inProgressText)
                    ? null
                    : new AnnotationStroke(
                        AnnotationTool.Text,
                        _inProgressState.PenColor,
                        _inProgressState.StrokeWidth,
                        _inProgressPoints,
                        text: _inProgressText);
            return new AnnotationStroke(
                _inProgressState.Tool,
                _inProgressState.PenColor,
                _inProgressState.StrokeWidth,
                _inProgressPoints,
                EffectiveFill(_inProgressState),
                markerNumber: _inProgressMarkerNumber);
        }
    }

    public void BeginStroke(AnnotationPoint point, AnnotationToolState toolState)
    {
        ArgumentNullException.ThrowIfNull(toolState);
        var state = toolState.Normalized();

        // The text tool places an editable entry at the click rather than a
        // point stroke; its content arrives through EditInProgressText.
        if (state.Tool == AnnotationTool.Text)
        {
            BeginText(point, toolState);
            return;
        }

        _inProgressState = state;
        // Shapes drag from a fixed anchor to a moving current point, so they begin with
        // the anchor duplicated and the drag replaces the duplicate. Pen begins with a
        // single point and grows by appending. Markers are placed at the click alone.
        _inProgressPoints = state.Tool == AnnotationTool.Pen
            ? new List<AnnotationPoint> { point }
            : state.Tool == AnnotationTool.Marker
                ? new List<AnnotationPoint> { point }
                : new List<AnnotationPoint> { point, point };
        _inProgressText = null;
        _inProgressMarkerNumber = state.Tool == AnnotationTool.Marker ? NextMarkerNumber() : null;
    }

    /// <summary>
    /// The number the next committed marker will carry: one past the highest number
    /// still committed, so undo never renumbers survivors and redo restores the
    /// original number.
    /// </summary>
    private int NextMarkerNumber()
    {
        int highest = 0;
        foreach (var stroke in _strokes)
            if (stroke.MarkerNumber is > 0 and { } number && number > highest)
                highest = number;
        return highest + 1;
    }

    /// <summary>
    /// Opens a text entry at <paramref name="point"/> snapshotting the shared
    /// style. The entry renders and commits like any other annotation; empty or
    /// cancelled input never becomes a document entry.
    /// </summary>
    public void BeginText(AnnotationPoint point, AnnotationToolState toolState)
    {
        ArgumentNullException.ThrowIfNull(toolState);
        var state = toolState.Normalized();

        _inProgressState = state;
        _inProgressPoints = new List<AnnotationPoint> { point };
        _inProgressText = string.Empty;
    }

    /// <summary>
    /// Places a numbered marker at <paramref name="point"/> snapshotting the shared
    /// style. The entry previews its assigned number and commits like any other
    /// annotation.
    /// </summary>
    public void BeginMarker(AnnotationPoint point, AnnotationToolState toolState)
    {
        ArgumentNullException.ThrowIfNull(toolState);
        var state = toolState.Normalized();

        _inProgressState = state;
        _inProgressPoints = new List<AnnotationPoint> { point };
        _inProgressText = null;
        _inProgressMarkerNumber = NextMarkerNumber();
    }

    /// <summary>Replaces the content of the open text entry; a no-op otherwise.</summary>
    public void EditInProgressText(string content)
    {
        if (_inProgressText is null)
            return;

        _inProgressText = content ?? string.Empty;
    }

    /// <summary>
    /// The fill style a stroke actually carries: tools that cannot fill never snapshot
    /// a lingering toolbar fill selection, so every stroke records only applicable style.
    /// </summary>
    private static AnnotationFillStyle EffectiveFill(AnnotationToolState state) =>
        state.ToolSupportsFill ? state.Fill : AnnotationFillStyle.None;

    public void AppendStrokePoint(AnnotationPoint point)
    {
        if (_inProgressPoints is null || _inProgressState?.Tool is not AnnotationTool.Pen)
            return;

        _inProgressPoints.Add(point);
    }

    /// <summary>
    /// Replaces the current drag point of an in-progress shape. The first point stays
    /// fixed as the anchor; pen strokes, text entries, and markers ignore this.
    /// </summary>
    public void UpdateStrokePoint(AnnotationPoint point)
    {
        if (_inProgressPoints is null
            || _inProgressState?.Tool is AnnotationTool.Pen or AnnotationTool.Text or AnnotationTool.Marker)
            return;

        _inProgressPoints[^1] = point;
    }

    public bool CommitStroke()
    {
        var stroke = InProgressStroke;
        if (stroke is null)
        {
            // An empty or whitespace text entry commits nothing and ends the entry.
            if (_inProgressText is not null)
                CancelStroke();
            return false;
        }

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
        _inProgressText = null;
        _inProgressMarkerNumber = null;
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

    public void SetFill(AnnotationFillStyle fill) =>
        SetToolState(ToolState with { Fill = fill });

    public void BeginStroke(AnnotationPoint point) => Document.BeginStroke(point, ToolState);
    public void AppendStrokePoint(AnnotationPoint point) => Document.AppendStrokePoint(point);
    public void UpdateStrokePoint(AnnotationPoint point) => Document.UpdateStrokePoint(point);
    public void BeginText(AnnotationPoint point) => Document.BeginText(point, ToolState);
    public void BeginMarker(AnnotationPoint point) => Document.BeginMarker(point, ToolState);
    public void EditInProgressText(string content) => Document.EditInProgressText(content);
    public bool CommitStroke() => Document.CommitStroke();
    public void CancelStroke() => Document.CancelStroke();
    public bool Undo() => Document.Undo();
    public bool Redo() => Document.Redo();
    public bool CanUndo => Document.CanUndo;
    public bool CanRedo => Document.CanRedo;
    public ContiguousBitmap Render() => Document.Render();
}

/// <summary>Software compositor for BGRA Frames and annotation strokes.</summary>
public static class AnnotationRenderer
{
    /// <summary>Minimum text em size in pixels regardless of shared stroke width.</summary>
    public const float MinimumTextEmSize = 12f;

    /// <summary>Minimum marker disc diameter in pixels regardless of shared stroke width.</summary>
    public const double MinimumMarkerDiameter = 24.0;

    /// <summary>
    /// Maps the shared stroke width onto the text em size (in pixels) so text
    /// participates in the same width control as the stroke tools.
    /// </summary>
    public static float TextEmSize(int strokeWidth) =>
        Math.Max(MinimumTextEmSize, strokeWidth * 6f);

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
        if (stroke.Points.Count == 0)
            return;

        switch (stroke.Tool)
        {
            case AnnotationTool.Pen:
                DrawPenStroke(width, height, stride, pixels, stroke);
                break;
            case AnnotationTool.Rectangle:
                DrawRectangleStroke(width, height, stride, pixels, stroke);
                break;
            case AnnotationTool.Ellipse:
                DrawEllipseStroke(width, height, stride, pixels, stroke);
                break;
            case AnnotationTool.Line:
                Stamp(width, height, stride, pixels, stroke.Points[0], stroke);
                DrawSegment(width, height, stride, pixels, stroke.Points[0], stroke.Points[^1], stroke);
                break;
            case AnnotationTool.Arrow:
                DrawArrowStroke(width, height, stride, pixels, stroke);
                break;
            case AnnotationTool.Text:
                DrawTextStroke(width, height, stride, pixels, stroke);
                break;
            case AnnotationTool.Marker:
                DrawMarkerStroke(width, height, stride, pixels, stroke);
                break;
        }
    }

    private static void DrawPenStroke(int width, int height, int stride, byte[] pixels, AnnotationStroke stroke)
    {
        var points = stroke.Points;
        Stamp(width, height, stride, pixels, points[0], stroke);
        for (int i = 1; i < points.Count; i++)
            DrawSegment(width, height, stride, pixels, points[i - 1], points[i], stroke);
    }

    private static void DrawRectangleStroke(int width, int height, int stride, byte[] pixels, AnnotationStroke stroke)
    {
        var (left, top, right, bottom) = OrderedBounds(stroke.Points[0], stroke.Points[^1]);
        FillBoundsIfFilled(width, height, stride, pixels, stroke, left, top, right, bottom);
        DrawSegment(width, height, stride, pixels, new AnnotationPoint(left, top), new AnnotationPoint(right, top), stroke);
        DrawSegment(width, height, stride, pixels, new AnnotationPoint(right, top), new AnnotationPoint(right, bottom), stroke);
        DrawSegment(width, height, stride, pixels, new AnnotationPoint(right, bottom), new AnnotationPoint(left, bottom), stroke);
        DrawSegment(width, height, stride, pixels, new AnnotationPoint(left, bottom), new AnnotationPoint(left, top), stroke);
    }

    private static void DrawEllipseStroke(int width, int height, int stride, byte[] pixels, AnnotationStroke stroke)
    {
        var (left, top, right, bottom) = OrderedBounds(stroke.Points[0], stroke.Points[^1]);
        int centerX = (left + right) / 2;
        int centerY = (top + bottom) / 2;
        double radiusX = Math.Max(0.5, (right - left) / 2.0);
        double radiusY = Math.Max(0.5, (bottom - top) / 2.0);
        FillEllipseIfFilled(width, height, stride, pixels, stroke, centerX, centerY, radiusX, radiusY);

        int steps = Math.Max(1, (int)Math.Ceiling(2 * Math.PI * Math.Max(radiusX, radiusY)));
        AnnotationPoint previous = EllipsePoint(centerX, centerY, radiusX, radiusY, 0);
        for (int step = 1; step <= steps; step++)
        {
            AnnotationPoint current = EllipsePoint(centerX, centerY, radiusX, radiusY, 2 * Math.PI * step / steps);
            DrawSegment(width, height, stride, pixels, previous, current, stroke);
            previous = current;
        }
    }

    private static void DrawArrowStroke(int width, int height, int stride, byte[] pixels, AnnotationStroke stroke)
    {
        var start = stroke.Points[0];
        var end = stroke.Points[^1];
        Stamp(width, height, stride, pixels, start, stroke);
        DrawSegment(width, height, stride, pixels, start, end, stroke);

        double length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        if (length < 1)
            return;

        // The arrowhead points toward the end along the shaft direction; the barbs keep a
        // 30-degree angle off the shaft so wide strokes still read as an arrow.
        double directionX = (end.X - start.X) / length;
        double directionY = (end.Y - start.Y) / length;
        const double BarbAngle = Math.PI / 6;
        double headLength = Math.Max(stroke.StrokeWidth * 3.0, 8.0);
        double cos = Math.Cos(BarbAngle);
        double sin = Math.Sin(BarbAngle);
        var firstBarb = new AnnotationPoint(
            (int)Math.Round(end.X - headLength * (directionX * cos - directionY * sin), MidpointRounding.AwayFromZero),
            (int)Math.Round(end.Y - headLength * (directionX * sin + directionY * cos), MidpointRounding.AwayFromZero));
        var secondBarb = new AnnotationPoint(
            (int)Math.Round(end.X - headLength * (directionX * cos + directionY * sin), MidpointRounding.AwayFromZero),
            (int)Math.Round(end.Y - headLength * (directionY * cos - directionX * sin), MidpointRounding.AwayFromZero));
        Stamp(width, height, stride, pixels, end, stroke);
        DrawSegment(width, height, stride, pixels, end, firstBarb, stroke);
        DrawSegment(width, height, stride, pixels, end, secondBarb, stroke);
    }

    private static (int Left, int Top, int Right, int Bottom) OrderedBounds(AnnotationPoint first, AnnotationPoint last) =>
        (Math.Min(first.X, last.X),
         Math.Min(first.Y, last.Y),
         Math.Max(first.X, last.X),
         Math.Max(first.Y, last.Y));

    /// <summary>
    /// Rasterizes a text entry with GDI+ onto a scratch ARGB surface and blends
    /// the covered pixels into the Frame buffer. Rendering reads the glyph alpha
    /// so anti-aliased edges composite with the annotation color.
    /// </summary>
    private static void DrawTextStroke(int width, int height, int stride, byte[] pixels, AnnotationStroke stroke)
    {
        if (string.IsNullOrWhiteSpace(stroke.Text))
            return;

        var anchor = stroke.Points[0];
        float emSize = TextEmSize(stroke.StrokeWidth);
        using var font = new Font(FontFamily.GenericSansSerif, emSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var scratch = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bounds = new Rectangle(0, 0, width, height);
        using (var graphics = Graphics.FromImage(scratch))
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // The anchor is the top-left of the text; content grows right and down
            // from the point where the user clicked.
            graphics.DrawString(stroke.Text, font, Brushes.White, new PointF(anchor.X, anchor.Y));
        }

        var data = scratch.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var scan = new byte[Math.Abs(data.Stride) * height];
            Marshal.Copy(data.Scan0, scan, 0, scan.Length);
            for (int y = 0; y < height; y++)
            {
                int scanRow = y * data.Stride;
                int pixelRow = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int alpha = scan[scanRow + x * 4 + 3];
                    if (alpha == 0)
                        continue;
                    BlendPixel(
                        pixels,
                        pixelRow + x * 4,
                        new AnnotationColor(
                            stroke.Color.Red,
                            stroke.Color.Green,
                            stroke.Color.Blue,
                            (byte)Math.Clamp((int)Math.Round(stroke.Color.Alpha * alpha / 255.0), 0, 255)));
                }
            }
        }
        finally
        {
            scratch.UnlockBits(data);
        }
    }

    /// <summary>
    /// The marker disc diameter mapped from the shared stroke width so markers
    /// participate in the same width control as the stroke tools while staying
    /// large enough to hold a readable numeral.
    /// </summary>
    public static double MarkerDiameter(int strokeWidth) =>
        Math.Max(MinimumMarkerDiameter, strokeWidth * 4.0);

    /// <summary>
    /// Draws one numbered marker: a solid disc in the shared annotation color with
    /// the sequence numeral rasterized in white at its center, mirroring the GDI+
    /// glyph path the text tool uses.
    /// </summary>
    private static void DrawMarkerStroke(int width, int height, int stride, byte[] pixels, AnnotationStroke stroke)
    {
        var anchor = stroke.Points[0];
        double radius = MarkerDiameter(stroke.StrokeWidth) / 2.0;
        int minX = Math.Max(0, (int)Math.Floor(anchor.X - radius));
        int maxX = Math.Min(width - 1, (int)Math.Ceiling(anchor.X + radius));
        int minY = Math.Max(0, (int)Math.Floor(anchor.Y - radius));
        int maxY = Math.Min(height - 1, (int)Math.Ceiling(anchor.Y + radius));
        double radiusSquared = radius * radius;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                double distanceX = x - anchor.X;
                double distanceY = y - anchor.Y;
                if (distanceX * distanceX + distanceY * distanceY <= radiusSquared)
                    BlendPixel(pixels, y * stride + x * 4, stroke.Color);
            }
        }

        DrawMarkerNumeral(width, height, stride, pixels, anchor, radius, stroke);
    }

    /// <summary>
    /// Rasterizes the marker numeral with GDI+ onto a scratch ARGB surface and
    /// blends the glyph alpha in white over the disc, so anti-aliased edges
    /// composite readably regardless of the annotation color.
    /// </summary>
    private static void DrawMarkerNumeral(
        int width,
        int height,
        int stride,
        byte[] pixels,
        AnnotationPoint anchor,
        double radius,
        AnnotationStroke stroke)
    {
        float emSize = (float)(radius * 1.1);
        using var font = new Font(FontFamily.GenericSansSerif, emSize, FontStyle.Bold, GraphicsUnit.Pixel);
        string numeral = stroke.MarkerNumber?.ToString() ?? string.Empty;
        if (numeral.Length == 0)
            return;

        using var scratch = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bounds = new Rectangle(0, 0, width, height);
        using (var graphics = Graphics.FromImage(scratch))
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var numeralSize = graphics.MeasureString(numeral, font);
            // The numeral is centered inside the disc.
            graphics.DrawString(
                numeral,
                font,
                Brushes.White,
                new PointF(
                    (float)(anchor.X - numeralSize.Width / 2),
                    (float)(anchor.Y - numeralSize.Height / 2)));
        }

        var data = scratch.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var scan = new byte[Math.Abs(data.Stride) * height];
            Marshal.Copy(data.Scan0, scan, 0, scan.Length);
            for (int y = minY(anchor, radius); y <= maxY(anchor, radius, height); y++)
            {
                int scanRow = y * data.Stride;
                int pixelRow = y * stride;
                for (int x = minX(anchor, radius); x <= maxX(anchor, radius, width); x++)
                {
                    int alpha = scan[scanRow + x * 4 + 3];
                    if (alpha == 0)
                        continue;
                    BlendPixel(
                        pixels,
                        pixelRow + x * 4,
                        new AnnotationColor(255, 255, 255, (byte)Math.Clamp(alpha, 0, 255)));
                }
            }
        }
        finally
        {
            scratch.UnlockBits(data);
        }

        static int minX(AnnotationPoint anchor, double radius) =>
            Math.Max(0, (int)Math.Floor(anchor.X - radius));
        static int maxX(AnnotationPoint anchor, double radius, int width) =>
            Math.Min(width - 1, (int)Math.Ceiling(anchor.X + radius));
        static int minY(AnnotationPoint anchor, double radius) =>
            Math.Max(0, (int)Math.Floor(anchor.Y - radius));
        static int maxY(AnnotationPoint anchor, double radius, int height) =>
            Math.Min(height - 1, (int)Math.Ceiling(anchor.Y + radius));
    }

    private static void FillBoundsIfFilled(
        int width,
        int height,
        int stride,
        byte[] pixels,
        AnnotationStroke stroke,
        int left,
        int top,
        int right,
        int bottom)
    {
        if (stroke.Fill != AnnotationFillStyle.Solid)
            return;

        for (int y = Math.Max(0, top); y <= Math.Min(height - 1, bottom); y++)
            for (int x = Math.Max(0, left); x <= Math.Min(width - 1, right); x++)
                BlendPixel(pixels, y * stride + x * 4, stroke.Color);
    }

    private static void FillEllipseIfFilled(
        int width,
        int height,
        int stride,
        byte[] pixels,
        AnnotationStroke stroke,
        int centerX,
        int centerY,
        double radiusX,
        double radiusY)
    {
        if (stroke.Fill != AnnotationFillStyle.Solid)
            return;

        int minX = Math.Max(0, centerX - (int)Math.Ceiling(radiusX));
        int maxX = Math.Min(width - 1, centerX + (int)Math.Ceiling(radiusX));
        int minY = Math.Max(0, centerY - (int)Math.Ceiling(radiusY));
        int maxY = Math.Min(height - 1, centerY + (int)Math.Ceiling(radiusY));
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                double distanceX = (x - centerX) / radiusX;
                double distanceY = (y - centerY) / radiusY;
                if (distanceX * distanceX + distanceY * distanceY <= 1.0)
                    BlendPixel(pixels, y * stride + x * 4, stroke.Color);
            }
        }
    }

    private static AnnotationPoint EllipsePoint(int centerX, int centerY, double radiusX, double radiusY, double angle) =>
        new(
            (int)Math.Round(centerX + radiusX * Math.Cos(angle), MidpointRounding.AwayFromZero),
            (int)Math.Round(centerY + radiusY * Math.Sin(angle), MidpointRounding.AwayFromZero));

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
