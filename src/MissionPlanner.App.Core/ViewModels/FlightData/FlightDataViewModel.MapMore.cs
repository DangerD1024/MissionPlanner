using System.Globalization;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>Typed coordinates: "lat;lng;alt", "lat;lng", "lat lng [alt]" or "lat, lng [, alt]".</summary>
public static class CoordinateInput
{
    public static (double Lat, double Lng, double? Alt)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim();
        string[] parts;
        if (t.Contains(';'))
            parts = t.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else if (t.Contains(", ") || (t.Count(c => c == ',') >= 1 && t.Count(c => c == '.') >= 1))
            parts = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else
            parts = t.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length is < 2 or > 3)
            return null;
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!FlightDataViewModel.TryParseNumber(parts[i], out values[i]))
                return null;
        }

        if (Math.Abs(values[0]) > 90 || Math.Abs(values[1]) > 180 || (values[0] == 0 && values[1] == 0))
            return null;
        return (values[0], values[1], parts.Length == 3 ? values[2] : null);
    }
}

public sealed partial class FlightDataViewModel
{
    private const string CoordsHelp = "Coordinates as lat;long;alt or lat;long (decimal degrees, e.g. -35.3632;149.1652;50)";

    private async Task<(double Lat, double Lng, double? Alt)?> AskCoordsAsync(string title)
    {
        var answer = await Services.Dialogs.PromptAsync(title, CoordsHelp);
        if (answer == null)
            return null;
        if (CoordinateInput.Parse(answer) is not { } c)
        {
            Report($"\"{answer}\" is not lat;long or lat;long;alt", true);
            return null;
        }

        return c;
    }

    /// <summary>Guided flight to typed coordinates; with an altitude (above home, in the altitude unit) it flies at that.</summary>
    public async Task FlyToCoordsAsync()
    {
        if (!CheckCanCommand() || await AskCoordsAsync("Fly To Coords") is not { } c)
            return;
        if (c.Alt is { } alt)
        {
            var vehicle = Services.Session.Vehicle;
            vehicle.GuidedMode.z = (float)(alt / CurrentState.multiplieralt);
            if (vehicle.GuidedMode.frame == 0)
                vehicle.GuidedMode.frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT;
        }

        await FlyToHereAsync(c.Lat, c.Lng);
    }

    /// <summary>DO_SET_ROI at typed coordinates (altitude above sea level, or the terrain there).</summary>
    public async Task PointCameraCoordsAsync()
    {
        if (!CheckCanCommand() || await AskCoordsAsync("Point Camera Coords") is not { } c)
            return;
        double altMsl;
        if (c.Alt is { } a)
        {
            altMsl = a / CurrentState.multiplieralt;
        }
        else
        {
            var terrain = await Task.Run(() => srtm.getAltitude(c.Lat, c.Lng));
            altMsl = terrain.alt;
        }

        await RunAsync("Point camera", (mav, sysid, compid, _) =>
                mav.doCommandInt(sysid, compid, MAVLink.MAV_CMD.DO_SET_ROI, 0, 0, 0, 0, (int)(c.Lat * 1e7), (int)(c.Lng * 1e7), (float)altMsl,
                    frame: MAVLink.MAV_FRAME.GLOBAL),
            $"Camera pointed at {c.Lat:0.000000} {c.Lng:0.000000}", "The vehicle rejected the region of interest");
    }

    /// <summary>One photo now (DO_DIGICAM_CONTROL shoot).</summary>
    public Task TriggerCameraAsync() =>
        RunAsync("Trigger camera", (mav, sysid, compid, _) =>
                mav.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_DIGICAM_CONTROL, 0, 0, 0, 0, 1, 0, 0),
            "Camera triggered", "The vehicle rejected the camera trigger");

    /// <summary>SET_GPS_GLOBAL_ORIGIN at a map point, at the terrain height there (for flying without GPS).</summary>
    public async Task SetEkfOriginHereAsync(double lat, double lng)
    {
        if (!CheckCanCommand())
            return;
        var terrain = await Task.Run(() => srtm.getAltitude(lat, lng));
        if (terrain.currenttype != srtm.tiletype.valid)
        {
            await Services.Dialogs.ShowMessageAsync("Set EKF Origin Here", "No terrain (SRTM) data for this area yet.", MessageKind.Error);
            return;
        }

        await RunAsync("Set EKF origin", (mav, sysid, compid, _) =>
        {
            mav.sendPacket(new MAVLink.mavlink_set_gps_global_origin_t
            {
                latitude = (int)(lat * 1e7),
                longitude = (int)(lng * 1e7),
                altitude = (int)(terrain.alt * 1000),
                target_system = (byte)sysid,
            }, sysid, compid);
            return true;
        }, "EKF origin sent", "Could not send the EKF origin");
    }

    /// <summary>GUIDED and TAKEOFF to a typed altitude (Mission Planner's map menu Takeoff).</summary>
    public async Task TakeoffAsync()
    {
        if (!CheckCanCommand())
            return;
        var initial = Settings.Instance["takeoff_alt"] is { Length: > 0 } s ? s : "5";
        var answer = await Services.Dialogs.PromptAsync("Takeoff", L.Format("Takeoff altitude ({0})", Units.Altitude), initial);
        if (answer == null)
            return;
        if (!TryParseNumber(answer, out var alt) || alt <= 0)
        {
            Report($"\"{answer}\" is not an altitude", true);
            return;
        }

        Settings.Instance["takeoff_alt"] = alt.ToString(CultureInfo.InvariantCulture);
        var metres = (float)(alt / CurrentState.multiplieralt);
        await RunAsync("Takeoff", (mav, sysid, compid, _) =>
        {
            mav.setMode(sysid, compid, "GUIDED");
            return mav.doCommand(sysid, compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, metres);
        }, $"Taking off to {alt} {Units.Altitude}", "The vehicle rejected the takeoff (is it armed?)");
    }

    /// <summary>DO_JUMP_TAG: continue the mission at a JUMP_TAG item.</summary>
    public async Task JumpToTagAsync()
    {
        if (!CheckCanCommand())
            return;
        var answer = await Services.Dialogs.PromptAsync("Jump to Tag", "Tag number (0–65535):", "1");
        if (answer == null)
            return;
        if (!ushort.TryParse(answer.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tag))
        {
            Report("The tag must be a number from 0 to 65535", true);
            return;
        }

        await RunAsync($"Jump to tag {tag}", (mav, sysid, compid, _) =>
                mav.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_JUMP_TAG, tag, 0, 0, 0, 0, 0, 0),
            $"Jumped to tag {tag}", $"The vehicle rejected the jump to tag {tag}");
    }
}
