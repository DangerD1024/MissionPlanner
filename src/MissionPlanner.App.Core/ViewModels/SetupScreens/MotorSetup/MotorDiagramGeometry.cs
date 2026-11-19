using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.MotorSetup;

/// <summary>Where one motor is drawn: centre, radius and the arm end it hangs on (coaxial pairs share an arm).</summary>
public readonly record struct MotorSpot(int Motor, double X, double Y, double Radius, double ArmX, double ArmY);

/// <summary>
/// Screen positions of a frame's motors, front up, shared by the drawing and the click test so the
/// two always agree. Coaxial motors (same position) are placed side by side across their arm.
/// </summary>
public sealed class MotorDiagramGeometry
{
    private MotorDiagramGeometry(double cx, double cy, double bodyRadius, IReadOnlyList<MotorSpot> spots)
    {
        CenterX = cx;
        CenterY = cy;
        BodyRadius = bodyRadius;
        Spots = spots;
    }

    public double CenterX { get; }

    public double CenterY { get; }

    public double BodyRadius { get; }

    public IReadOnlyList<MotorSpot> Spots { get; }

    public static MotorDiagramGeometry Empty { get; } = new(0, 0, 0, []);

    /// <summary>Lays out the frame in a <paramref name="width"/> × <paramref name="height"/> box, leaving <paramref name="margin"/> around for labels.</summary>
    public static MotorDiagramGeometry Compute(MotorLayout? layout, double width, double height, double margin)
    {
        var size = Math.Min(width, height);
        if (layout == null || layout.Motors.Count == 0 || size < 40)
            return Empty;

        var cx = width / 2;
        var cy = height / 2;
        var reach = Math.Max(0.05, layout.Motors.Max(m => Math.Sqrt(m.X * m.X + m.Y * m.Y)));
        var groups = layout.Motors.GroupBy(m => (Math.Round(m.X, 2), Math.Round(m.Y, 2))).ToList();
        var coaxial = groups.Any(g => g.Count() > 1);
        var motorR = Math.Clamp(size * (layout.Motors.Count > 8 ? 0.07 : 0.095), 10, 34);
        var scale = Math.Max(1, (size / 2 - motorR * (coaxial ? 1.3 : 1) - margin) / reach);
        var r = coaxial ? motorR * 0.7 : motorR;

        var spots = new List<MotorSpot>();
        foreach (var g in groups)
        {
            var list = g.OrderBy(m => m.Number).ToList();
            var ax = cx + list[0].X * scale;
            var ay = cy + list[0].Y * scale;
            if (list.Count == 1)
            {
                spots.Add(new MotorSpot(list[0].Number, ax, ay, r, ax, ay));
                continue;
            }

            var dx = ax - cx;
            var dy = ay - cy;
            var len = Math.Sqrt(dx * dx + dy * dy);
            // across the arm; a coaxial pair in the centre spreads sideways
            var (px, py) = len < 1e-6 ? (1.0, 0.0) : (-dy / len, dx / len);
            for (var i = 0; i < list.Count; i++)
            {
                var off = (i - (list.Count - 1) / 2.0) * r * 2.2;
                spots.Add(new MotorSpot(list[i].Number, ax + px * off, ay + py * off, r, ax, ay));
            }
        }

        return new MotorDiagramGeometry(cx, cy, Math.Max(6, size * 0.08), spots);
    }

    /// <summary>The motor under a point (within a little more than its radius), nearest first.</summary>
    public int? HitTest(double x, double y)
    {
        MotorSpot? best = null;
        var bestD = double.MaxValue;
        foreach (var s in Spots)
        {
            var d = Math.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y));
            if (d <= s.Radius * 1.25 && d < bestD)
            {
                best = s;
                bestD = d;
            }
        }

        return best?.Motor;
    }

    public MotorSpot? SpotOf(int motor)
    {
        foreach (var s in Spots)
        {
            if (s.Motor == motor)
                return s;
        }

        return null;
    }
}
