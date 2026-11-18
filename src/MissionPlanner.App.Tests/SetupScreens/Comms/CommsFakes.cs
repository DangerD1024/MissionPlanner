using System.Collections.Concurrent;
using System.Text;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

namespace MissionPlanner.App.Tests.SetupScreens.Comms;

/// <summary>Runs posted work at once and records dialogs; confirms answer <see cref="ConfirmAnswer"/>.</summary>
internal sealed class CommsUi : IUiDispatcher, IDialogService, IWindowService
{
    public bool ConfirmAnswer { get; set; } = true;

    public ConcurrentQueue<string> Messages { get; } = new();

    public ConcurrentQueue<string> Confirms { get; } = new();

    public string? SavePath { get; set; }

    public string? OpenPath { get; set; }

    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK")
    {
        Confirms.Enqueue(message);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue = "") => Task.FromResult<string?>(null);

    public Task<string?> OpenFileAsync(string title, params FileFilter[] filters) => Task.FromResult(OpenPath);

    public Task<string?> SaveFileAsync(string title, string suggestedName, params FileFilter[] filters) => Task.FromResult(SavePath);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public void Show(PageViewModel page, double width = 1100, double height = 750)
    {
    }

    public Task<bool> ShowDialogAsync(PageViewModel page, double width = 1000, double height = 700) => Task.FromResult(false);

    public AppServices Services() => new(this, this, this);
}

/// <summary>
/// A scripted SiK radio pair on a byte link, behaving like the firmware's at.c: "+++" answers OK and
/// enters command mode, typed characters are echoed, CR runs the line, ATI/ATI2/ATI3/ATI5/ATI7,
/// ATSn=v, AT&amp;W/&amp;F/&amp;T/&amp;E?, ATZ, ATO; "RT..." runs on the remote radio when it is linked.
/// </summary>
internal sealed class FakeSikRadio : IByteLink
{
    private readonly ByteQueue _out = new();
    private readonly StringBuilder _line = new();
    private bool _commandMode;

    public FakeSikRadio(bool remotePresent = true)
    {
        Local = new Radio("SiK 2.0 on HM-TRP", board: 0x4e, freq: 0x91);
        Remote = remotePresent ? new Radio("SiK 2.0 on HM-TRP", board: 0x4e, freq: 0x91) : null;
    }

    public sealed class Radio
    {
        private static readonly (string Name, int Value)[] Defaults =
        [
            ("FORMAT", 25), ("SERIAL_SPEED", 57), ("AIR_SPEED", 64), ("NETID", 25), ("TXPOWER", 20), ("ECC", 0),
            ("MAVLINK", 1), ("OPPRESEND", 0), ("MIN_FREQ", 915000), ("MAX_FREQ", 928000), ("NUM_CHANNELS", 50),
            ("DUTY_CYCLE", 100), ("LBT_RSSI", 0), ("MANCHESTER", 0), ("RTSCTS", 0), ("MAX_WINDOW", 131),
        ];

        public Radio(string banner, int board, int freq)
        {
            Banner = banner;
            Board = board;
            Freq = freq;
            Values = Defaults.Select(d => d.Value).ToArray();
            Stored = Values.ToArray();
        }

        public string Banner { get; }

        public int Board { get; }

        public int Freq { get; }

        /// <summary>Live (RAM) values, S0..S15.</summary>
        public int[] Values { get; set; }

        /// <summary>EEPROM values (what a restart loads).</summary>
        public int[] Stored { get; set; }

        public int Reboots { get; set; }

        public int Get(string name) => Values[Array.FindIndex(Defaults, d => d.Name == name)];

        public void Set(string name, int value) => Values[Array.FindIndex(Defaults, d => d.Name == name)] = value;

        public int StoredValue(string name) => Stored[Array.FindIndex(Defaults, d => d.Name == name)];

        public IEnumerable<string> Run(string cmd)
        {
            switch (cmd)
            {
                case "ATI":
                    return [Banner];
                case "ATI2":
                    return [Board.ToString()];
                case "ATI3":
                    return [Freq.ToString()];
                case "ATI5":
                    return Defaults.Select((d, i) => $"S{i}:{d.Name}={Values[i]}");
                case "ATI7":
                    return ["L/R RSSI: 200/190  L/R noise: 50/48 pkts: 12  txe=0 rxe=0 stx=0 srx=0 ecc=0/0 temp=40 dco=0"];
                case "AT&W":
                    Stored = Values.ToArray();
                    return ["OK"];
                case "AT&F":
                    Values = Defaults.Select(d => d.Value).ToArray();
                    return ["OK"];
                case "AT&T":
                    return ["OK"];
                case "AT&E?":
                    return ["ERROR"];
                case "ATZ":
                    Values = Stored.ToArray();
                    Reboots++;
                    return [];
            }

            if (cmd.StartsWith("ATS", StringComparison.Ordinal) && cmd.Contains('='))
            {
                var parts = cmd[3..].Split('=');
                if (int.TryParse(parts[0], out var reg) && int.TryParse(parts[1], out var v) && reg > 0 && reg < Values.Length)
                {
                    Values[reg] = v;
                    return ["OK"];
                }
            }

            return ["ERROR"];
        }
    }

    public Radio Local { get; }

    public Radio? Remote { get; }

    /// <summary>Command lines the radio executed, in order ("ATI5", "RTS3=42"...).</summary>
    public ConcurrentQueue<string> Executed { get; } = new();

    public bool InCommandMode => _commandMode;

    /// <summary>Start in command mode, as a radio a previous session did not leave.</summary>
    public void StartInCommandMode() => _commandMode = true;

    public string Name => "fake SiK";

    public bool IsOpen { get; private set; }

    public bool Disposed { get; private set; }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Dispose()
    {
        IsOpen = false;
        Disposed = true;
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs) => _out.Take(buffer, offset, count, timeoutMs);

    public void DiscardInput() => _out.Clear();

    public void Write(byte[] data, int offset, int count)
    {
        var text = Encoding.Latin1.GetString(data, offset, count);
        lock (_line)
        {
            if (!_commandMode)
            {
                // transparent: only the escape matters (guard times are not modelled)
                if (text == "+++")
                {
                    _commandMode = true;
                    Emit("OK\r\n");
                }

                return;
            }

            foreach (var c in text)
            {
                if (c == '\n')
                    continue;
                Emit(c.ToString()); // echo
                if (c != '\r')
                {
                    _line.Append(c);
                    continue;
                }

                Emit("\n");
                var cmd = _line.ToString().Trim().ToUpperInvariant();
                _line.Clear();
                if (cmd.Length == 0)
                    continue;
                Executed.Enqueue(cmd);
                foreach (var reply in Execute(cmd))
                    Emit(reply + "\r\n");
            }
        }
    }

    private IEnumerable<string> Execute(string cmd)
    {
        if (cmd == "AT")
            return ["OK"];
        if (cmd == "ATO")
        {
            _commandMode = false;
            return [];
        }

        if (cmd.StartsWith("RT", StringComparison.Ordinal))
            return Remote == null ? [] : Remote.Run("AT" + cmd[2..]).ToList();

        if (cmd.StartsWith("AT", StringComparison.Ordinal))
        {
            var lines = Local.Run(cmd).ToList();
            if (cmd == "ATZ")
                _commandMode = false;
            return lines;
        }

        return ["ERROR"];
    }

    private void Emit(string text) => _out.Append(Encoding.Latin1.GetBytes(text));
}

/// <summary>
/// The vehicle side of SERIAL_CONTROL for one device: records requests and answers polls with what
/// <see cref="Output"/> holds, 70 bytes per reply, like the autopilot with MULTI.
/// </summary>
internal sealed class FakeSerialControlVehicle : ISerialControlTransport
{
    private readonly object _gate = new();
    private Action<MAVLink.mavlink_serial_control_t>? _handler;

    public ConcurrentQueue<MAVLink.mavlink_serial_control_t> Sent { get; } = new();

    /// <summary>What the "device" will print.</summary>
    public ByteQueue Output { get; } = new();

    /// <summary>Bytes written to the device.</summary>
    public List<byte> Input { get; } = new();

    /// <summary>Device number replies carry (to test that others are ignored).</summary>
    public byte ReplyDevice { get; set; } = (byte)MAVLink.SERIAL_CONTROL_DEV.SHELL;

    /// <summary>Echo what is written, as a shell does.</summary>
    public bool Echo { get; set; } = true;

    public bool Subscribed => _handler != null;

    public void Send(MAVLink.mavlink_serial_control_t packet)
    {
        Sent.Enqueue(packet);
        if (packet.count > 0)
        {
            var data = packet.data.AsSpan(0, packet.count).ToArray();
            lock (_gate)
                Input.AddRange(data);
            if (Echo)
                Output.Append(data);
        }

        if ((packet.flags & (byte)MAVLink.SERIAL_CONTROL_FLAG.RESPOND) == 0)
            return;

        // reply now with what is waiting, more packets while there is more (MULTI)
        var buffer = new byte[70];
        do
        {
            var n = Output.Take(buffer, 0, 70, 0);
            var reply = new MAVLink.mavlink_serial_control_t
            {
                device = ReplyDevice,
                flags = (byte)MAVLink.SERIAL_CONTROL_FLAG.REPLY,
                count = (byte)n,
                data = buffer.ToArray(),
            };
            _handler?.Invoke(reply);
            if (n == 0 || (packet.flags & (byte)MAVLink.SERIAL_CONTROL_FLAG.MULTI) == 0)
                break;
        } while (true);
    }

    public IDisposable Subscribe(Action<MAVLink.mavlink_serial_control_t> handler)
    {
        _handler = handler;
        return new Off(() => _handler = null);
    }

    public string InputText
    {
        get
        {
            lock (_gate)
                return Encoding.Latin1.GetString(Input.ToArray());
        }
    }

    private sealed class Off(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>A CAN-over-MAVLink vehicle stand-in: records what is sent, lets the test inject messages.</summary>
internal sealed class FakeCanTransport : ICanFrameTransport
{
    private Action<MAVLink.MAVLinkMessage>? _handler;

    public ConcurrentQueue<object> Sent { get; } = new();

    public ConcurrentQueue<int> ForwardRequests { get; } = new();

    public bool Subscribed => _handler != null;

    public void Send(object mavlinkMessage) => Sent.Enqueue(mavlinkMessage);

    public IDisposable Subscribe(Action<MAVLink.MAVLinkMessage> handler)
    {
        _handler = handler;
        return new Off(() => _handler = null);
    }

    public void RequestForwarding(int bus) => ForwardRequests.Enqueue(bus);

    /// <summary>Delivers a message as if the vehicle had sent it.</summary>
    public void Inject(MAVLink.MAVLINK_MSG_ID id, object message)
    {
        var parse = new MAVLink.MavlinkParse();
        var bytes = parse.GenerateMAVLinkPacket20(id, message, false, 1, 1, 0);
        _handler?.Invoke(new MAVLink.MAVLinkMessage(bytes));
    }

    private sealed class Off(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>
/// A DroneCAN bus stand-in for the page: scripted nodes and parameters, plus — when started on a
/// stream — a reader that decodes single-frame NodeStatus broadcasts from the SLCAN text, so the
/// MAVLink CAN bridge can be tested end to end without the library.
/// </summary>
internal sealed class FakeDroneCanBus : IDroneCanBus
{
    private Stream? _stream;
    private CancellationTokenSource? _stop;

    public byte LocalNodeId => 127;

    public event Action<CanNodeStatus>? NodeStatusReceived;

    public event Action<CanNodeInfo>? NodeInfoReceived;

    public event Action<CanLogMessage>? LogReceived;

    public Dictionary<byte, List<CanParameter>> Params { get; } = new();

    public ConcurrentQueue<string> Calls { get; } = new();

    public bool Started => _stream != null;

    public bool Stopped { get; private set; }

    public bool SetResult { get; set; } = true;

    public string? FirmwareFile { get; private set; }

    public void Start(Stream slcan)
    {
        _stream = slcan;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        Task.Run(() => ReadLoop(slcan, token));
    }

    public void Stop()
    {
        Stopped = true;
        _stop?.Cancel();
    }

    public void Dispose() => Stop();

    public void RaiseStatus(CanNodeStatus status) => NodeStatusReceived?.Invoke(status);

    public void RaiseInfo(CanNodeInfo info) => NodeInfoReceived?.Invoke(info);

    public void RaiseLog(CanLogMessage log) => LogReceived?.Invoke(log);

    public void RequestNodeInfo(byte node) => Calls.Enqueue($"info {node}");

    public IReadOnlyList<CanParameter> GetParameters(byte node)
    {
        Calls.Enqueue($"get {node}");
        return Params.TryGetValue(node, out var list) ? list.ToList() : [];
    }

    public bool SetParameter(byte node, CanParameter parameter, double number, string text)
    {
        Calls.Enqueue($"set {node} {parameter.Name}={(parameter.Kind == CanParamKind.String ? text : number.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        return SetResult;
    }

    public bool SaveParameters(byte node)
    {
        Calls.Enqueue($"save {node}");
        return true;
    }

    public bool EraseParameters(byte node)
    {
        Calls.Enqueue($"erase {node}");
        return true;
    }

    public bool RestartNode(byte node)
    {
        Calls.Enqueue($"restart {node}");
        return true;
    }

    public void UpdateFirmware(byte node, string firmwareFile, IProgress<double> progress, CancellationToken cancel)
    {
        Calls.Enqueue($"update {node}");
        FirmwareFile = firmwareFile;
        progress.Report(50);
        progress.Report(100);
    }

    /// <summary>Sends an SLCAN line into the bus stream, as the library writes its frames.</summary>
    public void WriteLine(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\r");
        _stream!.Write(bytes, 0, bytes.Length);
    }

    private void ReadLoop(Stream stream, CancellationToken stop)
    {
        var line = new StringBuilder();
        var buffer = new byte[256];
        while (!stop.IsCancellationRequested)
        {
            int n;
            try
            {
                n = stream.Read(buffer, 0, buffer.Length);
            }
            catch
            {
                return;
            }

            for (var i = 0; i < n; i++)
            {
                var c = (char)buffer[i];
                if (c != '\r')
                {
                    line.Append(c);
                    continue;
                }

                var frame = Slcan.Parse(line.ToString());
                line.Clear();
                // uavcan.protocol.NodeStatus (data type 341), single frame: 7 bytes + tail byte
                if (frame is { Data.Length: 8 } && ((frame.Id >> 8) & 0xFFFF) == 341 && (frame.Id & 0x80) == 0)
                {
                    var d = frame.Data;
                    NodeStatusReceived?.Invoke(new CanNodeStatus((byte)(frame.Id & 0x7F), (CanNodeHealth)(d[4] >> 6),
                        (CanNodeMode)((d[4] >> 3) & 7), BitConverter.ToUInt32(d, 0), BitConverter.ToUInt16(d, 5)));
                }
            }
        }
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("timed out waiting for " + (what ?? "condition"));
            await Task.Delay(10);
        }
    }
}
