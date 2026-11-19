using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>The commands of the Servo/Relay tab (Mission Planner's ServoOptions / RelayOptions).</summary>
public static class ServoCommands
{
    public const int FirstServo = 5;
    public const int LastServo = 14;
    public const int DefaultLow = 1100;
    public const int DefaultHigh = 1900;

    /// <summary>MAV_CMD_DO_SET_SERVO: output number (1-based), PWM.</summary>
    public static VehicleCommand SetServo(int channel, int pwm) => new(MAVLink.MAV_CMD.DO_SET_SERVO, channel, pwm);

    /// <summary>MAV_CMD_DO_REPEAT_SERVO: toggles between <paramref name="pwm"/> and the trim, count times, a cycle every <paramref name="cycleSeconds"/>.</summary>
    public static VehicleCommand RepeatServo(int channel, int pwm, int count, double cycleSeconds) =>
        new(MAVLink.MAV_CMD.DO_REPEAT_SERVO, channel, pwm, count, (float)cycleSeconds);

    /// <summary>MAV_CMD_DO_SET_RELAY: relay instance (0-based, as Mission Planner), 1 on / 0 off.</summary>
    public static VehicleCommand SetRelay(int instance, bool on) => new(MAVLink.MAV_CMD.DO_SET_RELAY, instance, on ? 1 : 0);

    /// <summary>MAV_CMD_DO_REPEAT_RELAY: relay instance, count, cycle time in seconds.</summary>
    public static VehicleCommand RepeatRelay(int instance, int count, double cycleSeconds) =>
        new(MAVLink.MAV_CMD.DO_REPEAT_RELAY, instance, count, (float)cycleSeconds);

    /// <summary>The other end from where the output is now (at or near low → high, otherwise low).</summary>
    public static int TogglePwm(double output, int low, int high) =>
        output <= 0 || Math.Abs(output - low) <= Math.Abs(output - high) ? high : low;

    /// <summary>SERVOn_MIN / SERVOn_MAX when the vehicle has them, else 1100 / 1900.</summary>
    public static (int Low, int High) Range(int channel, Func<string, double?> param)
    {
        var min = param($"SERVO{channel}_MIN");
        var max = param($"SERVO{channel}_MAX");
        var low = min is > 0 and < 3000 ? (int)Math.Round(min.Value) : DefaultLow;
        var high = max is > 0 and < 3000 ? (int)Math.Round(max.Value) : DefaultHigh;
        return low < high ? (low, high) : (DefaultLow, DefaultHigh);
    }

    /// <summary>"Low" / "High" when the output sits within 15 µs of either end.</summary>
    public static string Position(double output, int low, int high)
    {
        if (output <= 0)
            return "";
        if (Math.Abs(output - low) <= 15)
            return "Low";
        if (Math.Abs(output - high) <= 15)
            return "High";
        return "";
    }
}

/// <summary>One servo output row: Low, High, Toggle, Repeat and its live PWM.</summary>
public sealed partial class ServoChannelRow : ObservableObject
{
    private readonly ServoRelayViewModel _owner;

    internal ServoChannelRow(ServoRelayViewModel owner, int channel)
    {
        _owner = owner;
        Channel = channel;
        _name = Settings.Instance[$"Servo{channel}_desc"] is { Length: > 0 } d ? d : $"Servo {channel}";
    }

    public int Channel { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>The output's SERVOn_FUNCTION, when it is not 0 (the autopilot then refuses DO_SET_SERVO).</summary>
    [ObservableProperty]
    private string _function = "";

    [ObservableProperty]
    private decimal? _low = ServoCommands.DefaultLow;

    [ObservableProperty]
    private decimal? _high = ServoCommands.DefaultHigh;

    /// <summary>SERVO_OUTPUT_RAW for this output, µs (0 when not reported).</summary>
    [ObservableProperty]
    private double _output;

    [ObservableProperty]
    private string _outputText = "-";

    /// <summary>"Low", "High" or "".</summary>
    [ObservableProperty]
    private string _position = "";

    public int LowPwm => (int)(Low ?? ServoCommands.DefaultLow);

    public int HighPwm => (int)(High ?? ServoCommands.DefaultHigh);

    internal void Update(double output)
    {
        if (Output.Equals(output))
            return;
        Output = output;
        OutputText = output > 0 ? output.ToString("0", CultureInfo.InvariantCulture) : "-";
        Position = ServoCommands.Position(output, LowPwm, HighPwm);
    }

    [RelayCommand]
    private Task SetLow() => _owner.SendServo(this, LowPwm, "low");

    [RelayCommand]
    private Task SetHigh() => _owner.SendServo(this, HighPwm, "high");

    [RelayCommand]
    private Task Toggle() => _owner.SendServo(this, ServoCommands.TogglePwm(Output, LowPwm, HighPwm), "toggled");

    [RelayCommand]
    private Task Repeat() => _owner.RepeatServo(this);

    [RelayCommand]
    private async Task RenameAsync()
    {
        var name = await _owner.Services.Dialogs.PromptAsync("Description", $"Name for output {Channel}", Name);
        if (name == null)
            return;
        name = name.Trim();
        if (name.Length == 0)
        {
            Settings.Instance.Remove($"Servo{Channel}_desc");
            Name = $"Servo {Channel}";
        }
        else
        {
            Settings.Instance[$"Servo{Channel}_desc"] = name;
            Name = name;
        }
    }
}

/// <summary>One relay row: On, Off, Toggle, Repeat, with the state from RELAY_STATUS or the last command.</summary>
public sealed partial class RelayRow : ObservableObject
{
    private readonly ServoRelayViewModel _owner;

    internal RelayRow(ServoRelayViewModel owner, int instance)
    {
        _owner = owner;
        Instance = instance;
        _name = Settings.Instance[$"Relay{instance}_desc"] is { Length: > 0 } d ? d : $"Relay {instance + 1}";
    }

    /// <summary>0-based instance, as DO_SET_RELAY takes it.</summary>
    public int Instance { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>On, off or unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsOn), nameof(IsOff))]
    private bool? _state;

    /// <summary>Whether <see cref="State"/> comes from the vehicle (RELAY_STATUS) rather than the last command.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _reported;

    public bool IsOn => State == true;

    public bool IsOff => State == false;

    public string StateText => State switch
    {
        true => Reported ? "On" : "On (sent)",
        false => Reported ? "Off" : "Off (sent)",
        _ => "-",
    };

    [RelayCommand]
    private Task On() => _owner.SendRelay(this, true);

    [RelayCommand]
    private Task Off() => _owner.SendRelay(this, false);

    [RelayCommand]
    private Task Toggle() => _owner.SendRelay(this, State != true);

    [RelayCommand]
    private Task Repeat() => _owner.RepeatRelay(this);

    [RelayCommand]
    private async Task RenameAsync()
    {
        var name = await _owner.Services.Dialogs.PromptAsync("Description", $"Name for relay {Instance + 1}", Name);
        if (name == null)
            return;
        name = name.Trim();
        if (name.Length == 0)
        {
            Settings.Instance.Remove($"Relay{Instance}_desc");
            Name = $"Relay {Instance + 1}";
        }
        else
        {
            Settings.Instance[$"Relay{Instance}_desc"] = name;
            Name = name;
        }
    }
}

/// <summary>
/// Servo/Relay tab, after Mission Planner's: outputs 5-14 with Low / High / Toggle / Repeat
/// (DO_SET_SERVO with the output's SERVOn_MIN / MAX, DO_REPEAT_SERVO) and their live PWM from
/// SERVO_OUTPUT_RAW; relays with On / Off / Toggle / Repeat (DO_SET_RELAY, DO_REPEAT_RELAY).
/// </summary>
public sealed partial class ServoRelayViewModel : PageViewModel
{
    private const int DefaultRelays = 6;

    private readonly IFlightCommands _commands;
    private long _tick;
    private Func<CurrentState, double>[] _outputs = [];

    public ServoRelayViewModel(AppServices services, IFlightCommands commands)
        : base("Servo/Relay")
    {
        Services = services;
        _commands = commands;
        for (var ch = ServoCommands.FirstServo; ch <= ServoCommands.LastServo; ch++)
            Servos.Add(new ServoChannelRow(this, ch));
        _outputs = Servos.Select(s => QuickValueCatalog.GetGetter($"ch{s.Channel}out") ?? (_ => 0)).ToArray();
        for (var i = 0; i < DefaultRelays; i++)
            Relays.Add(new RelayRow(this, i));
        services.Params.ParamsChanged += (_, _) => ApplyParams();
        ApplyParams();
    }

    public AppServices Services { get; }

    public ObservableCollection<ServoChannelRow> Servos { get; } = new();

    public ObservableCollection<RelayRow> Relays { get; } = new();

    /// <summary>DO_REPEAT_* cycles.</summary>
    [ObservableProperty]
    private decimal? _repeatCount = 3;

    /// <summary>DO_REPEAT_* cycle time, seconds.</summary>
    [ObservableProperty]
    private decimal? _repeatSeconds = 1;

    public bool CanCommand => _commands.CanCommand;

    public override void OnShown() => Update();

    public override void Tick()
    {
        if (_tick++ % 2 == 0)
            Update();
    }

    private void Update()
    {
        OnPropertyChanged(nameof(CanCommand));
        var cs = Services.Session.Cs;
        for (var i = 0; i < Servos.Count; i++)
        {
            double v;
            try
            {
                v = _outputs[i](cs);
            }
            catch (Exception)
            {
                v = 0;
            }

            Servos[i].Update(v);
        }

        if (_tick % 10 == 1)
            UpdateRelayStatus();
    }

    /// <summary>Output ranges and functions from the parameters (re-read when they change).</summary>
    private void ApplyParams()
    {
        var p = Services.Params;
        double? Param(string n) => p.Has(n) ? p.Get(n) : null;
        foreach (var row in Servos)
        {
            var (low, high) = ServoCommands.Range(row.Channel, Param);
            row.Low = low;
            row.High = high;
            var fn = Param($"SERVO{row.Channel}_FUNCTION");
            row.Function = fn is { } f && f != 0
                ? p.Meta($"SERVO{row.Channel}_FUNCTION")?.Values.FirstOrDefault(v => v.Key == f).Value ?? $"function {f:0}"
                : "";
        }
    }

    /// <summary>RELAY_STATUS (ArduPilot 4.5+) gives the configured relays and their states.</summary>
    private void UpdateRelayStatus()
    {
        if (Services.Session.Vehicle.getPacketLast((uint)MAVLink.MAVLINK_MSG_ID.RELAY_STATUS) is not { } msg ||
            msg.data is not MAVLink.mavlink_relay_status_t status)
            return;

        var highest = -1;
        for (var bit = 0; bit < 16; bit++)
        {
            if ((status.present & (1 << bit)) != 0)
                highest = bit;
        }

        while (Relays.Count < highest + 1)
            Relays.Add(new RelayRow(this, Relays.Count));
        foreach (var row in Relays)
        {
            if (row.Instance < 16 && (status.present & (1 << row.Instance)) != 0)
            {
                row.State = (status.on & (1 << row.Instance)) != 0;
                row.Reported = true;
            }
        }
    }

    internal Task SendServo(ServoChannelRow row, int pwm, string what)
    {
        var cmd = ServoCommands.SetServo(row.Channel, pwm);
        return _commands.RunAsync($"{row.Name} {what}", (mav, sysid, compid, _) => cmd.Send(mav, sysid, compid),
            $"{row.Name}: {pwm} µs", $"{row.Name}: the vehicle rejected DO_SET_SERVO" +
                                     (row.Function.Length > 0 ? $" (output used by {row.Function})" : ""));
    }

    internal Task RepeatServo(ServoChannelRow row)
    {
        var count = (int)(RepeatCount ?? 3);
        var seconds = (double)(RepeatSeconds ?? 1);
        var cmd = ServoCommands.RepeatServo(row.Channel, row.HighPwm, count, seconds);
        return _commands.RunAsync($"{row.Name} repeat", (mav, sysid, compid, _) => cmd.Send(mav, sysid, compid),
            $"{row.Name}: repeating {count} times", $"{row.Name}: the vehicle rejected DO_REPEAT_SERVO");
    }

    internal async Task SendRelay(RelayRow row, bool on)
    {
        var cmd = ServoCommands.SetRelay(row.Instance, on);
        var ok = false;
        await _commands.RunAsync($"{row.Name} {(on ? "on" : "off")}", (mav, sysid, compid, _) => ok = cmd.Send(mav, sysid, compid),
            $"{row.Name} {(on ? "on" : "off")}", $"{row.Name}: the vehicle rejected DO_SET_RELAY (is the relay configured?)");
        if (ok)
        {
            row.State = on;
            row.Reported = false;
        }
    }

    internal Task RepeatRelay(RelayRow row)
    {
        var count = (int)(RepeatCount ?? 3);
        var seconds = (double)(RepeatSeconds ?? 1);
        var cmd = ServoCommands.RepeatRelay(row.Instance, count, seconds);
        return _commands.RunAsync($"{row.Name} repeat", (mav, sysid, compid, _) => cmd.Send(mav, sysid, compid),
            $"{row.Name}: repeating {count} times", $"{row.Name}: the vehicle rejected DO_REPEAT_RELAY");
    }
}
