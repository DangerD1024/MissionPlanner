using System.Diagnostics;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Tests;

/// <summary>
/// A reboot drops a USB autopilot off the bus for a moment. The app must connect again once, when the
/// board is back, not start a second connect beside its own (two countdowns racing on one port).
/// </summary>
[TestClass]
public class RebootReconnectTests
{
    private static readonly LinkSpec Board = LinkSpec.Serial("/dev/serial/by-id/usb-ArduPilot_Test-if00", 115200);

    private static RebootReconnect Fast(Func<string, bool> portExists, double giveUpSeconds = 5) => new()
    {
        PortExists = portExists,
        Settle = TimeSpan.FromMilliseconds(20),
        Poll = TimeSpan.FromMilliseconds(10),
        GiveUpAfter = TimeSpan.FromSeconds(giveUpSeconds),
    };

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task ConnectsOnceWhenTheBoardIsBack()
    {
        var back = Stopwatch.StartNew();
        var connects = 0;
        var ok = await Fast(_ => back.ElapsedMilliseconds > 200).RunAsync(Board, () => true, () =>
        {
            connects++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, connects);
        Assert.IsGreaterThanOrEqualTo(200L, back.ElapsedMilliseconds, "waited for the port");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task TriesAgainWhenTheFirstConnectFails()
    {
        var connects = 0;
        var ok = await Fast(_ => true).RunAsync(Board, () => true,
            () => Task.FromResult(++connects == 2), CancellationToken.None);

        Assert.IsTrue(ok);
        Assert.AreEqual(2, connects, "the first attempt met the bootloader");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task GivesUpWhenTheBoardStaysAway()
    {
        var connects = 0;
        var ok = await Fast(_ => false, giveUpSeconds: 0.3).RunAsync(Board, () => true, () =>
        {
            connects++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, connects);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task StopsWhenSomethingElseConnected()
    {
        var connects = 0;
        var ok = await Fast(_ => true).RunAsync(Board, () => false, () =>
        {
            connects++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, connects, "auto-connect or the user got there first");
    }

    [TestMethod]
    public void OnlyARestartIntoTheFirmwareCounts()
    {
        Assert.IsTrue(VehicleReboot.IsRestart(Command(1)));
        Assert.IsFalse(VehicleReboot.IsRestart(Command(3)), "into the bootloader (firmware upload)");
        Assert.IsFalse(VehicleReboot.IsRestart(Command(42)), "the lock-up test");
        Assert.IsFalse(VehicleReboot.IsRestart(Command(1, MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION)));
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task AUsbDropAfterARebootIsTheReboot()
    {
        Assert.IsFalse(await DropAsync(sendReboot: false), "an unplugged cable is a lost link");
        Assert.IsTrue(await DropAsync(sendReboot: true), "the board left USB to restart");
    }

    /// <summary>Connects over a pseudo-terminal, optionally sends the reboot, then takes the device away.</summary>
    private static async Task<bool> DropAsync(bool sendReboot)
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("pseudo-terminal pair needs socat (Linux/macOS)");
        var pty = ConnectTests.PtyPair.TryStart();
        if (pty == null)
            Assert.Inconclusive("socat is not installed");

        var vehiclePort = new System.IO.Ports.SerialPort(pty.VehiclePath, 115200) { ReadTimeout = 500, WriteTimeout = 500 };
        vehiclePort.Open();
        var vehicle = new FakeVehicle(vehiclePort.BaseStream, 5);

        await using var session = new VehicleSession();
        await session.ConnectAsync(LinkSpec.Serial(pty.GcsPath, 115200), getParams: false);
        var lost = new TaskCompletionSource<bool>();
        session.LinkLost += (_, _) => lost.TrySetResult(session.LostByReboot);

        if (sendReboot)
        {
            // what VehicleReboot.Send puts on the wire (the fake vehicle does not answer commands)
            session.Mav.sendPacket(new MAVLink.mavlink_command_long_t
            {
                command = (ushort)MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN,
                param1 = 1,
                target_system = 1,
                target_component = 1,
            }, 1, 1);
        }

        pty.Dispose();
        var result = await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(LinkState.Disconnected, session.State);
        Assert.IsFalse(session.Mav.BaseStream?.IsOpen ?? false, "nothing reopened the port behind the app's back");

        _ = Task.Run(async () =>
        {
            await vehicle.DisposeAsync();
            vehiclePort.Dispose();
        });
        return result;
    }

    private static MAVLink.MAVLinkMessage Command(float p1, MAVLink.MAV_CMD command = MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN)
    {
        var bytes = new MAVLink.MavlinkParse().GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG,
            new MAVLink.mavlink_command_long_t { command = (ushort)command, param1 = p1, target_system = 1, target_component = 1 });
        return new MAVLink.MAVLinkMessage(bytes);
    }
}
