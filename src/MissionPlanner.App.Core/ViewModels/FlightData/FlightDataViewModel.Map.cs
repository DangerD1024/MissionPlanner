using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>Map: vehicle, track, mission overlay, pointer readout and the right-click commands.</summary>
public sealed partial class FlightDataViewModel
{
    private bool _missionDirty = true;
    private double _homeLat;
    private double _homeLng;
    private double _pointerLat;
    private double _pointerLng;
    private bool _hasPointer;
    private srtm.altresponce? _terrain;
    private bool _terrainBusy;
    private bool _terrainPending;
    private bool _mapPositionDirty;
    private int _currentWp = -1;

    /// <summary>What the map layers draw; refreshed at up to 10 Hz.</summary>
    public FlightMapModel MapModel { get; } = new();

    /// <summary>Keeps the vehicle in the middle of the map. Panning by hand turns it off.</summary>
    [ObservableProperty]
    private bool _autoPan = true;

    /// <summary>Shows the tuning graph over the bottom of the map.</summary>
    [ObservableProperty]
    private bool _showTuning;

    [ObservableProperty]
    private string _pointerText = "";

    [ObservableProperty]
    private int _maxZoom = 20;

    public int MinZoom => 2;

    /// <summary><see cref="Zoom"/> as a double, for the zoom slider.</summary>
    public double ZoomValue
    {
        get => Zoom;
        set
        {
            var z = (int)Math.Round(value);
            if (z != Zoom)
                Zoom = Math.Clamp(z, MinZoom, MaxZoom);
        }
    }

    partial void OnZoomChanged(int value)
    {
        OnPropertyChanged(nameof(ZoomValue));
        _mapPositionDirty = true;
    }

    partial void OnCenterLatChanged(double value) => _mapPositionDirty = true;

    partial void OnCenterLngChanged(double value) => _mapPositionDirty = true;

    partial void OnSelectedMapProviderChanged(MapProviderOption value)
    {
        MaxZoom = value.Provider.MaxZoom ?? 20;
        if (Zoom > MaxZoom)
            Zoom = MaxZoom;
        Settings.Instance["MapType"] = value.Name;
    }

    partial void OnShowTuningChanged(bool value)
    {
        if (!_shown)
            return;
        if (value)
            Tuning.OnShown();
        else
            Tuning.OnHidden();
    }

    partial void OnAutoPanChanged(bool value)
    {
        if (value && MapModel.HasPosition)
            CenterOnVehicle();
    }

    /// <summary>The user dragged the map.</summary>
    public void OnUserPanned() => AutoPan = false;

    private void OnMissionChanged()
    {
        _missionDirty = true;
        var count = Services.Mission.Mission.Count;
        if (WaypointNumbers.Count != count + 1)
        {
            // the combo drops its selection when its list is replaced: put it back
            var keep = SelectedWaypoint;
            WaypointNumbers = Enumerable.Range(0, count + 1).ToArray();
            SelectedWaypoint = Math.Clamp(keep, 0, count);
        }
    }

    private void UpdateMap(MAVState vehicle, CurrentState cs)
    {
        var m = MapModel;
        var c = false;

        var lat = cs.lat;
        var lng = cs.lng;
        var has = (lat != 0 || lng != 0) && !double.IsNaN(lat) && !double.IsNaN(lng);
        c |= Set(ref m.HasPosition, has);
        if (has)
        {
            c |= Set(ref m.Lat, lat);
            c |= Set(ref m.Lng, lng);
            c |= Set(ref m.Heading, cs.yaw);
            c |= Set(ref m.GroundCourse, cs.groundcourse);
            c |= Set(ref m.NavBearing, cs.nav_bearing);
            c |= Set(ref m.TargetBearing, cs.target_bearing);
            c |= m.Track.Add(lat, lng);
        }

        // navigation lines only mean something while the autopilot is navigating
        c |= Set(ref m.ShowNavLines, cs.armed || cs.wp_dist > 0);

        var wpno = (int)cs.wpno;
        if (wpno != _currentWp)
        {
            _currentWp = wpno;
            m.CurrentWp = wpno > 0 ? wpno.ToString(CultureInfo.InvariantCulture) : "";
            c = true;
        }

        var kind = VehicleIcons.For(vehicle.aptype, cs.firmware);
        if (kind != m.Kind)
        {
            m.Kind = kind;
            c = true;
        }

        var home = cs.HomeLocation;
        var hasHome = home != null && (home.Lat != 0 || home.Lng != 0);
        if (hasHome != m.HasHome || (hasHome && (home!.Lat != _homeLat || home.Lng != _homeLng)))
        {
            m.HasHome = hasHome;
            _homeLat = m.HomeLat = hasHome ? home!.Lat : 0;
            _homeLng = m.HomeLng = hasHome ? home!.Lng : 0;
            // the mission route starts at the vehicle's home
            _missionDirty = true;
            c = true;
        }

        var guided = vehicle.GuidedMode;
        var inGuided = guided.x != 0 && string.Equals(cs.mode, "Guided", StringComparison.OrdinalIgnoreCase);
        c |= Set(ref m.HasGuided, inGuided);
        if (inGuided)
        {
            c |= Set(ref m.GuidedLat, guided.x / 1e7);
            c |= Set(ref m.GuidedLng, guided.y / 1e7);
            c |= Set(ref m.GuidedAlt, guided.z * CurrentState.multiplieralt);
        }

        if (_missionDirty)
        {
            _missionDirty = false;
            m.Mission = MissionOverlay.Build(Services.Mission, m.HasHome ? new TrackPoint(m.HomeLat, m.HomeLng) : null);
            c = true;
        }

        if (c)
            m.Version++;

        if (AutoPan && has)
            CenterOnVehicle();
    }

    private void CenterOnVehicle()
    {
        var m = MapModel;
        // only move for a visible change, so the map is not re-rendered for noise
        var mpp = 156543.03392 * Math.Cos(m.Lat * Math.PI / 180) / Math.Pow(2, Zoom);
        if (TrackBuffer.DistanceMetres(CenterLat, CenterLng, m.Lat, m.Lng) < mpp * 0.5)
            return;
        CenterLat = m.Lat;
        CenterLng = m.Lng;
    }

    // ---- pointer readout ---------------------------------------------------------------------

    /// <summary>The mouse moved over the map.</summary>
    public void OnPointerMoved(double lat, double lng)
    {
        _pointerLat = lat;
        _pointerLng = lng;
        _hasPointer = true;
        UpdatePointerText();
        RequestTerrain();
    }

    private void UpdatePointerText()
    {
        if (!_hasPointer)
        {
            PointerText = "";
            return;
        }

        var text = string.Format(CultureInfo.InvariantCulture, "{0:0.000000}  {1:0.000000}", _pointerLat, _pointerLng);
        if (_terrain is { currenttype: srtm.tiletype.valid } t)
            text += string.Format(CultureInfo.InvariantCulture, "   {0:0} {1} (terrain)", t.alt * CurrentState.multiplieralt, Units.Altitude);
        PointerText = text;
    }

    /// <summary>One terrain lookup at a time, always for the latest pointer position (tiles load from disk).</summary>
    private void RequestTerrain()
    {
        if (_terrainBusy)
        {
            _terrainPending = true;
            return;
        }

        _terrainBusy = true;
        var lat = _pointerLat;
        var lng = _pointerLng;
        _ = Task.Run(() =>
        {
            srtm.altresponce? r;
            try
            {
                r = srtm.getAltitude(lat, lng);
            }
            catch (Exception)
            {
                r = null;
            }

            Services.Dispatcher.Post(() =>
            {
                _terrainBusy = false;
                _terrain = r;
                if (_terrainPending)
                {
                    _terrainPending = false;
                    RequestTerrain();
                }
                else
                {
                    UpdatePointerText();
                }
            });
        });
    }

    // ---- context menu ------------------------------------------------------------------------

    /// <summary>Guided mode to a map point at the remembered guided altitude (asked the first time).</summary>
    public Task FlyToHereAsync(double lat, double lng) => FlyToAsync(lat, lng, askAltitude: false);

    /// <summary>Asks for the guided altitude, remembers it, then flies to the point.</summary>
    public Task FlyToHereAltAsync(double lat, double lng) => FlyToAsync(lat, lng, askAltitude: true);

    private async Task FlyToAsync(double lat, double lng, bool askAltitude)
    {
        if (!CheckCanCommand())
            return;

        var vehicle = Services.Session.Vehicle;
        if (askAltitude || vehicle.GuidedMode.z == 0)
        {
            if (!await AskGuidedAltitudeAsync(vehicle))
                return;
        }

        if (lat == 0 || lng == 0)
        {
            Report("Bad coordinates", true);
            return;
        }

        var wp = new Locationwp
        {
            id = (ushort)MAVLink.MAV_CMD.WAYPOINT,
            alt = vehicle.GuidedMode.z,
            lat = lat,
            lng = lng,
            frame = vehicle.GuidedMode.frame,
        };
        var altText = (wp.alt * CurrentState.multiplieralt).ToString("0.#", CultureInfo.InvariantCulture) + " " + Units.Altitude;
        await RunAsync("Fly to here", (mav, sysid, compid, _) =>
        {
            // switches to Guided first if needed; failures are logged by the library
            mav.setGuidedModeWP(sysid, compid, wp);
            return true;
        }, $"Flying to {lat:0.000000} {lng:0.000000} at {altText}", "Fly to here failed");
    }

    private async Task<bool> AskGuidedAltitudeAsync(MAVState vehicle)
    {
        var cs = vehicle.cs;
        var frame = MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT;
        if (byte.TryParse(Settings.Instance["guided_alt_frame"], out var f) && Enum.IsDefined(typeof(MAVLink.MAV_FRAME), f))
            frame = (MAVLink.MAV_FRAME)f;

        var initial = Settings.Instance["guided_alt"] is { Length: > 0 } saved
            ? saved
            : ((cs.firmware == Firmwares.ArduCopter2 ? 10 : 100) * CurrentState.multiplieralt).ToString("0", CultureInfo.InvariantCulture);

        var reference = frame switch
        {
            MAVLink.MAV_FRAME.GLOBAL or MAVLink.MAV_FRAME.GLOBAL_INT => "above sea level",
            MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT or MAVLink.MAV_FRAME.GLOBAL_TERRAIN_ALT_INT => "above terrain",
            _ => "above home",
        };
        var answer = await Services.Dialogs.PromptAsync("Fly To Here Alt", $"Guided mode altitude ({Units.Altitude}, {reference})", initial);
        if (answer == null)
            return false;
        if (!TryParseNumber(answer, out var alt))
        {
            Report($"\"{answer}\" is not an altitude", true);
            return false;
        }

        Settings.Instance["guided_alt"] = alt.ToString(CultureInfo.InvariantCulture);
        Settings.Instance["guided_alt_frame"] = ((byte)frame).ToString(CultureInfo.InvariantCulture);
        vehicle.GuidedMode.z = (float)(alt / CurrentState.multiplieralt);
        vehicle.GuidedMode.frame = (byte)frame;
        return true;
    }

    /// <summary>Accepts "12.5" and "12,5" (the prompt is free text).</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return !double.IsNaN(value) && !double.IsInfinity(value);
        return false;
    }

    /// <summary>DO_SET_HOME at a map point, at the terrain altitude there (asks first).</summary>
    public async Task SetHomeHereAsync(double lat, double lng)
    {
        if (!CheckCanCommand())
            return;

        srtm.altresponce? alt;
        try
        {
            alt = await Task.Run(() => srtm.getAltitude(lat, lng));
        }
        catch (Exception)
        {
            alt = null;
        }

        if (alt == null || (alt.currenttype != srtm.tiletype.valid && alt.currenttype != srtm.tiletype.ocean))
        {
            await Services.Dialogs.ShowMessageAsync("Set Home Here",
                "No terrain (SRTM) data for this area yet. It may still be downloading: try again in a moment.", MessageKind.Error);
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Set Home Here",
                "This will reset the onboard home position (affects RTL etc). Are you sure?", "Set Home"))
            return;

        var altAmsl = (float)alt.alt;
        await RunAsync("Set home", (mav, sysid, compid, _) =>
        {
            var ok = mav.doCommandInt(sysid, compid, MAVLink.MAV_CMD.DO_SET_HOME, 0, 0, 0, 0,
                (int)(lat * 1e7), (int)(lng * 1e7), altAmsl);
            try
            {
                // refreshes cs.HomeLocation
                mav.getHomePositionAsync(sysid, compid).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Info("home position after DO_SET_HOME", ex);
            }

            return ok;
        }, "Home position set", "The vehicle rejected the new home position");
    }

    public void ClearTrack()
    {
        MapModel.Track.Clear();
        MapModel.Version++;
    }

    public void ZoomToVehicle()
    {
        if (!MapModel.HasPosition)
        {
            ShowToast("No vehicle position yet", true);
            return;
        }

        CenterLat = MapModel.Lat;
        CenterLng = MapModel.Lng;
        if (Zoom < 17)
            Zoom = Math.Min(17, MaxZoom);
    }

    [RelayCommand]
    private void ZoomIn() => ZoomValue = Zoom + 1;

    [RelayCommand]
    private void ZoomOut() => ZoomValue = Zoom - 1;

    // ---- remembered map position (Mission Planner's maplast_* keys) ---------------------------

    private void RestoreMapPosition()
    {
        var inv = CultureInfo.InvariantCulture;
        if (double.TryParse(Settings.Instance["maplast_lat"], NumberStyles.Float, inv, out var lat) &&
            double.TryParse(Settings.Instance["maplast_lng"], NumberStyles.Float, inv, out var lng) &&
            Math.Abs(lat) <= 85 && Math.Abs(lng) <= 180 && (Math.Round(lat, 1) != 0 || Math.Round(lng, 1) != 0))
        {
            CenterLat = lat;
            CenterLng = lng;
            if (int.TryParse(Settings.Instance["maplast_zoom"], NumberStyles.Integer, inv, out var zoom) && zoom is >= 2 and <= 22)
                Zoom = zoom;
        }
    }

    private void SaveMapPosition()
    {
        if (!_mapPositionDirty)
            return;
        _mapPositionDirty = false;
        var inv = CultureInfo.InvariantCulture;
        Settings.Instance["maplast_lat"] = CenterLat.ToString("0.0000000", inv);
        Settings.Instance["maplast_lng"] = CenterLng.ToString("0.0000000", inv);
        Settings.Instance["maplast_zoom"] = Zoom.ToString(inv);
    }
}
