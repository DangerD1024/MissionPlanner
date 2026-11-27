using System.Globalization;
using log4net;

namespace MissionPlanner.App.Core.Maps;

/// <summary>An airport the map marks, with the radius of the circle drawn around it (metres).</summary>
public sealed record Airport(string Ident, string Name, double Lat, double Lng, double RadiusM);

/// <summary>
/// Mission Planner's airports overlay (Config > Planner > Show Airports): the OurAirports list shipped as
/// <c>airports.csv</c>, read with Mission Planner's rules (<c>Airports.ReadOurairports</c>): airports with a
/// four-letter ICAO code, without small airfields, seaplane bases, heliports or closed ones; each with the
/// circle <c>GMapMarkerAirport</c> draws (9 km, 5.559 km in Australia). The 56,000-line file is read once,
/// in the background, the first time a map wants it.
/// </summary>
public sealed class AirportCatalog(string path)
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AirportCatalog));

    private static readonly string[] SkippedTypes = ["small_airport", "seaplane_base", "heliport", "closed"];

    private readonly object _gate = new();

    // sorted by latitude, for the map's range queries
    private volatile Airport[] _airports = [];
    private volatile bool _isLoaded;
    private Task? _loading;

    /// <summary>The file next to the program (copied there by the build).</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "airports.csv");

    public bool IsLoaded => _isLoaded;

    public int Count => _airports.Length;

    /// <summary>Raised on a worker thread once the file has been read (or found missing).</summary>
    public event EventHandler? Loaded;

    /// <summary>Starts reading the file unless it has been read or is being read.</summary>
    public Task LoadAsync()
    {
        lock (_gate)
            return _loading ??= Task.Run(Load);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(path))
            {
                using var reader = File.OpenText(path);
                _airports = Parse(reader).OrderBy(a => a.Lat).ToArray();
                Log.Info($"{_airports.Length} airports from {path}");
            }
            else
            {
                Log.Warn("no airport list at " + path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("reading " + path, ex);
        }

        _isLoaded = true;
        Loaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The airports in an OurAirports CSV that Mission Planner shows.</summary>
    public static IEnumerable<Airport> Parse(TextReader reader)
    {
        // id,ident,type,name,latitude_deg,longitude_deg,...
        if (reader.ReadLine() == null)
            yield break;
        while (reader.ReadLine() is { } line)
        {
            var f = SplitCsv(line);
            if (f.Count < 6 || f[1].Length != 4 || SkippedTypes.Any(t => f[2].Contains(t, StringComparison.Ordinal)))
                continue;
            if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng) ||
                lat is < -90 or > 90 || lng is < -180 or > 180)
                continue;
            yield return new Airport(f[1], f[3], lat, lng, RadiusFor(lat, lng));
        }
    }

    /// <summary>Mission Planner's circle: 9 km, 5.559 km in Australia.</summary>
    public static double RadiusFor(double lat, double lng) => lat < -10 && lng > 109 && lng < 180 ? 5559 : 9000;

    /// <summary>
    /// The airports inside a latitude/longitude box (west may be east of east across the date line),
    /// at most <paramref name="max"/> of them.
    /// </summary>
    public IReadOnlyList<Airport> Within(double south, double north, double west, double east, int max = 400)
    {
        var all = _airports;
        var found = new List<Airport>();
        if (all.Length == 0 || north < south)
            return found;

        // first airport at or north of the southern edge
        int lo = 0, hi = all.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (all[mid].Lat < south)
                lo = mid + 1;
            else
                hi = mid;
        }

        var wraps = west > east;
        for (var i = lo; i < all.Length && all[i].Lat <= north && found.Count < max; i++)
        {
            var lng = all[i].Lng;
            if (wraps ? lng >= west || lng <= east : lng >= west && lng <= east)
                found.Add(all[i]);
        }

        return found;
    }

    /// <summary>One CSV line: commas inside quotes belong to the field, "" is a quote.</summary>
    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }
}
