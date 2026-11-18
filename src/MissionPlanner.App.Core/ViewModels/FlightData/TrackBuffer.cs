namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>A point of the flown track, degrees.</summary>
public readonly record struct TrackPoint(double Lat, double Lng);

/// <summary>
/// The vehicle's recent ground track: a fixed-size ring of points, each at least
/// <see cref="MinSpacing"/> metres from the previous one, so hovering or slow flight does not
/// fill it with duplicates. Adding never allocates. UI thread only.
/// </summary>
public sealed class TrackBuffer
{
    private const double MetresPerDegree = 111_319.49;

    private readonly TrackPoint[] _points;
    private int _start;

    public TrackBuffer(int capacity = 2000, double minSpacingMetres = 1.5)
    {
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _points = new TrackPoint[capacity];
        MinSpacing = minSpacingMetres;
    }

    public int Capacity => _points.Length;

    /// <summary>Minimum distance between stored points, metres.</summary>
    public double MinSpacing { get; }

    public int Count { get; private set; }

    /// <summary>Moves whenever the stored points change.</summary>
    public long Version { get; private set; }

    /// <summary>Oldest first.</summary>
    public TrackPoint this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _points[(_start + index) % _points.Length];
        }
    }

    public TrackPoint? Last => Count == 0 ? null : this[Count - 1];

    /// <summary>Adds a position; returns false if it was dropped (no fix, or too close to the last point).</summary>
    public bool Add(double lat, double lng)
    {
        if ((lat == 0 && lng == 0) || double.IsNaN(lat) || double.IsNaN(lng) || Math.Abs(lat) > 90 || Math.Abs(lng) > 180)
            return false;

        if (Count > 0)
        {
            var last = this[Count - 1];
            if (DistanceMetres(last.Lat, last.Lng, lat, lng) < MinSpacing)
                return false;
        }

        if (Count < _points.Length)
        {
            _points[(_start + Count) % _points.Length] = new TrackPoint(lat, lng);
            Count++;
        }
        else
        {
            // full: overwrite the oldest
            _points[_start] = new TrackPoint(lat, lng);
            _start = (_start + 1) % _points.Length;
        }

        Version++;
        return true;
    }

    public void Clear()
    {
        Count = 0;
        _start = 0;
        Version++;
    }

    /// <summary>Equirectangular approximation; plenty for spacing decisions of a few metres.</summary>
    public static double DistanceMetres(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = (lat2 - lat1) * MetresPerDegree;
        var dLng = (lng2 - lng1) * MetresPerDegree * Math.Cos((lat1 + lat2) * 0.5 * Math.PI / 180);
        return Math.Sqrt(dLat * dLat + dLng * dLng);
    }
}
