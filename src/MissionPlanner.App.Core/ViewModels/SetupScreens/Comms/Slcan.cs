using System.Globalization;
using System.Text;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>One CAN frame: 29-bit (extended) or 11-bit id, up to 8 data bytes (64 for CAN FD).</summary>
public sealed record CanFrame(uint Id, byte[] Data, bool Extended = true, bool Fd = false)
{
    public const uint ExtendedIdMask = 0x1FFFFFFF;

    public override string ToString() => $"{Id:X8} [{Data.Length}] {Convert.ToHexString(Data)}";
}

/// <summary>
/// The Lawicel SLCAN text protocol the DroneCAN library speaks: "T" + 8 hex id + DLC + data for an
/// extended frame ("t" with a 3 digit id for standard), "D"/"B" for CAN FD, single letters for control.
/// </summary>
public static class Slcan
{
    private static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    public static int DlcToLength(int dlc) => dlc is >= 0 and < 16 ? FdLengths[dlc] : 64;

    public static int LengthToDlc(int length)
    {
        for (var dlc = 0; dlc < FdLengths.Length; dlc++)
        {
            if (FdLengths[dlc] >= length)
                return dlc;
        }

        return 15;
    }

    /// <summary>A frame line without its CR; null for control commands and malformed lines.</summary>
    public static CanFrame? Parse(string line)
    {
        line = line.Trim('\r', '\n', '\a');
        if (line.Length < 2)
            return null;

        int idLength;
        bool extended, fd;
        switch (line[0])
        {
            case 'T':
                (idLength, extended, fd) = (8, true, false);
                break;
            case 't':
                (idLength, extended, fd) = (3, false, false);
                break;
            case 'D':
            case 'B':
                (idLength, extended, fd) = (8, true, true);
                break;
            case 'd':
            case 'b':
                (idLength, extended, fd) = (3, false, true);
                break;
            default:
                return null;
        }

        if (line.Length < 1 + idLength + 1)
            return null;
        if (!uint.TryParse(line.AsSpan(1, idLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            return null;
        if (!int.TryParse(line.AsSpan(1 + idLength, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var dlc))
            return null;

        var length = fd ? DlcToLength(dlc) : Math.Min(dlc, 8);
        var hex = line.AsSpan(2 + idLength);
        if (hex.Length < length * 2)
            return null;
        byte[] data;
        try
        {
            data = Convert.FromHexString(hex[..(length * 2)]);
        }
        catch (FormatException)
        {
            return null;
        }

        return new CanFrame(extended ? id & CanFrame.ExtendedIdMask : id & 0x7FF, data, extended, fd);
    }

    /// <summary>The SLCAN line for a received frame, CR included. CAN FD goes out as "D" (the DroneCAN library's FD marker).</summary>
    public static string Format(CanFrame frame)
    {
        var sb = new StringBuilder(2 + 8 + 1 + frame.Data.Length * 2 + 1);
        if (frame.Extended)
            sb.Append(frame.Fd ? 'D' : 'T').Append((frame.Id & CanFrame.ExtendedIdMask).ToString("X8", CultureInfo.InvariantCulture));
        else
            sb.Append(frame.Fd ? 'd' : 't').Append((frame.Id & 0x7FF).ToString("X3", CultureInfo.InvariantCulture));

        var dlc = frame.Fd ? LengthToDlc(frame.Data.Length) : Math.Min(frame.Data.Length, 8);
        sb.Append(dlc.ToString("X1", CultureInfo.InvariantCulture));
        var length = frame.Fd ? DlcToLength(dlc) : dlc;
        var data = frame.Data.Length >= length ? frame.Data.AsSpan(0, length) : frame.Data.Concat(new byte[length - frame.Data.Length]).ToArray();
        sb.Append(Convert.ToHexString(data));
        sb.Append('\r');
        return sb.ToString();
    }
}

/// <summary>
/// An SLCAN "adapter" made of MAVLink: the DroneCAN library writes SLCAN text into this stream and
/// reads SLCAN text from it, as from a USB-CAN adapter. Frames it writes come out of
/// <see cref="FrameSent"/> (to go to the vehicle as CAN_FRAME); frames the vehicle forwards go in
/// through <see cref="Receive"/>. SLCAN control commands (open, close, bitrate, version) are answered
/// locally, since the vehicle's bus is already running.
/// </summary>
public sealed class SlcanBridgeStream : Stream
{
    private readonly ByteQueue _rx = new();
    private readonly StringBuilder _line = new();
    private readonly object _writeGate = new();
    private volatile bool _closed;

    /// <summary>A frame the DroneCAN library sent (raised on the writer's thread).</summary>
    public event Action<CanFrame>? FrameSent;

    public long FramesSent { get; private set; }

    public long FramesReceived { get; private set; }

    /// <summary>A frame from the bus, for the library to read.</summary>
    public void Receive(CanFrame frame)
    {
        if (_closed)
            return;
        FramesReceived++;
        _rx.Append(Encoding.ASCII.GetBytes(Slcan.Format(frame)));
    }

    public override bool CanRead => !_closed;

    public override bool CanWrite => !_closed;

    public override bool CanSeek => false;

    public override bool CanTimeout => true;

    public override int ReadTimeout { get; set; } = 100;

    public override int WriteTimeout { get; set; } = 1000;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Returns 0 (no data) after <see cref="ReadTimeout"/>, like a serial port with nothing to say.</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_closed)
            throw new ObjectDisposedException(nameof(SlcanBridgeStream));
        return _rx.Take(buffer, offset, count, ReadTimeout);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_closed)
            throw new ObjectDisposedException(nameof(SlcanBridgeStream));

        lock (_writeGate)
        {
            for (var i = offset; i < offset + count; i++)
            {
                var c = (char)buffer[i];
                if (c is '\r' or '\n')
                {
                    if (_line.Length > 0)
                        HandleLine(_line.ToString());
                    _line.Clear();
                }
                else
                {
                    _line.Append(c);
                    if (_line.Length > 300)
                        _line.Clear();
                }
            }
        }
    }

    private void HandleLine(string line)
    {
        var frame = Slcan.Parse(line);
        if (frame != null)
        {
            FramesSent++;
            FrameSent?.Invoke(frame);
            return;
        }

        // control commands of an SLCAN adapter: answer as one that accepted them
        var reply = line[0] switch
        {
            'F' => "F00\r", // status flags: no errors
            'V' => "V1013\r", // hardware/software version
            'v' => "vSTA\r",
            'N' => "NMAVL\r", // serial number
            _ => "\r", // O, C, S, s, M, m, Z, L...: OK
        };
        _rx.Append(Encoding.ASCII.GetBytes(reply));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        _rx.Release();
        base.Dispose(disposing);
    }
}
