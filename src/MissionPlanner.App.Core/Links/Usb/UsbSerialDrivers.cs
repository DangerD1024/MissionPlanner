namespace MissionPlanner.App.Core.Links.Usb;

/// <summary>
/// Sets up one chip family over raw USB transfers: line coding (baud, 8N1) and the DTR/RTS
/// lines. The request values follow the Linux kernel drivers (cdc-acm, ftdi_sio, cp210x,
/// ch341) and usb-serial-for-android.
/// </summary>
public abstract class UsbSerialDriver
{
    protected const int Timeout = 1000;

    protected UsbSerialDriver(IUsbConnection connection, UsbSerialPortInfo port)
    {
        Connection = connection;
        Port = port;
    }

    protected IUsbConnection Connection { get; }

    protected UsbSerialPortInfo Port { get; }

    /// <summary>Bytes at the start of every incoming USB packet that are not data (FTDI's modem status).</summary>
    public virtual int PacketHeader => 0;

    public static UsbSerialDriver For(IUsbConnection connection, UsbSerialPortInfo port) => port.Chip switch
    {
        UsbSerialChip.Ftdi => new FtdiDriver(connection, port),
        UsbSerialChip.Cp210x => new Cp210xDriver(connection, port),
        UsbSerialChip.Ch34x => new Ch34xDriver(connection, port),
        _ => new CdcAcmDriver(connection, port),
    };

    /// <summary>Claims the interfaces and sets the line up.</summary>
    public virtual void Open(int baud, bool dtr, bool rts)
    {
        Claim(Port.Control);
        if (Port.Data.Number != Port.Control.Number)
            Claim(Port.Data);
        Configure(baud);
        SetLines(dtr, rts);
    }

    public virtual void Close()
    {
    }

    protected abstract void Configure(int baud);

    public abstract void SetBaud(int baud);

    public abstract void SetLines(bool dtr, bool rts);

    private void Claim(UsbInterfaceInfo i)
    {
        if (!Connection.ClaimInterface(i.Number))
            throw new IOException($"Could not claim USB interface {i.Number}; another app may be using the device.");
    }

    protected void Out(int requestType, int request, int value, int index, byte[]? data = null)
    {
        var n = Connection.ControlTransfer(requestType, request, value & 0xFFFF, index & 0xFFFF, data, data?.Length ?? 0, Timeout);
        if (n < 0)
            throw new IOException($"USB control request 0x{request:x2} failed ({n}).");
    }

    protected byte[] In(int requestType, int request, int value, int index, int length)
    {
        var buffer = new byte[length];
        var n = Connection.ControlTransfer(requestType, request, value & 0xFFFF, index & 0xFFFF, buffer, length, Timeout);
        if (n < 0)
            throw new IOException($"USB control request 0x{request:x2} failed ({n}).");
        return buffer;
    }

    protected static void CheckBaud(int baud)
    {
        if (baud <= 0)
            throw new ArgumentOutOfRangeException(nameof(baud), baud, "the baud rate must be positive");
    }
}

/// <summary>USB CDC-ACM (class requests to the communication interface).</summary>
public sealed class CdcAcmDriver : UsbSerialDriver
{
    private const int RequestTypeOut = 0x21; // class, interface, host to device
    private const int SetLineCoding = 0x20;
    private const int SetControlLineState = 0x22;

    public CdcAcmDriver(IUsbConnection connection, UsbSerialPortInfo port)
        : base(connection, port)
    {
    }

    protected override void Configure(int baud) => SetBaud(baud);

    public override void SetBaud(int baud)
    {
        CheckBaud(baud);
        // dwDTERate, bCharFormat 0 (1 stop bit), bParityType 0 (none), bDataBits 8
        var coding = new byte[7];
        BitConverter.TryWriteBytes(coding.AsSpan(0, 4), baud);
        coding[6] = 8;
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(coding, 0, 4);
        // a lone data interface (no communication interface) takes no class requests
        if (Port.Control.Number != Port.Data.Number)
            Out(RequestTypeOut, SetLineCoding, 0, Port.Control.Number, coding);
    }

    public override void SetLines(bool dtr, bool rts)
    {
        if (Port.Control.Number != Port.Data.Number)
            Out(RequestTypeOut, SetControlLineState, (dtr ? 1 : 0) | (rts ? 2 : 0), Port.Control.Number);
    }
}

/// <summary>FTDI FT232R / FT-X / FT2232 (vendor requests, 3 MHz baud clock).</summary>
public sealed class FtdiDriver : UsbSerialDriver
{
    private const int RequestTypeOut = 0x40;
    private const int Reset = 0x00;
    private const int ModemControl = 0x01;
    private const int FlowControl = 0x02;
    private const int SetBaudRate = 0x03;
    private const int SetData = 0x04;
    private const int SetLatencyTimer = 0x09;

    // the sub-integer part of the divisor in eighths, as the chip encodes it
    private static readonly int[] Fraction = [0, 3, 2, 4, 1, 5, 6, 7];

    public FtdiDriver(IUsbConnection connection, UsbSerialPortInfo port)
        : base(connection, port)
    {
    }

    public override int PacketHeader => 2;

    /// <summary>The port index the requests carry: 0 on single-port chips, 1-based on FT2232/4232.</summary>
    private int Index => Port.PortCount > 1 ? Port.Port + 1 : 0;

    protected override void Configure(int baud)
    {
        Out(RequestTypeOut, Reset, 0, Index);
        Out(RequestTypeOut, FlowControl, 0, Index);
        Out(RequestTypeOut, SetData, 8, Index); // 8 data bits, no parity, 1 stop bit
        // MAVLink is many small packets: hand them over after 1 ms instead of 16
        Out(RequestTypeOut, SetLatencyTimer, 1, Index);
        SetBaud(baud);
    }

    public override void SetBaud(int baud)
    {
        var (value, index) = BaudValue(baud, Port.PortCount > 1 ? Port.Port + 1 : (int?)null);
        Out(RequestTypeOut, SetBaudRate, value, index);
    }

    public override void SetLines(bool dtr, bool rts) =>
        // the high byte says which lines the low byte sets
        Out(RequestTypeOut, ModemControl, 0x0300 | (dtr ? 1 : 0) | (rts ? 2 : 0), Index);

    /// <summary>The SetBaudRate value and index for the 3 MHz clock of FT232R / FT-X chips.</summary>
    public static (int Value, int Index) BaudValue(int baud, int? multiPortIndex = null)
    {
        CheckBaud(baud);
        int encoded;
        if (baud >= 2_500_000)
            encoded = 0; // 3 Mbaud
        else if (baud >= 1_750_000)
            encoded = 1; // 2 Mbaud
        else
        {
            var eighths = (int)Math.Round(24_000_000.0 / baud);
            eighths = Math.Clamp(eighths, 16, (0x3FFF << 3) | 7);
            encoded = (eighths >> 3) | (Fraction[eighths & 7] << 14);
        }

        var value = encoded & 0xFFFF;
        var index = encoded >> 16;
        if (multiPortIndex is { } p)
            index = (index << 8) | p;
        return (value, index);
    }
}

/// <summary>Silicon Labs CP2102 / CP2104 / CP2105 (vendor interface requests).</summary>
public sealed class Cp210xDriver : UsbSerialDriver
{
    private const int RequestTypeOut = 0x41; // vendor, interface, host to device
    private const int IfcEnable = 0x00;
    private const int SetLineCtl = 0x03;
    private const int SetMhs = 0x07;
    private const int Purge = 0x12;
    private const int SetBaudRate = 0x1E;

    public Cp210xDriver(IUsbConnection connection, UsbSerialPortInfo port)
        : base(connection, port)
    {
    }

    private int Index => Port.Data.Number;

    protected override void Configure(int baud)
    {
        Out(RequestTypeOut, IfcEnable, 1, Index);
        Out(RequestTypeOut, SetLineCtl, 0x0800, Index); // 8 data bits, no parity, 1 stop bit
        SetBaud(baud);
        Out(RequestTypeOut, Purge, 0x0F, Index);
    }

    public override void SetBaud(int baud)
    {
        CheckBaud(baud);
        var data = new byte[4];
        BitConverter.TryWriteBytes(data, baud);
        if (!BitConverter.IsLittleEndian)
            Array.Reverse(data);
        Out(RequestTypeOut, SetBaudRate, 0, Index, data);
    }

    public override void SetLines(bool dtr, bool rts) =>
        Out(RequestTypeOut, SetMhs, 0x0300 | (dtr ? 1 : 0) | (rts ? 2 : 0), Index);

    public override void Close()
    {
        try
        {
            Out(RequestTypeOut, IfcEnable, 0, Index);
        }
        catch (IOException)
        {
            // unplugged
        }
    }
}

/// <summary>WCH CH340 / CH341 (vendor requests; the init sequence of the Linux ch341 driver).</summary>
public sealed class Ch34xDriver : UsbSerialDriver
{
    private const int RequestTypeOut = 0x40;
    private const int RequestTypeIn = 0xC0;
    private const int ReadVersion = 0x5F;
    private const int ReadRegister = 0x95;
    private const int WriteRegister = 0x9A;
    private const int SerialInit = 0xA1;
    private const int ModemControl = 0xA4;
    private const int LcrEnableRxTx8N1 = 0xC3;

    public Ch34xDriver(IUsbConnection connection, UsbSerialPortInfo port)
        : base(connection, port)
    {
    }

    protected override void Configure(int baud)
    {
        In(RequestTypeIn, ReadVersion, 0, 0, 2);
        Out(RequestTypeOut, SerialInit, 0, 0);
        SetBaud(baud);
        In(RequestTypeIn, ReadRegister, 0x2518, 0, 2);
        Out(RequestTypeOut, WriteRegister, 0x2518, LcrEnableRxTx8N1);
        In(RequestTypeIn, ReadRegister, 0x0706, 0, 2);
        Out(RequestTypeOut, SerialInit, 0x501F, 0xD90A);
        SetBaud(baud);
    }

    public override void SetBaud(int baud)
    {
        var (a, b) = BaudValues(baud);
        Out(RequestTypeOut, WriteRegister, 0x1312, a);
        Out(RequestTypeOut, WriteRegister, 0x0F2C, b);
    }

    public override void SetLines(bool dtr, bool rts) =>
        // the lines are active low
        Out(RequestTypeOut, ModemControl, ~((dtr ? 1 << 5 : 0) | (rts ? 1 << 6 : 0)), 0);

    /// <summary>The two register values (0x1312 and 0x0F2C) for a baud rate.</summary>
    public static (int Reg1312, int Reg0F2C) BaudValues(int baud)
    {
        CheckBaud(baud);
        long factor;
        long divisor;
        if (baud == 921600)
        {
            divisor = 7;
            factor = 0xF300;
        }
        else
        {
            const long baseFactor = 1532620800;
            factor = baseFactor / baud;
            divisor = 3;
            while (factor > 0xFFF0 && divisor > 0)
            {
                factor >>= 3;
                divisor--;
            }

            if (factor > 0xFFF0)
                throw new ArgumentOutOfRangeException(nameof(baud), baud, "the CH340 cannot run this slowly");
            factor = 0x10000 - factor;
        }

        divisor |= 0x80; // send each byte at once instead of when the buffer is full
        return ((int)((factor & 0xFF00) | divisor), (int)(factor & 0xFF));
    }
}
