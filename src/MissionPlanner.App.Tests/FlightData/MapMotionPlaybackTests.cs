using System.Diagnostics;
using MissionPlanner.App.Core.Maps;
using MissionPlanner.App.Core.Synthetic;
using MissionPlanner.App.Core.ViewModels.FlightData;

namespace MissionPlanner.App.Tests.FlightData;

/// <summary>A telemetry log played on the DATA screen: the map's vehicle glides between its positions.</summary>
[TestClass]
public class MapMotionPlaybackTests
{
    private const double Lat0 = -35.36;
    private const double Lng0 = 149.16;
    private const double Speed = 20; // m/s north

    [ClassInitialize]
    public static void Init(TestContext _) =>
        MapTiles.Initialize(Path.Combine(TestEnvironment.DataRoot, "gmapcache") + Path.DirectorySeparatorChar);

    /// <summary>A plane flying north, its position logged twice a second (10 m apart), the attitude 4 times.</summary>
    private static string CreateFlight(double seconds)
    {
        var path = TlogWriter.TempPath("map-motion");
        using var w = new TlogWriter(path);
        w.Heartbeat(TlogWriter.T0, gcs: true);
        w.Heartbeat(TlogWriter.T0);
        var frame = new LocalFrame(Lat0, Lng0);
        for (var k = 0; k <= seconds * 4; k++)
        {
            var t = TlogWriter.T0.AddMilliseconds(250 * k);
            var ms = (uint)(250 * k);
            w.Write(t, MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t(ms, 0, 0, 0, 0, 0, 0));
            if (k % 2 == 0)
            {
                var (lat, lng) = frame.ToGeo(0, Speed * k * 0.25);
                w.Write(t, MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT, new MAVLink.mavlink_global_position_int_t(
                    ms, (int)Math.Round(lat * 1e7), (int)Math.Round(lng * 1e7), 100_000, 100_000, (short)(Speed * 100), 0, 0, 0));
            }

            if (k % 4 == 0)
                w.Heartbeat(t);
        }

        return path;
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task APlayedLogGlidesInsteadOfHopping()
    {
        var path = CreateFlight(6);
        var vm = new FlightDataViewModel(FakeUi.Services(out _)) { Zoom = 18 };
        await vm.Tlogs.LoadFileAsync(path);
        try
        {
            Assert.IsTrue(vm.Tlogs.IsPlaying);
            var frame = new LocalFrame(Lat0, Lng0);
            var samples = new List<(double Time, double North)>();
            var clock = Stopwatch.StartNew();
            var nextTick = 0.0;
            // the view's animation frames (60 a second) and the page's 20 Hz tick, for 3 s
            while (clock.Elapsed.TotalSeconds < 3.5)
            {
                var now = clock.Elapsed.TotalSeconds;
                if (now >= nextTick)
                {
                    vm.Tick();
                    nextTick += 0.05;
                }

                vm.AdvanceVehicleMotion();
                if (vm.MapModel.HasPosition)
                    samples.Add((clock.Elapsed.TotalSeconds, frame.ToLocal(vm.MapModel.Lat, vm.MapModel.Lng).North));
                await Task.Delay(15);
            }

            Assert.IsNotEmpty(samples, "the vehicle was never shown");
            // once under way (half a second in), over every tenth of a second it moves at about its
            // speed; drawn as logged it would stand still four tenths and hop 10 m in one
            var start = samples[0].Time + 0.5;
            var windows = 0;
            for (var t = start; t + 0.1 <= samples[^1].Time; t += 0.1)
            {
                var a = samples.First(s => s.Time >= t);
                var b = samples.First(s => s.Time >= t + 0.1);
                var speed = (b.North - a.North) / (b.Time - a.Time);
                Assert.IsTrue(speed is > Speed * 0.5 and < Speed * 1.5, $"{speed:0.0} m/s at {t:0.00} s");
                windows++;
            }

            Assert.IsGreaterThan(15, windows);
            Assert.IsTrue(vm.IsVehicleMoving, "worth drawing every frame");

            // the track is the logged positions, 10 m apart
            var track = vm.MapModel.Track;
            Assert.IsGreaterThanOrEqualTo(4, track.Count);
            for (var i = 1; i < track.Count; i++)
            {
                var d = frame.ToLocal(track[i].Lat, track[i].Lng).North - frame.ToLocal(track[i - 1].Lat, track[i - 1].Lng).North;
                Assert.AreEqual(10, d, 0.05, $"track point {i}");
            }
        }
        finally
        {
            vm.Tlogs.StopCommand.Execute(null);
            await vm.Tlogs.Engine.WaitForLoopAsync();
            File.Delete(path);
        }
    }
}
