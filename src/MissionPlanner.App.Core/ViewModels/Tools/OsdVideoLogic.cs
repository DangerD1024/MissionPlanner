using System.Globalization;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A BGRA picture (width × height × 4 bytes, rows without padding).</summary>
public sealed class OsdFrame(byte[] pixels, int width, int height)
{
    public byte[] Pixels { get; } = pixels;

    public int Width { get; } = width;

    public int Height { get; } = height;
}

/// <summary>
/// Draws the HUD over a video frame. The app implements it with its HUD control (rendering needs the
/// UI framework); called from a worker thread, it does its own marshalling.
/// </summary>
public interface IHudCompositor
{
    /// <summary>Draws <paramref name="hud"/> over the BGRA frame, in place.</summary>
    Task ComposeAsync(byte[] bgra, int width, int height, HudModel hud, CancellationToken cancel);
}

/// <summary>
/// Mission Planner's OSD video timing: a frame at <c>t</c> seconds into the video shows the log as
/// it was at log start + t + offset (the "time offset in seconds" slider, −900…900).
/// </summary>
public static class OsdTimeAlignment
{
    public const double MaxOffset = 900;

    public static DateTime LogTimeFor(DateTime logStart, double videoSeconds, double offsetSeconds) =>
        logStart.AddSeconds(videoSeconds + offsetSeconds);

    /// <summary>The offset that puts log time <paramref name="logTime"/> on the frame at <paramref name="videoSeconds"/>.</summary>
    public static double OffsetFor(DateTime logStart, DateTime logTime, double videoSeconds) =>
        Math.Clamp((logTime - logStart).TotalSeconds - videoSeconds, -MaxOffset, MaxOffset);
}

/// <summary>A <see cref="HudModel"/> of a <see cref="CurrentState"/>, as the flight data screen fills it.</summary>
public static class HudSnapshot
{
    public static HudModel From(CurrentState cs, long version)
    {
        var gps = cs.gpsstatus;
        var ekf = cs.ekfstatus;
        var vibe = Math.Max(cs.vibex, Math.Max(cs.vibey, cs.vibez));
        return new HudModel
        {
            Version = version,
            Connected = true,
            Roll = cs.roll,
            Pitch = cs.pitch,
            Heading = cs.yaw,
            TargetHeading = cs.target_bearing,
            NavBearing = cs.nav_bearing,
            Airspeed = cs.airspeed,
            Groundspeed = cs.groundspeed,
            Altitude = cs.alt,
            VerticalSpeed = cs.verticalspeed,
            Armed = cs.armed,
            Mode = cs.mode ?? "",
            ReadyToArm = cs.prearmstatus,
            BatteryVoltage = cs.battery_voltage,
            BatteryCurrent = cs.current,
            BatteryRemaining = cs.battery_remaining,
            GpsText = FlightDataViewModel.GpsText(gps, cs.satcount),
            GpsLevel = gps >= 3 ? HudLevel.Ok : gps >= 2 ? HudLevel.Warning : HudLevel.Bad,
            WpDistance = cs.wp_dist,
            WpNumber = (int)cs.wpno,
            TimeInAir = TimeSpan.FromSeconds(Math.Floor(cs.timeInAir)),
            LinkQuality = cs.linkqualitygcs,
            EkfLevel = ekf > 0.8 ? HudLevel.Bad : ekf > 0.5 ? HudLevel.Warning : HudLevel.Ok,
            VibeLevel = vibe > 60 ? HudLevel.Bad : vibe > 30 ? HudLevel.Warning : HudLevel.Ok,
            Message = cs.messageHigh ?? "",
            SpeedUnit = Units.Speed,
            AltUnit = Units.Altitude,
        };
    }
}

/// <summary>
/// The HUD over the time of a telemetry log, one snapshot every 100 ms (Mission Planner's
/// OSDVideo kept a CurrentState per 100 ms of log the same way).
/// </summary>
public sealed class TlogHudTimeline
{
    public const int StepMs = 100;

    private readonly DateTime[] _times;
    private readonly HudModel[] _frames;

    public TlogHudTimeline(IReadOnlyList<(DateTime Time, HudModel Hud)> snapshots)
    {
        var sorted = snapshots.OrderBy(s => s.Time).ToArray();
        _times = sorted.Select(s => s.Time).ToArray();
        _frames = sorted.Select(s => s.Hud).ToArray();
    }

    public int Count => _times.Length;

    public DateTime Start => _times.Length > 0 ? _times[0] : DateTime.MinValue;

    public DateTime End => _times.Length > 0 ? _times[^1] : DateTime.MinValue;

    public TimeSpan Duration => End - Start;

    /// <summary>The HUD as it was at <paramref name="time"/> (the last snapshot at or before it); null before the log starts.</summary>
    public HudModel? At(DateTime time)
    {
        var i = Array.BinarySearch(_times, time);
        if (i < 0)
            i = ~i - 1;
        return i < 0 ? null : _frames[i];
    }

    /// <summary>The time a snapshot is filed under: the log time rounded down to 100 ms.</summary>
    public static DateTime Bucket(DateTime t) => new(t.Ticks - t.Ticks % (StepMs * TimeSpan.TicksPerMillisecond), t.Kind);

    /// <summary>Plays a telemetry log through the MAVLink library, as Mission Planner's OSD video did.</summary>
    public static TlogHudTimeline Load(string path, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var snapshots = new List<(DateTime, HudModel)>();
        using var mav = new MAVLinkInterface(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { logreadmode = true };
        mav.speechenabled = false;
        var stream = mav.logplaybackfile.BaseStream;
        var length = Math.Max(1, stream.Length);
        var last = DateTime.MinValue;
        long version = 0;
        var packets = 0;
        while (stream.Position < stream.Length)
        {
            if ((++packets & 0x3ff) == 0)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report((double)stream.Position / length);
            }

            try
            {
                mav.readPacketAsync().GetAwaiter().GetResult();
            }
            catch (Exception) when (!cancel.IsCancellationRequested)
            {
                continue;
            }

            var now = mav.lastlogread;
            if (now == DateTime.MinValue)
                continue;
            var bucket = Bucket(now);
            if (bucket == last)
                continue;
            last = bucket;
            var vehicle = mav.MAV;
            // derived values (time in air, link quality, messages) as playback does
            vehicle.cs.UpdateCurrentSettings(null, true, mav, vehicle);
            snapshots.Add((bucket, HudSnapshot.From(vehicle.cs, ++version)));
        }

        progress?.Report(1);
        return new TlogHudTimeline(snapshots);
    }
}

/// <summary>Mission Planner's osdvideo.txt next to the video: the video, the tlog and the offset.</summary>
public static class OsdVideoConfig
{
    public const string FileName = "osdvideo.txt";

    public static string PathFor(string video) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video)) ?? ".", FileName);

    /// <summary>The tlog and offset saved for this video, or null.</summary>
    public static (string Tlog, double Offset)? Load(string video)
    {
        try
        {
            var file = PathFor(video);
            if (!File.Exists(file))
                return null;
            var lines = File.ReadAllLines(file);
            if (lines.Length < 3 || !string.Equals(Path.GetFullPath(lines[0].Trim()), Path.GetFullPath(video), StringComparison.OrdinalIgnoreCase))
                return null;
            if (!double.TryParse(lines[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var offset))
                return null;
            return (lines[1].Trim(), offset);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string video, string tlog, double offset)
    {
        try
        {
            File.WriteAllLines(PathFor(video), [video, tlog, offset.ToString(CultureInfo.InvariantCulture)]);
        }
        catch (Exception)
        {
            // a read-only folder: the settings still remember it
        }
    }
}
