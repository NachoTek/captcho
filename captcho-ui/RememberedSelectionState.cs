using System;
using System.Collections.Generic;
using System.Linq;
using captcho.Capture;

namespace captcho.UI;

/// <summary>Supplies the current monitor rectangles in Virtual Desktop coordinates.</summary>
public interface IVirtualDesktopTopologyProvider
{
    IReadOnlyList<MonitorRect> GetCurrent();
}

/// <summary>Persists changes to Always remembered Selection geometry.</summary>
public interface IRememberedSelectionPersistence
{
    bool Save(RememberedSelectionGeometry? geometry);
}

/// <summary>
/// Owns remembered Selection state for one workflow session and persists only the
/// Always lifetime through Configuration.
/// </summary>
public sealed class RememberedSelectionState
{
    private readonly AppSettings _settings;
    private readonly IVirtualDesktopTopologyProvider _topology;
    private readonly IRememberedSelectionPersistence? _persistence;
    private RememberSelectionLifetime _observedLifetime;
    private RememberedSelectionGeometry? _sessionSelection;

    public RememberedSelectionState(
        AppSettings settings,
        IVirtualDesktopTopologyProvider topology,
        IRememberedSelectionPersistence? persistence = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _persistence = persistence;
        _observedLifetime = settings.EffectiveRememberSelection;
    }

    internal static RememberedSelectionState Disabled { get; } = new(
        AppSettings.WithDefaults(),
        new EmptyTopologyProvider());

    public SelectionGeometry? GetInitialGeometry()
    {
        var lifetime = ObserveLifetime();
        if (lifetime == RememberSelectionLifetime.Never)
            return null;

        var remembered = lifetime == RememberSelectionLifetime.Session
            ? _sessionSelection
            : _settings.RememberedSelection;
        if (remembered is null)
            return null;

        var current = GetCurrentTopology();
        if (!TopologyMatches(remembered.Topology, current, remembered) || !Contains(current, remembered))
        {
            if (lifetime == RememberSelectionLifetime.Session)
                _sessionSelection = null;
            else
                PersistAlways(null);
            return null;
        }

        return new SelectionGeometry
        {
            X = remembered.X,
            Y = remembered.Y,
            Width = remembered.Width,
            Height = remembered.Height,
        };
    }

    public void Remember(SelectionGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var lifetime = ObserveLifetime();
        if (lifetime == RememberSelectionLifetime.Never)
            return;

        var current = GetCurrentTopology();
        var remembered = new RememberedSelectionGeometry
        {
            X = geometry.X,
            Y = geometry.Y,
            Width = geometry.Width,
            Height = geometry.Height,
            Topology = new List<MonitorRect>(MonitorLayout.SortByPosition(current)),
        };
        if (!Contains(current, remembered))
            return;

        if (lifetime == RememberSelectionLifetime.Session)
            _sessionSelection = remembered;
        else
            PersistAlways(remembered);
    }

    private RememberSelectionLifetime ObserveLifetime()
    {
        var current = _settings.EffectiveRememberSelection;
        if (current != _observedLifetime)
        {
            _sessionSelection = null;
            _observedLifetime = current;
        }
        return current;
    }

    private IReadOnlyList<MonitorRect> GetCurrentTopology()
    {
        try
        {
            return _topology.GetCurrent() ?? Array.Empty<MonitorRect>();
        }
        catch
        {
            return Array.Empty<MonitorRect>();
        }
    }

    private void PersistAlways(RememberedSelectionGeometry? remembered)
    {
        _persistence?.Save(remembered);
    }

    private static bool TopologyMatches(
        IReadOnlyList<MonitorRect>? stored,
        IReadOnlyList<MonitorRect> current,
        RememberedSelectionGeometry geometry)
    {
        if (stored is null || stored.Count == 0)
            return false;

        var storedLocal = MonitorLayout.SortByPosition(stored.Where(m => Intersects(m, geometry)).ToArray());
        var currentLocal = MonitorLayout.SortByPosition(current.Where(m => Intersects(m, geometry)).ToArray());
        return storedLocal.SequenceEqual(currentLocal);
    }

    private static bool Intersects(MonitorRect monitor, RememberedSelectionGeometry geometry) =>
        geometry.X < monitor.Right
        && (long)geometry.X + geometry.Width > monitor.Left
        && geometry.Y < monitor.Bottom
        && (long)geometry.Y + geometry.Height > monitor.Top;

    private static bool Contains(
        IReadOnlyList<MonitorRect> topology,
        RememberedSelectionGeometry geometry)
    {
        if (topology.Count == 0 || geometry.Width == 0 || geometry.Height == 0)
            return false;

        if (topology.Any(m => m.Right <= m.Left || m.Bottom <= m.Top))
            return false;

        int left = topology.Min(m => m.Left);
        int top = topology.Min(m => m.Top);
        int right = topology.Max(m => m.Right);
        int bottom = topology.Max(m => m.Bottom);
        long geometryRight = (long)geometry.X + geometry.Width;
        long geometryBottom = (long)geometry.Y + geometry.Height;
        return geometry.X >= left
            && geometry.Y >= top
            && geometryRight <= right
            && geometryBottom <= bottom;
    }

    private sealed class EmptyTopologyProvider : IVirtualDesktopTopologyProvider
    {
        public IReadOnlyList<MonitorRect> GetCurrent() => Array.Empty<MonitorRect>();
    }
}
