using System.Globalization;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>One photo the vehicle reported (CAMERA_FEEDBACK) and the ground it covers.</summary>
public sealed record PhotoFootprint(int Index, double Lat, double Lng, double AltAboveHome, IReadOnlyList<TrackPoint> Corners);

/// <summary>
/// Mission Planner's "Camera Overlap": the ground each photo covers, from where the vehicle was,
/// its attitude and the camera's field of view, for a camera looking straight down from the body.
/// </summary>
public static class CameraFootprints
{
    public const string HorizontalFovKey = "next_cam_hfov";
    public const string VerticalFovKey = "next_cam_vfov";

    /// <summary>Horizontal field of view in degrees (across the vehicle).</summary>
    public static double HorizontalFov
    {
        get => Settings.Instance.GetDouble(HorizontalFovKey, 62.2);
        set => Settings.Instance[HorizontalFovKey] = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Vertical field of view in degrees (along the vehicle).</summary>
    public static double VerticalFov
    {
        get => Settings.Instance.GetDouble(VerticalFovKey, 48.8);
        set => Settings.Instance[VerticalFovKey] = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The four ground corners (front-left, front-right, back-right, back-left) for a camera at
    /// <paramref name="height"/> metres above the ground; a ray above the horizon is cut at 10 heights.
    /// </summary>
    public static IReadOnlyList<TrackPoint> Corners(double lat, double lng, double height, double rollDeg, double pitchDeg, double yawDeg,
        double hfovDeg, double vfovDeg)
    {
        if (height <= 0.5)
            return [];
        const double d2r = Math.PI / 180;
        var tx = Math.Tan(vfovDeg / 2 * d2r); // forward
        var ty = Math.Tan(hfovDeg / 2 * d2r); // right
        var (r, p, y) = (rollDeg * d2r, pitchDeg * d2r, yawDeg * d2r);
        var (cr, sr, cp, sp, cy, sy) = (Math.Cos(r), Math.Sin(r), Math.Cos(p), Math.Sin(p), Math.Cos(y), Math.Sin(y));

        var list = new List<TrackPoint>(4);
        foreach (var (fx, fy) in new[] { (tx, -ty), (tx, ty), (-tx, ty), (-tx, -ty) })
        {
            // body frame ray (forward, right, down), then body to north-east-down (ZYX Euler)
            var (bx, by, bz) = (fx, fy, 1.0);
            var n = cp * cy * bx + (sr * sp * cy - cr * sy) * by + (cr * sp * cy + sr * sy) * bz;
            var e = cp * sy * bx + (sr * sp * sy + cr * cy) * by + (cr * sp * sy - sr * cy) * bz;
            var d = -sp * bx + sr * cp * by + cr * cp * bz;
            var scale = d > 0.1 ? height / d : 10 * height / Math.Max(1e-6, Math.Sqrt(n * n + e * e));
            var (dn, de) = (n * scale, e * scale);
            const double earth = 6378137.0;
            var dLat = dn / earth * 180 / Math.PI;
            var dLng = de / (earth * Math.Cos(lat * d2r)) * 180 / Math.PI;
            list.Add(new TrackPoint(lat + dLat, lng + dLng));
        }

        return list;
    }

    /// <summary>A footprint from a CAMERA_FEEDBACK message (altitude relative to home as the height).</summary>
    public static PhotoFootprint From(MAVLink.mavlink_camera_feedback_t f) =>
        new(f.img_idx, f.lat / 1e7, f.lng / 1e7, f.alt_rel,
            Corners(f.lat / 1e7, f.lng / 1e7, f.alt_rel, f.roll, f.pitch, f.yaw, HorizontalFov, VerticalFov));
}

/// <summary>The photos of this flight, fed from the link's reader thread.</summary>
public sealed class PhotoStore
{
    private readonly object _gate = new();
    private readonly List<PhotoFootprint> _photos = new();
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public void OnPacket(MAVLink.MAVLinkMessage msg)
    {
        if (msg.msgid != (uint)MAVLink.MAVLINK_MSG_ID.CAMERA_FEEDBACK)
            return;
        var f = msg.ToStructure<MAVLink.mavlink_camera_feedback_t>();
        if (f.lat == 0 && f.lng == 0)
            return;
        lock (_gate)
            _photos.Add(CameraFootprints.From(f));
        Interlocked.Increment(ref _version);
    }

    public IReadOnlyList<PhotoFootprint> Snapshot()
    {
        lock (_gate)
            return _photos.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
            _photos.Clear();
        Interlocked.Increment(ref _version);
    }
}
