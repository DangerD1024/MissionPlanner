using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotSpatial.Projections;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// Mission Planner's "Shp to Poly": every feature of an ESRI shapefile written as a polygon file
/// (poly-1.poly, poly-2.poly … next to the .shp), reprojected to WGS84 when a .prj says how.
/// </summary>
public static class ShapefilePolygons
{
    /// <summary>The points of each record (all parts in order), in the file's own coordinates (X, Y).</summary>
    public static List<List<(double X, double Y)>> ReadFeatures(byte[] shp)
    {
        if (shp.Length < 100 || BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(0, 4)) != 9994)
            throw new InvalidDataException("Not an ESRI shapefile.");

        var features = new List<List<(double X, double Y)>>();
        var pos = 100;
        while (pos + 8 <= shp.Length)
        {
            var contentWords = BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(pos + 4, 4));
            var start = pos + 8;
            var end = start + contentWords * 2;
            if (contentWords < 2 || end > shp.Length)
                break;
            var type = BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(start, 4));
            var points = new List<(double X, double Y)>();
            switch (type)
            {
                case 1 or 11 or 21: // point, pointZ, pointM
                    points.Add((BitConverter.ToDouble(shp, start + 4), BitConverter.ToDouble(shp, start + 12)));
                    break;
                case 3 or 5 or 13 or 15 or 23 or 25: // polyline, polygon (+Z, +M): box, parts, points
                {
                    var numPoints = BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(start + 40, 4));
                    var numParts = BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(start + 36, 4));
                    var at = start + 44 + numParts * 4;
                    for (var i = 0; i < numPoints && at + i * 16 + 16 <= end; i++)
                        points.Add((BitConverter.ToDouble(shp, at + i * 16), BitConverter.ToDouble(shp, at + i * 16 + 8)));
                    break;
                }
                case 8 or 18 or 28: // multipoint: box, count, points
                {
                    var numPoints = BinaryPrimitives.ReadInt32LittleEndian(shp.AsSpan(start + 36, 4));
                    var at = start + 40;
                    for (var i = 0; i < numPoints && at + i * 16 + 16 <= end; i++)
                        points.Add((BitConverter.ToDouble(shp, at + i * 16), BitConverter.ToDouble(shp, at + i * 16 + 8)));
                    break;
                }
            }

            // a null shape is still a feature (it numbers as one)
            features.Add(points);
            pos = end;
        }

        return features;
    }

    /// <summary>
    /// Converts <paramref name="shpFile"/>; returns the files written. The .prj (ESRI WKT) next to
    /// the shapefile, when there is one, gives the coordinate system to reproject from.
    /// </summary>
    public static IReadOnlyList<string> Convert(string shpFile)
    {
        var features = ReadFeatures(File.ReadAllBytes(shpFile));
        var dir = Path.GetDirectoryName(Path.GetFullPath(shpFile))!;

        ProjectionInfo? source = null;
        var prj = Path.Combine(dir, Path.GetFileNameWithoutExtension(shpFile) + ".prj");
        if (File.Exists(prj))
        {
            var wkt = File.ReadAllText(prj).Replace("\r", "").Replace("\n", "").Trim();
            if (wkt.Length > 0)
                source = ProjectionInfo.FromEsriString(wkt);
        }

        var wgs84 = KnownCoordinateSystems.Geographic.World.WGS1984;
        var written = new List<string>();
        var a = 1;
        foreach (var feature in features)
        {
            var sb = new StringBuilder();
            sb.Append("#Shap to Poly - Mission Planner\r\n");
            var xy = new double[feature.Count * 2];
            for (var i = 0; i < feature.Count; i++)
            {
                xy[i * 2] = feature[i].X;
                xy[i * 2 + 1] = feature[i].Y;
            }

            if (source != null && feature.Count > 0)
                Reproject.ReprojectPoints(xy, new double[feature.Count], source, wgs84, 0, feature.Count);

            for (var i = 0; i < feature.Count; i++)
            {
                sb.Append(xy[i * 2 + 1].ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(xy[i * 2].ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            var file = Path.Combine(dir, "poly-" + a + ".poly");
            File.WriteAllText(file, sb.ToString());
            written.Add(file);
            a++;
        }

        return written;
    }
}

/// <summary>TOOLS > Shapefile to polygon (Ctrl+F "Shp to Poly").</summary>
public sealed partial class ShpToPolyViewModel(AppServices services)
    : ToolPageViewModel("Shapefile to polygon",
        "Writes each shape of an ESRI shapefile (.shp) as a polygon file (poly-1.poly, poly-2.poly …) next to it, for PLAN's Load Polygon. A .prj beside the shapefile is used to convert the coordinates to latitude/longitude.",
        services)
{
    public override bool NeedsVehicle => false;

    [ObservableProperty]
    private string _output = "";

    [RelayCommand]
    private async Task ConvertAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("Shape file", new FileFilter("Shape file", "shp"));
        if (file == null)
            return;
        await ConvertFileAsync(file);
    }

    internal async Task ConvertFileAsync(string file)
    {
        if (!File.Exists(file))
            return;
        IsBusy = true;
        Status = "Converting " + Path.GetFileName(file) + "…";
        StatusIsError = false;
        try
        {
            var written = await Task.Run(() => ShapefilePolygons.Convert(file));
            Output = string.Join(Environment.NewLine, written);
            Report(written.Count == 0
                ? Path.GetFileName(file) + " has no shapes."
                : $"Written {written.Count} polygon files to {Path.GetDirectoryName(written[0])}.");
        }
        catch (Exception ex)
        {
            Report("Could not convert the shapefile: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
