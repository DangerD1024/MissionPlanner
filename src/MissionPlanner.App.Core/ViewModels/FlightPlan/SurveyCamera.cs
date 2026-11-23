using System.Globalization;
using System.Xml.Linq;
using GMap.NET;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>A survey camera: lens focal length and sensor size in millimetres, image size in pixels.</summary>
public sealed record CameraSpec(string Name, double FocalMm, int ImageWidth, int ImageHeight, double SensorWidthMm, double SensorHeightMm)
{
    public override string ToString() => Name;
}

/// <summary>
/// Mission Planner's camera list: camerasBuiltin.xml (compiled in, the same file the WinForms app
/// ships) plus the user's cameras.xml in the data folder.
/// </summary>
public static class CameraDatabase
{
    private static IReadOnlyList<CameraSpec>? _all;

    public static IReadOnlyList<CameraSpec> All => _all ??= Load();

    private static List<CameraSpec> Load()
    {
        var list = new List<CameraSpec>();
        try
        {
            using var s = typeof(CameraDatabase).Assembly.GetManifestResourceStream("camerasBuiltin.xml");
            if (s != null)
                list.AddRange(Parse(XDocument.Load(s)));
        }
        catch (Exception)
        {
            // a broken resource leaves the list empty; manual entry still works
        }

        try
        {
            var user = Path.Combine(MissionPlanner.Utilities.Settings.GetUserDataDirectory(), "cameras.xml");
            if (File.Exists(user))
            {
                foreach (var c in Parse(XDocument.Load(user)))
                {
                    list.RemoveAll(x => x.Name == c.Name);
                    list.Add(c);
                }
            }
        }
        catch (Exception)
        {
            // ignore a damaged user file
        }

        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Cameras of a Mission Planner camera file (Cameras / Camera with name, flen, imgw, imgh, senw, senh).</summary>
    public static IEnumerable<CameraSpec> Parse(XDocument doc)
    {
        double D(XElement e, string n) => double.TryParse(e.Element(n)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        foreach (var e in doc.Descendants("Camera"))
        {
            var name = e.Element("name")?.Value.Trim() ?? "";
            var spec = new CameraSpec(name, D(e, "flen"), (int)D(e, "imgw"), (int)D(e, "imgh"), D(e, "senw"), D(e, "senh"));
            if (name.Length > 0 && spec.FocalMm > 0 && spec.SensorWidthMm > 0 && spec.SensorHeightMm > 0 && spec.ImageWidth > 0)
                yield return spec;
        }
    }
}

/// <summary>The survey numbers from a camera, as Mission Planner's Grid: footprint, GSD, lane spacing and trigger distance.</summary>
public static class SurveyCameraMath
{
    /// <summary>
    /// Ground covered by one photo at <paramref name="altitude"/> metres: across the lanes and along them.
    /// Landscape puts the sensor's long side across the lanes; portrait along them.
    /// </summary>
    public static (double Across, double Along) Footprint(CameraSpec c, double altitude, bool portrait)
    {
        var w = c.SensorWidthMm * altitude / c.FocalMm;
        var h = c.SensorHeightMm * altitude / c.FocalMm;
        return portrait ? (h, w) : (w, h);
    }

    /// <summary>Ground sample distance in centimetres per pixel.</summary>
    public static double GsdCm(CameraSpec c, double altitude) => c.SensorWidthMm * altitude * 100 / (c.FocalMm * c.ImageWidth);

    /// <summary>Lane spacing for a side overlap in percent.</summary>
    public static double LaneSpacing(CameraSpec c, double altitude, double sidelapPercent, bool portrait) =>
        Footprint(c, altitude, portrait).Across * (1 - sidelapPercent / 100);

    /// <summary>Distance between photos for a forward overlap in percent.</summary>
    public static double TriggerDistance(CameraSpec c, double altitude, double overlapPercent, bool portrait) =>
        Footprint(c, altitude, portrait).Along * (1 - overlapPercent / 100);

    /// <summary>The four corners of a photo centred on <paramref name="centre"/>, its long axis along <paramref name="laneAngleDeg"/>.</summary>
    public static PointLatLng[] Corners(PointLatLng centre, double across, double along, double laneAngleDeg)
    {
        const double earth = 6378137.0;
        var a = laneAngleDeg * Math.PI / 180;
        var (fn, fe) = (Math.Cos(a), Math.Sin(a)); // along the lane (north, east)
        var (rn, re) = (-fe, fn); // across
        PointLatLng P(double f, double r)
        {
            var n = fn * f + rn * r;
            var e = fe * f + re * r;
            return new PointLatLng(centre.Lat + n / earth * 180 / Math.PI,
                centre.Lng + e / (earth * Math.Cos(centre.Lat * Math.PI / 180)) * 180 / Math.PI);
        }

        var (hf, hr) = (along / 2, across / 2);
        return [P(hf, -hr), P(hf, hr), P(-hf, hr), P(-hf, -hr)];
    }
}
