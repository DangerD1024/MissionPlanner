using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

namespace MissionPlanner.App.Tests.SetupScreens.Rtk;

/// <summary>Collects what the injector would send to the vehicle.</summary>
internal sealed class CollectingSink : IRtcmSink
{
    public ConcurrentQueue<MAVLink.mavlink_gps_rtcm_data_t> Packets { get; } = new();

    public bool Connected { get; set; } = true;

    public bool Send(MAVLink.mavlink_gps_rtcm_data_t packet)
    {
        if (!Connected)
            return false;
        Packets.Enqueue(packet);
        return true;
    }

    public List<byte> Reassembled()
    {
        var r = new ArduPilotRtcmReassembler();
        foreach (var p in Packets)
            r.Handle(p);
        return r.Injected;
    }
}

/// <summary>A TCP server streaming a fixed byte sequence; optionally drops each client after one burst.</summary>
internal sealed class RtcmTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public RtcmTcpServer(byte[] burst, bool dropAfterBurst)
    {
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                    var s = client.GetStream();
                    do
                    {
                        await s.WriteAsync(burst, _stop.Token);
                        await Task.Delay(100, _stop.Token);
                    }
                    while (!dropAfterBurst && !_stop.IsCancellationRequested);
                }
                catch (Exception)
                {
                    if (_stop.IsCancellationRequested)
                        return;
                }
            }
        });
    }

    private int _connections;

    public int Connections => Volatile.Read(ref _connections);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }
}

[TestClass]
public class RtkInjectorTests
{
    private static readonly byte[] Base = RtcmFrameBuilder.Type1005(-35.363261, 149.165230, 584.25);
    private static readonly byte[] Gps = RtcmFrameBuilder.Msm4(1074, [3, 7, 12, 30], [45, 38, 27, 50]);
    private static readonly byte[] Big = RtcmFrameBuilder.Padded(1077, 1000);

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NtripFramesReachTheSinkIntactAndAreCounted()
    {
        var bursts = new[] { Base, Gps, Big };
        var i = 0;
        await using var caster = new FakeCaster("M", () => bursts[i++ % 3], chunked: true);
        var sink = new CollectingSink();
        await using var injector = new RtkInjector(sink);
        RtcmBasePosition? seenBase = null;
        injector.BasePositionReceived += (_, p) => seenBase = p;

        injector.Start(new NtripRtcmSource(new NtripEndpoint("127.0.0.1", caster.Port, "M"), null));
        Assert.IsTrue(await Wait.Until(() => injector.GetStatus().FramesForwarded >= 6, TimeSpan.FromSeconds(10)));
        await injector.StopAsync();

        var st = injector.GetStatus();
        Assert.AreEqual(RtkState.Stopped, st.State);
        var frames = st.FramesForwarded;
        var expected = Enumerable.Range(0, (int)frames).SelectMany(n => bursts[n % 3]).ToList();
        CollectionAssert.AreEqual(expected, sink.Reassembled(), "the vehicle rebuilds every frame byte for byte");
        Assert.AreEqual(expected.Count, st.BytesForwarded);
        Assert.AreEqual(sink.Packets.Count, st.PacketsSent);
        Assert.IsGreaterThanOrEqualTo(expected.Count, st.BytesIn);

        var stats = injector.Statistics.Snapshot(DateTime.UtcNow);
        CollectionAssert.AreEqual(new[] { 1005, 1074, 1077 }, stats.Messages.Select(m => m.Type).ToArray());
        Assert.IsNotNull(seenBase);
        Assert.AreEqual(149.165230, seenBase.Lng, 1e-7);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FramesWithoutAVehicleAreCountedNotSent()
    {
        await using var server = new RtcmTcpServer(Base.Concat(Gps).ToArray(), dropAfterBurst: false);
        var sink = new CollectingSink { Connected = false };
        await using var injector = new RtkInjector(sink);
        injector.Start(new TcpRtcmSource("127.0.0.1", server.Port));

        Assert.IsTrue(await Wait.Until(() => injector.GetStatus().FramesNotSent >= 2, TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0, injector.GetStatus().FramesForwarded);
        Assert.AreEqual(RtkState.Running, injector.GetStatus().State);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DroppedConnectionsAreReopened()
    {
        RtkInjector.ReconnectDelay = TimeSpan.FromMilliseconds(100);
        try
        {
            await using var server = new RtcmTcpServer(Base, dropAfterBurst: true);
            var sink = new CollectingSink();
            await using var injector = new RtkInjector(sink);
            injector.Start(new TcpRtcmSource("127.0.0.1", server.Port));

            Assert.IsTrue(await Wait.Until(() => server.Connections >= 3 && injector.GetStatus().FramesForwarded >= 3, TimeSpan.FromSeconds(15)));
            Assert.IsGreaterThanOrEqualTo(2, injector.GetStatus().Reconnects);
        }
        finally
        {
            RtkInjector.ReconnectDelay = TimeSpan.FromSeconds(3);
        }
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ABadMountpointStopsWithoutRetrying()
    {
        await using var caster = new FakeCaster("GOOD", () => [], chunked: false);
        await using var injector = new RtkInjector(new CollectingSink());
        injector.Start(new NtripRtcmSource(new NtripEndpoint("127.0.0.1", caster.Port, "WRONG"), null));

        Assert.IsTrue(await Wait.Until(() => injector.GetStatus().State == RtkState.Failed, TimeSpan.FromSeconds(10)));
        StringAssert.Contains(injector.GetStatus().Message, "WRONG");
        await Task.Delay(300);
        Assert.HasCount(1, caster.Requests);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task GpsRtcmDataReachesAVehicleThroughTheSession()
    {
        // vehicle on TCP loopback
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var ui = new SetupTestUi();
        var services = ui.CreateServices();
        var connecting = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        using var vehicleSide = await accept;
        listener.Stop();
        await using var vehicle = new FakeVehicle(vehicleSide.GetStream(), 0);
        await connecting;

        try
        {
            var stream = Base.Concat(Gps).Concat(Big).ToArray();
            await using var server = new RtcmTcpServer(stream, dropAfterBurst: false);
            await using var injector = new RtkInjector(new SessionRtcmSink(services.Session));
            injector.Start(new TcpRtcmSource("127.0.0.1", server.Port));

            static IEnumerable<MAVLink.mavlink_gps_rtcm_data_t> Rtcm(FakeVehicle v) => v.Received
                .Where(m => m.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GPS_RTCM_DATA)
                .Select(m => m.ToStructure<MAVLink.mavlink_gps_rtcm_data_t>());

            // 3 frames per burst: 1 + 1 + 6 packets (the 1006-byte frame needs two blocks)
            Assert.IsTrue(await Wait.Until(() => Rtcm(vehicle).Count() >= 16, TimeSpan.FromSeconds(20)), "GPS_RTCM_DATA arrives");
            await injector.StopAsync();
            await Task.Delay(300);

            var packets = Rtcm(vehicle).ToList();
            var reassembler = new ArduPilotRtcmReassembler();
            foreach (var p in packets)
                reassembler.Handle(p);

            Assert.AreEqual(0, reassembler.Discarded);
            var injected = reassembler.Injected.ToArray();
            Assert.AreEqual(0, injected.Length % stream.Length, "whole bursts only");
            for (var i = 0; i < injected.Length; i += stream.Length)
                CollectionAssert.AreEqual(stream, injected.Skip(i).Take(stream.Length).ToArray());
            Assert.AreEqual(injector.GetStatus().PacketsSent, packets.Count);
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PageShowsStreamAndSavesSettings()
    {
        var ui = new SetupTestUi();
        var vm = new GpsInjectViewModel(ui.CreateServices());
        using var probe = new UdpClient(0);
        var udpPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();

        vm.SelectedSource = vm.SourceOptions.Single(o => o.Kind == RtcmSourceKind.UdpListen);
        vm.UdpPort = udpPort;
        vm.LogRaw = false;
        Assert.AreEqual("UdpListen", MissionPlanner.Utilities.Settings.Instance["next_rtk_source"]);
        Assert.AreEqual(udpPort.ToString(), MissionPlanner.Utilities.Settings.Instance["next_rtk_udp_port"]);

        vm.OnShown();
        await vm.ToggleCommand.ExecuteAsync(null);
        Assert.IsTrue(vm.IsRunning);
        Assert.AreEqual("Stop", vm.ConnectText);

        using var sender = new UdpClient();
        var glo = RtcmFrameBuilder.Msm4(1084, [1, 2], [40, 20]);
        var ok = await Wait.Until(() => vm.Messages.Count >= 3, TimeSpan.FromSeconds(10), () =>
        {
            var d = Base.Concat(Gps).Concat(glo).ToArray();
            sender.Send(d, d.Length, new IPEndPoint(IPAddress.Loopback, udpPort));
            vm.UpdateStatus();
        });
        Assert.IsTrue(ok, "messages listed");

        CollectionAssert.AreEqual(new[] { 1005, 1074, 1084 }, vm.Messages.Select(m => m.Type).ToArray());
        Assert.IsTrue(vm.Indicators.Single(x => x.Name == "Base").IsActive);
        Assert.IsTrue(vm.Indicators.Single(x => x.Name == "GPS").IsActive);
        Assert.AreEqual("4 sats", vm.Indicators.Single(x => x.Name == "GPS").Detail);
        Assert.IsTrue(vm.Indicators.Single(x => x.Name == "GLONASS").IsActive);
        Assert.IsFalse(vm.Indicators.Single(x => x.Name == "Galileo").IsActive);
        Assert.IsTrue(vm.HasBasePosition);
        StringAssert.Contains(vm.BasePositionText, "-35.3632610");
        CollectionAssert.AreEqual(new[] { "G3", "G7", "G12", "G30", "R1", "R2" }, vm.Satellites.Select(s => s.Label).ToArray());
        Assert.IsTrue(vm.Satellites.Single(s => s.Label == "R2").IsWeak);
        Assert.IsTrue(vm.Satellites.Single(s => s.Label == "G3").IsGood);
        StringAssert.Contains(vm.ErrorsText, "not sent (no vehicle link)");
        Assert.AreEqual("No vehicle connected", vm.VehicleGpsText);

        await vm.ToggleCommand.ExecuteAsync(null);
        Assert.IsFalse(vm.IsRunning);
        Assert.AreEqual("Connect", vm.ConnectText);
    }

    [TestMethod]
    public void PastedNtripUrlFillsTheFields()
    {
        var vm = new GpsInjectViewModel(new SetupTestUi().CreateServices());
        vm.NtripHost = "http://bob:secret@caster.example.org:2102/NEAR";
        Assert.AreEqual("caster.example.org", vm.NtripHost);
        Assert.AreEqual(2102, vm.NtripPort);
        Assert.AreEqual("NEAR", vm.NtripMountpoint);
        Assert.AreEqual("bob", vm.NtripUsername);
        Assert.AreEqual("secret", vm.NtripPassword);
        Assert.AreEqual("caster.example.org", MissionPlanner.Utilities.Settings.Instance["next_rtk_ntrip_host"]);
        Assert.AreEqual("NEAR", MissionPlanner.Utilities.Settings.Instance["next_rtk_ntrip_mount"]);
    }

    [TestMethod]
    public void SavedBasePositionsRoundTrip()
    {
        var path = Path.Combine(TestEnvironment.DataRoot, "bases-" + Guid.NewGuid().ToString("N") + ".json");
        SavedBasePositions.Save([new SavedBasePosition("Field", -35.1, 149.2, 600.5)], path);
        var back = SavedBasePositions.Load(path);
        Assert.HasCount(1, back);
        Assert.AreEqual(new SavedBasePosition("Field", -35.1, 149.2, 600.5), back[0]);
        Assert.IsEmpty(SavedBasePositions.Load(path + ".missing"));
    }

    [TestMethod]
    public void RateTrackerAveragesOverASecond()
    {
        var r = new RateTracker();
        var t = DateTime.UtcNow;
        Assert.AreEqual(0, r.Update(1000, t));
        Assert.AreEqual(0, r.Update(1500, t.AddSeconds(0.5)), "not a full second yet");
        Assert.AreEqual(2000, r.Update(3000, t.AddSeconds(1)), 1e-9);
        Assert.AreEqual(2000, r.Update(3100, t.AddSeconds(1.2)), 1e-9);
    }
}
