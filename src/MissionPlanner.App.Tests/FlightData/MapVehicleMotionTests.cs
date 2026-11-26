using MissionPlanner.App.Core.Synthetic;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>The vehicle on the 2D map between position reports: fed timed samples, drawn at 60 fps.</summary>
[TestClass]
public class MapVehicleMotionTests
{
    private const double Fps = 60;
    private const double Mpp = 1.0; // metres per pixel, about zoom 17 here
    private static readonly LocalFrame Origin = new(-35.36, 149.16);

    private static (double East, double North) Local(MapVehicleMotion m) => Origin.ToLocal(m.Lat, m.Lng);

    private static void Report(MapVehicleMotion m, double north, double east, double velNorth, double velEast, double time)
    {
        var (lat, lng) = Origin.ToGeo(east, north);
        m.AddPosition(lat, lng, velNorth, velEast, time);
    }

    /// <summary>
    /// Flies <paramref name="northAt"/> (with its speed <paramref name="speedAt"/>) reported every
    /// <paramref name="every"/> seconds, and returns the north position drawn at each frame.
    /// </summary>
    private static List<(double Time, double North)> Fly(MapVehicleMotion m, double seconds, double every,
        Func<double, double> northAt, Func<double, double> speedAt)
    {
        var frames = new List<(double, double)>();
        var next = 0.0;
        for (var i = 0; i <= seconds * Fps; i++)
        {
            var t = i / Fps;
            if (t >= next - 1e-9)
            {
                Report(m, northAt(t), 0, speedAt(t), 0, t);
                next += every;
            }

            m.Advance(t, Mpp);
            frames.Add((t, Local(m).North));
        }

        return frames;
    }

    private static IEnumerable<double> Steps(List<(double Time, double North)> frames, double after) =>
        frames.Zip(frames.Skip(1)).Where(p => p.First.Time >= after).Select(p => p.Second.North - p.First.North);

    [TestMethod]
    public void GlidesBetweenReportsInsteadOfJumping()
    {
        // 20 m/s north, reported twice a second: drawn as is, the plane would hop 10 m every half second
        var m = new MapVehicleMotion();
        var frames = Fly(m, 3, 0.5, t => 20 * t, _ => 20);

        // once under way every frame moves about 20/60 m: no hop at a report, no stop before one
        foreach (var step in Steps(frames, after: 0.5))
            Assert.IsTrue(step is > 0.9 * 20 / Fps and < 1.1 * 20 / Fps, $"step {step:0.000} m");

        // and it keeps up: the ease costs about its time constant of travel
        var (time, north) = frames[^1];
        Assert.AreEqual(20 * time, north, 20 * 0.2, "keeps up with the reports");
        Assert.IsTrue(m.IsMoving);
    }

    [TestMethod]
    public void CorrectionsAreEasedOut()
    {
        // accelerating at 4 m/s²: each report is off from where the last one's speed predicted
        var m = new MapVehicleMotion();
        var frames = Fly(m, 3, 0.5, t => 2 * t * t, t => 4 * t);

        var steps = Steps(frames, after: 0.5).ToList();
        Assert.IsTrue(steps.All(s => s > 0), "never moves back");
        // drawn as reported it would hop 2 m and more at every report; eased, the 0.5 m by which each
        // report is off makes one frame's step a few centimetres longer
        for (var i = 1; i < steps.Count; i++)
            Assert.IsLessThan(0.1, Math.Abs(steps[i] - steps[i - 1]), $"step {i}: {steps[i - 1]:0.000} then {steps[i]:0.000} m");
        Assert.AreEqual(2 * 3 * 3, frames[^1].North, 3, "keeps up");
    }

    [TestMethod]
    public void AJumpIsShownAtOnce()
    {
        var m = new MapVehicleMotion();
        Report(m, 0, 0, 0, 0, 0);
        for (var t = 0.0; t < 0.5; t += 1 / Fps)
            m.Advance(t, Mpp);

        // a new SITL home a kilometre away: there at the next frame, not slid across the map
        Report(m, 1000, 0, 0, 0, 0.5);
        Assert.IsTrue(m.Advance(0.5 + 1 / Fps, Mpp));
        Assert.AreEqual(1000, Local(m).North, 0.01);
        for (var t = 0.5 + 2 / Fps; t < 1.0; t += 1 / Fps)
            m.Advance(t, Mpp);

        // a small correction glides
        Report(m, 1030, 0, 0, 0, 1.0);
        m.Advance(1.0 + 1 / Fps, Mpp);
        Assert.IsTrue(Local(m).North is > 1000 and < 1015, $"glides: {Local(m).North:0.0}");
        for (var t = 1.0 + 2 / Fps; t < 2; t += 1 / Fps)
            m.Advance(t, Mpp);
        Assert.AreEqual(1030, Local(m).North, 0.01);
    }

    [TestMethod]
    public void AFastVehicleAfterAGapIsNoJump()
    {
        // 50 m/s, then 1.8 s without reports: 90 m on, but only 40 m from where it was predicted
        var m = new MapVehicleMotion();
        Report(m, 0, 0, 50, 0, 0);
        for (var t = 0.0; t < 1.8; t += 1 / Fps)
            m.Advance(t, Mpp);
        var before = Local(m).North;
        Assert.AreEqual(50, before, 8, "predicted for a second, then held");

        Report(m, 90, 0, 50, 0, 1.8);
        m.Advance(1.8 + 1 / Fps, Mpp);
        Assert.IsTrue(Local(m).North - before is > 0 and < 15, $"glides on from {before:0.0}: {Local(m).North:0.0}");
    }

    [TestMethod]
    public void TheHeadingTurnsTheShortWayRoundThroughNorth()
    {
        // turning right at 20 °/s through north, heading reported 4 times a second
        var m = new MapVehicleMotion();
        Report(m, 0, 0, 0, 0, 0);
        var headings = new List<double>();
        var next = 0.0;
        for (var i = 0; i <= 2 * Fps; i++)
        {
            var t = i / Fps;
            if (t >= next - 1e-9)
            {
                m.AddHeading(PoseTracker.Norm360(340 + 20 * t), t);
                next += 0.25;
            }

            m.Advance(t, Mpp);
            headings.Add(m.Heading);
        }

        for (var i = 1; i < headings.Count; i++)
        {
            var step = PoseTracker.WrapDeg(headings[i] - headings[i - 1]);
            Assert.IsTrue(step is > -0.5 and < 2, $"frame {i}: {headings[i - 1]:0.0} -> {headings[i]:0.0}");
            Assert.IsTrue(headings[i] is >= 0 and < 360);
        }

        Assert.IsTrue(headings.Any(h => h > 355) && headings.Any(h => h < 5), "went through north");
        Assert.AreEqual(20, PoseTracker.WrapDeg(headings[^1]), 3, "ends near 020");
    }

    [TestMethod]
    public void AFirstHeadingAfterThePositionIsNoTurn()
    {
        var m = new MapVehicleMotion();
        Report(m, 0, 0, 0, 0, 0);
        m.Advance(0, Mpp);
        Assert.AreEqual(0, m.Heading);

        // ATTITUDE arrives a moment after GLOBAL_POSITION_INT: no spin read from "0 then 200"
        m.AddHeading(200, 0.1);
        for (var t = 0.1; t < 0.6; t += 1 / Fps)
        {
            m.Advance(t, Mpp);
            Assert.AreEqual(200, m.Heading, 0.01, $"at {t:0.00} s");
        }
    }

    [TestMethod]
    public void AStillVehicleIsNotRedrawn()
    {
        var m = new MapVehicleMotion();
        Report(m, 5, 5, 0, 0, 0);
        m.AddHeading(90, 0);
        m.SetBearings(0, 0, 0);
        Assert.IsTrue(m.Advance(0, Mpp), "the first position is drawn");
        var (east, north) = Local(m);

        // the same report again and again (a hovering copter's packets): nothing to draw
        for (var i = 1; i <= 3 * Fps; i++)
        {
            var t = i / Fps;
            if (i % 6 == 0)
            {
                Report(m, 5, 5, 0, 0, t);
                m.AddHeading(90, t);
            }

            Assert.IsFalse(m.Advance(t, Mpp), $"redrawn at {t:0.00} s");
            Assert.IsFalse(m.IsMoving, $"moving at {t:0.00} s");
        }

        Assert.AreEqual(east, Local(m).East, 1e-6);
        Assert.AreEqual(north, Local(m).North, 1e-6);
        Assert.AreEqual(90, m.Heading, 1e-6);
    }

    [TestMethod]
    public void WhenReportsStopItSettlesWhereItWasLastReported()
    {
        // a log paused, a link lost: 20 m/s north, the last report at 1 s
        var m = new MapVehicleMotion();
        Fly(m, 1, 0.5, t => 20 * t, _ => 20);
        Assert.IsTrue(m.IsMoving);

        var redraws = 0;
        for (var t = 1 + 1 / Fps; t < 5; t += 1 / Fps)
        {
            if (m.Advance(t, Mpp))
                redraws++;
            if (t is > 1.5 and < 1.9)
                Assert.IsTrue(Local(m).North > 25, "keeps going for a while");
        }

        Assert.AreEqual(20, Local(m).North, 0.05, "back where it was last reported");
        Assert.IsFalse(m.IsMoving, "at rest");
        Assert.IsFalse(m.Advance(5.5, Mpp), "nothing left to draw");
        Assert.IsGreaterThan(0, redraws);
    }

    [TestMethod]
    public void SlowOnScreenIsNotDrawnEveryFrame()
    {
        // 20 m/s at 100 m a pixel (zoomed far out): 0.2 px a second
        var m = new MapVehicleMotion();
        Report(m, 0, 0, 20, 0, 0);
        Assert.IsTrue(m.Advance(0, 100));
        var redraws = 0;
        for (var i = 1; i <= Fps; i++)
        {
            if (m.Advance(i / Fps, 100))
                redraws++;
        }

        Assert.AreEqual(0, redraws, "less than half a pixel in a second");
        Assert.IsFalse(m.IsMoving, "not worth every frame");

        // the same flight zoomed in is
        var near = new MapVehicleMotion();
        Fly(near, 1, 0.5, t => 20 * t, _ => 20);
        Assert.IsTrue(near.IsMoving);
    }

    [TestMethod]
    public void ClearingHidesTheVehicleOnce()
    {
        var m = new MapVehicleMotion();
        Report(m, 0, 0, 0, 0, 0);
        Assert.IsTrue(m.Advance(0, Mpp));
        Assert.IsTrue(m.HasPosition);

        m.Clear();
        Assert.IsFalse(m.HasReports);
        Assert.IsTrue(m.Advance(0.1, Mpp), "the vehicle goes");
        Assert.IsFalse(m.HasPosition);
        Assert.IsFalse(m.Advance(0.2, Mpp));
        Assert.IsFalse(m.IsMoving);
    }

    [TestMethod]
    public void BearingsAreEasedTheShortWayRound()
    {
        var m = new MapVehicleMotion();
        Report(m, 0, 0, 0, 0, 0);
        m.SetBearings(350, 90, 180);
        m.Advance(0, Mpp);
        Assert.AreEqual(350, m.GroundCourse, 1e-9);

        m.SetBearings(10, 90, 180);
        for (var t = 1 / Fps; t < 1; t += 1 / Fps)
        {
            m.Advance(t, Mpp);
            Assert.IsTrue(m.GroundCourse is > 349 or < 10.01, $"course {m.GroundCourse:0.0} went the long way");
        }

        Assert.AreEqual(10, m.GroundCourse, 0.01);
        Assert.AreEqual(90, m.NavBearing, 1e-9);
    }
}
