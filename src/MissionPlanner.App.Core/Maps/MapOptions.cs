using GMap.NET;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Maps;

/// <summary>
/// Mission Planner's map options (Config > Planner), read and written with its own settings keys:
/// how much of the flown track is kept, which vehicle lines are drawn and how long, and whether
/// map tiles come from the server, the cache or both. Changed through the map settings dialog.
/// </summary>
public static class MapOptions
{
    public const string TrackLengthKey = "NUM_tracklength";
    public const string DisplayCogKey = "GMapMarkerBase_DisplayCOG";
    public const string DisplayHeadingKey = "GMapMarkerBase_DisplayHeading";
    public const string DisplayNavBearingKey = "GMapMarkerBase_DisplayNavBearing";
    public const string DisplayTargetKey = "GMapMarkerBase_DisplayTarget";

    /// <summary>Mission Planner saves the line length, and reads it at start-up, under this spelling (lower-case l).</summary>
    public const string LineLengthKey = "GMapMarkerBase_length";

    /// <summary>The GMap.NET <see cref="GMap.NET.AccessMode"/> name.</summary>
    public const string AccessModeKey = "mapCache";

    /// <summary>
    /// Points kept for the flown track when nothing is set. Mission Planner's default is 200 points taken every
    /// 0.3 s; this app keeps a point every 1.5 m instead, and has kept 2000 of them from the start.
    /// </summary>
    public const int DefaultTrackLength = 2000;

    /// <summary>Mission Planner's limits for the track length.</summary>
    public const int MinTrackLength = 100;
    public const int MaxTrackLength = 200000;

    /// <summary>Length of the vehicle's lines in pixels, Mission Planner's default and limits.</summary>
    public const int DefaultLineLength = 500;
    public const int MinLineLength = 10;
    public const int MaxLineLength = 2000;

    public static int TrackLength =>
        Math.Clamp(Settings.Instance.GetInt32(TrackLengthKey, DefaultTrackLength), MinTrackLength, MaxTrackLength);

    public static bool DisplayCog => Settings.Instance.GetBoolean(DisplayCogKey, true);

    public static bool DisplayHeading => Settings.Instance.GetBoolean(DisplayHeadingKey, true);

    public static bool DisplayNavBearing => Settings.Instance.GetBoolean(DisplayNavBearingKey, true);

    public static bool DisplayTarget => Settings.Instance.GetBoolean(DisplayTargetKey, true);

    public static int LineLength =>
        Math.Clamp(Settings.Instance.GetInt32(LineLengthKey, DefaultLineLength), MinLineLength, MaxLineLength);

    /// <summary>Where tiles come from; Mission Planner's default (server, kept in the cache) when unset or unknown.</summary>
    public static AccessMode AccessMode =>
        Enum.TryParse<AccessMode>(Settings.Instance[AccessModeKey], out var mode) && Enum.IsDefined(mode)
            ? mode
            : AccessMode.ServerAndCache;
}
