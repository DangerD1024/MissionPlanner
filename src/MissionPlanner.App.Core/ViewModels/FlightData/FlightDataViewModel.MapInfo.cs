using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>How far along the mission the vehicle is (Mission Planner's distance bar).</summary>
public static class MissionProgress
{
    /// <summary>
    /// Distance along the route from home to each mission item (index = mission sequence; 0 = home).
    /// Items without a location keep the distance of the item before.
    /// </summary>
    public static double[] Cumulative(double homeLat, double homeLng, IReadOnlyList<(double Lat, double Lng, bool HasLocation)> items)
    {
        var cum = new double[items.Count + 1];
        var lastLat = homeLat;
        var lastLng = homeLng;
        for (var i = 0; i < items.Count; i++)
        {
            var (lat, lng, has) = items[i];
            cum[i + 1] = cum[i];
            if (!has || (lat == 0 && lng == 0))
                continue;
            if (lastLat != 0 || lastLng != 0)
                cum[i + 1] += new PointLatLngAlt(lastLat, lastLng).GetDistance(new PointLatLngAlt(lat, lng));
            lastLat = lat;
            lastLng = lng;
        }

        return cum;
    }

    /// <summary>Flown and total metres for the current waypoint and the distance still to it.</summary>
    public static (double Flown, double Total) At(double[] cumulative, int currentWp, double distToWp)
    {
        if (cumulative.Length < 2)
            return (0, 0);
        var total = cumulative[^1];
        var wp = Math.Clamp(currentWp, 0, cumulative.Length - 1);
        return (Math.Clamp(cumulative[wp] - distToWp, 0, total), total);
    }
}

public sealed partial class FlightDataViewModel
{
    private double[] _missionCumulative = [];
    private int _infoTick;

    /// <summary>"3D fix · HDOP 0.7 · 14 sats" over the map.</summary>
    [ObservableProperty]
    private string _gpsInfo = "";

    [ObservableProperty]
    private bool _showWind;

    /// <summary>Where the wind blows towards, for the arrow (0 = up/north).</summary>
    [ObservableProperty]
    private double _windArrowAngle;

    [ObservableProperty]
    private string _windText = "";

    [ObservableProperty]
    private bool _showMissionProgress;

    [ObservableProperty]
    private double _missionFraction;

    [ObservableProperty]
    private string _missionProgressText = "";

    /// <summary>The route distances, rebuilt when the mission or home changes.</summary>
    private void RebuildMissionProgress()
    {
        var store = Services.Mission;
        var items = store.Mission.Select(i => (i.Lat, i.Lng, i.HasLocation)).ToList();
        var home = Services.Session.Cs.HomeLocation;
        var (hl, hg) = home != null && (home.Lat != 0 || home.Lng != 0) ? (home.Lat, home.Lng) : (store.HomeLat, store.HomeLng);
        _missionCumulative = MissionProgress.Cumulative(hl, hg, items);
    }

    /// <summary>GPS line, wind arrow and mission progress over the map (a few times a second).</summary>
    private void UpdateMapInfo(CurrentState cs)
    {
        if (++_infoTick % 4 != 0)
            return;

        if (!IsConnected)
        {
            GpsInfo = "";
            ShowWind = false;
            ShowMissionProgress = false;
            return;
        }

        var fix = (int)cs.gpsstatus switch
        {
            0 or 1 => "No fix",
            2 => "2D fix",
            3 => "3D fix",
            4 => "DGPS",
            5 => "RTK float",
            6 => "RTK fixed",
            _ => "GPS " + cs.gpsstatus.ToString(CultureInfo.InvariantCulture),
        };
        GpsInfo = string.Create(CultureInfo.InvariantCulture, $"{fix} · HDOP {cs.gpshdop:0.0} · {cs.satcount:0} sats");

        // CurrentState.wind_dir is where the wind comes from
        var windMs = cs.wind_vel / CurrentState.multiplierspeed;
        ShowWind = windMs > 0.3;
        if (ShowWind)
        {
            WindArrowAngle = (cs.wind_dir + 180) % 360;
            WindText = string.Create(CultureInfo.InvariantCulture, $"{cs.wind_dir:0}° {cs.wind_vel:0.0} {Units.Speed}");
        }

        var auto = string.Equals(cs.mode, "Auto", StringComparison.OrdinalIgnoreCase);
        if (_missionCumulative.Length != Services.Mission.Mission.Count + 1)
            RebuildMissionProgress();
        var (flown, total) = MissionProgress.At(_missionCumulative, (int)cs.wpno, cs.wp_dist / CurrentState.multiplierdist);
        ShowMissionProgress = auto && total > 0;
        if (ShowMissionProgress)
        {
            MissionFraction = flown / total;
            MissionProgressText = string.Create(CultureInfo.InvariantCulture,
                $"Mission {flown * CurrentState.multiplierdist / 1000:0.00} / {total * CurrentState.multiplierdist / 1000:0.00} k{Units.Distance} · WP {cs.wpno:0}");
        }
    }

    private Mission.PoiActions? _poi;

    /// <summary>The map's POI menu.</summary>
    public Mission.PoiActions Poi => _poi ??= new Mission.PoiActions(Services);

    /// <summary>A click on the HUD's EKF, Vibe or "Not Ready to Arm" opens its window, as Mission Planner.</summary>
    public void OpenHudItem(string item)
    {
        PageViewModel? page = item switch
        {
            "ekf" => new EkfStatusViewModel(Services),
            "vibe" => new VibrationViewModel(Services),
            "prearm" => new PrearmViewModel(Services),
            _ => null,
        };
        if (page != null)
            Services.Windows.Show(page, 560, item == "prearm" ? 420 : 380);
    }

    private bool? _hudSwapped;

    /// <summary>The HUD is shown big on the map side and the map small in its place (remembered).</summary>
    public bool IsHudSwapped
    {
        get => _hudSwapped ??= Settings.Instance.GetBoolean("next_hud_swap", false);
        set
        {
            if (IsHudSwapped == value)
                return;
            _hudSwapped = value;
            Settings.Instance["next_hud_swap"] = value.ToString();
            OnPropertyChanged();
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SwapHud() => IsHudSwapped = !IsHudSwapped;

    /// <summary>HUD menu > HUD options: ground colour, cells, user items.</summary>
    public Task OpenHudOptionsAsync() => Services.Windows.ShowDialogAsync(new HudOptionsViewModel(), 820, 620);

    /// <summary>Details of an ADS-B aircraft clicked on the map.</summary>
    public void ShowAdsbDetails(AdsbContact c)
    {
        var alt = c.AltitudeM * CurrentState.multiplieralt;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{c.Label} (ICAO {c.IcaoText}) · {alt:0} {Units.Altitude} · {c.SpeedMs * CurrentState.multiplierspeed:0} {Units.Speed} · heading {c.HeadingDeg:0}° · {(c.VerticalSpeedMs >= 0 ? "+" : "")}{c.VerticalSpeedMs:0.0} m/s · squawk {c.Squawk:0000}");
        if (c.IsStale)
            text += string.Create(CultureInfo.InvariantCulture, $" · last heard {c.Age.TotalSeconds:0} s ago");
        ShowToast(text, c.Threat == MAVLink.MAV_COLLISION_THREAT_LEVEL.HIGH, 8000);
    }
}
