using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Mobile;

/// <summary>How a status value reads at a glance: the views colour it.</summary>
public enum StatusLevel
{
    None,
    Ok,
    Warning,
    Bad,
}

/// <summary>
/// The phone's top bar and telemetry strip (as the DJI and QGroundControl apps show them): mode,
/// armed, GPS, battery, link, and height / distance / speeds. Plain properties for binding,
/// refreshed from the vehicle's state a few times a second.
/// </summary>
public sealed partial class MobileStatus : ObservableObject
{
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _linkText = "No link";
    [ObservableProperty] private StatusLevel _linkLevel = StatusLevel.None;

    [ObservableProperty] private string _modeText = "—";
    [ObservableProperty] private bool _isArmed;
    [ObservableProperty] private string _armText = "DISARMED";

    /// <summary>"Ready to fly", "Not ready", "Flying" — the coloured strip under the bar.</summary>
    [ObservableProperty] private string _readyText = "Not connected";
    [ObservableProperty] private StatusLevel _readyLevel = StatusLevel.None;

    [ObservableProperty] private string _gpsText = "—";
    [ObservableProperty] private StatusLevel _gpsLevel = StatusLevel.None;

    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _voltageText = "";
    [ObservableProperty] private StatusLevel _batteryLevel = StatusLevel.None;

    [ObservableProperty] private string _altitudeText = "0.0";
    [ObservableProperty] private string _distanceText = "0";
    [ObservableProperty] private string _groundSpeedText = "0.0";
    [ObservableProperty] private string _verticalSpeedText = "0.0";
    [ObservableProperty] private string _flightTimeText = "00:00";
    [ObservableProperty] private string _altitudeUnit = "m";
    [ObservableProperty] private string _speedUnit = "m/s";
    [ObservableProperty] private string _distanceUnit = "m";

    /// <summary>The latest important vehicle message (PreArm..., failsafe), empty when there is none.</summary>
    [ObservableProperty] private string _message = "";

    public void Update(VehicleSession s)
    {
        var cs = s.Cs;
        var connected = s.IsConnected;
        IsConnected = connected;
        if (!connected)
        {
            LinkText = s.State == LinkState.Connecting ? "Connecting…" : "No link";
            LinkLevel = s.State == LinkState.Connecting ? StatusLevel.Warning : StatusLevel.None;
            ReadyText = s.State == LinkState.Connecting ? "Connecting…" : "Not connected";
            ReadyLevel = StatusLevel.None;
            ModeText = "—";
            IsArmed = false;
            ArmText = "DISARMED";
            GpsText = BatteryText = "—";
            GpsLevel = BatteryLevel = StatusLevel.None;
            VoltageText = Message = "";
            return;
        }

        var lost = s.IsLinkStale || s.IsReconnecting;
        var quality = cs.linkqualitygcs;
        LinkText = lost ? "Lost" : quality.ToString("0", CultureInfo.InvariantCulture) + "%";
        LinkLevel = lost ? StatusLevel.Bad : quality >= 80 ? StatusLevel.Ok : quality >= 50 ? StatusLevel.Warning : StatusLevel.Bad;

        ModeText = string.IsNullOrEmpty(cs.mode) ? "—" : cs.mode.ToUpperInvariant();
        IsArmed = cs.armed;
        ArmText = cs.armed ? "ARMED" : "DISARMED";

        (ReadyText, ReadyLevel) = Readiness(cs.armed, cs.prearmstatus, cs.failsafe, lost, cs.alt, cs.groundspeed, cs.landed_state);

        GpsText = cs.satcount.ToString(CultureInfo.InvariantCulture) + " " + GpsFix((int)cs.gpsstatus);
        GpsLevel = cs.gpsstatus >= 3 && cs.satcount >= 6 ? StatusLevel.Ok : cs.gpsstatus >= 2 ? StatusLevel.Warning : StatusLevel.Bad;

        var remaining = cs.battery_remaining;
        BatteryText = remaining is >= 0 and <= 100 ? remaining.ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
        VoltageText = cs.battery_voltage > 0 ? cs.battery_voltage.ToString("0.0", CultureInfo.InvariantCulture) + " V" : "";
        BatteryLevel = remaining is < 0 or > 100 ? StatusLevel.None : remaining > 30 ? StatusLevel.Ok : remaining > 15 ? StatusLevel.Warning : StatusLevel.Bad;

        AltitudeUnit = Units.Altitude;
        SpeedUnit = Units.Speed;
        DistanceUnit = Units.Distance;
        AltitudeText = Num(cs.alt, "0.0");
        DistanceText = Num(cs.DistToHome, "0");
        GroundSpeedText = Num(cs.groundspeed, "0.0");
        VerticalSpeedText = Num(cs.verticalspeed, "0.0");
        var t = TimeSpan.FromSeconds(Math.Max(0, cs.timeInAir));
        FlightTimeText = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        Message = cs.messageHigh ?? "";
    }

    /// <summary>The words and colour of the readiness strip, as DJI's "Ready to Go (GPS)" line.</summary>
    public static (string Text, StatusLevel Level) Readiness(bool armed, bool readyToArm, bool failsafe, bool linkLost, double alt, double groundSpeed,
        byte landedState = 0)
    {
        if (linkLost)
            return ("Link lost", StatusLevel.Bad);
        if (failsafe)
            return ("Failsafe", StatusLevel.Bad);
        if (armed)
        {
            // MAV_LANDED_STATE when the vehicle sends it: 2 in air, 3 taking off, 4 landing
            var flying = landedState switch
            {
                2 or 3 or 4 => true,
                1 => false,
                _ => alt > 0.5 || groundSpeed > 1,
            };
            return (landedState == 4 ? "Landing" : landedState == 3 ? "Taking off" : flying ? "Flying" : "Armed", StatusLevel.Ok);
        }
        return readyToArm ? ("Ready to arm", StatusLevel.Ok) : ("Not ready to arm", StatusLevel.Warning);
    }

    /// <summary>A reading without "-0.0" for values that round to zero.</summary>
    public static string Num(double v, string format)
    {
        var text = v.ToString(format, CultureInfo.InvariantCulture);
        return text.StartsWith('-') && text.Trim('-', '0', '.').Length == 0 ? text[1..] : text;
    }

    public static string GpsFix(int status) => status switch
    {
        >= 6 => "RTK",
        5 => "RTK float",
        4 => "DGPS",
        3 => "3D",
        2 => "2D",
        1 => "No fix",
        _ => "No GPS",
    };
}
