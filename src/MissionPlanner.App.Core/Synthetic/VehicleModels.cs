using System.Numerics;

namespace MissionPlanner.App.Core.Synthetic;

/// <summary>
/// Simple vehicle models for the chase view, in body axes (x forward, y right, z down, metres):
/// interleaved position, normal, colour. The front is red-tipped and the wings carry navigation
/// colours (red left, green right) so the attitude reads at a glance.
/// </summary>
public static class VehicleModels
{
    public const int FloatsPerVertex = 9;

    private static readonly Vector3 Body = new(0.82f, 0.84f, 0.88f);
    private static readonly Vector3 Dark = new(0.22f, 0.24f, 0.27f);
    private static readonly Vector3 Red = new(0.9f, 0.18f, 0.16f);
    private static readonly Vector3 Green = new(0.2f, 0.78f, 0.25f);
    private static readonly Vector3 Rotor = new(0.55f, 0.58f, 0.62f);

    public static float[] For(VehicleShape shape) => shape switch
    {
        VehicleShape.Plane => Plane(),
        VehicleShape.Rover => Rover(),
        _ => Multirotor(),
    };

    /// <summary>An X quad, 1 m across: body, arms (front pair red), rotor discs.</summary>
    public static float[] Multirotor()
    {
        var m = new List<float>();
        Box(m, new Vector3(0, 0, 0), new Vector3(0.32f, 0.22f, 0.12f), Body);
        Box(m, new Vector3(0.2f, 0, -0.02f), new Vector3(0.08f, 0.1f, 0.06f), Red); // nose
        foreach (var (sx, sy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
        {
            var tip = new Vector3(0.35f * sx, 0.35f * sy, 0);
            Beam(m, Vector3.Zero, tip, 0.05f, sx > 0 ? Red : Dark);
            Disc(m, tip + new Vector3(0, 0, -0.06f), 0.2f, 12, Rotor);
        }

        return m.ToArray();
    }

    /// <summary>A small plane, 1.6 m span: fuselage, wing (red left / green right tips), tail.</summary>
    public static float[] Plane()
    {
        var m = new List<float>();
        Box(m, new Vector3(0, 0, 0), new Vector3(1.2f, 0.14f, 0.14f), Body);
        Box(m, new Vector3(0.62f, 0, 0), new Vector3(0.06f, 0.12f, 0.12f), Red); // nose
        Box(m, new Vector3(0.08f, 0, -0.06f), new Vector3(0.26f, 1.4f, 0.03f), Body); // wing
        Box(m, new Vector3(0.08f, -0.74f, -0.06f), new Vector3(0.26f, 0.08f, 0.035f), Red);
        Box(m, new Vector3(0.08f, 0.74f, -0.06f), new Vector3(0.26f, 0.08f, 0.035f), Green);
        Box(m, new Vector3(-0.52f, 0, -0.04f), new Vector3(0.16f, 0.5f, 0.025f), Body); // tailplane
        Box(m, new Vector3(-0.52f, 0, -0.18f), new Vector3(0.18f, 0.025f, 0.24f), Dark); // fin
        return m.ToArray();
    }

    /// <summary>A rover: body and four wheels.</summary>
    public static float[] Rover()
    {
        var m = new List<float>();
        Box(m, new Vector3(0, 0, -0.12f), new Vector3(0.8f, 0.5f, 0.18f), Body);
        Box(m, new Vector3(0.36f, 0, -0.16f), new Vector3(0.08f, 0.3f, 0.08f), Red);
        foreach (var (sx, sy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
            Box(m, new Vector3(0.28f * sx, 0.3f * sy, 0), new Vector3(0.2f, 0.1f, 0.2f), Dark);
        return m.ToArray();
    }

    private static void Box(List<float> m, Vector3 c, Vector3 size, Vector3 color)
    {
        var h = size / 2;
        // six faces: normal, then the two in-plane axes
        Face(m, c, h, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, color);
        Face(m, c, h, -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, color);
        Face(m, c, h, Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, color);
        Face(m, c, h, -Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, color);
        Face(m, c, h, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, color);
        Face(m, c, h, -Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, color);
    }

    private static void Face(List<float> m, Vector3 c, Vector3 h, Vector3 n, Vector3 a, Vector3 b, Vector3 color)
    {
        var centre = c + n * h;
        var ea = a * h;
        var eb = b * h;
        var p0 = centre - ea - eb;
        var p1 = centre + ea - eb;
        var p2 = centre + ea + eb;
        var p3 = centre - ea + eb;
        Tri(m, p0, p1, p2, n, color);
        Tri(m, p0, p2, p3, n, color);
    }

    /// <summary>A square beam from a to b, in the horizontal plane.</summary>
    private static void Beam(List<float> m, Vector3 a, Vector3 b, float width, Vector3 color)
    {
        var dir = Vector3.Normalize(b - a);
        var side = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitZ)) * (width / 2);
        var up = new Vector3(0, 0, width / 2);
        Vector3[] q = [a - side - up, a + side - up, a + side + up, a - side + up, b - side - up, b + side - up, b + side + up, b - side + up];
        void Quad(int i0, int i1, int i2, int i3)
        {
            var n = Vector3.Normalize(Vector3.Cross(q[i1] - q[i0], q[i2] - q[i0]));
            Tri(m, q[i0], q[i1], q[i2], n, color);
            Tri(m, q[i0], q[i2], q[i3], n, color);
        }

        Quad(0, 1, 5, 4);
        Quad(1, 2, 6, 5);
        Quad(2, 3, 7, 6);
        Quad(3, 0, 4, 7);
        Quad(4, 5, 6, 7);
    }

    private static void Disc(List<float> m, Vector3 c, float r, int segments, Vector3 color)
    {
        for (var i = 0; i < segments; i++)
        {
            var a0 = i * 2 * MathF.PI / segments;
            var a1 = (i + 1) * 2 * MathF.PI / segments;
            var p0 = c + new Vector3(MathF.Cos(a0) * r, MathF.Sin(a0) * r, 0);
            var p1 = c + new Vector3(MathF.Cos(a1) * r, MathF.Sin(a1) * r, 0);
            Tri(m, c, p0, p1, -Vector3.UnitZ, color);
        }
    }

    private static void Tri(List<float> m, Vector3 a, Vector3 b, Vector3 c, Vector3 n, Vector3 color)
    {
        foreach (var p in new[] { a, b, c })
        {
            m.Add(p.X);
            m.Add(p.Y);
            m.Add(p.Z);
            m.Add(n.X);
            m.Add(n.Y);
            m.Add(n.Z);
            m.Add(color.X);
            m.Add(color.Y);
            m.Add(color.Z);
        }
    }
}
