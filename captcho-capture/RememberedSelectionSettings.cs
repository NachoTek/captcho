using System.Collections.Generic;

namespace captcho.Capture;

/// <summary>How long confirmed Selection geometry is retained.</summary>
public enum RememberSelectionLifetime
{
    Never,
    Session,
    Always,
}

/// <summary>
/// Persisted Selection geometry and the monitor topology on which it was confirmed.
/// Geometry is reused only when that topology still matches the Virtual Desktop.
/// </summary>
public sealed class RememberedSelectionGeometry
{
    public int X { get; set; }
    public int Y { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public List<MonitorRect> Topology { get; set; } = new();

    public RememberedSelectionGeometry Normalized()
    {
        var topology = MonitorLayout.SortByPosition(Topology ?? new List<MonitorRect>());
        return new RememberedSelectionGeometry
        {
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            Topology = new List<MonitorRect>(topology),
        };
    }
}
