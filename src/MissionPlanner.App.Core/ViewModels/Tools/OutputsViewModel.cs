using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Outputs;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A choice in a combo box.</summary>
public sealed record OutputChoice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// TOOLS > Outputs: Mission Planner's MAVLink mirror, NMEA, Cursor on Target and MicroDrone outputs
/// (its Ctrl+G and Ctrl+F > Mavlink / NMEA / CoT / MicroDrone windows), any number at once.
/// </summary>
public sealed partial class OutputsViewModel : ToolPageViewModel
{
    private int _ticks;
    private bool _loading;

    public OutputsViewModel(AppServices services)
        : base("Outputs",
            "Send the vehicle's MAVLink stream or its position to other programs and devices: a second ground station, an antenna tracker, TAK, a GPS-input device. Outputs keep running on every screen and start again with the app.",
            services)
    {
    }

    public override bool NeedsVehicle => false;

    public IReadOnlyList<OutputItem> Items => Services.Outputs.Items;

    public static IReadOnlyList<OutputChoice<OutputKind>> Kinds { get; } =
        OutputItem.Names.Select(p => new OutputChoice<OutputKind>(p.Key, p.Value)).ToList();

    public static IReadOnlyList<OutputChoice<OutputTransportKind>> Transports { get; } =
    [
        new(OutputTransportKind.UdpSend, "UDP to an address"),
        new(OutputTransportKind.UdpListen, "UDP, listen on a port"),
        new(OutputTransportKind.TcpServer, "TCP server (others connect)"),
        new(OutputTransportKind.TcpClient, "TCP client (connect to a server)"),
        new(OutputTransportKind.Serial, "Serial port"),
    ];

    public static IReadOnlyList<int> Bauds { get; } = [4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    public IReadOnlyList<string> SerialPorts { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanEdit))]
    private OutputItem? _selected;

    public bool HasSelection => Selected != null;

    /// <summary>Settings can be changed while the output is stopped.</summary>
    public bool CanEdit => Selected is { IsRunning: false };

    [ObservableProperty]
    private OutputChoice<OutputKind>? _newKind = Kinds[0];

    // ---- editor of the selected output --------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial), nameof(ShowAddress), nameof(PortLabel), nameof(AddressLabel))]
    private OutputChoice<OutputTransportKind>? _transport;

    [ObservableProperty]
    private string _address = "";

    [ObservableProperty]
    private decimal _port;

    [ObservableProperty]
    private decimal _rateHz;

    [ObservableProperty]
    private bool _allowWrite;

    [ObservableProperty]
    private string _cotType = "";

    [ObservableProperty]
    private string _cotCallsign = "";

    public bool IsSerial => Transport?.Value == OutputTransportKind.Serial;

    public bool ShowAddress => Transport?.Value is OutputTransportKind.TcpClient or OutputTransportKind.UdpSend;

    public string AddressLabel => Transport?.Value == OutputTransportKind.UdpSend
        ? "Address (IP, broadcast 255.255.255.255 or multicast such as 239.2.3.1)"
        : "Server (host or IP address)";

    public string PortLabel => IsSerial ? "Baud rate" : "Port";

    public bool IsMirror => Selected?.Config.Kind == OutputKind.MavlinkMirror;

    public bool IsCot => Selected?.Config.Kind == OutputKind.CursorOnTarget;

    public bool HasRate => Selected is { Config.Kind: OutputKind.Nmea or OutputKind.CursorOnTarget or OutputKind.MicroDrone };

    public override void OnShown()
    {
        base.OnShown();
        SerialPorts = LinkFactory.GetSerialPortNames();
        OnPropertyChanged(nameof(SerialPorts));
        Selected ??= Items.FirstOrDefault();
        foreach (var item in Items)
            item.UpdateStatus();
    }

    public override void Tick()
    {
        base.Tick();
        if (++_ticks % 20 != 0)
            return;
        foreach (var item in Items.Where(i => i.IsRunning))
            item.UpdateStatus();
        OnPropertyChanged(nameof(CanEdit));
    }

    partial void OnSelectedChanged(OutputItem? value)
    {
        _loading = true;
        try
        {
            if (value == null)
                return;
            var c = value.Config;
            Transport = Transports.FirstOrDefault(t => t.Value == c.Transport);
            Address = c.Address;
            Port = c.Port;
            RateHz = (decimal)c.RateHz;
            AllowWrite = c.AllowWrite;
            CotType = c.CotType;
            CotCallsign = c.CotCallsign;
        }
        finally
        {
            _loading = false;
            OnPropertyChanged(nameof(IsMirror));
            OnPropertyChanged(nameof(IsCot));
            OnPropertyChanged(nameof(HasRate));
        }
    }

    partial void OnTransportChanged(OutputChoice<OutputTransportKind>? value)
    {
        if (_loading || Selected == null || value == null)
            return;
        // serial wants a port name and a baud, the network kinds a port number
        var wasSerial = Selected.Config.Transport == OutputTransportKind.Serial;
        if (value.Value == OutputTransportKind.Serial && !wasSerial)
        {
            Port = 57600;
            Address = SerialPorts.FirstOrDefault() ?? "";
        }
        else if (value.Value != OutputTransportKind.Serial && wasSerial)
        {
            var d = OutputConfig.Default(Selected.Config.Kind);
            Port = d.Transport == OutputTransportKind.Serial ? 14551 : d.Port;
            Address = "127.0.0.1";
        }

        Apply();
    }

    partial void OnAddressChanged(string value) => Apply();

    partial void OnPortChanged(decimal value)
    {
        OnPropertyChanged(nameof(Baud));
        Apply();
    }

    /// <summary>The baud rate of a serial output (stored in Port).</summary>
    public int Baud
    {
        get => (int)Port;
        set => Port = value;
    }

    partial void OnRateHzChanged(decimal value) => Apply();

    partial void OnAllowWriteChanged(bool value) => Apply();

    partial void OnCotTypeChanged(string value) => Apply();

    partial void OnCotCallsignChanged(string value) => Apply();

    private void Apply()
    {
        if (_loading || Selected == null)
            return;
        var c = Selected.Config;
        if (Transport != null)
            c.Transport = Transport.Value;
        c.Address = Address.Trim();
        c.Port = (int)Port;
        c.RateHz = (double)Math.Max(0.1m, RateHz);
        c.AllowWrite = AllowWrite;
        c.CotType = CotType.Trim();
        c.CotCallsign = CotCallsign.Trim();
        Selected.SettingsChanged();
        Services.Outputs.Save();
    }

    [RelayCommand]
    private void Add()
    {
        if (NewKind == null)
            return;
        Selected = Services.Outputs.Add(NewKind.Value);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected is not { } item)
            return;
        await Services.Outputs.RemoveAsync(item);
        Selected = Items.FirstOrDefault();
    }

    [RelayCommand]
    private async Task StartStopAsync(OutputItem? item)
    {
        item ??= Selected;
        if (item == null)
            return;
        if (item.IsRunning)
        {
            await item.StopAsync();
        }
        else
        {
            if (item.Config.Kind == OutputKind.MavlinkMirror && item.Config.AllowWrite &&
                item.Config.Transport is OutputTransportKind.TcpServer or OutputTransportKind.UdpListen &&
                !await Services.Dialogs.ConfirmAsync("MAVLink mirror",
                    "Anyone who can reach this port will be able to command the vehicle. Untick \"Let the other side control the vehicle\" for a read-only mirror.",
                    "Start"))
                return;
            item.Start();
        }

        Services.Outputs.Save();
        OnPropertyChanged(nameof(CanEdit));
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        SerialPorts = LinkFactory.GetSerialPortNames();
        OnPropertyChanged(nameof(SerialPorts));
    }
}
