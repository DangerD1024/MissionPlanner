using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using log4net;

namespace MissionPlanner.App.Core.Mission;

/// <summary>A point of interest on the maps.</summary>
public sealed record Poi(double Lat, double Lng, string Name)
{
    public string Text => string.Create(CultureInfo.InvariantCulture, $"{Name} ({Lat:0.000000}, {Lng:0.000000})");
}

/// <summary>
/// Points of interest, shared by the DATA and PLAN maps and kept in poi.txt in the user data folder:
/// one "lat TAB lng TAB name" line each, the same file Mission Planner uses, so both apps see the
/// same POIs. Saved on every change once the file has been read.
/// </summary>
public sealed class PoiStore
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(PoiStore));
    private readonly string _path;
    private bool _loaded;

    public PoiStore(string? path = null)
    {
        _path = path ?? Path.Combine(MissionPlanner.Utilities.Settings.GetUserDataDirectory(), "poi.txt");
        try
        {
            Merge(File.ReadAllLines(_path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // none yet
        }
        catch (Exception ex)
        {
            // a file that cannot be read is never overwritten with a list missing its POIs
            Log.Warn("read " + _path, ex);
            return;
        }

        _loaded = true;
    }

    public ObservableCollection<Poi> Items { get; } = new();

    /// <summary>Raised after any change (maps redraw).</summary>
    public event EventHandler? Changed;

    public void Add(double lat, double lng, string name)
    {
        Items.Add(new Poi(lat, lng, name.Replace('\t', ' ').Replace('\n', ' ').Trim()));
        Commit();
    }

    public void Remove(Poi poi)
    {
        if (Items.Remove(poi))
            Commit();
    }

    public void Rename(Poi poi, string name)
    {
        var i = Items.IndexOf(poi);
        if (i < 0)
            return;
        Items[i] = poi with { Name = name.Replace('\t', ' ').Replace('\n', ' ').Trim() };
        Commit();
    }

    /// <summary>The POI nearest to a point within <paramref name="metres"/>, if any.</summary>
    public Poi? Near(double lat, double lng, double metres)
    {
        Poi? best = null;
        var bestD = metres;
        foreach (var p in Items)
        {
            var d = new MissionPlanner.Utilities.PointLatLngAlt(lat, lng).GetDistance(new MissionPlanner.Utilities.PointLatLngAlt(p.Lat, p.Lng));
            if (d <= bestD)
            {
                bestD = d;
                best = p;
            }
        }

        return best;
    }

    /// <summary>Adds the POIs of another file (Mission Planner's POI > Load), skipping ones already held.</summary>
    public int LoadFile(string path)
    {
        var added = Merge(File.ReadAllLines(path));
        Commit();
        return added;
    }

    public void SaveFile(string path) => File.WriteAllText(path, Format(Items), Encoding.ASCII);

    /// <summary>The file text: "lat TAB lng TAB name" per line.</summary>
    public static string Format(IEnumerable<Poi> pois)
    {
        var sb = new StringBuilder();
        foreach (var p in pois)
            sb.Append(p.Lat.ToString("0.0000000", CultureInfo.InvariantCulture)).Append('\t')
                .Append(p.Lng.ToString("0.0000000", CultureInfo.InvariantCulture)).Append('\t')
                .Append(p.Name).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>POIs in a file's lines; malformed lines are skipped.</summary>
    public static IEnumerable<Poi> Parse(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var items = line.Split('\t');
            if (items.Length < 3 ||
                !double.TryParse(items[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(items[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
                continue;
            yield return new Poi(lat, lng, items[2].Trim());
        }
    }

    private int Merge(IEnumerable<string> lines)
    {
        var held = new HashSet<(long, long, string)>(Items.Select(Key));
        var added = 0;
        foreach (var p in Parse(lines))
        {
            if (!held.Add(Key(p)))
                continue;
            Items.Add(p);
            added++;
        }

        return added;
    }

    private static (long, long, string) Key(Poi p) => ((long)Math.Round(p.Lat * 1e7), (long)Math.Round(p.Lng * 1e7), p.Name);

    private void Commit()
    {
        if (_loaded)
        {
            try
            {
                SaveFile(_path);
            }
            catch (Exception ex)
            {
                Log.Warn("save " + _path, ex);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
