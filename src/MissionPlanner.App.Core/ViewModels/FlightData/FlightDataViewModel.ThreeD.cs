using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Synthetic;
using MissionPlanner.ArduPilot.Mavlink;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>What the map side of the DATA screen shows.</summary>
public enum MapViewMode
{
    /// <summary>The 2D map, as before.</summary>
    Map2D,

    /// <summary>The 3D chase view (vehicle model over terrain).</summary>
    Map3D,

    /// <summary>The 2D map on top, the 3D view below.</summary>
    Split,
}

/// <summary>The 3D scene's telemetry and the map's 2D / 3D / split choice.</summary>
public sealed partial class FlightDataViewModel
{
    public const string MapViewKey = "next_map_view";

    /// <summary>The app's 3D scene (behind the HUD, in the map panel, in the stream).</summary>
    public SyntheticScene Scene => SyntheticScene.For(Services);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Shows2DMap), nameof(Shows3DMap), nameof(IsSplitMap), nameof(IsMap2D), nameof(IsMap3D))]
    private MapViewMode _mapView = Enum.TryParse<MapViewMode>(Settings.Instance[MapViewKey], out var m) ? m : MapViewMode.Map2D;

    partial void OnMapViewChanged(MapViewMode value) => Settings.Instance[MapViewKey] = value.ToString();

    public bool Shows2DMap => MapView != MapViewMode.Map3D;

    public bool Shows3DMap => MapView != MapViewMode.Map2D;

    public bool IsSplitMap => MapView == MapViewMode.Split;

    public bool IsMap2D => MapView == MapViewMode.Map2D;

    public bool IsMap3D => MapView == MapViewMode.Map3D;

    [RelayCommand]
    private void ShowMapView(MapViewMode mode) => MapView = mode;

    private void InitThreeD() =>
        // the satellite imagery comes through the map's own provider and tile cache
        Scene.Imagery = () => SelectedMapProvider.Provider;

    /// <summary>Hands the newest telemetry to the 3D scene while any 3D view is open.</summary>
    private void FeedScene(Session.VehicleSession s, CurrentState cs)
    {
        var scene = Scene;
        if (!scene.IsActive)
            return;
        RequestFastAttitude(s);
        var hasPosition = s.IsConnected && Session.VehicleLocation.IsKnown(cs);
        var shape = s.Vehicle.aptype switch
        {
            MAVLink.MAV_TYPE.FIXED_WING or MAVLink.MAV_TYPE.VTOL_DUOROTOR or MAVLink.MAV_TYPE.VTOL_QUADROTOR
                or MAVLink.MAV_TYPE.VTOL_TILTROTOR => VehicleShape.Plane,
            MAVLink.MAV_TYPE.GROUND_ROVER or MAVLink.MAV_TYPE.SURFACE_BOAT => VehicleShape.Rover,
            _ => VehicleShape.Multirotor,
        };
        // no position yet: show the map's centre from a little above the ground
        var pose = new VehiclePose(
            hasPosition ? cs.lat : CenterLat,
            hasPosition ? cs.lng : CenterLng,
            cs.altasl,
            cs.roll,
            cs.pitch,
            cs.yaw,
            cs.vx,
            cs.vy,
            cs.vz,
            cs.armed,
            hasPosition,
            shape,
            cs.altasl - cs.alt);

        HookPackets(s.Mav);

        // connected with a position: attitude and position come straight from the packets (exact
        // arrival times, every sample); only the slow state goes from here
        if (hasPosition && Environment.TickCount64 - Interlocked.Read(ref _lastPositionPacket) < 1000 && scene.HasPose)
        {
            scene.UpdateState(cs.armed, shape, cs.altasl - cs.alt);
            return;
        }

        // otherwise (no position yet, log playback) from CurrentState — only real new samples:
        // this runs at 20 Hz whatever the telemetry rate, and handing the same values again would
        // look like "no movement since now" and pull the prediction back
        if (_lastScenePose is { } last && last == pose && Environment.TickCount64 - _lastScenePoseAt < 1000)
            return;
        _lastScenePose = pose;
        _lastScenePoseAt = Environment.TickCount64;
        scene.UpdatePose(pose);
    }

    private MAVLinkInterface? _hooked;
    private long _lastPositionPacket;

    /// <summary>Feeds ATTITUDE and GLOBAL_POSITION_INT of the selected vehicle to the scene as they arrive.</summary>
    private void HookPackets(MAVLinkInterface mav)
    {
        if (ReferenceEquals(_hooked, mav))
            return;
        _hooked = mav;
        mav.OnPacketReceived += (_, m) =>
        {
            var scene = Scene;
            if (!scene.IsActive || m.sysid != mav.MAV.sysid || m.compid != mav.MAV.compid)
                return;
            if (m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE)
            {
                var a = m.ToStructure<MAVLink.mavlink_attitude_t>();
                const double deg = 180 / Math.PI;
                scene.UpdateAttitude(a.roll * deg, a.pitch * deg, PoseTracker.Norm360(a.yaw * deg));
            }
            else if (m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT)
            {
                var g = m.ToStructure<MAVLink.mavlink_global_position_int_t>();
                if (!Session.VehicleLocation.IsKnown(g.lat / 1e7, g.lon / 1e7, Session.VehicleLocation.HasGpsFix(mav.MAV.cs)))
                    return;
                Interlocked.Exchange(ref _lastPositionPacket, Environment.TickCount64);
                scene.UpdatePosition(new VehiclePose(g.lat / 1e7, g.lon / 1e7, g.alt / 1000.0, 0, 0, 0,
                    g.vx / 100.0, g.vy / 100.0, g.vz / 100.0, false, true, VehicleShape.Multirotor));
            }
        };
    }

    private object? _fastRatesFor;
    private long _fastRatesAt;

    /// <summary>
    /// The 3D view needs attitude and position more often than the usual stream rates (4 and 2 Hz)
    /// to move smoothly. While a 3D view is open: ATTITUDE 25 Hz and position 10 Hz (about 1 kB/s),
    /// or 50 / 30 Hz from a simulator on this computer. Asked for again every few seconds while the
    /// measured rate stays low (the connection's own stream setup can override the first request).
    /// </summary>
    private void RequestFastAttitude(Session.VehicleSession s)
    {
        if (!s.IsConnected)
        {
            _fastRatesFor = null;
            return;
        }

        var local = IsLocalLink(s.Link);
        var attitudeHz = local ? 50 : 25;
        var positionHz = local ? 30 : 10;
        var vehicle = s.Vehicle;
        var now = Environment.TickCount64;
        var again = ReferenceEquals(_fastRatesFor, vehicle) && now - _fastRatesAt > 3000 && Scene.AttitudeRate < attitudeHz * 0.6;
        if (ReferenceEquals(_fastRatesFor, vehicle) && !again)
            return;
        _fastRatesFor = vehicle;
        _fastRatesAt = now;

        var mav = s.Mav;
        var cs = s.Cs;
        var (sysid, compid) = (vehicle.sysid, vehicle.compid);
        _ = Task.Run(() =>
        {
            try
            {
                // the classic stream rates the app uses (kept by the vehicle until changed)...
                cs.rateattitude = Math.Max(cs.rateattitude, attitudeHz);
                cs.rateposition = Math.Max(cs.rateposition, positionHz);
                mav.requestDatastream(MAVLink.MAV_DATA_STREAM.EXTRA1, cs.rateattitude, sysid, compid);
                mav.requestDatastream(MAVLink.MAV_DATA_STREAM.POSITION, cs.rateposition, sysid, compid);
                // ...and the per-message interval of newer firmware
                mav.doCommand(sysid, compid, MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL, (float)MAVLink.MAVLINK_MSG_ID.ATTITUDE, 1e6f / attitudeHz, 0, 0, 0, 0, 0, false);
                mav.doCommand(sysid, compid, MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL, (float)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT, 1e6f / positionHz, 0, 0, 0, 0, 0, false);
            }
            catch (Exception)
            {
                // an older autopilot: the usual rates stay
            }
        });
    }

    /// <summary>A TCP/UDP link to this computer (SITL), where telemetry bandwidth is free.</summary>
    public static bool IsLocalLink(Links.LinkSpec? link) =>
        link is { Kind: Links.LinkKind.Tcp or Links.LinkKind.UdpConnect } &&
        (link.Address is "127.0.0.1" or "localhost" or "::1" || link.Address.StartsWith("127.", StringComparison.Ordinal)) ||
        link is { Kind: Links.LinkKind.UdpListen } && link.Address is "" or "127.0.0.1";

    private VehiclePose? _lastScenePose;
    private long _lastScenePoseAt;
}
