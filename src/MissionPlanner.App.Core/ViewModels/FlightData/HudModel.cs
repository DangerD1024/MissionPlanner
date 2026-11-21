namespace MissionPlanner.App.Core.ViewModels.FlightData;

public enum HudLevel
{
    Ok,
    Warning,
    Bad,
}

/// <summary>
/// Everything the HUD draws, copied from CurrentState once per UI tick. Plain fields, no change
/// notification: the HUD redraws as a whole when <see cref="Version"/> moves.
/// </summary>
public sealed class HudModel
{
    /// <summary>A copy with another attitude (the 3D view's frame, so the ladder matches its horizon).</summary>
    public HudModel WithAttitude(double roll, double pitch, double heading)
    {
        var m = (HudModel)MemberwiseClone();
        m.Roll = roll;
        m.Pitch = pitch;
        m.Heading = heading;
        return m;
    }

    public long Version;

    public bool Connected;
    public double Roll;
    public double Pitch;
    public double Heading;
    public double TargetHeading;
    public double NavBearing;
    public double Airspeed;
    public double Groundspeed;
    public double TargetSpeed;
    public double Altitude;
    public double TargetAltitude;
    public double VerticalSpeed;
    public string SpeedUnit = "m/s";
    public string AltUnit = "m";

    public bool Armed;
    public string Mode = "";
    public bool ReadyToArm;

    public double BatteryVoltage;
    public double BatteryCurrent;
    public int BatteryRemaining = -1;

    public string GpsText = "No GPS";
    public HudLevel GpsLevel = HudLevel.Bad;

    public double WpDistance;
    public int WpNumber;
    public TimeSpan TimeInAir;
    public int LinkQuality;

    public HudLevel EkfLevel = HudLevel.Ok;
    public HudLevel VibeLevel = HudLevel.Ok;

    /// <summary>High-severity status text, shown in the middle for a while.</summary>
    public string Message = "";

    /// <summary>Connected, but no packets for a while: "RECONNECTING · 7 s" (TCP/UDP) or "NO DATA · 7 s".</summary>
    public bool LinkLost;
    public string LinkLostText = "";

    /// <summary>Volts per cell (HUD options cell count), or null.</summary>
    public double? CellVoltage;

    /// <summary>"#rrggbb" ground colour from the HUD options; empty = standard green.</summary>
    public string GroundColor = "";

    /// <summary>User items: "Label value unit" lines drawn on the left of the HUD.</summary>
    public string[] UserLines = [];
}
