using System.Globalization;
using System.Security;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.Logs;
using MissionPlanner.Log;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A flight path written as Google Earth KML and as GPX.</summary>
public static class TrackExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The points worth writing: a real position, no duplicates in a row.</summary>
    public static IReadOnlyList<LogTrackPoint> Clean(IEnumerable<LogTrackPoint> track)
    {
        var list = new List<LogTrackPoint>();
        foreach (var p in track)
        {
            if (p.Lat == 0 && p.Lng == 0 || Math.Abs(p.Lat) > 90 || Math.Abs(p.Lng) > 180)
                continue;
            if (list.Count > 0 && list[^1].Lat == p.Lat && list[^1].Lng == p.Lng && list[^1].Alt == p.Alt)
                continue;
            list.Add(p);
        }

        return list;
    }

    public static string Kml(IReadOnlyList<LogTrackPoint> track, string name)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<kml xmlns=\"http://www.opengis.net/kml/2.2\">\n<Document>\n")
            .Append("  <name>").Append(SecurityElement.Escape(name)).Append("</name>\n")
            .Append("  <Style id=\"path\"><LineStyle><color>ff00ffff</color><width>3</width></LineStyle>")
            .Append("<PolyStyle><color>4c00ff00</color></PolyStyle></Style>\n")
            .Append("  <Placemark>\n    <name>Flight path</name>\n    <styleUrl>#path</styleUrl>\n")
            .Append("    <LineString>\n      <extrude>1</extrude>\n      <altitudeMode>absolute</altitudeMode>\n      <coordinates>\n");
        foreach (var p in track)
            sb.Append("        ").Append(p.Lng.ToString("0.0000000", Inv)).Append(',')
                .Append(p.Lat.ToString("0.0000000", Inv)).Append(',').Append(p.Alt.ToString("0.00", Inv)).Append('\n');
        sb.Append("      </coordinates>\n    </LineString>\n  </Placemark>\n");
        if (track.Count > 0)
        {
            sb.Append(Point("Start", track[0])).Append(Point("End", track[^1]));
        }

        sb.Append("</Document>\n</kml>\n");
        return sb.ToString();

        static string Point(string label, LogTrackPoint p) =>
            $"  <Placemark><name>{label}</name><Point><altitudeMode>absolute</altitudeMode><coordinates>" +
            $"{p.Lng.ToString("0.0000000", Inv)},{p.Lat.ToString("0.0000000", Inv)},{p.Alt.ToString("0.00", Inv)}</coordinates></Point></Placemark>\n";
    }

    public static string Gpx(IReadOnlyList<LogTrackPoint> track, string name, DateTime? start)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<gpx version=\"1.1\" creator=\"Mission Planner 3\" xmlns=\"http://www.topografix.com/GPX/1/1\">\n")
            .Append("  <trk>\n    <name>").Append(SecurityElement.Escape(name)).Append("</name>\n    <trkseg>\n");
        foreach (var p in track)
        {
            sb.Append("      <trkpt lat=\"").Append(p.Lat.ToString("0.0000000", Inv)).Append("\" lon=\"")
                .Append(p.Lng.ToString("0.0000000", Inv)).Append("\"><ele>").Append(p.Alt.ToString("0.00", Inv)).Append("</ele>");
            if (start is { } s && !double.IsNaN(p.Time))
                sb.Append("<time>").Append(s.AddSeconds(p.Time).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv)).Append("</time>");
            sb.Append("</trkpt>\n");
        }

        sb.Append("    </trkseg>\n  </trk>\n</gpx>\n");
        return sb.ToString();
    }
}

/// <summary>Mission Planner's log tools (Ctrl+F screen and the DataFlash Logs tab), one button each.</summary>
public sealed partial class LogToolsViewModel(AppServices services)
    : ToolPageViewModel("Log tools",
        "Convert, export and tidy up flight logs. Output files are written next to the log you pick unless a tool asks where.",
        services)
{
    private MissionPlanner.ArduPilot.RemoteLog? _remote;

    public override bool NeedsVehicle => false;

    [ObservableProperty]
    private string _log = "";

    [ObservableProperty]
    private decimal _splitPieces = 4;

    [ObservableProperty]
    private string _dashWareTypes = "GPS;ATT;NTUN;CTUN;MODE;BAT";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteLogText))]
    private bool _isRemoteLogging;

    public string RemoteLogText => IsRemoteLogging ? "Stop remote DataFlash log" : "Start remote DataFlash log";

    private static readonly FileFilter DataFlash = new("DataFlash logs", "bin", "BIN", "log", "LOG");
    private static readonly FileFilter Tlog = new("Telemetry logs", "tlog");
    private static readonly FileFilter AnyLog = new("Logs", "bin", "BIN", "log", "LOG", "tlog");

    private void Append(string line)
    {
        Log = line + Environment.NewLine + Log;
    }

    private async Task RunToolAsync(string what, Func<string> work)
    {
        IsBusy = true;
        Status = what + "…";
        StatusIsError = false;
        try
        {
            var result = await Task.Run(work);
            Append(result);
            Report(result);
        }
        catch (Exception ex)
        {
            Append($"{what} failed: {ex.Message}");
            Report($"{what} failed: {ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string LogDir => Settings.Instance.LogDir;

    [RelayCommand]
    private async Task BinToLogAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("DataFlash log to convert", new FileFilter("Binary DataFlash logs", "bin", "BIN"));
        if (file == null)
            return;
        var output = Path.ChangeExtension(file, ".log");
        await RunToolAsync("Converting " + Path.GetFileName(file), () =>
        {
            BinaryLog.ConvertBin(file, output);
            return "Written " + output;
        });
    }

    [RelayCommand]
    private async Task KmlAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("Log to export", AnyLog);
        if (file == null)
            return;
        await RunToolAsync("Exporting " + Path.GetFileName(file), () =>
        {
            using var source = LogSource.Open(file);
            var track = TrackExport.Clean(source.Track);
            if (track.Count == 0)
                return $"{Path.GetFileName(file)} has no GPS positions to export.";
            var name = Path.GetFileNameWithoutExtension(file);
            File.WriteAllText(file + ".kml", TrackExport.Kml(track, name));
            File.WriteAllText(file + ".gpx", TrackExport.Gpx(track, name, source.StartTime));
            return $"Written {file}.kml and .gpx ({track.Count} points).";
        });
    }

    [RelayCommand]
    private async Task SplitAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("DataFlash log to split", DataFlash);
        if (file == null)
            return;
        var pieces = (int)SplitPieces;
        await RunToolAsync("Splitting " + Path.GetFileName(file), () =>
        {
            using (var buffer = new DFLogBuffer(file))
                buffer.SplitLog(pieces);
            return $"Split into {pieces} pieces: {file}_split0.bin …";
        });
    }

    [RelayCommand]
    private async Task SortAsync()
    {
        var folder = await Services.Dialogs.PickFolderAsync("Folder of logs to sort");
        if (folder == null)
            return;
        if (!await Services.Dialogs.ConfirmAsync("Sort logs",
                $"Move the logs in {folder} (and its subfolders) into a folder per vehicle type and system ID? Empty logs are deleted and tiny ones moved aside, as Mission Planner does.",
                "Sort"))
            return;
        await RunToolAsync("Sorting logs", () =>
        {
            var count = 0;
            foreach (var pattern in new[] { "*.tlog", "*.bin", "*.log", "*.rlog" })
            {
                var files = Directory.GetFiles(folder, pattern, SearchOption.AllDirectories);
                count += files.Length;
                LogSort.SortLogs(files, folder);
            }

            return $"Sorted {count} logs in {folder}.";
        });
    }

    [RelayCommand]
    private async Task AnonymiseAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("Log to anonymise", AnyLog);
        if (file == null)
            return;
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".bin")
            ext = ".log";
        var output = await Services.Dialogs.SaveFileAsync("Anonymised log", Path.GetFileNameWithoutExtension(file) + "-anon" + ext);
        if (output == null)
            return;
        await RunToolAsync("Anonymising " + Path.GetFileName(file), () =>
        {
            Privacy.anonymise(file, output);
            return $"Written {output}. Check it before sharing: positions are shifted, but other identifying data may remain.";
        });
    }

    [RelayCommand]
    private async Task DashWareAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("DataFlash log", new FileFilter("Binary DataFlash logs", "bin", "BIN"));
        if (file == null)
            return;
        var types = DashWareTypes.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
        await RunToolAsync("Creating the DashWare file", () =>
        {
            DashWare.Create(file, file + ".csv", types.Count > 0 ? types : null);
            return "Written " + file + ".csv";
        });
    }

    [RelayCommand]
    private async Task ExtractGpsInjectAsync()
    {
        var file = await Services.Dialogs.OpenFileAsync("Telemetry log", Tlog);
        if (file == null)
            return;
        var output = await Services.Dialogs.SaveFileAsync("RTCM data", Path.GetFileNameWithoutExtension(file) + "-rtcm.dat");
        if (output == null)
            return;
        await RunToolAsync("Extracting GPS correction data", () =>
        {
            var bytes = GpsInjectExtractor.Extract(file, output);
            return bytes == 0 ? "No GPS_INJECT_DATA or GPS_RTCM_DATA in that log." : $"Written {bytes} bytes of correction data to {output}.";
        });
    }

    /// <summary>Streams the vehicle's DataFlash log over MAVLink (REMOTE_LOG_DATA_BLOCK) into the log folder.</summary>
    [RelayCommand]
    private void ToggleRemoteLog()
    {
        if (_remote != null)
        {
            var v = Services.Session.Vehicle;
            _remote.Stop(v.sysid, v.compid);
            _remote.Dispose();
            _remote = null;
            IsRemoteLogging = false;
            Report("Remote DataFlash log stopped.");
            return;
        }

        if (!IsConnected)
        {
            Report("Connect to a vehicle first (it needs LOG_BACKEND_TYPE with MAVLink).", true);
            return;
        }

        var vehicle = Services.Session.Vehicle;
        _remote = MissionPlanner.ArduPilot.RemoteLog.StartRemoteLog(Services.Session.Mav, vehicle.sysid, vehicle.compid);
        IsRemoteLogging = true;
        Report($"Remote DataFlash log started: it is written to {LogDir}. The vehicle must have LOG_BACKEND_TYPE including MAVLink.");
    }
}

/// <summary>Pulls the GPS correction data (GPS_INJECT_DATA, GPS_RTCM_DATA) out of a telemetry log.</summary>
public static class GpsInjectExtractor
{
    /// <summary>Writes the payloads in order to <paramref name="output"/>; returns the byte count.</summary>
    public static long Extract(string tlog, string output)
    {
        using var dest = File.Create(output);
        // the library's own tlog playback, as Mission Planner's tool: it handles the record framing
        using var mav = new MAVLinkInterface(File.OpenRead(tlog)) { logreadmode = true };
        long written = 0;
        while (mav.logplaybackfile.BaseStream.Position < mav.logplaybackfile.BaseStream.Length)
        {
            var packet = mav.readPacketAsync().GetAwaiter().GetResult();
            if (packet == null || packet.Length == 0)
                continue;
            if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GPS_INJECT_DATA)
            {
                var d = packet.ToStructure<MAVLink.mavlink_gps_inject_data_t>();
                dest.Write(d.data, 0, d.len);
                written += d.len;
            }
            else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GPS_RTCM_DATA)
            {
                var d = packet.ToStructure<MAVLink.mavlink_gps_rtcm_data_t>();
                dest.Write(d.data, 0, d.len);
                written += d.len;
            }
        }

        return written;
    }
}
