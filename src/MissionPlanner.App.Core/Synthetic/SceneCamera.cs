using System.Numerics;

namespace MissionPlanner.App.Core.Synthetic;

/// <summary>
/// Camera maths of the 3D view. World axes: x east, y up, z south (= −north), metres, centred on the
/// vehicle so floats stay precise. Matrices are System.Numerics (row vectors, v·M); uploaded as they
/// are they read as column-major in GLSL, so shaders use <c>u_mvp * vec4(p, 1)</c>.
/// </summary>
public static class SceneCamera
{
    /// <summary>
    /// Vertical field of view that matches the HUD's pitch ladder (h/50 pixels per degree at the
    /// centre): tan(fov/2) = (h/2) / (h/50 · 180/π).
    /// </summary>
    public static readonly float HudVerticalFov = 2 * MathF.Atan(50f * MathF.PI / 360f);

    /// <summary>NED vector to world.</summary>
    public static Vector3 FromNed(float north, float east, float down) => new(east, -down, -north);

    /// <summary>
    /// Body to world rotation for a roll / pitch / yaw in degrees (aerospace Z-Y-X): its rows are the
    /// world directions of the body's forward, right and down axes.
    /// </summary>
    public static Matrix4x4 BodyToWorld(double rollDeg, double pitchDeg, double yawDeg)
    {
        double r = rollDeg * Math.PI / 180, p = pitchDeg * Math.PI / 180, y = yawDeg * Math.PI / 180;
        double cr = Math.Cos(r), sr = Math.Sin(r), cp = Math.Cos(p), sp = Math.Sin(p), cy = Math.Cos(y), sy = Math.Sin(y);
        var fwd = FromNed((float)(cp * cy), (float)(cp * sy), (float)-sp);
        var right = FromNed((float)(sr * sp * cy - cr * sy), (float)(sr * sp * sy + cr * cy), (float)(sr * cp));
        var down = FromNed((float)(cr * sp * cy + sr * sy), (float)(cr * sp * sy - sr * cy), (float)(cr * cp));
        return new Matrix4x4(
            fwd.X, fwd.Y, fwd.Z, 0,
            right.X, right.Y, right.Z, 0,
            down.X, down.Y, down.Z, 0,
            0, 0, 0, 1);
    }

    /// <summary>The view from the vehicle (first person): looking along the body's forward axis, its "up" the body's up.</summary>
    public static Matrix4x4 FirstPerson(Vector3 eye, double rollDeg, double pitchDeg, double yawDeg, double tiltDeg = 0)
    {
        var b = BodyToWorld(rollDeg, pitchDeg, yawDeg);
        var forward = new Vector3(b.M11, b.M12, b.M13);
        var up = -new Vector3(b.M31, b.M32, b.M33);
        if (tiltDeg != 0)
        {
            // an FPV camera tilted up on the frame: turn about the body's right axis
            var t = (float)(tiltDeg * Math.PI / 180);
            var f2 = forward * MathF.Cos(t) + up * MathF.Sin(t);
            up = up * MathF.Cos(t) - forward * MathF.Sin(t);
            forward = f2;
        }

        return Matrix4x4.CreateLookAt(eye, eye + forward, up);
    }

    /// <summary>Vertical field of view (radians) for a horizontal one (degrees) at an aspect ratio.</summary>
    public static float VerticalFromHorizontal(double horizontalDeg, double aspect) =>
        (float)(2 * Math.Atan(Math.Tan(horizontalDeg * Math.PI / 360) / aspect));

    /// <summary>
    /// Where the chase camera sits: <paramref name="distance"/> metres from the target, looking
    /// along compass heading <paramref name="headingDeg"/>, <paramref name="elevationDeg"/> above
    /// the target. The horizon stays level.
    /// </summary>
    public static Vector3 ChasePosition(Vector3 target, double headingDeg, double elevationDeg, double distance)
    {
        double h = headingDeg * Math.PI / 180, e = elevationDeg * Math.PI / 180;
        // the camera looks along the heading, so it sits behind: opposite direction, raised
        var back = FromNed((float)(-Math.Cos(h) * Math.Cos(e)), (float)(-Math.Sin(h) * Math.Cos(e)), (float)-Math.Sin(e));
        return target + back * (float)distance;
    }

    public static Matrix4x4 Chase(Vector3 target, double headingDeg, double elevationDeg, double distance) =>
        Matrix4x4.CreateLookAt(ChasePosition(target, headingDeg, elevationDeg, distance), target, Vector3.UnitY);

    /// <summary>
    /// Perspective projection, image rows top-down when read back (y flipped), with the centre moved
    /// down by <paramref name="centreDownFraction"/> of the height (the HUD draws its horizon a little
    /// below the middle).
    /// </summary>
    public static Matrix4x4 Projection(float verticalFov, float aspect, float near, float far, float centreDownFraction = 0)
    {
        var p = Matrix4x4.CreatePerspectiveFieldOfView(verticalFov, aspect, near, far);
        // clip.y' = -(clip.y + d·clip.w), d = -2·fraction: shift down, then flip for top-down rows
        var n = Matrix4x4.Identity;
        n.M22 = -1;
        n.M42 = 2 * centreDownFraction;
        return p * n;
    }

    /// <summary>The image point (0..1, top-down) a world point lands on; null behind the camera.</summary>
    public static Vector2? Project(Vector3 world, Matrix4x4 view, Matrix4x4 projection)
    {
        var clip = Vector4.Transform(new Vector4(world, 1), view * projection);
        if (clip.W <= 0)
            return null;
        // projection already flips y, so NDC +y is down the image
        return new Vector2((clip.X / clip.W + 1) / 2, (clip.Y / clip.W + 1) / 2);
    }
}
