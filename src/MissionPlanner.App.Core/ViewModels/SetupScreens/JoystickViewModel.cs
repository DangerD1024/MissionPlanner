using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

public sealed record InputOption(JoystickInput Input)
{
    public string Label => Input.Label;

    public override string ToString() => Label;
}

public sealed record ButtonOption(int Button)
{
    public string Label => Button < 0 ? "—" : "Button " + (Button + 1).ToString(CultureInfo.InvariantCulture);

    public override string ToString() => Label;
}

/// <summary>A live axis reading.</summary>
public sealed partial class AxisReading(int index) : ObservableObject
{
    public string Label { get; } = "Axis " + (index + 1).ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private string _text = "0";
}

public sealed partial class ButtonReading(int index) : ObservableObject
{
    public string Label { get; } = (index + 1).ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isPressed;
}

public sealed partial class HatReading(int index) : ObservableObject
{
    public string Label { get; } = "Hat " + (index + 1).ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private string _text = "centre";
}

/// <summary>One RC channel's mapping row.</summary>
public sealed partial class ChannelRowViewModel : ObservableObject
{
    private readonly JoystickViewModel _owner;

    public ChannelRowViewModel(JoystickViewModel owner, ChannelMapping map, string role)
    {
        _owner = owner;
        Map = map;
        Label = "RC" + map.Channel.ToString(CultureInfo.InvariantCulture) + (role.Length > 0 ? " " + role : "");
    }

    public ChannelMapping Map { get; }

    public int Channel => Map.Channel;

    public string Label { get; }

    public ObservableCollection<InputOption> Options => _owner.InputOptions;

    public InputOption? SelectedInput
    {
        get => Options.FirstOrDefault(o => o.Input == Map.Input);
        set
        {
            if (value == null || value.Input == Map.Input)
                return;
            Map.Input = value.Input;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMapped));
            _owner.MarkDirty();
        }
    }

    public bool IsMapped => Map.Input.Kind != JoystickInputKind.None;

    public bool Reverse
    {
        get => Map.Reverse;
        set
        {
            if (Map.Reverse == value)
                return;
            Map.Reverse = value;
            OnPropertyChanged();
            _owner.MarkDirty();
        }
    }

    public decimal Expo
    {
        get => Map.Expo;
        set
        {
            var v = (int)Math.Clamp(value, -100, 100);
            if (Map.Expo == v)
                return;
            Map.Expo = v;
            OnPropertyChanged();
            _owner.MarkDirty();
        }
    }

    public decimal Trim
    {
        get => Map.Trim;
        set
        {
            var v = (int)Math.Clamp(value, -500, 500);
            if (Map.Trim == v)
                return;
            Map.Trim = v;
            OnPropertyChanged();
            _owner.MarkDirty();
        }
    }

    [ObservableProperty]
    private double _output;

    [ObservableProperty]
    private string _outputText = "—";

    [ObservableProperty]
    private string _vehicleText = "";

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isDetecting;

    internal void RefreshSelection() => OnPropertyChanged(nameof(SelectedInput));

    [RelayCommand]
    private Task DetectAsync() => _owner.DetectAxisAsync(this);
}

/// <summary>One button-function row.</summary>
public sealed partial class ButtonRowViewModel : ObservableObject
{
    private readonly JoystickViewModel _owner;

    public ButtonRowViewModel(JoystickViewModel owner, ButtonMapping map, int row)
    {
        _owner = owner;
        Map = map;
        Row = row;
    }

    public ButtonMapping Map { get; }

    public int Row { get; }

    public string Title => "#" + (Row + 1).ToString(CultureInfo.InvariantCulture);

    public ObservableCollection<ButtonOption> ButtonOptions => _owner.ButtonOptions;

    public IReadOnlyList<ButtonFunctionInfo> Functions => ButtonFunctionInfo.All;

    public ObservableCollection<string> Modes => _owner.Modes;

    public ButtonOption? SelectedButton
    {
        get => ButtonOptions.FirstOrDefault(o => o.Button == Map.Button);
        set
        {
            if (value == null || value.Button == Map.Button)
                return;
            Map.Button = value.Button;
            OnPropertyChanged();
            _owner.MarkDirty();
        }
    }

    public ButtonFunctionInfo SelectedFunction
    {
        get => ButtonFunctionInfo.For(Map.Function);
        set
        {
            if (value == null || value.Function == Map.Function)
                return;
            Map.Function = value.Function;
            Map.P1 = value.D1;
            Map.P2 = value.D2;
            Map.P3 = value.D3;
            Map.P4 = value.D4;
            OnPropertyChanged(string.Empty);
            _owner.MarkDirty();
        }
    }

    public bool UsesMode => SelectedFunction.UsesMode;

    public string? Mode
    {
        get => Map.Mode;
        set
        {
            if (Map.Mode == value)
                return;
            Map.Mode = value;
            OnPropertyChanged();
            _owner.MarkDirty();
        }
    }

    public string P1Label => SelectedFunction.P1;
    public string P2Label => SelectedFunction.P2;
    public string P3Label => SelectedFunction.P3;
    public string P4Label => SelectedFunction.P4;
    public bool HasP1 => P1Label.Length > 0;
    public bool HasP2 => P2Label.Length > 0;
    public bool HasP3 => P3Label.Length > 0;
    public bool HasP4 => P4Label.Length > 0;

    public decimal P1 { get => (decimal)Map.P1; set => Set(value, v => Map.P1 = v, Map.P1); }
    public decimal P2 { get => (decimal)Map.P2; set => Set(value, v => Map.P2 = v, Map.P2); }
    public decimal P3 { get => (decimal)Map.P3; set => Set(value, v => Map.P3 = v, Map.P3); }
    public decimal P4 { get => (decimal)Map.P4; set => Set(value, v => Map.P4 = v, Map.P4); }

    private void Set(decimal value, Action<float> set, float current, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if ((float)value == current)
            return;
        set((float)value);
        OnPropertyChanged(name);
        _owner.MarkDirty();
    }

    [ObservableProperty]
    private bool _isPressed;

    [ObservableProperty]
    private bool _isDetecting;

    internal void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private Task DetectAsync() => _owner.DetectButtonAsync(this);
}

/// <summary>
/// Joystick (Mission Planner's JoystickSetup): pick a joystick or gamepad, see its axes and buttons
/// live, map axes to RC channels (reverse, expo, trim) and buttons to actions, and send
/// RC_CHANNELS_OVERRIDE (or MANUAL_CONTROL) at 25 Hz. Mappings are saved per device. Sending keeps
/// running when the page is left.
/// </summary>
public sealed partial class JoystickViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(JoystickViewModel));
    private const string DeviceKey = "next_joystick_device";

    private readonly JoystickController _controller;
    private readonly Func<IJoystickBackend> _backendFactory;
    private readonly JoystickProfileStore _store;
    private IJoystickBackend? _backend;
    private Task? _backendTask;
    private IJoystickDevice? _device;
    private bool _shown;
    private int _tick;
    private bool _switching;

    public JoystickViewModel(AppServices services)
        : this(services, JoystickBackends.Create, new SessionJoystickVehicle(services.Session), new JoystickProfileStore())
    {
    }

    /// <summary>For tests: a scripted backend, vehicle and profile folder.</summary>
    internal JoystickViewModel(AppServices services, Func<IJoystickBackend> backend, IJoystickVehicle vehicle, JoystickProfileStore store)
        : base("Joystick")
    {
        Services = services;
        _backendFactory = backend;
        _store = store;
        Vehicle = vehicle;
        _controller = new JoystickController(vehicle);
        _controller.DeviceLost += (_, _) => services.Dispatcher.Post(() =>
        {
            IsSending = false;
            StatusText = _controller.Lost ?? "The joystick was disconnected.";
        });
    }

    public AppServices Services { get; }

    internal IJoystickVehicle Vehicle { get; }

    internal JoystickController Controller => _controller;

    // ---- backend and devices

    [ObservableProperty]
    private string _backendText = "Looking for joystick support…";

    [ObservableProperty]
    private bool _backendMissing;

    public ObservableCollection<JoystickDeviceInfo> Devices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    private JoystickDeviceInfo? _selectedDevice;

    public bool HasDevice => SelectedDevice != null && _device != null;

    [ObservableProperty]
    private string _deviceText = "No joystick selected.";

    public ObservableCollection<AxisReading> Axes { get; } = new();

    public ObservableCollection<ButtonReading> ButtonStates { get; } = new();

    public ObservableCollection<HatReading> Hats { get; } = new();

    // ---- mapping

    public ObservableCollection<InputOption> InputOptions { get; } = new();

    public ObservableCollection<ButtonOption> ButtonOptions { get; } = new();

    public ObservableCollection<string> Modes { get; } = new();

    public ObservableCollection<ChannelRowViewModel> Channels { get; } = new();

    public ObservableCollection<ButtonRowViewModel> ButtonRows { get; } = new();

    [ObservableProperty]
    private bool _showAllChannels;

    public bool ManualControl
    {
        get => _controller.Profile.ManualControl;
        set
        {
            if (_controller.Profile.ManualControl == value)
                return;
            _controller.Profile.ManualControl = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public bool Elevons
    {
        get => _controller.Profile.Elevons;
        set
        {
            if (_controller.Profile.Elevons == value)
                return;
            _controller.Profile.Elevons = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _profileText = "";

    /// <summary>Where the device's profile file lives (tooltip).</summary>
    [ObservableProperty]
    private string _profilePath = "";

    private static string Count(int n, string one, string many) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    // ---- sending

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnableText), nameof(IsIdle))]
    private bool _isSending;

    public bool IsIdle => !IsSending;

    public string EnableText => IsSending ? "Disable" : "Enable";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _sendText = "";

    [ObservableProperty]
    private string _lastActionText = "";

    // ---- lifecycle

    public override void OnShown()
    {
        _shown = true;
        RefreshModes();
        if (_backend == null)
        {
            _backendTask ??= Task.Run(() =>
            {
                IJoystickBackend b;
                try
                {
                    b = _backendFactory();
                }
                catch (Exception ex)
                {
                    Log.Warn("joystick backend", ex);
                    b = new NoJoystickBackend("No joystick support: " + ex.Message);
                }

                Services.Dispatcher.Post(() =>
                {
                    _backend = b;
                    BackendText = b.Status;
                    BackendMissing = b is NoJoystickBackend;
                    if (_shown || IsSending)
                        RefreshDevices();
                });
            });
            return;
        }

        RefreshDevices();
    }

    public override void OnHidden()
    {
        _shown = false;
        // a joystick that is not driving the vehicle is let go, as Mission Planner closes it
        if (!IsSending)
            CloseDevice();
    }

    /// <summary>Waits for the backend (tests).</summary>
    internal Task BackendReady => _backendTask ?? Task.CompletedTask;

    [RelayCommand]
    private void RefreshDevices()
    {
        if (_backend == null)
            return;
        IReadOnlyList<JoystickDeviceInfo> list;
        try
        {
            list = _backend.GetDevices();
        }
        catch (Exception ex)
        {
            StatusText = "Could not list joysticks: " + ex.Message;
            list = [];
        }

        var keep = SelectedDevice?.Name ?? Settings.Instance[DeviceKey];
        _switching = true;
        Devices.Clear();
        foreach (var d in list)
            Devices.Add(d);
        _switching = false;

        var pick = Devices.FirstOrDefault(d => d.Name == keep) ?? Devices.FirstOrDefault();
        if (pick != null && _device != null && _device.Info.Id == pick.Id && _device.IsAttached)
        {
            _switching = true;
            SelectedDevice = pick;
            _switching = false;
            return;
        }

        SelectedDevice = pick;
        if (pick == null)
        {
            CloseDevice();
            DeviceText = _backend is NoJoystickBackend ? "" : "No joystick found. Plug one in and press refresh.";
        }
    }

    partial void OnSelectedDeviceChanged(JoystickDeviceInfo? value)
    {
        if (_switching)
            return;
        if (IsSending)
        {
            _controller.SetSending(false);
            IsSending = false;
        }

        OpenDevice(value);
    }

    private void OpenDevice(JoystickDeviceInfo? info)
    {
        CloseDevice();
        if (info == null || _backend == null)
            return;

        try
        {
            _device = _backend.Open(info);
        }
        catch (Exception ex)
        {
            DeviceText = ex.Message;
            OnPropertyChanged(nameof(HasDevice));
            return;
        }

        Settings.Instance[DeviceKey] = info.Name;
        var rows = Math.Min(16, Math.Max(_device.ButtonCount, 1));
        var stored = _store.Load(info.Name);
        var profile = (stored ?? JoystickProfile.CreateDefault(info.Name, rows)).Normalise(rows);
        profile.Device = info.Name;
        _controller.Profile = profile;
        _controller.Attach(_device);
        _controller.Start();

        DeviceText = $"{Count(_device.AxisCount, "axis", "axes")}, {Count(_device.ButtonCount, "button", "buttons")}, " +
                     $"{Count(_device.HatCount, "hat", "hats")}  ·  {info.Backend}";
        BuildRows(profile);
        IsDirty = false;
        ProfilePath = _store.PathFor(info.Name);
        ProfileText = stored != null ? "Profile loaded (" + Path.GetFileName(ProfilePath) + ")" : "New profile (not saved yet)";
        OnPropertyChanged(nameof(HasDevice));
        OnPropertyChanged(nameof(ManualControl));
        OnPropertyChanged(nameof(Elevons));
    }

    private void CloseDevice()
    {
        if (_device == null)
            return;
        _controller.Attach(null);
        _ = _controller.StopAsync();
        try
        {
            _device.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("joystick close", ex);
        }

        _device = null;
        Axes.Clear();
        ButtonStates.Clear();
        Hats.Clear();
        OnPropertyChanged(nameof(HasDevice));
    }

    private void BuildRows(JoystickProfile profile)
    {
        var dev = _device!;
        InputOptions.Clear();
        var inputs = new List<JoystickInput> { JoystickInput.None };
        for (var i = 0; i < dev.AxisCount; i++)
            inputs.Add(JoystickInput.Axis(i));
        for (var i = 0; i < dev.HatCount; i++)
        {
            inputs.Add(new JoystickInput(JoystickInputKind.HatVertical, i));
            inputs.Add(new JoystickInput(JoystickInputKind.HatHorizontal, i));
        }

        inputs.Add(new JoystickInput(JoystickInputKind.ButtonAxis, 0));
        inputs.Add(new JoystickInput(JoystickInputKind.ButtonAxis, 1));
        inputs.Add(new JoystickInput(JoystickInputKind.Trim));
        // keep a saved mapping visible even if this device reports fewer axes
        foreach (var c in profile.Channels)
            if (!inputs.Contains(c.Input))
                inputs.Add(c.Input);
        foreach (var i in inputs)
            InputOptions.Add(new InputOption(i));

        ButtonOptions.Clear();
        ButtonOptions.Add(new ButtonOption(-1));
        var buttons = Math.Max(dev.ButtonCount, profile.Buttons.Max(b => b.Button) + 1);
        for (var i = 0; i < buttons; i++)
            ButtonOptions.Add(new ButtonOption(i));

        Channels.Clear();
        foreach (var map in profile.Channels)
        {
            Channels.Add(new ChannelRowViewModel(this, map, RoleName(map.Channel)) { IsVisible = map.Channel <= 8 || ShowAllChannels });
        }

        ButtonRows.Clear();
        for (var i = 0; i < profile.Buttons.Count; i++)
            ButtonRows.Add(new ButtonRowViewModel(this, profile.Buttons[i], i));
    }

    private string RoleName(int channel)
    {
        int Map(string p, int d) => (int)(Vehicle.GetParam(p) ?? d);
        if (channel == Map("RCMAP_ROLL", 1))
            return "Roll";
        if (channel == Map("RCMAP_PITCH", 2))
            return "Pitch";
        if (channel == Map("RCMAP_THROTTLE", 3))
            return "Throttle";
        if (channel == Map("RCMAP_YAW", 4))
            return "Yaw";
        return "";
    }

    private void RefreshModes()
    {
        var current = Modes.ToList();
        List<string> names;
        try
        {
            names = MissionPlanner.ArduPilot.Common.getModesList(Services.Session.Cs.firmware)?.Select(kv => kv.Value).ToList() ?? new();
        }
        catch (Exception)
        {
            names = new();
        }

        if (names.Count == 0)
            names = ["Stabilize", "AltHold", "Loiter", "Guided", "Auto", "RTL", "Land", "Manual", "FBWA"];
        if (names.SequenceEqual(current))
            return;
        Modes.Clear();
        foreach (var n in names)
            Modes.Add(n);
        foreach (var row in ButtonRows)
            row.Refresh();
    }

    partial void OnShowAllChannelsChanged(bool value)
    {
        foreach (var c in Channels)
            c.IsVisible = c.Channel <= 8 || value;
    }

    internal void MarkDirty()
    {
        IsDirty = true;
        ProfileText = "Unsaved changes";
    }

    // ---- commands

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_device == null)
        {
            await Services.Dialogs.ShowMessageAsync(Title, "Select a joystick first.", MessageKind.Warning);
            return;
        }

        try
        {
            _store.Save(_controller.Profile);
            IsDirty = false;
            ProfilePath = _store.PathFor(_controller.Profile.Device);
            ProfileText = "Saved (" + Path.GetFileName(ProfilePath) + ")";
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ShowMessageAsync(Title, "Could not save the profile: " + ex.Message, MessageKind.Error);
        }
    }

    [RelayCommand]
    private async Task ResetProfileAsync()
    {
        if (_device == null ||
            !await Services.Dialogs.ConfirmAsync(Title, "Clear every channel and button mapping for this joystick?", "Clear"))
            return;
        var rows = Math.Min(16, Math.Max(_device.ButtonCount, 1));
        var profile = JoystickProfile.CreateDefault(_device.Info.Name, rows);
        _controller.Profile = profile;
        BuildRows(profile);
        OnPropertyChanged(nameof(ManualControl));
        OnPropertyChanged(nameof(Elevons));
        MarkDirty();
    }

    [RelayCommand]
    private async Task ToggleSendingAsync()
    {
        if (IsSending)
        {
            _controller.SetSending(false);
            IsSending = false;
            StatusText = "Stopped; the overridden channels were handed back to the RC receiver.";
            return;
        }

        if (_device == null || !_device.IsAttached)
        {
            await Services.Dialogs.ShowMessageAsync(Title, "Please connect a joystick.", MessageKind.Warning);
            return;
        }

        if (!_controller.Profile.Channels.Any(c => c.Input.Kind != JoystickInputKind.None) &&
            !_controller.Profile.Buttons.Any(b => b.Button >= 0 && b.Function != JoystickButtonFunction.None))
        {
            await Services.Dialogs.ShowMessageAsync(Title, "Map at least one channel or button first.", MessageKind.Warning);
            return;
        }

        _controller.Start();
        _controller.SetSending(true);
        IsSending = true;
        StatusText = "";
    }

    internal async Task DetectAxisAsync(ChannelRowViewModel row)
    {
        if (_device == null)
            return;
        row.IsDetecting = true;
        StatusText = $"Move the stick or hat for {row.Label} now…";
        try
        {
            var baseline = _controller.Snapshot;
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(50);
                var now = _controller.Snapshot;
                var found = DetectInput(baseline, now);
                if (found is { } input)
                {
                    var option = InputOptions.FirstOrDefault(o => o.Input == input);
                    if (option == null)
                    {
                        option = new InputOption(input);
                        InputOptions.Add(option);
                    }

                    row.SelectedInput = option;
                    StatusText = $"{row.Label} ← {input.Label}";
                    return;
                }
            }

            StatusText = "No movement detected.";
        }
        finally
        {
            row.IsDetecting = false;
        }
    }

    /// <summary>The axis that moved more than a quarter of its travel, or a hat that was pressed.</summary>
    internal static JoystickInput? DetectInput(JoystickSnapshot before, JoystickSnapshot now)
    {
        const int threshold = 16000;
        var best = -1;
        var bestDelta = threshold;
        for (var i = 0; i < Math.Min(before.Axes.Count, now.Axes.Count); i++)
        {
            var d = Math.Abs(now.Axes[i] - before.Axes[i]);
            if (d > bestDelta)
            {
                best = i;
                bestDelta = d;
            }
        }

        if (best >= 0)
            return JoystickInput.Axis(best);

        for (var i = 0; i < Math.Min(before.Hats.Count, now.Hats.Count); i++)
        {
            var changed = now.Hats[i] & ~before.Hats[i];
            if ((changed & (HatDirection.Up | HatDirection.Down)) != 0)
                return new JoystickInput(JoystickInputKind.HatVertical, i);
            if ((changed & (HatDirection.Left | HatDirection.Right)) != 0)
                return new JoystickInput(JoystickInputKind.HatHorizontal, i);
        }

        return null;
    }

    internal async Task DetectButtonAsync(ButtonRowViewModel row)
    {
        if (_device == null)
            return;
        row.IsDetecting = true;
        StatusText = $"Press the joystick button for row {row.Title} now…";
        try
        {
            var baseline = _controller.Snapshot.Buttons.ToArray();
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(50);
                var now = _controller.Snapshot;
                for (var i = 0; i < now.Buttons.Count; i++)
                {
                    var was = i < baseline.Length && baseline[i];
                    if (now.Buttons[i] == was)
                        continue;
                    if (!ButtonOptions.Any(o => o.Button == i))
                        ButtonOptions.Add(new ButtonOption(i));
                    row.SelectedButton = ButtonOptions.First(o => o.Button == i);
                    StatusText = $"Row {row.Title} ← button {i + 1}";
                    return;
                }
            }

            StatusText = "No button press detected.";
        }
        finally
        {
            row.IsDetecting = false;
        }
    }

    // ---- live display

    public override void Tick()
    {
        _tick++;
        if (_tick % 100 == 0 && _shown && _backend is not null and not NoJoystickBackend && !IsSending)
            RefreshDevices();
        UpdateLive();
    }

    internal void UpdateLive()
    {
        var snap = _controller.Snapshot;
        Sync(Axes, snap.Axes.Count, i => new AxisReading(i));
        for (var i = 0; i < snap.Axes.Count; i++)
        {
            Axes[i].Value = snap.Axes[i];
            Axes[i].Text = snap.Axes[i].ToString(CultureInfo.InvariantCulture);
        }

        Sync(ButtonStates, snap.Buttons.Count, i => new ButtonReading(i));
        for (var i = 0; i < snap.Buttons.Count; i++)
            ButtonStates[i].IsPressed = snap.Buttons[i];

        Sync(Hats, snap.Hats.Count, i => new HatReading(i));
        for (var i = 0; i < snap.Hats.Count; i++)
            Hats[i].Text = snap.Hats[i] == HatDirection.Centered ? "centre" : snap.Hats[i].ToString().Replace(", ", "+", StringComparison.Ordinal).ToLowerInvariant();

        var outputs = _controller.Outputs;
        var cs = Services.Session.Cs;
        var connected = Vehicle.IsConnected;
        foreach (var row in Channels)
        {
            var pwm = outputs[row.Channel - 1];
            row.Output = pwm;
            row.OutputText = pwm == 0 ? "—" : pwm.ToString(CultureInfo.InvariantCulture);
            var vin = connected ? ChannelIn(cs, row.Channel) : 0;
            row.VehicleText = vin > 0 ? vin.ToString("0", CultureInfo.InvariantCulture) : "";
        }

        foreach (var row in ButtonRows)
            row.IsPressed = row.Map.Button >= 0 && snap.Button(row.Map.Button);

        if (IsSending)
        {
            var kind = _controller.Profile.ManualControl ? "MANUAL_CONTROL" : "RC_CHANNELS_OVERRIDE";
            SendText = connected
                ? $"Sending {kind} at {JoystickController.RateHz} Hz to system {Vehicle.SysId}  ·  {_controller.PacketsSent:N0} packets"
                : $"Enabled; waiting for a vehicle connection ({kind}).";
        }
        else
        {
            SendText = "Not sending. The page shows what would be sent.";
        }

        if (_controller.LastAction is { } action)
            LastActionText = "Last button action: " + action;
    }

    private static void Sync<T>(ObservableCollection<T> list, int count, Func<int, T> make)
    {
        while (list.Count > count)
            list.RemoveAt(list.Count - 1);
        while (list.Count < count)
            list.Add(make(list.Count));
    }

    internal static float ChannelIn(CurrentState cs, int ch) => ch switch
    {
        1 => cs.ch1in, 2 => cs.ch2in, 3 => cs.ch3in, 4 => cs.ch4in,
        5 => cs.ch5in, 6 => cs.ch6in, 7 => cs.ch7in, 8 => cs.ch8in,
        9 => cs.ch9in, 10 => cs.ch10in, 11 => cs.ch11in, 12 => cs.ch12in,
        13 => cs.ch13in, 14 => cs.ch14in, 15 => cs.ch15in, 16 => cs.ch16in,
        _ => 0,
    };
}
