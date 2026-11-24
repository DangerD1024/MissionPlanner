using System.Collections.Concurrent;
using MissionPlanner.App.Core.ViewModels.Tools;
using MissionPlanner.App.Tests.FlightData;

namespace MissionPlanner.App.Tests.Tools;

/// <summary>TOOLS > Remote DataFlash logger: START / blocks / acks / STOP and the .bin it writes.</summary>
[TestClass]
public class RemoteLoggerTests
{
    private const uint Start = (uint)MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_COMMANDS.MAV_REMOTE_LOG_DATA_BLOCK_START;
    private const uint Stop = (uint)MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_COMMANDS.MAV_REMOTE_LOG_DATA_BLOCK_STOP;

    private sealed class FakeLogVehicle : IRemoteLogTransport
    {
        public ConcurrentQueue<(uint Seqno, MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_STATUSES Status)> Sent { get; } = new();

        public Action<uint, byte[]>? Handler { get; private set; }

        public void SendStatus(uint seqno, MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_STATUSES status) => Sent.Enqueue((seqno, status));

        public IDisposable Subscribe(Action<uint, byte[]> handler)
        {
            Handler = handler;
            return new Off(() => Handler = null);
        }

        public void Send(uint seqno, byte fill) => Handler?.Invoke(seqno, Enumerable.Repeat(fill, RemoteLogWriter.BlockSize).ToArray());

        private sealed class Off(Action a) : IDisposable
        {
            public void Dispose() => a();
        }
    }

    private static string TempBin() => Path.Combine(TestEnvironment.DataRoot, "remote-" + Guid.NewGuid().ToString("N") + ".bin");

    [TestMethod]
    public void BlocksAreWrittenInPlaceAndAcknowledged()
    {
        var vehicle = new FakeLogVehicle();
        var path = TempBin();
        using (var writer = new RemoteLogWriter(vehicle, path))
        {
            writer.Start();
            Assert.IsNotNull(vehicle.Handler, "subscribed to REMOTE_LOG_DATA_BLOCK");
            Assert.IsTrue(vehicle.Sent.TryDequeue(out var first));
            Assert.AreEqual(Start, first.Seqno, "START asks the vehicle to stream");

            vehicle.Send(0, 0xA0);
            vehicle.Send(2, 0xA2); // block 1 lost for now
            vehicle.Send(1, 0xA1); // resent by the vehicle
            vehicle.Send(2, 0xA2); // a duplicate lands in the same place

            Assert.AreEqual(4, writer.Blocks);
            Assert.AreEqual(2, writer.LastSeqno);
            Assert.AreEqual(3L * RemoteLogWriter.BlockSize, writer.Length);
            CollectionAssert.AreEqual(new uint[] { 0, 2, 1, 2 }, vehicle.Sent.Select(s => s.Seqno).ToArray(), "every block is acknowledged");
            Assert.IsTrue(vehicle.Sent.All(s => s.Status == MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_STATUSES.MAV_REMOTE_LOG_DATA_BLOCK_ACK));
            while (vehicle.Sent.TryDequeue(out _))
            {
            }

            writer.Stop();
            Assert.IsTrue(vehicle.Sent.TryDequeue(out var last));
            Assert.AreEqual(Stop, last.Seqno, "STOP ends the stream");
            Assert.IsNull(vehicle.Handler, "unsubscribed");
            vehicle.Send(3, 0xFF); // ignored after stop
        }

        var bytes = File.ReadAllBytes(path);
        Assert.AreEqual(3 * RemoteLogWriter.BlockSize, bytes.Length);
        Assert.IsTrue(bytes.Take(200).All(b => b == 0xA0));
        Assert.IsTrue(bytes.Skip(200).Take(200).All(b => b == 0xA1));
        Assert.IsTrue(bytes.Skip(400).All(b => b == 0xA2));
    }

    [TestMethod]
    public void TheFileIsALogTheLogToolsCanRead()
    {
        // a real DataFlash stream (an FMT record describing itself) split into 200-byte blocks
        var fmt = new byte[89];
        fmt[0] = 0xA3;
        fmt[1] = 0x95;
        fmt[2] = 0x80; // FMT
        fmt[3] = 0x80; // type
        fmt[4] = 89; // length
        "FMT"u8.CopyTo(fmt.AsSpan(5));
        "BBnNZ"u8.CopyTo(fmt.AsSpan(9));
        "Type,Length,Name,Format,Columns"u8.CopyTo(fmt.AsSpan(25));
        var stream = Enumerable.Range(0, 10).SelectMany(_ => fmt).ToArray(); // 890 bytes
        var padded = new byte[(stream.Length + 199) / 200 * 200];
        stream.CopyTo(padded, 0);

        var vehicle = new FakeLogVehicle();
        var path = TempBin();
        using (var writer = new RemoteLogWriter(vehicle, path))
        {
            writer.Start();
            for (uint i = 0; i < padded.Length / 200; i++)
                vehicle.Handler!(i, padded.AsSpan((int)i * 200, 200).ToArray());
        }

        using var log = new MissionPlanner.Utilities.DFLogBuffer(path);
        Assert.IsTrue(log.Count >= 10, $"the DataFlash reader finds the records ({log.Count})");
    }

    [TestMethod]
    public void PageStartsStopsAndOffersTheFile()
    {
        var services = FakeUi.Services(out var ui);
        var vm = new RemoteLoggerViewModel(services);

        vm.StartStopCommand.Execute(null);
        Assert.IsFalse(vm.IsRunning, "not connected");
        Assert.IsTrue(vm.StatusIsError);

        var vehicle = new FakeLogVehicle();
        var path = TempBin();
        Assert.IsTrue(vm.Start(vehicle, path));
        Assert.IsTrue(vm.IsRunning);
        Assert.AreEqual(path, vm.FilePath);
        Assert.IsFalse(vm.CanOpen, "the file opens once the log is stopped");

        vehicle.Send(0, 1);
        vehicle.Send(1, 2);
        vm.Tick();
        StringAssert.StartsWith(vm.Progress, "2 blocks, 400 bytes");

        vm.StartStopCommand.Execute(null);
        Assert.IsFalse(vm.IsRunning);
        Assert.IsFalse(vm.StatusIsError, vm.Status);
        StringAssert.Contains(vm.Status, "400 bytes");
        Assert.IsTrue(vm.CanOpen);
        Assert.AreEqual(400, new FileInfo(path).Length);

        vm.OpenInBrowserCommand.Execute(null);
        Assert.AreEqual(1, ui.Shown.Count, "the log browser opened");
    }

    [TestMethod]
    public void StoppingWithNothingReceivedSaysWhy()
    {
        var vm = new RemoteLoggerViewModel(FakeUi.Services(out _));
        Assert.IsTrue(vm.Start(new FakeLogVehicle(), TempBin()));
        vm.StartStopCommand.Execute(null);
        Assert.IsTrue(vm.StatusIsError);
        StringAssert.Contains(vm.Status, "LOG_BACKEND_TYPE");
    }
}
