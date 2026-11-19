using System.Collections.Concurrent;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;

namespace MissionPlanner.App.Tests.SetupScreens.Joystick;

/// <summary>A scripted joystick: tests set axes, buttons and hats directly.</summary>
internal sealed class FakeJoystickDevice(JoystickDeviceInfo info, int axes = 6, int buttons = 12, int hats = 1) : IJoystickDevice
{
    public short[] AxisValues { get; } = new short[axes];
    public bool[] ButtonValues { get; } = new bool[buttons];
    public HatDirection[] HatValues { get; } = new HatDirection[hats];

    public JoystickDeviceInfo Info { get; } = info;
    public int AxisCount => AxisValues.Length;
    public int ButtonCount => ButtonValues.Length;
    public int HatCount => HatValues.Length;
    public bool IsAttached { get; set; } = true;
    public bool Disposed { get; private set; }

    public JoystickSnapshot Read() => new((short[])AxisValues.Clone(), (bool[])ButtonValues.Clone(), (HatDirection[])HatValues.Clone());

    public void Dispose() => Disposed = true;
}

internal sealed class FakeJoystickBackend : IJoystickBackend
{
    public List<FakeJoystickDevice> Devices { get; } = new();

    public string Name => "fake";
    public string Status => "Fake joystick backend";

    public FakeJoystickDevice Add(string name)
    {
        var d = new FakeJoystickDevice(new JoystickDeviceInfo("fake:" + Devices.Count, name, Name));
        Devices.Add(d);
        return d;
    }

    public IReadOnlyList<JoystickDeviceInfo> GetDevices() => Devices.Select(d => d.Info).ToList();

    public IJoystickDevice Open(JoystickDeviceInfo device) =>
        Devices.FirstOrDefault(d => d.Info.Id == device.Id) ?? throw new IOException("gone");

    public void Dispose()
    {
    }
}

/// <summary>Records what the joystick would send and do.</summary>
internal sealed class FakeJoystickVehicle : IJoystickVehicle
{
    public bool IsConnected { get; set; } = true;
    public uint SysId { get; set; } = 1;
    public byte CompId { get; set; } = 1;
    public bool IsCopter { get; set; } = true;
    public Dictionary<string, double> Params { get; } = new();
    public ConcurrentQueue<MAVLink.mavlink_rc_channels_override_t> Overrides { get; } = new();
    public ConcurrentQueue<MAVLink.mavlink_manual_control_t> ManualControls { get; } = new();
    public ConcurrentQueue<string> Actions { get; } = new();
    public bool CommandResult { get; set; } = true;

    public double? GetParam(string name) => Params.TryGetValue(name, out var v) ? v : null;

    public void SendRcOverride(MAVLink.mavlink_rc_channels_override_t rc) => Overrides.Enqueue(rc);

    public void SendManualControl(MAVLink.mavlink_manual_control_t mc) => ManualControls.Enqueue(mc);

    public bool Arm(bool arm)
    {
        Actions.Enqueue(arm ? "arm" : "disarm");
        return CommandResult;
    }

    public void SetMode(string mode) => Actions.Enqueue("mode " + mode);

    public bool Command(MAVLink.MAV_CMD cmd, float p1, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0,
        bool requireAck = true)
    {
        Actions.Enqueue(FormattableString.Invariant($"{cmd} {p1} {p2} {p3} {p4} {p5} {p6} {p7}"));
        return CommandResult;
    }

    public bool SetParam(string name, double value)
    {
        Actions.Enqueue(FormattableString.Invariant($"param {name}={value}"));
        Params[name] = value;
        return true;
    }

    public bool PointGimbal()
    {
        Actions.Enqueue("roi");
        return true;
    }
}
