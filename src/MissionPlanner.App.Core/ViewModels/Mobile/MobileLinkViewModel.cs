using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Links.Usb;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Mobile;

/// <summary>A way to reach the vehicle from a phone: a USB radio or board, Wi-Fi telemetry (UDP), a TCP bridge, or SITL.</summary>
public sealed record MobileLinkChoice(LinkKind Kind, string Title, string Hint);

/// <summary>
/// The phone's connect page: pick USB (a telemetry radio or the flight controller on an OTG
/// cable, when the phone has a USB host), UDP (listen; ESP8266 / ELRS / Herelink Wi-Fi bridges
/// send to 14550), UDP to a host, or TCP; recent links one tap away; connect at start if wanted.
/// </summary>
public sealed partial class MobileLinkViewModel : ObservableObject
{
    public const string RecentKey = "next_mobile_links";
    public const string AutoKey = "next_mobile_autoconnect";
    private const int MaxRecent = 6;

    private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(MobileLinkViewModel));

    private readonly AppServices _services;
    private readonly IUsbHost? _usb;
    private CancellationTokenSource? _cancel;
    private bool _fillingUsb;
    private bool _usbListed;

    public MobileLinkViewModel(AppServices services, IUsbHost? usb = null)
    {
        _services = services;
        _usb = usb ?? LinkFactory.UsbHost;
        var kinds = new List<MobileLinkChoice>();
        if (_usb != null)
            kinds.Add(new(LinkKind.Serial, "USB (radio or flight controller)", "A telemetry radio or the flight controller's own USB port, on an OTG adapter or a USB-C cable."));
        kinds.Add(new(LinkKind.UdpListen, "UDP (Wi-Fi telemetry)", "Listens for the vehicle on this port: Wi-Fi bridges and SITL send to 14550."));
        kinds.Add(new(LinkKind.Tcp, "TCP", "Connects to a host and port, e.g. a TCP telemetry bridge or SITL on 5760."));
        kinds.Add(new(LinkKind.UdpConnect, "UDP to host", "Sends to and receives from a host and port."));
        Kinds = kinds;
        _selectedKind = Kinds[0];
        _autoConnect = Settings.Instance.GetBoolean(AutoKey, false);
        RefreshUsb();
        foreach (var r in (Settings.Instance[RecentKey] ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var spec = LinkSpec.Parse(r);
                // a USB link means nothing on a device without a USB host
                if (spec.Kind != LinkKind.Serial || (_usb != null && UsbSerialAddress.IsUsb(spec.Address)))
                    Recent.Add(spec);
            }
            catch (FormatException)
            {
            }
        }

        if (Recent.FirstOrDefault() is { } last)
            Fill(last);
        services.Session.StateChanged += (_, _) => services.Dispatcher.Post(OnStateChanged);
        if (_usb != null)
            _usb.DevicesChanged += (_, _) => services.Dispatcher.Post(RefreshUsb);
    }

    public IReadOnlyList<MobileLinkChoice> Kinds { get; }

    public ObservableCollection<LinkSpec> Recent { get; } = new();

    /// <summary>Serial ports on the USB devices plugged in now.</summary>
    public ObservableCollection<UsbSerialPortInfo> UsbPorts { get; } = new();

    public static IReadOnlyList<string> BaudOptions { get; } =
        ["57600", "115200", "921600", "1500000", "460800", "230400", "38400", "19200", "9600"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsHost), nameof(NeedsPort), nameof(IsUsb), nameof(ShowNoUsb), nameof(KindHint))]
    private MobileLinkChoice _selectedKind;

    [ObservableProperty]
    private UsbSerialPortInfo? _selectedUsbPort;

    [ObservableProperty]
    private string _baud = "57600";

    [ObservableProperty]
    private string _host = "192.168.4.1";

    [ObservableProperty]
    private string _port = "14550";

    [ObservableProperty]
    private bool _autoConnect;

    partial void OnAutoConnectChanged(bool value) => Settings.Instance[AutoKey] = value.ToString();

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _error = "";

    public bool IsUsb => SelectedKind.Kind == LinkKind.Serial;

    public bool NeedsHost => SelectedKind.Kind is LinkKind.Tcp or LinkKind.UdpConnect;

    public bool NeedsPort => !IsUsb;

    public bool HasUsbPorts => UsbPorts.Count > 0;

    /// <summary>USB chosen and nothing plugged in.</summary>
    public bool ShowNoUsb => IsUsb && UsbPorts.Count == 0;

    public string KindHint => SelectedKind.Hint;

    public LinkState State => _services.Session.State;

    public bool IsConnected => State == LinkState.Connected;

    public bool IsConnecting => State == LinkState.Connecting;

    public string ButtonText => State switch
    {
        LinkState.Connected => "Disconnect",
        LinkState.Connecting => "Cancel",
        _ => "Connect",
    };

    /// <summary>What the link is, once connected ("udp:14550 · ArduCopter V4.6.2").</summary>
    public string ConnectedText => _services.Session is { IsConnected: true } s
        ? $"{Describe(s.Link)} · {FirmwareName(s.Cs.firmware)} {s.Cs.version?.ToString(3)}".Trim()
        : "";

    /// <summary>"ArduCopter" rather than the library's ArduCopter2.</summary>
    public static string FirmwareName(MissionPlanner.ArduPilot.Firmwares fw) =>
        fw == MissionPlanner.ArduPilot.Firmwares.ArduCopter2 ? "ArduCopter" : fw.ToString();

    /// <summary>"FT231X USB UART (FTDI) · 57600" for a USB link, the link itself otherwise.</summary>
    public string Describe(LinkSpec? spec)
    {
        if (spec == null)
            return "";
        if (spec.Kind != LinkKind.Serial || UsbSerialAddress.TryParse(spec.Address) is not { } address)
            return spec.ToString();
        var name = UsbSerialProbe.Find(UsbPorts, address)?.Title
                   ?? string.Create(CultureInfo.InvariantCulture, $"USB {address.VendorId:x4}:{address.ProductId:x4}");
        return string.Create(CultureInfo.InvariantCulture, $"{name} · {spec.Value}");
    }

    partial void OnSelectedKindChanged(MobileLinkChoice value)
    {
        if (value.Kind == LinkKind.Tcp && Port == "14550")
            Port = "5760";
        else if (value.Kind != LinkKind.Tcp && Port == "5760")
            Port = "14550";
    }

    partial void OnSelectedUsbPortChanged(UsbSerialPortInfo? value)
    {
        // radios at 57600, a board's own USB at 115200 (where it does not matter)
        if (value != null && !_fillingUsb)
            Baud = value.DefaultBaud.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Lists the USB serial ports again; one just plugged in is chosen.</summary>
    public void RefreshUsb()
    {
        if (_usb == null)
            return;
        IReadOnlyList<UsbSerialPortInfo> ports;
        try
        {
            ports = UsbSerialProbe.Ports(_usb.Devices());
        }
        catch (Exception ex)
        {
            Log.Warn("listing USB devices", ex);
            ports = [];
        }

        var before = UsbPorts.Select(p => p.Address).ToHashSet();
        var selected = SelectedUsbPort?.Address;
        _fillingUsb = true;
        try
        {
            UsbPorts.Clear();
            foreach (var p in ports)
                UsbPorts.Add(p);
            SelectedUsbPort = (selected == null ? null : UsbSerialProbe.Find(UsbPorts, selected)) ?? UsbPorts.FirstOrDefault();
        }
        finally
        {
            _fillingUsb = false;
        }

        var added = UsbPorts.FirstOrDefault(p => !before.Contains(p.Address));
        if (added != null && (_usbListed || before.Count > 0))
        {
            // plugged in while the app runs: that is what the user wants to connect to
            SelectedUsbPort = added;
            if (State == LinkState.Disconnected)
                SelectedKind = Kinds.First(k => k.Kind == LinkKind.Serial);
        }

        _usbListed = true;
        OnPropertyChanged(nameof(HasUsbPorts));
        OnPropertyChanged(nameof(ShowNoUsb));
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ConnectedText));
    }

    /// <summary>The link the fields describe, or null with <see cref="Error"/> set.</summary>
    public LinkSpec? BuildSpec()
    {
        Error = "";
        if (IsUsb)
        {
            if (SelectedUsbPort == null)
            {
                Error = "Plug in the telemetry radio or the flight controller (an OTG adapter may be needed).";
                return null;
            }

            if (!int.TryParse(Baud.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var baud) || baud <= 0)
            {
                Error = $"\"{Baud}\" is not a baud rate.";
                return null;
            }

            return LinkSpec.Serial(SelectedUsbPort.Address.ToString(), baud);
        }

        if (!int.TryParse(Port.Trim(), out var port) || port is < 1 or > 65535)
        {
            Error = $"\"{Port}\" is not a port number (1–65535).";
            return null;
        }

        var host = Host.Trim();
        if (NeedsHost && host.Length == 0)
        {
            Error = "Enter the host (IP address or name).";
            return null;
        }

        return SelectedKind.Kind switch
        {
            LinkKind.Tcp => LinkSpec.Tcp(host, port),
            LinkKind.UdpConnect => LinkSpec.UdpConnect(host, port),
            _ => LinkSpec.UdpListen(port),
        };
    }

    // runs again while connecting: the same button is Cancel then
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Toggle()
    {
        switch (State)
        {
            case LinkState.Connecting:
                _cancel?.Cancel();
                return;
            case LinkState.Connected:
                await _services.Session.DisconnectAsync();
                return;
        }

        if (BuildSpec() is { } spec)
            await ConnectAsync(spec);
    }

    [RelayCommand]
    private Task ConnectRecent(LinkSpec? spec)
    {
        if (spec == null || State != LinkState.Disconnected)
            return Task.CompletedTask;
        Fill(spec);
        return ConnectAsync(spec);
    }

    /// <summary>At start: the last link again, if the user asked for that.</summary>
    public Task ConnectAtStartAsync() =>
        AutoConnect && State == LinkState.Disconnected && Recent.FirstOrDefault() is { } last ? ConnectAsync(last) : Task.CompletedTask;

    public async Task<bool> ConnectAsync(LinkSpec spec)
    {
        Error = "";
        if (!await AllowUsbAsync(spec))
        {
            ProgressText = "";
            return false;
        }

        _cancel = new CancellationTokenSource();
        var progress = new Progress<OperationProgress>(p => ProgressText = p.Status);
        try
        {
            ProgressText = "Connecting…";
            await _services.Session.ConnectAsync(spec, getParams: true, progress, _cancel.Token);
            Remember(spec);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ConnectException ex)
        {
            Error = ex.Message.Split('\n')[0];
            return false;
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            ProgressText = "";
            OnStateChanged();
        }
    }

    /// <summary>A USB link needs the device plugged in and the user's permission (Android asks once per device).</summary>
    private async Task<bool> AllowUsbAsync(LinkSpec spec)
    {
        if (spec.Kind != LinkKind.Serial || _usb == null || UsbSerialAddress.TryParse(spec.Address) is not { } address)
            return true;
        RefreshUsb();
        var port = UsbSerialProbe.Find(UsbPorts, address);
        if (port == null)
        {
            Error = "The USB device is not plugged in.";
            return false;
        }

        if (_usb.HasPermission(port.Device))
            return true;
        ProgressText = "Waiting for USB permission…";
        if (await _usb.RequestPermissionAsync(port.Device))
            return true;
        Error = "Mission Planner was not allowed to use the USB device.";
        return false;
    }

    private void Remember(LinkSpec spec)
    {
        var existing = Recent.FirstOrDefault(r => r == spec);
        if (existing != null)
            Recent.Remove(existing);
        Recent.Insert(0, spec);
        while (Recent.Count > MaxRecent)
            Recent.RemoveAt(Recent.Count - 1);
        Settings.Instance[RecentKey] = string.Join("|", Recent.Select(r => r.ToString()));
        Settings.Instance.Save();
    }

    private void Fill(LinkSpec spec)
    {
        SelectedKind = Kinds.FirstOrDefault(k => k.Kind == spec.Kind) ?? Kinds[0];
        if (spec.Kind == LinkKind.Serial)
        {
            if (UsbSerialAddress.TryParse(spec.Address) is { } address && UsbSerialProbe.Find(UsbPorts, address) is { } port)
            {
                _fillingUsb = true;
                SelectedUsbPort = port;
                _fillingUsb = false;
            }

            Baud = spec.Value.ToString(CultureInfo.InvariantCulture);
            return;
        }

        if (spec.Kind != LinkKind.UdpListen)
            Host = spec.Address;
        Port = spec.Value.ToString(CultureInfo.InvariantCulture);
    }
}
