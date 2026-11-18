using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

public sealed record MapProviderOption(string Name, GMap.NET.MapProviders.GMapProvider Provider)
{
    public override string ToString() => Name;
}

/// <summary>DATA screen: HUD, telemetry tabs and the map. Skeleton: HUD and map position only.</summary>
public sealed partial class FlightDataViewModel : PageViewModel
{
    // ArduPilot's default SITL location (CMAC, Canberra)
    private const double DefaultLat = -35.363261;
    private const double DefaultLng = 149.165230;

    public FlightDataViewModel(AppServices services)
        : base("Flight Data")
    {
        Services = services;
        MapProviders = MapTiles.CommonProviders.Select(p => new MapProviderOption(p.Name, p)).ToArray();
        _selectedMapProvider = MapProviders[0];
        Tlogs = new TlogPlayerViewModel(services);
        Tuning = new TuningGraphViewModel(services);
    }

    public AppServices Services { get; }

    public HudModel Hud { get; } = new();

    /// <summary>Raised after <see cref="Hud"/> and the other telemetry values were refreshed.</summary>
    public event EventHandler? TelemetryUpdated;

    public TlogPlayerViewModel Tlogs { get; }

    public TuningGraphViewModel Tuning { get; }

    public IReadOnlyList<MapProviderOption> MapProviders { get; }

    [ObservableProperty]
    private MapProviderOption _selectedMapProvider;

    [ObservableProperty]
    private double _centerLat = DefaultLat;

    [ObservableProperty]
    private double _centerLng = DefaultLng;

    [ObservableProperty]
    private int _zoom = 17;

    public override void Tick()
    {
        var s = Services.Session;
        var cs = s.Cs;
        var h = Hud;
        h.Connected = s.IsConnected || s.Mav.logreadmode;
        h.Roll = cs.roll;
        h.Pitch = cs.pitch;
        h.Heading = cs.yaw;
        h.TargetHeading = cs.target_bearing;
        h.NavBearing = cs.nav_bearing;
        h.Airspeed = cs.airspeed;
        h.Groundspeed = cs.groundspeed;
        h.Altitude = cs.alt;
        h.VerticalSpeed = cs.verticalspeed;
        h.Armed = cs.armed;
        h.Mode = cs.mode;
        h.ReadyToArm = cs.prearmstatus;
        h.BatteryVoltage = cs.battery_voltage;
        h.BatteryCurrent = cs.current;
        h.BatteryRemaining = cs.battery_remaining;
        h.GpsText = cs.gpsstatus switch
        {
            0 => "No GPS",
            1 => "No Fix",
            2 => $"2D Fix ({cs.satcount:0})",
            3 => $"3D Fix ({cs.satcount:0})",
            4 => $"3D DGPS ({cs.satcount:0})",
            5 => $"RTK Float ({cs.satcount:0})",
            6 => $"RTK Fixed ({cs.satcount:0})",
            _ => cs.gpsstatus.ToString("0"),
        };
        h.GpsLevel = cs.gpsstatus >= 3 ? HudLevel.Ok : cs.gpsstatus >= 2 ? HudLevel.Warning : HudLevel.Bad;
        h.WpDistance = cs.wp_dist;
        h.WpNumber = (int)cs.wpno;
        h.TimeInAir = TimeSpan.FromSeconds(cs.timeInAir);
        h.LinkQuality = cs.linkqualitygcs;
        h.EkfLevel = cs.ekfstatus > 0.8 ? HudLevel.Bad : cs.ekfstatus > 0.5 ? HudLevel.Warning : HudLevel.Ok;
        var vibe = Math.Max(cs.vibex, Math.Max(cs.vibey, cs.vibez));
        h.VibeLevel = vibe > 60 ? HudLevel.Bad : vibe > 30 ? HudLevel.Warning : HudLevel.Ok;
        h.Message = cs.messageHigh ?? "";
        h.Version++;

        TelemetryUpdated?.Invoke(this, EventArgs.Empty);
    }
}
