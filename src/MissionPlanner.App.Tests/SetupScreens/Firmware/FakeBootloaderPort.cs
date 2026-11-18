using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

/// <summary>
/// A board in its bootloader behind an <see cref="ICommsSerial"/>: answers the ArduPilot bootloader
/// protocol from an in-memory flash, so <see cref="Px4Bootloader"/> can be tested byte for byte.
/// </summary>
internal sealed class FakeBootloaderPort : ICommsSerial
{
    private const byte INSYNC = 0x12, OK = 0x10, FAILED = 0x11, INVALID = 0x13, EOC = 0x20;

    private readonly object _gate = new();
    private readonly Queue<byte> _out = new();
    private readonly List<byte> _in = new();

    public FakeBootloaderPort(string name, int boardId, int flashSize = 64 * 1024, int blRev = 5)
    {
        PortName = name;
        BoardId = boardId;
        BlRev = blRev;
        Flash = new byte[flashSize];
        Array.Fill(Flash, (byte)0xff);
    }

    public int BoardId { get; }

    public int BlRev { get; }

    public byte[] Flash { get; }

    public int ProgramAddress { get; private set; }

    public bool Rebooted { get; private set; }

    public int EraseCount { get; private set; }

    public bool Silent { get; set; }

    public List<byte> Commands { get; } = new();

    /// <summary>Fills the flash as if <paramref name="image"/> had been flashed before.</summary>
    public void Preload(byte[] image) => Array.Copy(image, Flash, image.Length);

    // ---- protocol ------------------------------------------------------------------------------

    private void Process()
    {
        while (_in.Count > 0)
        {
            var cmd = _in[0];
            var len = CommandLength(cmd);
            if (len == null)
            {
                _in.Clear();
                Reply(INSYNC, INVALID);
                return;
            }

            if (_in.Count < len.Value)
                return;
            var packet = _in.GetRange(0, len.Value).ToArray();
            _in.RemoveRange(0, len.Value);
            Commands.Add(cmd);
            if (packet[^1] != EOC)
            {
                Reply(INSYNC, INVALID);
                continue;
            }

            Handle(packet);
        }
    }

    private int? CommandLength(byte cmd) => cmd switch
    {
        0x21 or 0x23 or 0x24 or 0x29 or 0x2c or 0x2e or 0x30 => 2, // GET_SYNC, CHIP_ERASE, CHIP_VERIFY, GET_CRC, GET_CHIP, GET_CHIP_DES, REBOOT
        0x22 => 3, // GET_DEVICE param
        0x2b => 6, // GET_SN addr[4]
        0x28 => 3, // READ_MULTI len
        0x27 => _in.Count >= 2 ? _in[1] + 3 : int.MaxValue, // PROG_MULTI len data EOC
        _ => null,
    };

    private void Handle(byte[] p)
    {
        switch (p[0])
        {
            case 0x21:
                Reply(INSYNC, OK);
                break;
            case 0x22:
                var value = p[1] switch
                {
                    1 => BlRev,
                    2 => BoardId,
                    3 => 0,
                    4 => Flash.Length,
                    6 => 0,
                    _ => -1,
                };
                if (value < 0)
                {
                    Reply(INSYNC, INVALID);
                    return;
                }

                ReplyInt(value);
                Reply(INSYNC, OK);
                break;
            case 0x23:
                Array.Fill(Flash, (byte)0xff);
                ProgramAddress = 0;
                EraseCount++;
                Reply(INSYNC, OK);
                break;
            case 0x24:
                ProgramAddress = 0;
                Reply(INSYNC, OK);
                break;
            case 0x27:
                var n = p[1];
                if (ProgramAddress + n > Flash.Length)
                {
                    Reply(INSYNC, FAILED);
                    return;
                }

                Array.Copy(p, 2, Flash, ProgramAddress, n);
                ProgramAddress += n;
                Reply(INSYNC, OK);
                break;
            case 0x28:
                var count = p[1];
                for (var i = 0; i < count; i++)
                    _out.Enqueue(Flash[ProgramAddress + i]);
                ProgramAddress += count;
                Reply(INSYNC, OK);
                break;
            case 0x29:
                ReplyInt((int)ApjFirmware.Crc32(Flash, 0));
                Reply(INSYNC, OK);
                break;
            case 0x2b:
                ReplyInt(0x11223344 + p[1]);
                Reply(INSYNC, OK);
                break;
            case 0x2c:
                ReplyInt(0x450);
                Reply(INSYNC, OK);
                break;
            case 0x2e:
                var desc = "STM32H7[4-5]x,V"u8.ToArray();
                ReplyInt(desc.Length);
                foreach (var b in desc)
                    _out.Enqueue(b);
                Reply(INSYNC, OK);
                break;
            case 0x30:
                Rebooted = true;
                break;
        }
    }

    private void Reply(byte a, byte b)
    {
        _out.Enqueue(a);
        _out.Enqueue(b);
    }

    private void ReplyInt(int v)
    {
        foreach (var b in BitConverter.GetBytes(v))
            _out.Enqueue(b);
    }

    // ---- ICommsSerial --------------------------------------------------------------------------

    public Stream BaseStream { get; } = new MemoryStream();
    public int BaudRate { get; set; } = 115200;

    public int BytesToRead
    {
        get
        {
            lock (_gate)
                return _out.Count;
        }
    }

    public int BytesToWrite => 0;
    public int DataBits { get; set; } = 8;
    public bool DtrEnable { get; set; }
    public bool IsOpen { get; private set; }
    public string PortName { get; set; }
    public int ReadBufferSize { get; set; } = 4096;
    public int ReadTimeout { get; set; } = 500;
    public bool RtsEnable { get; set; }
    public int WriteBufferSize { get; set; } = 4096;
    public int WriteTimeout { get; set; } = 500;

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Dispose() => IsOpen = false;

    public void DiscardInBuffer()
    {
        lock (_gate)
            _out.Clear();
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var deadline = Environment.TickCount64 + ReadTimeout;
        while (true)
        {
            lock (_gate)
            {
                if (_out.Count > 0)
                {
                    var n = 0;
                    while (n < count && _out.Count > 0)
                        buffer[offset + n++] = _out.Dequeue();
                    return n;
                }
            }

            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("fake port: read timed out");
            Thread.Sleep(1);
        }
    }

    public int ReadByte()
    {
        var b = new byte[1];
        Read(b, 0, 1);
        return b[0];
    }

    public int ReadChar() => ReadByte();

    public string ReadExisting() => "";

    public string ReadLine() => "";

    public void Write(string text)
    {
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (!IsOpen)
            throw new InvalidOperationException("port closed");
        if (Silent)
            return;
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
                _in.Add(buffer[offset + i]);
            Process();
        }
    }

    public void WriteLine(string text)
    {
    }

    public void toggleDTR()
    {
    }
}

/// <summary>Hands out fake ports by name; unknown names fail to open like a missing device.</summary>
internal sealed class FakePortFactory : IBootloaderFactory
{
    private readonly Dictionary<string, Func<ICommsSerial>> _ports = new();

    public List<string> Opened { get; } = new();

    public void Add(string name, Func<ICommsSerial> port) => _ports[name] = port;

    public IBootloader Open(string portName)
    {
        lock (Opened)
            Opened.Add(portName);
        if (!_ports.TryGetValue(portName, out var make))
            throw new IOException("no such port " + portName);
        return new Px4Bootloader(make());
    }
}
