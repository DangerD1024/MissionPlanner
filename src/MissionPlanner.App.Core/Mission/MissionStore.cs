using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Session;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Mission;

/// <summary>Altitude reference of a mission item, as Mission Planner names them.</summary>
public enum AltFrame : byte
{
    Absolute = (byte)MAVLink.MAV_FRAME.GLOBAL,
    Relative = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
    Terrain = (byte)MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT,
}

/// <summary>One row of a mission, fence or rally list. Lat/lng in degrees, alt in metres.</summary>
public sealed partial class MissionItem : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLocation), nameof(CommandName))]
    private MAVLink.MAV_CMD _command = MAVLink.MAV_CMD.WAYPOINT;

    [ObservableProperty] private float _p1;
    [ObservableProperty] private float _p2;
    [ObservableProperty] private float _p3;
    [ObservableProperty] private float _p4;
    [ObservableProperty] private double _lat;
    [ObservableProperty] private double _lng;
    [ObservableProperty] private float _alt;
    [ObservableProperty] private AltFrame _frame = AltFrame.Relative;

    /// <summary>1-based number shown in lists and on the map (home is 0).</summary>
    [ObservableProperty] private int _seq;

    /// <summary>Ground distance from the previous located item, metres.</summary>
    [ObservableProperty] private double _distance;

    /// <summary>Bearing from the previous located item, degrees.</summary>
    [ObservableProperty] private double _azimuth;

    /// <summary>Climb gradient from the previous located item, percent.</summary>
    [ObservableProperty] private double _gradient;

    public bool HasLocation => Locationwp.isLocationCommand((ushort)Command);

    public string CommandName => Command.ToString();

    public Locationwp ToLocationwp() => new()
    {
        id = (ushort)Command,
        p1 = P1,
        p2 = P2,
        p3 = P3,
        p4 = P4,
        lat = Lat,
        lng = Lng,
        alt = Alt,
        frame = (byte)Frame,
    };

    public static MissionItem FromLocationwp(Locationwp wp) => new()
    {
        Command = (MAVLink.MAV_CMD)wp.id,
        P1 = wp.p1,
        P2 = wp.p2,
        P3 = wp.p3,
        P4 = wp.p4,
        Lat = wp.lat,
        Lng = wp.lng,
        Alt = wp.alt,
        Frame = ToAltFrame(wp.frame),
    };

    public MissionItem Clone() => FromLocationwp(ToLocationwp());

    /// <summary>Maps any MAV_FRAME (including the _INT variants) to the three Mission Planner modes.</summary>
    public static AltFrame ToAltFrame(byte frame) => (MAVLink.MAV_FRAME)frame switch
    {
        MAVLink.MAV_FRAME.GLOBAL or MAVLink.MAV_FRAME.GLOBAL_INT => AltFrame.Absolute,
        MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT or MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT_INT => AltFrame.Terrain,
        _ => AltFrame.Relative,
    };
}

/// <summary>
/// The planned mission, fence and rally points, shared by the Flight Data map and the Flight Plan
/// editor. Owns file I/O and vehicle transfers; all collection changes happen on the UI thread.
/// </summary>
public sealed partial class MissionStore : ObservableObject
{
    public ObservableCollection<MissionItem> Mission { get; } = new();

    public ObservableCollection<MissionItem> Fence { get; } = new();

    public ObservableCollection<MissionItem> Rally { get; } = new();

    /// <summary>Home (AMSL altitude); item 0 of an uploaded mission.</summary>
    [ObservableProperty] private double _homeLat;
    [ObservableProperty] private double _homeLng;
    [ObservableProperty] private double _homeAlt;
    [ObservableProperty] private bool _homeSet;

    [ObservableProperty] private double _defaultAlt = 100;
    [ObservableProperty] private AltFrame _defaultFrame = AltFrame.Relative;
    [ObservableProperty] private double _waypointRadius = 2;
    [ObservableProperty] private double _loiterRadius = 45;

    public MissionStore()
    {
        Mission.CollectionChanged += OnListChanged;
        Fence.CollectionChanged += OnListChanged;
        Rally.CollectionChanged += OnListChanged;
    }

    /// <summary>Raised on the UI thread after any change to the lists, an item, or home.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<MissionItem> Get(MAVLink.MAV_MISSION_TYPE type) => type switch
    {
        MAVLink.MAV_MISSION_TYPE.FENCE => Fence,
        MAVLink.MAV_MISSION_TYPE.RALLY => Rally,
        _ => Mission,
    };

    public void SetHome(double lat, double lng, double altAmsl)
    {
        HomeLat = lat;
        HomeLng = lng;
        HomeAlt = altAmsl;
        HomeSet = true;
        Recalculate();
    }

    /// <summary>Numbers items and works out distance, bearing and gradient from the previous located item.</summary>
    public void Recalculate()
    {
        foreach (var list in new[] { Mission, Fence, Rally })
        {
            double? prevLat = HomeSet && list == Mission ? HomeLat : null;
            double prevLng = HomeLng;
            double prevAlt = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                item.Seq = i + 1;
                if (!item.HasLocation || (item.Lat == 0 && item.Lng == 0))
                {
                    item.Distance = 0;
                    item.Azimuth = 0;
                    item.Gradient = 0;
                    continue;
                }

                if (prevLat is { } plat)
                {
                    var a = new PointLatLngAlt(plat, prevLng);
                    var b = new PointLatLngAlt(item.Lat, item.Lng);
                    item.Distance = a.GetDistance(b);
                    item.Azimuth = a.GetBearing(b);
                    item.Gradient = item.Distance > 0.1 ? (item.Alt - prevAlt) / item.Distance * 100 : 0;
                }
                else
                {
                    item.Distance = 0;
                    item.Azimuth = 0;
                    item.Gradient = 0;
                }

                prevLat = item.Lat;
                prevLng = item.Lng;
                prevAlt = item.Alt;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public MissionItem NewWaypoint(double lat, double lng, MAVLink.MAV_CMD command = MAVLink.MAV_CMD.WAYPOINT) => new()
    {
        Command = command,
        Lat = lat,
        Lng = lng,
        Alt = (float)DefaultAlt,
        Frame = DefaultFrame,
    };

    // ---- files -------------------------------------------------------------------------------

    /// <summary>Reads a QGC WPL 110 file into a list. For a mission, item 0 becomes home.</summary>
    public void LoadWaypointFile(string path, MAVLink.MAV_MISSION_TYPE type = MAVLink.MAV_MISSION_TYPE.MISSION)
    {
        var items = WaypointFile.ReadWaypointFile(path);
        if (items.Count == 0)
            throw new InvalidDataException("The file has no waypoints or is not a QGC WPL file.");

        var home = items[0];
        items.RemoveAt(0);
        if (type == MAVLink.MAV_MISSION_TYPE.MISSION && (home.lat != 0 || home.lng != 0))
            SetHome(home.lat, home.lng, home.alt);

        Replace(Get(type), items);
    }

    /// <summary>Writes a list as QGC WPL 110, home (or a blank) as item 0, like the WinForms app.</summary>
    public void SaveWaypointFile(string path, MAVLink.MAV_MISSION_TYPE type = MAVLink.MAV_MISSION_TYPE.MISSION)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("QGC WPL 110\n");
        var isMission = type == MAVLink.MAV_MISSION_TYPE.MISSION;
        sb.AppendFormat(inv, "0\t1\t0\t16\t0\t0\t0\t0\t{0:0.0000000}\t{1:0.0000000}\t{2:0.000000}\t1\n",
            isMission ? HomeLat : 0, isMission ? HomeLng : 0, isMission ? HomeAlt : 0);
        var list = Get(type);
        for (var i = 0; i < list.Count; i++)
        {
            var w = list[i];
            sb.AppendFormat(inv, "{0}\t0\t{1}\t{2}\t{3:0.00000000}\t{4:0.00000000}\t{5:0.00000000}\t{6:0.00000000}\t{7:0.00000000}\t{8:0.00000000}\t{9:0.000000}\t1\n",
                i + 1, (byte)w.Frame, (ushort)w.Command, w.P1, w.P2, w.P3, w.P4, w.Lat, w.Lng, w.Alt);
        }

        File.WriteAllText(path, sb.ToString());
    }

    // ---- vehicle -----------------------------------------------------------------------------

    /// <summary>Downloads a list from the vehicle. For a mission, item 0 becomes home. Call from the UI thread.</summary>
    public async Task ReadFromVehicleAsync(VehicleSession session, MAVLink.MAV_MISSION_TYPE type, IProgress<OperationProgress>? progress = null)
    {
        var mav = session.Mav;
        var vehicle = session.Vehicle;
        var items = await Task.Run(() => mav_mission.download(mav, vehicle.sysid, vehicle.compid, type,
            (pct, status) => progress?.Report(new OperationProgress(pct, status))));

        if (type == MAVLink.MAV_MISSION_TYPE.MISSION && items.Count > 0)
        {
            var home = items[0];
            items.RemoveAt(0);
            if (home.lat != 0 || home.lng != 0)
                SetHome(home.lat, home.lng, home.alt);
        }

        Replace(Get(type), items);
    }

    /// <summary>Uploads a list, then writes the waypoint and loiter radius parameters. Call from the UI thread.</summary>
    public async Task WriteToVehicleAsync(VehicleSession session, MAVLink.MAV_MISSION_TYPE type, IProgress<OperationProgress>? progress = null)
    {
        var mav = session.Mav;
        var vehicle = session.Vehicle;
        var list = Get(type).Select(i => i.ToLocationwp()).ToList();

        if (type == MAVLink.MAV_MISSION_TYPE.FENCE)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var w = list[i];
                w.frame = (byte)MAVLink.MAV_FRAME.GLOBAL;
                list[i] = w;
            }
        }

        if (type == MAVLink.MAV_MISSION_TYPE.MISSION && vehicle.apname == MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA)
        {
            list.Insert(0, new Locationwp
            {
                id = (ushort)MAVLink.MAV_CMD.WAYPOINT,
                frame = (byte)MAVLink.MAV_FRAME.GLOBAL,
                lat = HomeLat,
                lng = HomeLng,
                alt = (float)HomeAlt,
            });
        }

        var wpRadius = WaypointRadius;
        var loiterRadius = LoiterRadius;
        await Task.Run(async () =>
        {
            await mav_mission.upload(mav, vehicle.sysid, vehicle.compid, type, list,
                (pct, status) => progress?.Report(new OperationProgress(pct, status))).ConfigureAwait(false);

            if (type != MAVLink.MAV_MISSION_TYPE.MISSION)
                return;

            // parameters that do not exist on this vehicle type are skipped by setParam
            progress?.Report(new OperationProgress(100, "Setting waypoint radius"));
            TrySetParam(mav, vehicle, "WP_RADIUS", wpRadius);
            TrySetParam(mav, vehicle, "WP_RADIUS_M", wpRadius);
            TrySetParam(mav, vehicle, "WPNAV_RADIUS", wpRadius * 100);
            if (!TrySetParam(mav, vehicle, "LOITER_RAD", loiterRadius))
                TrySetParam(mav, vehicle, "WP_LOITER_RAD", loiterRadius);
        });
    }

    private static bool TrySetParam(MAVLinkInterface mav, MAVState vehicle, string name, double value)
    {
        if (!vehicle.param.ContainsKey(name))
            return false;
        try
        {
            return mav.setParam(vehicle.sysid, vehicle.compid, name, value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Replace(ObservableCollection<MissionItem> list, IEnumerable<Locationwp> items)
    {
        list.Clear();
        foreach (var wp in items)
            list.Add(MissionItem.FromLocationwp(wp));
        Recalculate();
    }

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (MissionItem item in e.OldItems)
                item.PropertyChanged -= OnItemChanged;
        }

        if (e.NewItems != null)
        {
            foreach (MissionItem item in e.NewItems)
                item.PropertyChanged += OnItemChanged;
        }

        if (e.Action != NotifyCollectionChangedAction.Add || sender != Mission || e.NewStartingIndex != Mission.Count - 1)
            Recalculate();
        else
            Recalculate();
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // derived fields change during Recalculate itself
        if (e.PropertyName is nameof(MissionItem.Seq) or nameof(MissionItem.Distance) or nameof(MissionItem.Azimuth)
            or nameof(MissionItem.Gradient) or nameof(MissionItem.HasLocation) or nameof(MissionItem.CommandName))
            return;
        Recalculate();
    }
}
