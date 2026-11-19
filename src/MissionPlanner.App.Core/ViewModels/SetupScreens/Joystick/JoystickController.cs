using System.Threading.Channels;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;

/// <summary>
/// The joystick loop (Mission Planner's JoystickBase.mainloop + MainV2.joysticksend): at 25 Hz on a
/// background task it reads the device, computes the RC outputs from the profile, runs button
/// actions on a separate worker so a slow command never stalls the stream, and, while sending is
/// enabled and a vehicle is connected, sends RC_CHANNELS_OVERRIDE (or MANUAL_CONTROL).
/// </summary>
public sealed class JoystickController : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(JoystickController));

    public const int RateHz = 25;

    private readonly IJoystickVehicle _vehicle;
    private readonly object _gate = new();
    private readonly Dictionary<(int Hat, bool Vertical), HatAccumulator> _hats = new();
    private readonly double[] _buttonAxis = [double.NaN, double.NaN];
    private readonly Channel<(ButtonMapping Map, bool Down)> _actions = Channel.CreateUnbounded<(ButtonMapping, bool)>();
    private readonly Task _actionWorker;
    private bool[] _previousButtons = [];
    private CancellationTokenSource? _stop;
    private Task _loop = Task.CompletedTask;
    private bool _sending;
    private ushort[] _outputs = new ushort[JoystickProfile.ChannelCount];
    private JoystickSnapshot _snapshot = JoystickSnapshot.Empty;
    private long _packets;
    private string? _lastAction;
    private string? _lost;

    public JoystickController(IJoystickVehicle vehicle)
    {
        _vehicle = vehicle;
        _actionWorker = Task.Run(ActionLoop);
    }

    public IJoystickDevice? Device { get; private set; }

    public JoystickProfile Profile { get; set; } = JoystickProfile.CreateDefault("", 0);

    public bool IsRunning => _stop != null;

    public bool IsSending
    {
        get
        {
            lock (_gate)
                return _sending;
        }
    }

    /// <summary>The latest device reading.</summary>
    public JoystickSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    /// <summary>The computed PWM per channel 1-16 (0 = not driven).</summary>
    public ushort[] Outputs
    {
        get
        {
            lock (_gate)
                return (ushort[])_outputs.Clone();
        }
    }

    public long PacketsSent => Interlocked.Read(ref _packets);

    /// <summary>The result of the last button action, for the page.</summary>
    public string? LastAction
    {
        get
        {
            lock (_gate)
                return _lastAction;
        }
    }

    /// <summary>Set when the device was unplugged (sending stops).</summary>
    public string? Lost
    {
        get
        {
            lock (_gate)
                return _lost;
        }
    }

    /// <summary>Raised on a worker after a button action ran (text = outcome).</summary>
    public event EventHandler<string>? ActionCompleted;

    /// <summary>Raised on the loop when the device disappears.</summary>
    public event EventHandler? DeviceLost;

    /// <summary>Uses <paramref name="device"/> from now on (null: none), resetting hat and button state.</summary>
    public void Attach(IJoystickDevice? device)
    {
        lock (_gate)
        {
            Device = device;
            _previousButtons = [];
            _hats.Clear();
            _buttonAxis[0] = _buttonAxis[1] = double.NaN;
            _snapshot = JoystickSnapshot.Empty;
            _lost = null;
        }
    }

    public void Start()
    {
        if (_stop != null)
            return;
        var stop = new CancellationTokenSource();
        _stop = stop;
        _loop = Task.Run(() => Loop(stop.Token));
    }

    public async Task StopAsync()
    {
        var stop = _stop;
        if (stop == null)
            return;
        _stop = null;
        await stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        stop.Dispose();
    }

    /// <summary>Starts or stops sending. Stopping hands the overridden channels back to the RC receiver.</summary>
    public void SetSending(bool on)
    {
        bool was;
        lock (_gate)
        {
            was = _sending;
            _sending = on;
        }

        if (was && !on)
            Release();
    }

    private async Task Loop(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / RateHz));
        try
        {
            do
            {
                try
                {
                    Step();
                }
                catch (Exception ex)
                {
                    Log.Warn("joystick step", ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One loop iteration: read, map, run button edges, send. Public for tests.</summary>
    public void Step()
    {
        var device = Device;
        var profile = Profile;
        if (device != null && !device.IsAttached)
        {
            var wasSending = IsSending;
            lock (_gate)
            {
                if (_lost != null)
                    return;
                _lost = $"{device.Info.Name} was disconnected.";
                _sending = false;
            }

            if (wasSending)
                Release();
            DeviceLost?.Invoke(this, EventArgs.Empty);
            return;
        }

        var snap = device?.Read() ?? JoystickSnapshot.Empty;
        var sending = IsSending;
        var connected = _vehicle.IsConnected;

        // button edges: button axes always (the page previews them), actions only while sending
        var buttons = snap.Buttons;
        foreach (var map in profile.Buttons)
        {
            if (map.Button < 0)
                continue;
            var now = snap.Button(map.Button);
            var before = map.Button < _previousButtons.Length && _previousButtons[map.Button];
            if (now == before)
                continue;

            if (map.Function is JoystickButtonFunction.Button_axis0 or JoystickButtonFunction.Button_axis1)
                _buttonAxis[map.Function == JoystickButtonFunction.Button_axis0 ? 0 : 1] = now ? map.P2 : map.P1;
            else if (sending && connected && (now || map.Function == JoystickButtonFunction.Do_Set_Relay))
                _actions.Writer.TryWrite((map, now));
        }

        _previousButtons = buttons.ToArray();

        var outputs = Compute(profile, snap);
        lock (_gate)
        {
            _snapshot = snap;
            _outputs = outputs;
        }

        if (!sending || !connected || device == null)
            return;

        try
        {
            if (profile.ManualControl)
            {
                var mask = 0;
                for (var i = 0; i < Math.Min(16, buttons.Count); i++)
                    if (buttons[i])
                        mask |= 1 << i;
                _vehicle.SendManualControl(JoystickMapper.ManualControl(outputs, Range,
                    Role("RCMAP_ROLL", 1), Role("RCMAP_PITCH", 2), Role("RCMAP_THROTTLE", 3), Role("RCMAP_YAW", 4),
                    (byte)_vehicle.SysId, (ushort)mask));
            }
            else
            {
                _vehicle.SendRcOverride(JoystickMapper.Override(outputs, _vehicle.SysId, _vehicle.CompId));
            }

            Interlocked.Increment(ref _packets);
        }
        catch (Exception ex)
        {
            Log.Warn("joystick send", ex);
        }
    }

    private int Role(string param, int fallback) => (int)(_vehicle.GetParam(param) ?? fallback);

    private RcRange Range(int channel) => JoystickMapper.RangeFor(channel, _vehicle.GetParam);

    /// <summary>PWM per channel for this reading; also advances held hats.</summary>
    internal ushort[] Compute(JoystickProfile profile, JoystickSnapshot snap)
    {
        var outputs = new ushort[JoystickProfile.ChannelCount];
        foreach (var map in profile.Channels)
        {
            if (map.Channel < 1 || map.Channel > outputs.Length || map.Input.Kind == JoystickInputKind.None)
                continue;
            var range = Range(map.Channel);
            outputs[map.Channel - 1] = map.Input.Kind switch
            {
                JoystickInputKind.Axis => JoystickMapper.Pwm(JoystickMapper.Normalise(snap.Axis(map.Input.Index)), map.Reverse, map.Expo, range, map.Trim),
                JoystickInputKind.HatVertical or JoystickInputKind.HatHorizontal =>
                    JoystickMapper.Pwm(Hat(map.Input, snap), map.Reverse, map.Expo, range, map.Trim),
                JoystickInputKind.ButtonAxis => (ushort)Math.Clamp(
                    (int)Math.Round((double.IsNaN(_buttonAxis[Math.Min(1, map.Input.Index)]) ? ButtonAxisRest(profile, map.Input.Index, range) : _buttonAxis[Math.Min(1, map.Input.Index)]) + map.Trim),
                    range.Min, range.Max),
                JoystickInputKind.Trim => (ushort)Math.Clamp(range.Trim + map.Trim, range.Min, range.Max),
                _ => 0,
            };
        }

        if (profile.Elevons && outputs[0] != 0 && outputs[1] != 0)
        {
            // mix the un-reversed stick values; the reverse flags apply to the mixed outputs
            var ch1 = profile.Channel(1);
            var ch2 = profile.Channel(2);
            var roll = Unreversed(ch1, snap, outputs[0]);
            var pitch = Unreversed(ch2, snap, outputs[1]);
            (outputs[0], outputs[1]) = JoystickMapper.Elevons(roll, pitch, ch1.Reverse, ch2.Reverse);
        }

        return outputs;
    }

    private ushort Unreversed(ChannelMapping map, JoystickSnapshot snap, ushort computed) =>
        map.Input.Kind == JoystickInputKind.Axis && map.Reverse
            ? JoystickMapper.Pwm(JoystickMapper.Normalise(snap.Axis(map.Input.Index)), false, map.Expo, Range(map.Channel), map.Trim)
            : computed;

    private static double ButtonAxisRest(JoystickProfile profile, int index, RcRange range)
    {
        var f = index == 0 ? JoystickButtonFunction.Button_axis0 : JoystickButtonFunction.Button_axis1;
        var map = profile.Buttons.FirstOrDefault(b => b.Function == f);
        return map != null && map.P1 > 0 ? map.P1 : range.Trim;
    }

    private double Hat(JoystickInput input, JoystickSnapshot snap)
    {
        var vertical = input.Kind == JoystickInputKind.HatVertical;
        if (!_hats.TryGetValue((input.Index, vertical), out var acc))
            _hats[(input.Index, vertical)] = acc = new HatAccumulator();
        var h = snap.Hat(input.Index);
        var dir = vertical
            ? (h.HasFlag(HatDirection.Up) ? 1 : 0) - (h.HasFlag(HatDirection.Down) ? 1 : 0)
            : (h.HasFlag(HatDirection.Right) ? 1 : 0) - (h.HasFlag(HatDirection.Left) ? 1 : 0);
        acc.Update(dir);
        return acc.Value;
    }

    /// <summary>Hands the overridden channels back to the receiver (Mission Planner's clearRCOverride).</summary>
    private void Release()
    {
        if (!_vehicle.IsConnected || Profile.ManualControl)
            return;
        var outputs = Outputs;
        var any = outputs.Any(o => o != 0);
        if (!any)
            return;
        try
        {
            var rc = JoystickMapper.Override(outputs, _vehicle.SysId, _vehicle.CompId, release: true);
            for (var i = 0; i < 3; i++)
                _vehicle.SendRcOverride(rc);
        }
        catch (Exception ex)
        {
            Log.Warn("joystick release", ex);
        }
    }

    private async Task ActionLoop()
    {
        await foreach (var (map, down) in _actions.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            string result;
            try
            {
                result = await Task.Run(() => Run(map, down)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = $"{ButtonFunctionInfo.For(map.Function).Label} failed: {ex.Message}";
            }

            lock (_gate)
                _lastAction = result;
            ActionCompleted?.Invoke(this, result);
        }
    }

    /// <summary>Runs one button action on the vehicle (blocking). Mirrors JoystickBase.ProcessButtonEvent.</summary>
    internal string Run(ButtonMapping map, bool down)
    {
        var v = _vehicle;
        var label = ButtonFunctionInfo.For(map.Function).Label;
        string Ok(bool ok) => ok ? label + ": done" : label + ": refused";

        switch (map.Function)
        {
            case JoystickButtonFunction.ChangeMode:
                if (string.IsNullOrWhiteSpace(map.Mode))
                    return "Change mode: no mode chosen";
                v.SetMode(map.Mode);
                return "Mode " + map.Mode + " requested";
            case JoystickButtonFunction.Arm:
                return Ok(v.Arm(true));
            case JoystickButtonFunction.Disarm:
                return Ok(v.Arm(false));
            case JoystickButtonFunction.TakeOff:
                v.SetMode("Guided");
                var alt = map.P1 > 0 ? map.P1 : v.IsCopter ? 2 : 20;
                return Ok(v.Command(MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, alt));
            case JoystickButtonFunction.Do_Set_Relay:
                return Ok(v.Command(MAVLink.MAV_CMD.DO_SET_RELAY, map.P1, down ? 1 : 0));
            case JoystickButtonFunction.Do_Repeat_Relay:
                return Ok(v.Command(MAVLink.MAV_CMD.DO_REPEAT_RELAY, map.P1, map.P2, map.P3));
            case JoystickButtonFunction.Do_Set_Servo:
                return Ok(v.Command(MAVLink.MAV_CMD.DO_SET_SERVO, map.P1, map.P2));
            case JoystickButtonFunction.Do_Repeat_Servo:
                return Ok(v.Command(MAVLink.MAV_CMD.DO_REPEAT_SERVO, map.P1, map.P2, map.P3, map.P4));
            case JoystickButtonFunction.Digicam_Control:
                return Ok(v.Command(MAVLink.MAV_CMD.DO_DIGICAM_CONTROL, 0, 0, 0, 0, 1, 0, 0));
            case JoystickButtonFunction.Mount_Mode:
                if (v.GetParam("MNT_MODE") != null)
                    return Ok(v.SetParam("MNT_MODE", map.P1));
                return Ok(v.Command(MAVLink.MAV_CMD.DO_MOUNT_CONFIGURE, map.P1, 0, 0, 0, 0, 0, 0, requireAck: false));
            case JoystickButtonFunction.Toggle_Pan_Stab:
                var current = v.GetParam("MNT_STAB_PAN");
                if (current == null)
                    return "Toggle pan stabilisation: MNT_STAB_PAN not on this vehicle";
                return Ok(v.SetParam("MNT_STAB_PAN", current > 0 ? 0 : 1));
            case JoystickButtonFunction.Gimbal_pnt_track:
                return Ok(v.PointGimbal());
            case JoystickButtonFunction.Mount_Control_0:
                return Ok(v.Command(MAVLink.MAV_CMD.DO_MOUNT_CONTROL, 0, 0, 0, 0, 0, 0, (float)MAVLink.MAV_MOUNT_MODE.MAVLINK_TARGETING,
                    requireAck: false));
            default:
                return label;
        }
    }

    public async ValueTask DisposeAsync()
    {
        SetSending(false);
        await StopAsync().ConfigureAwait(false);
        _actions.Writer.TryComplete();
        await _actionWorker.ConfigureAwait(false);
    }
}
