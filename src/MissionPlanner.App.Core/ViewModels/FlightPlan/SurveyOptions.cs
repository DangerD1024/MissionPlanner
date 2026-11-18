using System.Globalization;
using MissionPlanner.App.Core.Mission;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>How the camera is triggered along the lanes.</summary>
public enum SurveyTrigger
{
    /// <summary>DO_SET_CAM_TRIGG_DIST at the start, off at the end (Mission Planner's "Trigger distance").</summary>
    CameraDistance,

    /// <summary>Waypoints only.</summary>
    None,
}

/// <summary>What the survey ends with.</summary>
public enum SurveyFinish
{
    None,
    ReturnToLaunch,
    LandAtHome,
}

/// <summary>A labelled choice for a combo box.</summary>
public sealed record SurveyChoice<T>(string Name, T Value)
{
    public override string ToString() => Name;
}

/// <summary>Every input of the Survey (Grid) dialog. Distances in metres, angles in degrees, speed in m/s.</summary>
public sealed record SurveyOptions
{
    public double Altitude { get; init; } = 100;

    /// <summary>Distance between lanes.</summary>
    public double LaneSpacing { get; init; } = 50;

    /// <summary>Distance between camera triggers; 0 = none.</summary>
    public double TriggerDistance { get; init; } = 30;

    /// <summary>Bearing the lanes run along.</summary>
    public double Angle { get; init; }

    /// <summary>Lanes follow the polygon's longest edge.</summary>
    public bool AutoAngle { get; init; } = true;

    /// <summary>Extension past the lane end, for lanes flown one way (1) and the other (2).</summary>
    public double Overshoot1 { get; init; }

    public double Overshoot2 { get; init; }

    /// <summary>Run-up before the lane start, for lanes flown one way (1) and the other (2).</summary>
    public double LeadIn1 { get; init; }

    public double LeadIn2 { get; init; }

    public Grid.StartPosition StartFrom { get; init; } = Grid.StartPosition.Home;

    /// <summary>Minimum number of lanes between consecutive lanes (wide turns for planes); 0 = next lane.</summary>
    public double MinLaneSeparation { get; init; }

    /// <summary>Flies the area a second time with lanes at 90 degrees.</summary>
    public bool Crossgrid { get; init; }

    public bool AddTakeoff { get; init; }

    public double TakeoffAltitude { get; init; } = 30;

    /// <summary>Adds DO_CHANGE_SPEED before the survey (and restores the cruise speed after it).</summary>
    public bool ChangeSpeed { get; init; }

    /// <summary>Survey speed; also used for the flight time estimate.</summary>
    public double Speed { get; init; } = 5;

    public SurveyTrigger Trigger { get; init; } = SurveyTrigger.CameraDistance;

    /// <summary>Lane starts become spline waypoints.</summary>
    public bool UseSpline { get; init; }

    public SurveyFinish Finish { get; init; } = SurveyFinish.None;

    /// <summary>Altitude frame of the generated items.</summary>
    public AltFrame Frame { get; init; } = AltFrame.Relative;

    /// <summary>True when the camera is triggered: trigger type set and a trigger distance above 0.</summary>
    public bool UsesCamera => Trigger == SurveyTrigger.CameraDistance && TriggerDistance > 0;

    // ---- settings ("next_survey_*") ----------------------------------------------------------

    public const string KeyPrefix = "next_survey_";

    /// <summary>Reads the remembered inputs; a missing or bad value keeps <paramref name="defaults"/>.</summary>
    public static SurveyOptions Load(Func<string, string?> read, SurveyOptions? defaults = null)
    {
        var d = defaults ?? new SurveyOptions();

        double D(string key, double fallback) =>
            read(KeyPrefix + key) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                                           && double.IsFinite(v)
                ? v
                : fallback;

        bool B(string key, bool fallback) => read(KeyPrefix + key) is { } s && bool.TryParse(s, out var v) ? v : fallback;

        T E<T>(string key, T fallback) where T : struct, Enum =>
            read(KeyPrefix + key) is { } s && Enum.TryParse<T>(s, out var v) && Enum.IsDefined(v) ? v : fallback;

        var start = E("startfrom", d.StartFrom);
        return d with
        {
            Altitude = D("alt", d.Altitude),
            LaneSpacing = D("lanespacing", d.LaneSpacing),
            TriggerDistance = D("triggerdist", d.TriggerDistance),
            AutoAngle = B("autoangle", d.AutoAngle),
            Angle = D("angle", d.Angle),
            Overshoot1 = D("overshoot1", d.Overshoot1),
            Overshoot2 = D("overshoot2", d.Overshoot2),
            LeadIn1 = D("leadin1", d.LeadIn1),
            LeadIn2 = D("leadin2", d.LeadIn2),
            // "Point" needs a start point the dialog does not offer
            StartFrom = start == Grid.StartPosition.Point ? d.StartFrom : start,
            MinLaneSeparation = D("minlanesep", d.MinLaneSeparation),
            Crossgrid = B("crossgrid", d.Crossgrid),
            AddTakeoff = B("takeoff", d.AddTakeoff),
            TakeoffAltitude = D("takeoffalt", d.TakeoffAltitude),
            ChangeSpeed = B("usespeed", d.ChangeSpeed),
            Speed = D("speed", d.Speed),
            Trigger = E("trigger", d.Trigger),
            UseSpline = B("spline", d.UseSpline),
            Finish = E("finish", d.Finish),
        };
    }

    /// <summary>Writes the inputs worth remembering (not the frame: it follows the planner's default).</summary>
    public void Save(Action<string, string> write)
    {
        var inv = CultureInfo.InvariantCulture;
        void D(string key, double v) => write(KeyPrefix + key, v.ToString("R", inv));
        void B(string key, bool v) => write(KeyPrefix + key, v.ToString(inv));

        D("alt", Altitude);
        D("lanespacing", LaneSpacing);
        D("triggerdist", TriggerDistance);
        B("autoangle", AutoAngle);
        D("angle", Angle);
        D("overshoot1", Overshoot1);
        D("overshoot2", Overshoot2);
        D("leadin1", LeadIn1);
        D("leadin2", LeadIn2);
        write(KeyPrefix + "startfrom", StartFrom.ToString());
        D("minlanesep", MinLaneSeparation);
        B("crossgrid", Crossgrid);
        B("takeoff", AddTakeoff);
        D("takeoffalt", TakeoffAltitude);
        B("usespeed", ChangeSpeed);
        D("speed", Speed);
        write(KeyPrefix + "trigger", Trigger.ToString());
        B("spline", UseSpline);
        write(KeyPrefix + "finish", Finish.ToString());
    }
}
