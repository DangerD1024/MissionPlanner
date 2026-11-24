namespace MissionPlanner.App.Core.Services;

/// <summary>Where the ground station is, from a GPS connected to this computer.</summary>
/// <param name="Alt">Metres above sea level.</param>
/// <param name="Source">Who reported it, shown beside the marker ("Moving base").</param>
public sealed record GcsPosition(double Lat, double Lng, double Alt, string Source, DateTime Time);

/// <summary>
/// The ground station's own position (TOOLS > Moving Base), shown as an extra marker on the DATA
/// and PLAN maps as Mission Planner shows its moving base. Set from any thread.
/// </summary>
public sealed class GcsLocationService
{
    private GcsPosition? _current;

    /// <summary>The last known position, or null when nothing reports one.</summary>
    public GcsPosition? Current => Volatile.Read(ref _current);

    /// <summary>Raised (on the thread that changed it) when the position changes or goes away.</summary>
    public event EventHandler? Changed;

    public void Set(GcsPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        Volatile.Write(ref _current, position);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (Interlocked.Exchange(ref _current, null) != null)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
