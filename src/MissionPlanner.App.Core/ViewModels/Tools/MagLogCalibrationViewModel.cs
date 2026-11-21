using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.Logs;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>Compass offsets from logged magnetometer readings (Mission Planner's "mag calb log").</summary>
public static class MagLogFit
{
    public sealed record Fit(int Compass, double OfsX, double OfsY, double OfsZ, double Radius, double Rms, int Samples)
    {
        public string Text => string.Format(CultureInfo.InvariantCulture,
            "Compass {0}: offsets {1:0} {2:0} {3:0}, field {4:0} mGauss, fit error {5:0.0} ({6} samples){7}",
            Compass, OfsX, OfsY, OfsZ, Radius, Rms, Samples, Quality);

        /// <summary>A word on whether to trust it: too few samples or a poor fit.</summary>
        public string Quality => Samples < 50 ? " — too few samples, rotate the vehicle more" :
            Rms > Radius * 0.1 ? " — poor fit, check for interference" : "";

        public bool IsGood => Samples >= 50 && Rms <= Radius * 0.1;
    }

    /// <summary>
    /// Least-squares sphere through the points: x²+y²+z² = a·x + b·y + c·z + d, centre (a, b, c)/2.
    /// Returns centre, radius and the RMS distance of the points from the sphere; null when degenerate.
    /// </summary>
    public static (double X, double Y, double Z, double R, double Rms)? Sphere(IReadOnlyList<(double X, double Y, double Z)> pts)
    {
        if (pts.Count < 10)
            return null;
        var m = new double[4, 5];
        foreach (var (x, y, z) in pts)
        {
            double[] row = [x, y, z, 1];
            var rhs = x * x + y * y + z * z;
            for (var i = 0; i < 4; i++)
            {
                for (var j = 0; j < 4; j++)
                    m[i, j] += row[i] * row[j];
                m[i, 4] += row[i] * rhs;
            }
        }

        // Gauss-Jordan with partial pivoting
        for (var col = 0; col < 4; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 4; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                    pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-9)
                return null;
            for (var c = 0; c < 5; c++)
                (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            for (var r = 0; r < 4; r++)
            {
                if (r == col)
                    continue;
                var f = m[r, col] / m[col, col];
                for (var c = col; c < 5; c++)
                    m[r, c] -= f * m[col, c];
            }
        }

        var a = m[0, 4] / m[0, 0];
        var b = m[1, 4] / m[1, 1];
        var cc = m[2, 4] / m[2, 2];
        var d = m[3, 4] / m[3, 3];
        var cx = a / 2;
        var cy = b / 2;
        var cz = cc / 2;
        var r2 = d + cx * cx + cy * cy + cz * cz;
        if (r2 <= 0)
            return null;
        var radius = Math.Sqrt(r2);
        var sum = 0.0;
        foreach (var (x, y, z) in pts)
        {
            var e = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz)) - radius;
            sum += e * e;
        }

        return (cx, cy, cz, radius, Math.Sqrt(sum / pts.Count));
    }

    /// <summary>
    /// Keeps at most a few samples per 20-mGauss cell, as Mission Planner does, so time spent still
    /// does not pull the fit towards one direction.
    /// </summary>
    public static List<(double X, double Y, double Z)> Thin(IEnumerable<(double X, double Y, double Z)> pts, int perCell = 3, double cell = 20)
    {
        var seen = new Dictionary<(long, long, long), int>();
        var list = new List<(double, double, double)>();
        foreach (var p in pts)
        {
            if (p.X == 0 && p.Y == 0 && p.Z == 0)
                continue;
            var key = ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
            seen.TryGetValue(key, out var n);
            if (n >= perCell)
                continue;
            seen[key] = n + 1;
            list.Add(p);
        }

        return list;
    }

    /// <summary>
    /// Raw field samples per compass (1-based): MAG/MAG2/MAG3 (or MAG with instances) minus the offsets
    /// logged with them in a DataFlash log; RAW_IMU / SCALED_IMU2 / SCALED_IMU3 minus SENSOR_OFFSETS in
    /// a telemetry log.
    /// </summary>
    public static Dictionary<int, List<(double X, double Y, double Z)>> Samples(LogSource log)
    {
        var result = new Dictionary<int, List<(double, double, double)>>();
        void Add(int compass, IEnumerable<(double, double, double)> pts)
        {
            if (!result.TryGetValue(compass, out var list))
                result[compass] = list = new List<(double, double, double)>();
            list.AddRange(pts);
        }

        // DataFlash: MAG (instances 0..2) or MAG, MAG2, MAG3
        string[] magTypes = ["MAG", "MAG2", "MAG3"];
        for (var t = 0; t < magTypes.Length; t++)
        {
            var type = log.FindType(magTypes[t]);
            if (type == null)
                continue;
            var instances = type.Instances.Count > 0 ? type.Instances.Select(i => (string?)i).ToList() : [null];
            for (var i = 0; i < instances.Count; i++)
            {
                var table = log.GetTable(type.Name, instances[i]);
                if (table == null || !table.Columns.ContainsKey("MagX"))
                    continue;
                var compass = (type.Instances.Count > 0 ? i : t) + 1;
                Add(compass, Rows(table, "MagX", "MagY", "MagZ", "OfsX", "OfsY", "OfsZ"));
            }
        }

        // telemetry log: the field minus the offsets the autopilot reported at that time
        string[] imu = ["RAW_IMU", "SCALED_IMU2", "SCALED_IMU3"];
        var offsets = log.FindType("SENSOR_OFFSETS") is { } so ? log.GetTable(so.Name, so.Instances.FirstOrDefault()) : null;
        for (var n = 0; n < imu.Length; n++)
        {
            var type = log.FindType(imu[n]);
            if (type == null)
                continue;
            var table = log.GetTable(type.Name, type.Instances.FirstOrDefault());
            if (table == null || !table.Columns.ContainsKey("xmag"))
                continue;
            var xs = table.Columns["xmag"];
            var ys = table.Columns["ymag"];
            var zs = table.Columns["zmag"];
            var pts = new List<(double, double, double)>();
            for (var k = 0; k < table.Times.Length; k++)
            {
                var (ox, oy, oz) = n == 0 && offsets != null ? At(offsets, table.Times[k]) : (0, 0, 0);
                pts.Add((xs[k] - ox, ys[k] - oy, zs[k] - oz));
            }

            Add(n + 1, pts);
        }

        return result;
    }

    private static IEnumerable<(double, double, double)> Rows(LogTable t, string x, string y, string z, string ox, string oy, string oz)
    {
        var hasOfs = t.Columns.ContainsKey(ox);
        for (var k = 0; k < t.Times.Length; k++)
        {
            yield return hasOfs
                ? (t.Columns[x][k] - t.Columns[ox][k], t.Columns[y][k] - t.Columns[oy][k], t.Columns[z][k] - t.Columns[oz][k])
                : (t.Columns[x][k], t.Columns[y][k], t.Columns[z][k]);
        }
    }

    private static (double, double, double) At(LogTable offsets, double time)
    {
        var i = Array.BinarySearch(offsets.Times, time);
        if (i < 0)
            i = Math.Max(0, ~i - 1);
        return (offsets.Columns["mag_ofs_x"][i], offsets.Columns["mag_ofs_y"][i], offsets.Columns["mag_ofs_z"][i]);
    }

    /// <summary>A fit for every compass in the log.</summary>
    public static IReadOnlyList<Fit> FromLog(LogSource log) =>
        Samples(log).OrderBy(p => p.Key)
            .Select(p =>
            {
                var pts = Thin(p.Value);
                return Sphere(pts) is { } s ? new Fit(p.Key, -s.X, -s.Y, -s.Z, s.R, s.Rms, pts.Count) : null;
            })
            .Where(f => f != null)
            .Select(f => f!)
            .ToList();

    /// <summary>The offset parameters of compass 1, 2, 3.</summary>
    public static (string X, string Y, string Z) ParamNames(int compass) => compass switch
    {
        1 => ("COMPASS_OFS_X", "COMPASS_OFS_Y", "COMPASS_OFS_Z"),
        _ => ($"COMPASS_OFS{compass}_X", $"COMPASS_OFS{compass}_Y", $"COMPASS_OFS{compass}_Z"),
    };
}

/// <summary>TOOLS > Compass from a log: offsets fitted to the magnetometer data of a flight log.</summary>
public sealed partial class MagLogCalibrationViewModel(AppServices services)
    : ToolPageViewModel("Compass from a log",
        "Fits compass offsets to the magnetometer readings of a log in which the vehicle was turned through every direction. Works with DataFlash logs (MAG) and telemetry logs (RAW_IMU).",
        services)
{
    public ObservableCollection<MagLogFit.Fit> Fits { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFits))]
    private string _logName = "";

    public bool HasFits => Fits.Count > 0;

    public override bool NeedsVehicle => false;

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("Log with compass data", new FileFilter("Logs", "bin", "BIN", "log", "LOG", "tlog"));
        if (file == null)
            return;
        IsBusy = true;
        Status = "Reading " + Path.GetFileName(file) + "…";
        try
        {
            var fits = await Task.Run(() =>
            {
                using var log = LogSource.Open(file);
                return MagLogFit.FromLog(log);
            });
            Fits.Clear();
            foreach (var f in fits)
                Fits.Add(f);
            LogName = Path.GetFileName(file);
            Report(fits.Count == 0 ? "No magnetometer data in that log." : $"{fits.Count} compass(es) fitted.", fits.Count == 0);
        }
        catch (Exception ex)
        {
            Report("Could not read the log: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the offsets of one compass (and turns offset learning off, as Mission Planner).</summary>
    [RelayCommand]
    private async Task ApplyAsync(MagLogFit.Fit fit)
    {
        if (!IsConnected)
        {
            Report("Connect to the vehicle the log came from first.", true);
            return;
        }

        var (px, py, pz) = MagLogFit.ParamNames(fit.Compass);
        if (!Services.Params.Has(px))
        {
            Report($"This vehicle has no {px}.", true);
            return;
        }

        if (!fit.IsGood && !await Services.Dialogs.ConfirmAsync("Compass from a log", fit.Text + ". Write these offsets anyway?", "Write"))
            return;

        IsBusy = true;
        try
        {
            if (Services.Params.Has("COMPASS_LEARN"))
                await Services.Params.SetAsync("COMPASS_LEARN", 0);
            var ok = await Services.Params.SetAsync(px, Math.Round(fit.OfsX)) &
                     await Services.Params.SetAsync(py, Math.Round(fit.OfsY)) &
                     await Services.Params.SetAsync(pz, Math.Round(fit.OfsZ));
            Report(ok ? $"Compass {fit.Compass} offsets written." : $"Could not write the offsets of compass {fit.Compass}.", !ok);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
