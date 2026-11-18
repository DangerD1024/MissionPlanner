using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

namespace MissionPlanner.App.Tests.SetupScreens.Comms;

/// <summary>
/// The terminal and DroneCAN pages against a scripted autopilot over a real VehicleSession (TCP
/// loopback): SERIAL_CONTROL to its shell, and CAN forwarding with a node broadcasting NodeStatus.
/// </summary>
[TestClass]
public sealed class CommsVehicleTests
{
    private static async Task<(AppServices Services, CommsUi Ui, ScriptedAutopilot Vehicle, TcpClient Socket)> ConnectAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ui = new CommsUi();
        var services = ui.Services();
        var accept = listener.AcceptTcpClientAsync();
        var connect = services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", port), getParams: false);
        var socket = await accept;
        listener.Stop();
        var vehicle = new ScriptedAutopilot(socket.GetStream());
        await connect;
        return (services, ui, vehicle, socket);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task TerminalRunsShellCommandsThroughSerialControl()
    {
        var (services, _, vehicle, socket) = await ConnectAsync();
        try
        {
            var vm = new TerminalViewModel(services);
            Assert.IsTrue(vm.IsMavlinkMode);
            await vm.OpenCommand.ExecuteAsync(null);
            Assert.IsTrue(vm.IsOpen, vm.Status);

            // the first CR wakes the shell, which prints its prompt
            await Wait.UntilAsync(() =>
            {
                vm.Tick();
                return vm.Output.Contains("nsh> ");
            }, 10000, "the shell prompt");

            vm.CommandText = "ver";
            await vm.SendCommand.ExecuteAsync(null);
            await Wait.UntilAsync(() =>
            {
                vm.Tick();
                return vm.Output.Contains("ArduPilot test shell");
            }, 10000, "the command output");
            StringAssert.Contains(vm.Output, "ver\n", "the shell's echo");

            vm.OpenCommand.Execute(null);
            await Wait.UntilAsync(() => !vm.IsOpen && vehicle.ShellReleased, 5000, "the port handed back");
        }
        finally
        {
            await services.Session.DisposeAsync();
            socket.Dispose();
            await vehicle.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DroneCanSeesNodesThroughMavlinkForwarding()
    {
        var (services, _, vehicle, socket) = await ConnectAsync();
        var bus = new FakeDroneCanBus();
        try
        {
            var session = services.Session;
            var vm = new DroneCanViewModel(services, libraryAvailable: true, () => bus,
                b => new MavlinkCanForwarder(new MavlinkCanFrameTransport(session), b, (byte)session.Vehicle.sysid, session.Vehicle.compid));
            await vm.ToggleConnectCommand.ExecuteAsync(null);
            Assert.IsTrue(vm.IsConnected, vm.Status);

            await Wait.UntilAsync(() => vehicle.ForwardBus == 1, 10000, "CAN_FORWARD for bus 1");
            await Wait.UntilAsync(() => vm.Nodes.Any(n => n.Id == 10 && n.UptimeSec > 0), 10000, "node 10 from forwarded NodeStatus");
            Assert.AreEqual(CanNodeHealth.Ok, vm.Nodes.Single().Health);

            // what the library sends goes to the vehicle as CAN_FRAME on bus index 0
            bus.WriteLine("T1E0AF9FE3AABBCC");
            await Wait.UntilAsync(() => !vehicle.FramesFromGcs.IsEmpty, 10000, "a CAN_FRAME at the vehicle");
            var frame = vehicle.FramesFromGcs.First();
            Assert.AreEqual(0x9E0AF9FEu, frame.id);
            Assert.AreEqual(0, frame.bus);

            for (var i = 0; i < 20; i++)
                vm.Tick();
            StringAssert.StartsWith(vm.Traffic, "CAN1 frames in ");

            await vm.ToggleConnectCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.IsConnected);
            Assert.IsTrue(bus.Stopped);
            await Wait.UntilAsync(() => vehicle.ForwardBus == 0, 5000, "CAN_FORWARD 0 on disconnect");
        }
        finally
        {
            await services.Session.DisposeAsync();
            socket.Dispose();
            await vehicle.DisposeAsync();
        }
    }
}

/// <summary>
/// An autopilot stand-in on a stream: heartbeats; a system shell on SERIAL_CONTROL that echoes and
/// answers "ver"; MAV_CMD_CAN_FORWARD acknowledged, after which a DroneCAN node 10 broadcasts
/// NodeStatus as CAN_FRAME on the forwarded bus.
/// </summary>
internal sealed class ScriptedAutopilot : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly object _writeGate = new();
    private readonly MAVLink.MavlinkParse _parse = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sender;
    private readonly Task _receiver;
    private readonly StringBuilder _shellLine = new();
    private readonly ByteQueue _shellOut = new();
    private int _seq;
    private uint _uptime = 1;

    public ScriptedAutopilot(Stream stream)
    {
        _stream = stream;
        _sender = Task.Run(SendLoop);
        _receiver = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
    }

    /// <summary>The bus forwarding was last asked for (0 = stopped, -1 = never).</summary>
    public int ForwardBus { get; private set; } = -1;

    public bool ShellReleased { get; private set; }

    public ConcurrentQueue<MAVLink.mavlink_can_frame_t> FramesFromGcs { get; } = new();

    private void Write(MAVLink.MAVLINK_MSG_ID id, object msg)
    {
        var bytes = _parse.GenerateMAVLinkPacket20(id, msg, false, 1, 1, _seq++ & 0xff);
        lock (_writeGate)
        {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
    }

    private async Task SendLoop()
    {
        var n = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Write(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(0, (byte)MAVLink.MAV_TYPE.QUADROTOR,
                    (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA, (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED,
                    (byte)MAVLink.MAV_STATE.STANDBY, 3));

                if (ForwardBus > 0 && n++ % 2 == 0)
                {
                    // uavcan.protocol.NodeStatus from node 10: priority 16, type 341; uptime, health OK, mode OPERATIONAL
                    var id = (16u << 24) | (341u << 8) | 10u | 0x80000000u;
                    var data = new byte[8];
                    BitConverter.GetBytes(_uptime++).CopyTo(data, 0);
                    data[4] = 0;
                    data[7] = (byte)(0xC0 | (n & 0x1F));
                    Write(MAVLink.MAVLINK_MSG_ID.CAN_FRAME, new MAVLink.mavlink_can_frame_t(id, 255, 190, (byte)(ForwardBus - 1), 8, data));
                }

                await Task.Delay(100, _stop.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // stopping or the link closed
        }
    }

    private void ReceiveLoop()
    {
        var parse = new MAVLink.MavlinkParse();
        while (!_stop.IsCancellationRequested)
        {
            MAVLink.MAVLinkMessage? msg;
            try
            {
                msg = parse.ReadPacket(_stream);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                continue;
            }

            if (msg == null || msg.Length == 0)
                continue;

            switch (msg.data)
            {
                case MAVLink.mavlink_serial_control_t sc when sc.device == (byte)MAVLink.SERIAL_CONTROL_DEV.SHELL:
                    OnShell(sc);
                    break;
                case MAVLink.mavlink_command_long_t cmd when cmd.command == (ushort)MAVLink.MAV_CMD.CAN_FORWARD:
                    ForwardBus = (int)cmd.param1;
                    Write(MAVLink.MAVLINK_MSG_ID.COMMAND_ACK, new MAVLink.mavlink_command_ack_t
                    {
                        command = cmd.command,
                        result = (byte)MAVLink.MAV_RESULT.ACCEPTED,
                        target_system = (byte)msg.sysid,
                        target_component = msg.compid,
                    });
                    break;
                case MAVLink.mavlink_can_frame_t frame:
                    FramesFromGcs.Enqueue(frame);
                    break;
            }
        }
    }

    private void OnShell(MAVLink.mavlink_serial_control_t sc)
    {
        if ((sc.flags & (byte)MAVLink.SERIAL_CONTROL_FLAG.EXCLUSIVE) == 0)
        {
            ShellReleased = true;
            return;
        }

        for (var i = 0; i < sc.count; i++)
        {
            var c = (char)sc.data[i];
            if (c == '\r' || c == '\n')
            {
                var line = _shellLine.ToString();
                _shellLine.Clear();
                var reply = "\r\n" + (line == "ver" ? "ArduPilot test shell 1.0\r\n" : line.Length > 0 ? line + ": command not found\r\n" : "") +
                            "\x1b[32mnsh>\x1b[0m ";
                _shellOut.Append(Encoding.ASCII.GetBytes(reply));
            }
            else
            {
                _shellLine.Append(c);
                _shellOut.Append([(byte)c]); // echo
            }
        }

        if ((sc.flags & (byte)MAVLink.SERIAL_CONTROL_FLAG.RESPOND) == 0)
            return;

        var buffer = new byte[70];
        do
        {
            var n = _shellOut.Take(buffer, 0, 70, 0);
            Write(MAVLink.MAVLINK_MSG_ID.SERIAL_CONTROL, new MAVLink.mavlink_serial_control_t
            {
                device = (byte)MAVLink.SERIAL_CONTROL_DEV.SHELL,
                flags = (byte)MAVLink.SERIAL_CONTROL_FLAG.REPLY,
                count = (byte)n,
                data = buffer.ToArray(),
            });
            if (n == 0)
                break;
        } while ((sc.flags & (byte)MAVLink.SERIAL_CONTROL_FLAG.MULTI) != 0);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _sender.ConfigureAwait(false);
        await Task.WhenAny(_receiver, Task.Delay(2000)).ConfigureAwait(false);
        _stop.Dispose();
    }
}
