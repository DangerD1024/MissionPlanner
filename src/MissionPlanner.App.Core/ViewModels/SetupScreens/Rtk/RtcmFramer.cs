using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

/// <summary>A complete RTCM 3 frame whose CRC-24Q checked out: preamble, length, payload, CRC.</summary>
public sealed class RtcmFrame
{
    public RtcmFrame(byte[] bytes)
    {
        Bytes = bytes;
        MessageType = bytes.Length >= 5 ? (bytes[3] << 4) | (bytes[4] >> 4) : 0;
    }

    /// <summary>The whole frame, ready to forward to the vehicle unchanged.</summary>
    public byte[] Bytes { get; }

    /// <summary>The 12-bit message number (1005, 1074, ...).</summary>
    public int MessageType { get; }

    public int Length => Bytes.Length;
}

/// <summary>
/// Splits a byte stream into RTCM 3 frames. Unlike the byte-at-a-time parser in rtcm3.cs it keeps
/// the bytes of a candidate frame, so after a bad CRC it resynchronises on the next 0xD3 inside
/// it instead of losing the frame that follows.
/// </summary>
public sealed class RtcmFramer
{
    public const byte Preamble = 0xD3;
    public const int MaxFrame = 3 + 1023 + 3;

    private readonly byte[] _buf = new byte[4 * MaxFrame];
    private int _count;

    /// <summary>Candidate frames rejected by their CRC.</summary>
    public long CrcErrors { get; private set; }

    /// <summary>Bytes that were not part of any RTCM frame (NMEA, UBX, noise...).</summary>
    public long OtherBytes { get; private set; }

    public void Reset() => _count = 0;

    /// <summary>Adds received bytes; complete frames are appended to <paramref name="frames"/>.</summary>
    public void Push(ReadOnlySpan<byte> data, List<RtcmFrame> frames)
    {
        while (data.Length > 0)
        {
            var take = Math.Min(data.Length, _buf.Length - _count);
            data[..take].CopyTo(_buf.AsSpan(_count));
            _count += take;
            data = data[take..];
            Drain(frames);
        }
    }

    private void Drain(List<RtcmFrame> frames)
    {
        while (_count > 0)
        {
            var start = Array.IndexOf(_buf, Preamble, 0, _count);
            if (start < 0)
            {
                OtherBytes += _count;
                _count = 0;
                return;
            }

            if (start > 0)
            {
                OtherBytes += start;
                Consume(start);
            }

            if (_count < 3)
                return;

            // six reserved bits follow the preamble and are always zero
            if ((_buf[1] & 0xFC) != 0)
            {
                OtherBytes++;
                Consume(1);
                continue;
            }

            var payload = ((_buf[1] & 0x03) << 8) | _buf[2];
            var total = payload + 6;
            if (_count < total)
                return;

            var crc = rtcm3.crc24.crc24q(_buf, (uint)(payload + 3), 0);
            var sent = ((uint)_buf[payload + 3] << 16) | ((uint)_buf[payload + 4] << 8) | _buf[payload + 5];
            if (crc != sent)
            {
                CrcErrors++;
                OtherBytes++;
                Consume(1);
                continue;
            }

            // an empty frame carries no message number; nothing to forward
            if (payload >= 2)
                frames.Add(new RtcmFrame(_buf.AsSpan(0, total).ToArray()));
            Consume(total);
        }
    }

    private void Consume(int n)
    {
        Buffer.BlockCopy(_buf, n, _buf, 0, _count - n);
        _count -= n;
    }
}
