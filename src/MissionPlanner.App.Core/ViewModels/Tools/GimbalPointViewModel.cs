using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>Where the camera points: pitch below the horizon is negative, yaw relative to the vehicle's nose (degrees).</summary>
public readonly record struct GimbalAngles(double Pitch, double Roll, double Yaw, string Source);

/// <summary>The point on the ground the camera looks at.</summary>
public sealed record GimbalTarget(double Lat, double Lng, double GroundAlt, double Distance, double Bearing);

/// <summary>
/// Mission Planner's gimbal pointing algorithm (ExtLibs GimbalPoint.ProjectPoint): the camera's
/// line of sight from the vehicle's altitude above sea level is followed out along the camera's
/// bearing in 50 m steps, each step's two terrain heights forming a segment; the first segment the
/// sight line crosses is where it hits the ground (up to 1 km).
/// </summary>
public static class GimbalPointing
{
    public const double StepMetres = 50;
    public const double MaxDistance = 1000;

    /// <summary>ArduPilot's angle_input: the angle (same unit as the limits) a PWM value maps to.</summary>
    public static double AngleInput(bool reversed, double pwm, double pwmMin, double pwmMax, double angleMin, double angleMax)
    {
        if (pwmMax == pwmMin)
            return reversed ? angleMax : angleMin;
        return (reversed ? -1.0 : 1.0) * (pwm - pwmMin) * (angleMax - angleMin) / (pwmMax - pwmMin) + (reversed ? angleMax : angleMin);
    }

    /// <summary>Where two segments cross, or null (parallel, or not within both).</summary>
    public static (double X, double Y)? Intersection((double X, double Y) start1, (double X, double Y) end1,
        (double X, double Y) start2, (double X, double Y) end2)
    {
        var denom = (end1.X - start1.X) * (end2.Y - start2.Y) - (end1.Y - start1.Y) * (end2.X - start2.X);
        if (denom == 0)
            return null;
        var numer = (start1.Y - start2.Y) * (end2.X - start2.X) - (start1.X - start2.X) * (end2.Y - start2.Y);
        var r = numer / denom;
        var numer2 = (start1.Y - start2.Y) * (end1.X - start1.X) - (start1.X - start2.X) * (end1.Y - start1.Y);
        var s = numer2 / denom;
        if (r < 0 || r > 1 || s < 0 || s > 1)
            return null;
        return (start1.X + r * (end1.X - start1.X), start1.Y + r * (end1.Y - start1.Y));
    }

    /// <summary>
    /// The ground point the camera sees. <paramref name="pitch"/> is the camera's pitch to the
    /// horizon (negative down), <paramref name="bearing"/> its direction over the ground (vehicle
    /// heading + gimbal yaw), <paramref name="terrain"/> the ground height above sea level at a
    /// point. Null when the camera looks at or above the horizon or nothing is hit within 1 km.
    /// </summary>
    public static GimbalTarget? Project(double lat, double lng, double altAsl, double pitch, double bearing,
        Func<double, double, double> terrain)
    {
        if (pitch >= 0 || pitch <= -180)
            return null;
        var here = new PointLatLngAlt(lat, lng);
        // the sight line from the vehicle down to sea level ("flat terrain distance out - at 0 alt")
        var distFlat = Math.Tan((90 + pitch) * MathHelper.deg2rad) * altAsl;
        if (pitch <= -90)
            distFlat = 0;
        var sightFrom = (0.0, altAsl);
        var sightTo = (distFlat, 0.0);

        var previousAlt = terrain(lat, lng);
        for (var dist = 0.0; dist < MaxDistance; dist += StepMetres)
        {
            var next = here.newpos(bearing, dist + StepMetres);
            var nextAlt = terrain(next.Lat, next.Lng);
            if (Intersection(sightFrom, sightTo, (dist, previousAlt), (dist + StepMetres, nextAlt)) is { } hit)
            {
                var p = here.newpos(bearing, hit.X);
                return new GimbalTarget(p.Lat, p.Lng, hit.Y, hit.X, (bearing % 360 + 360) % 360);
            }

            previousAlt = nextAlt;
        }

        return null;
    }

    /// <summary>
    /// The gimbal's angles: MOUNT_STATUS / GIMBAL_DEVICE_ATTITUDE_STATUS (CurrentState campoint),
    /// the attitude of the mount's own MAVLink component for a SToRM32 (MNT_TYPE 4), or, when the
    /// mount reports nothing, worked out from the PWM of the mount servos and the angle limits.
    /// </summary>
    public static GimbalAngles Angles(CurrentState cs, Func<string, double?> param, CurrentState? mountComponent)
    {
        double yaw = cs.campointc, roll = cs.campointb, pitch = cs.campointa;
        var source = "mount status";
        var type = param("MNT1_TYPE") ?? param("MNT_TYPE");
        if (type == 4 && mountComponent != null)
        {
            yaw = mountComponent.yaw;
            roll = mountComponent.roll;
            pitch = mountComponent.pitch;
            source = "mount component attitude";
        }

        if (Math.Abs(roll) > 180 || yaw == 0 && pitch == 0)
        {
            var y = ServoAngle(cs, param, 6, "YAW", "PAN");
            var p = ServoAngle(cs, param, 7, "PITCH", "TIL");
            if (y != null || p != null)
            {
                yaw = y ?? 0;
                // servo angles are to the airframe: add the vehicle's pitch, and its roll as far as the camera is turned sideways
                pitch = (p ?? 0) + cs.pitch - Math.Sin(yaw * MathHelper.deg2rad) * cs.roll;
                roll = 0;
                source = "mount servo outputs";
            }
        }

        return new GimbalAngles(pitch, roll, yaw, source);
    }

    /// <summary>
    /// Degrees of a mount axis from the servo output with function <paramref name="function"/>
    /// (6 mount yaw, 7 mount tilt): its PWM against SERVOn_MIN/MAX, mapped onto MNT1_x_MIN/MAX (degrees)
    /// or the older MNT_ANGMIN/MAX_x (centidegrees). Null without such an output.
    /// </summary>
    public static double? ServoAngle(CurrentState cs, Func<string, double?> param, int function, string newAxis, string oldAxis)
    {
        for (var ch = 1; ch <= 16; ch++)
        {
            if (param($"SERVO{ch}_FUNCTION") != function)
                continue;
            var pwm = ServoOut(cs, ch);
            var min = param($"SERVO{ch}_MIN") ?? 1100;
            var max = param($"SERVO{ch}_MAX") ?? 1900;
            var reversed = param($"SERVO{ch}_REVERSED") == 1;
            double angleMin, angleMax;
            if (param($"MNT1_{newAxis}_MIN") is { } nmin && param($"MNT1_{newAxis}_MAX") is { } nmax)
            {
                angleMin = nmin;
                angleMax = nmax;
            }
            else if (param($"MNT_ANGMIN_{oldAxis}") is { } omin && param($"MNT_ANGMAX_{oldAxis}") is { } omax)
            {
                angleMin = omin / 100.0;
                angleMax = omax / 100.0;
            }
            else
            {
                return null;
            }

            if (pwm <= 0)
                return null;
            return AngleInput(reversed, pwm, min, max, angleMin, angleMax);
        }

        return null;
    }

    private static double ServoOut(CurrentState cs, int ch) => ch switch
    {
        1 => cs.ch1out, 2 => cs.ch2out, 3 => cs.ch3out, 4 => cs.ch4out, 5 => cs.ch5out, 6 => cs.ch6out,
        7 => cs.ch7out, 8 => cs.ch8out, 9 => cs.ch9out, 10 => cs.ch10out, 11 => cs.ch11out, 12 => cs.ch12out,
        13 => cs.ch13out, 14 => cs.ch14out, 15 => cs.ch15out, 16 => cs.ch16out,
        _ => 0,
    };
}

/// <summary>Mission Planner's "gimbal test": runs the gimbal pointing algorithm on the connected vehicle and shows where the camera looks.</summary>
public sealed partial class GimbalPointViewModel(AppServices services)
    : ToolPageViewModel("Gimbal pointing",
        "Works out the point on the ground the camera looks at, from the gimbal angles, the vehicle's position and the terrain.",
        services)
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(GimbalPointViewModel));
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private CancellationTokenSource? _track;

    [ObservableProperty]
    private string _anglesText = "";

    [ObservableProperty]
    private string _vehicleText = "";

    [ObservableProperty]
    private string _targetText = "";

    [ObservableProperty]
    private GimbalTarget? _target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackText))]
    private bool _isTracking;

    public string TrackText => IsTracking ? "Stop" : "Follow (every second)";

    [RelayCommand]
    private async Task ProjectAsync()
    {
        if (!IsConnected)
        {
            Report("Connect to a vehicle first.", true);
            return;
        }

        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await ProjectOnceAsync(CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TrackAsync()
    {
        if (IsTracking)
        {
            _track?.Cancel();
            return;
        }

        if (!IsConnected)
        {
            Report("Connect to a vehicle first.", true);
            return;
        }

        IsTracking = true;
        _track = new CancellationTokenSource();
        var token = _track.Token;
        try
        {
            while (!token.IsCancellationRequested && IsConnected)
            {
                await ProjectOnceAsync(token);
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _track.Dispose();
            _track = null;
            IsTracking = false;
        }
    }

    private async Task ProjectOnceAsync(CancellationToken cancel)
    {
        var session = Services.Session;
        var mav = session.Mav;
        var vehicle = session.Vehicle;
        var cs = vehicle.cs;
        try
        {
            // ask for MOUNT_STATUS, as Mission Planner did, and give it a moment to arrive
            await Task.Run(mav.GetMountStatus, cancel);
            await Task.Delay(300, cancel);

            CurrentState? mount = null;
            if (mav.MAVlist.Contains(vehicle.sysid, (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_GIMBAL, false))
                mount = mav.MAVlist[vehicle.sysid, (int)MAVLink.MAV_COMPONENT.MAV_COMP_ID_GIMBAL].cs;

            var angles = GimbalPointing.Angles(cs, Services.Params.Get, mount);
            double lat = cs.lat, lng = cs.lng, alt = cs.altasl, heading = cs.yaw;
            AnglesText = string.Format(Inv, "Camera pitch {0:0.0}°, roll {1:0.0}°, yaw {2:0.0}° (from the {3})",
                angles.Pitch, angles.Roll, angles.Yaw, angles.Source);
            VehicleText = string.Format(Inv, "Vehicle {0:0.000000} {1:0.000000}, {2:0.0} m above sea level, heading {3:0}°",
                lat, lng, alt, heading);

            if (lat == 0 && lng == 0)
            {
                Target = null;
                TargetText = "";
                Report("The vehicle has no position yet.", true);
                return;
            }

            var unknownTerrain = false;
            var target = await Task.Run(() => GimbalPointing.Project(lat, lng, alt, angles.Pitch, heading + angles.Yaw, (la, ln) =>
            {
                var r = srtm.getAltitude(la, ln);
                if (r.currenttype == srtm.tiletype.invalid)
                    unknownTerrain = true;
                return r.alt;
            }), cancel);

            Target = target;
            if (target == null)
            {
                TargetText = "";
                Report(angles.Pitch >= 0
                    ? "The camera points at or above the horizon: it sees no ground."
                    : $"The line of sight does not reach the ground within {GimbalPointing.MaxDistance:0} m.", true);
                return;
            }

            TargetText = string.Format(Inv, "{0:0.000000} {1:0.000000}, ground {2:0.0} m above sea level, {3:0} m away at {4:0}°",
                target.Lat, target.Lng, target.GroundAlt, target.Distance, target.Bearing);
            Report(unknownTerrain
                ? "Camera target found (terrain height unknown here: sea level was used)."
                : "Camera target found.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("gimbal point", ex);
            Report("Gimbal pointing failed: " + ex.Message, true);
        }
    }

    public override void OnHidden()
    {
        base.OnHidden();
        _track?.Cancel();
    }
}
