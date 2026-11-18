using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

public enum TerminalMode
{
    /// <summary>A port on the connected vehicle through SERIAL_CONTROL (the system shell by default).</summary>
    MavlinkShell,

    /// <summary>A serial port on this computer.</summary>
    SerialPort,
}

/// <summary>A SERIAL_CONTROL device offered in the list.</summary>
public sealed record TerminalDevice(MAVLink.SERIAL_CONTROL_DEV Device, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A baud rate in a list, 0 meaning "leave as is".</summary>
public sealed record BaudOption(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A line ending choice for sent commands.</summary>
public sealed record LineEnding(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// Terminal (Mission Planner's ConfigTerminal): a text console on the vehicle's system shell
/// (NSH / ArduPilot shell) or another of its ports through MAVLink SERIAL_CONTROL, or on a serial
/// port of this computer. Received text shows in a scrolling monospace view; typed commands keep a
/// history. The port is released when the page is left, as Mission Planner does.
/// </summary>
public sealed partial class TerminalViewModel : PageViewModel
{
    private readonly Func<ISerialControlTransport> _serialControl;
    private readonly Func<string, int, IByteLink> _openSerial;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly TerminalBuffer _buffer = new();
    private readonly CommandHistory _history = new();
    private IByteLink? _link;
    private CancellationTokenSource? _readStop;
    private Task _reader = Task.CompletedTask;
    private long _openedAt;
    private bool _heardBack;
    private bool _silenceHinted;

    public TerminalViewModel(AppServices services)
        : this(services, () => new MavlinkSerialControlTransport(services.Session), CommsByteLink.Serial)
    {
    }

    /// <summary>Tests pass a fake vehicle shell and fake serial ports.</summary>
    internal TerminalViewModel(AppServices services, Func<ISerialControlTransport> serialControl, Func<string, int, IByteLink> openSerial)
        : base("Terminal")
    {
        Services = services;
        _serialControl = serialControl;
        _openSerial = openSerial;

        Devices =
        [
            new(MAVLink.SERIAL_CONTROL_DEV.SHELL, "System shell (NSH)"),
            .. Enumerable.Range(0, 10).Select(i => new TerminalDevice(MAVLink.SERIAL_CONTROL_DEV.SERIAL_CONTROL_SERIAL0 + (byte)i, $"SERIAL{i}")),
            new(MAVLink.SERIAL_CONTROL_DEV.TELEM1, "TELEM1"),
            new(MAVLink.SERIAL_CONTROL_DEV.TELEM2, "TELEM2"),
            new(MAVLink.SERIAL_CONTROL_DEV.GPS1, "GPS1"),
            new(MAVLink.SERIAL_CONTROL_DEV.GPS2, "GPS2"),
        ];
        _selectedDevice = Devices[0];
        DeviceBauds = [new(0, "keep baud"), .. new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 }.Select(b => new BaudOption(b, b.ToString()))];
        _selectedDeviceBaud = DeviceBauds[0];
        Bauds = [4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1500000];
        _selectedBaud = int.TryParse(Settings.Instance["terminal_baud"], out var b) && b > 0 ? b : 115200;
        LineEndings = [new("CR", "\r"), new("LF", "\n"), new("CR LF", "\r\n")];
        _selectedLineEnding = LineEndings[0];
        RefreshPorts();
        var last = Settings.Instance["terminal_port"];
        // a port typed in earlier (a symlink, a pty) is kept even though the OS list lacks it
        if (last != null && !Ports.Contains(last) && File.Exists(last))
            Ports.Insert(0, last);
        _selectedPort = last != null && Ports.Contains(last) ? last : Ports.FirstOrDefault();
    }

    public AppServices Services { get; }

    public IReadOnlyList<TerminalMode> Modes { get; } = [TerminalMode.MavlinkShell, TerminalMode.SerialPort];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMavlinkMode), nameof(IsSerialMode), nameof(ShowDeviceBaud))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private TerminalMode _mode = TerminalMode.MavlinkShell;

    /// <summary>Radio button: the vehicle's shell / ports over MAVLink.</summary>
    public bool IsMavlinkMode
    {
        get => Mode == TerminalMode.MavlinkShell;
        set
        {
            if (value)
                Mode = TerminalMode.MavlinkShell;
        }
    }

    /// <summary>Radio button: a serial port on this computer.</summary>
    public bool IsSerialMode
    {
        get => Mode == TerminalMode.SerialPort;
        set
        {
            if (value)
                Mode = TerminalMode.SerialPort;
        }
    }

    public IReadOnlyList<TerminalDevice> Devices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeviceBaud))]
    private TerminalDevice _selectedDevice;

    /// <summary>Baud rate to set on a vehicle serial port; 0 keeps the port's own.</summary>
    public IReadOnlyList<BaudOption> DeviceBauds { get; }

    [ObservableProperty]
    private BaudOption _selectedDeviceBaud;

    public bool ShowDeviceBaud => IsMavlinkMode && SelectedDevice.Device != MAVLink.SERIAL_CONTROL_DEV.SHELL;

    public ObservableCollection<string> Ports { get; } = new();

    public IReadOnlyList<int> Bauds { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private string? _selectedPort;

    [ObservableProperty]
    private int _selectedBaud;

    public IReadOnlyList<LineEnding> LineEndings { get; }

    [ObservableProperty]
    private LineEnding _selectedLineEnding;

    /// <summary>Show sent commands in the output (for devices that do not echo).</summary>
    [ObservableProperty]
    private bool _localEcho;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClosed), nameof(OpenText))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SendCtrlCCommand), nameof(OpenCommand))]
    private bool _isOpen;

    public bool IsClosed => !IsOpen;

    public string OpenText => IsOpen ? "Disconnect" : "Connect";

    [ObservableProperty]
    private string _status = "";

    /// <summary>The terminal text (bounded scrollback).</summary>
    [ObservableProperty]
    private string _output = "";

    [ObservableProperty]
    private string _commandText = "";

    public bool VehicleConnected => Services.Session.IsConnected;

    partial void OnSelectedPortChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            Settings.Instance["terminal_port"] = value;
    }

    partial void OnSelectedBaudChanged(int value) => Settings.Instance["terminal_baud"] = value.ToString();

    [RelayCommand]
    private void RefreshPorts()
    {
        var current = SelectedPort;
        Ports.Clear();
        foreach (var p in LinkFactory.GetSerialPortNames().OrderBy(p => p, StringComparer.Ordinal))
            Ports.Add(p);
        if (current != null && !Ports.Contains(current))
            Ports.Insert(0, current);
        if (current != null)
            SelectedPort = current;
    }

    private bool CanOpen() => IsOpen || IsMavlinkMode || !string.IsNullOrEmpty(SelectedPort);

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (IsOpen)
        {
            await CloseAsync("Closed.");
            return;
        }

        IByteLink link;
        string what;
        if (IsMavlinkMode)
        {
            if (!Services.Session.IsConnected)
            {
                await Services.Dialogs.ShowMessageAsync("Terminal", "Connect to the vehicle first: the MAVLink shell runs over the vehicle link.", MessageKind.Warning);
                return;
            }

            link = new SerialControlLink(_serialControl(), SelectedDevice.Device, ShowDeviceBaud ? (uint)SelectedDeviceBaud.Value : 0);
            what = $"{SelectedDevice.Label} on the vehicle (SERIAL_CONTROL)";
        }
        else
        {
            var port = SelectedPort!;
            var session = Services.Session;
            if (session.IsConnected && session.Link is { Kind: LinkKind.Serial } vehicleLink && SikRadioViewModel.SamePort(vehicleLink.Address, port))
            {
                await Services.Dialogs.ShowMessageAsync("Terminal",
                    $"{port} carries the vehicle link. Pick another port, or disconnect the vehicle first.", MessageKind.Warning);
                return;
            }

            link = _openSerial(port, SelectedBaud);
            what = $"{port} at {SelectedBaud} baud";
        }

        try
        {
            await Task.Run(link.Open);
        }
        catch (Exception ex)
        {
            link.Dispose();
            Status = "Could not open: " + ex.Message;
            AppendLocal($"*** Cannot open {what}: {ex.Message}");
            return;
        }

        _link = link;
        _openedAt = Environment.TickCount64;
        _heardBack = false;
        _silenceHinted = false;
        IsOpen = true;
        Status = "Connected to " + what;
        AppendLocal("*** Opened " + what);

        var stop = new CancellationTokenSource();
        _readStop = stop;
        _reader = Task.Run(() => ReadLoop(link, stop.Token));

        // wake the shell up: most print their prompt on a bare return
        if (IsMavlinkMode && SelectedDevice.Device == MAVLink.SERIAL_CONTROL_DEV.SHELL)
            await SendRawAsync(SelectedLineEnding.Value);
    }

    [RelayCommand(CanExecute = nameof(IsOpen))]
    private async Task SendAsync()
    {
        var cmd = CommandText;
        _history.Add(cmd);
        CommandText = "";
        if (LocalEcho)
            AppendLocal("> " + cmd);
        // "+++" (SiK/modem escape) goes out without a line ending, as in Mission Planner
        await SendRawAsync(cmd == "+++" ? cmd : cmd + SelectedLineEnding.Value);
    }

    /// <summary>Ctrl-C, to stop a running shell command.</summary>
    [RelayCommand(CanExecute = nameof(IsOpen))]
    private Task SendCtrlCAsync() => SendRawAsync("\x03");

    [RelayCommand]
    private void Clear()
    {
        _buffer.Clear();
        Output = "";
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        var path = await Services.Dialogs.SaveFileAsync("Save terminal log",
            $"terminal-{DateTime.Now:yyyy-MM-dd HH-mm-ss}.txt", new FileFilter("Text", "txt", "log"));
        if (path == null)
            return;
        try
        {
            await File.WriteAllTextAsync(path, _buffer.FullLog);
            Status = "Log saved to " + path;
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ShowMessageAsync("Terminal", "Could not save the log: " + ex.Message, MessageKind.Error);
        }
    }

    /// <summary>Up arrow in the command box.</summary>
    public void HistoryUp()
    {
        if (_history.Previous() is { } cmd)
            CommandText = cmd;
    }

    /// <summary>Down arrow in the command box.</summary>
    public void HistoryDown()
    {
        if (_history.Next() is { } cmd)
            CommandText = cmd;
    }

    public IReadOnlyList<string> History => _history.Items;

    public override void Tick()
    {
        var changed = false;
        while (_incoming.TryDequeue(out var text))
        {
            _buffer.Append(text);
            changed = true;
            _heardBack = true;
        }

        // the system shell exists on some autopilots only (not on SITL): say so rather than look dead
        if (IsOpen && !_heardBack && !_silenceHinted && _link is SerialControlLink { Device: MAVLink.SERIAL_CONTROL_DEV.SHELL } &&
            Environment.TickCount64 - _openedAt > 4000)
        {
            _silenceHinted = true;
            Status = "No reply from the system shell: this firmware may not have one (SITL does not). A SERIALn port works anywhere.";
        }

        if (changed)
            Output = _buffer.Text;

        OnPropertyChanged(nameof(VehicleConnected));
        if (IsOpen && _link is SerialControlLink && !Services.Session.IsConnected)
            _ = CloseAsync("The vehicle link closed.");
    }

    public override void OnHidden()
    {
        // as Mission Planner: leaving the page hands the port back
        if (IsOpen)
            _ = CloseAsync("Closed when the page was left.");
    }

    private async Task SendRawAsync(string text)
    {
        var link = _link;
        if (link == null)
            return;
        try
        {
            await Task.Run(() => link.Write(text));
        }
        catch (Exception ex)
        {
            AppendLocal("*** Send failed: " + ex.Message);
            await CloseAsync("Send failed.");
        }
    }

    private void ReadLoop(IByteLink link, CancellationToken stop)
    {
        var buffer = new byte[4096];
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!link.IsOpen)
                {
                    Services.Dispatcher.Post(() => _ = CloseAsync("The port closed."));
                    return;
                }

                var n = link.Read(buffer, 0, buffer.Length, 100);
                if (n > 0)
                    _incoming.Enqueue(ByteLinkExtensions.Text.GetString(buffer, 0, n));
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                Services.Dispatcher.Post(() =>
                {
                    AppendLocal("*** Read failed: " + ex.Message);
                    _ = CloseAsync("Read failed.");
                });
                return;
            }
            catch
            {
                return;
            }
        }
    }

    private async Task CloseAsync(string status)
    {
        var link = _link;
        if (link == null)
            return;
        _link = null;
        IsOpen = false;
        _readStop?.Cancel();
        try
        {
            await _reader;
        }
        catch
        {
            // the reader reports its own errors
        }

        _readStop?.Dispose();
        _readStop = null;
        await Task.Run(link.Dispose);
        Tick();
        AppendLocal("*** " + status);
        Status = status;
    }

    private void AppendLocal(string line)
    {
        _buffer.AppendLine(line);
        Output = _buffer.Text;
    }
}
