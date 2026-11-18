using System.Buffers;
using System.Buffers.Binary;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>What a telemetry log covers, read from its first and last records.</summary>
public sealed record TlogInfo(string Path, long Length, DateTime Start, DateTime End)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>
/// Finds record boundaries in a telemetry log without decoding it. A tlog is a sequence of
/// records: an 8-byte big-endian timestamp (UTC microseconds since 1970) followed by one raw
/// MAVLink 1 or 2 frame (<c>MAVLinkInterface.SaveToTlog</c>). A position is taken as a record
/// start only if a few well-formed records with plausible, progressing timestamps follow it.
/// </summary>
internal static class TlogScanner
{
    private const ulong MinMicros = 946_684_800UL * 1_000_000; // 2000-01-01
    private const ulong MaxMicros = 4_102_444_800UL * 1_000_000; // 2100-01-01
    private const ulong MaxStepBackMicros = 60UL * 1_000_000;
    private const ulong MaxStepForwardMicros = 3600UL * 1_000_000;
    private const int HeadTailBytes = 256 * 1024;
    private const int SeekWindowBytes = 64 * 1024;
    private const int ChainLength = 3;

    /// <summary>Start and end time of a log. Throws <see cref="InvalidDataException"/> if it holds no MAVLink records.</summary>
    public static TlogInfo Inspect(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
        var length = fs.Length;

        var headLength = (int)Math.Min(HeadTailBytes, length);
        var head = ReadAt(fs, 0, headLength);
        var first = FindRecord(head, 0, endsAtEof: headLength == length);
        if (first < 0)
            throw new InvalidDataException("No MAVLink telemetry records were found in this file.");
        var startMicros = ReadMicros(head, first);

        var tailStart = Math.Max(0, length - HeadTailBytes);
        var tail = tailStart == 0 ? head : ReadAt(fs, tailStart, (int)(length - tailStart));
        var endMicros = LastTimestamp(tail) ?? startMicros;
        if (endMicros < startMicros)
            endMicros = startMicros;

        return new TlogInfo(path, length, ToLocal(startMicros), ToLocal(endMicros));
    }

    /// <summary>
    /// The first record start at or after <paramref name="target"/> (and its time), or
    /// <paramref name="target"/> itself when none is found nearby. Moves the stream's position.
    /// </summary>
    public static (long Position, DateTime? Time) AlignToRecord(Stream stream, long target)
    {
        var length = stream.Length;
        target = Math.Clamp(target, 0, length);
        var count = (int)Math.Min(SeekWindowBytes, length - target);
        if (count <= 0)
            return (length, null);

        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            stream.Position = target;
            var read = ReadFully(stream, buffer, count);
            var span = buffer.AsSpan(0, read);
            var i = FindRecord(span, 0, endsAtEof: target + read == length);
            return i < 0 ? (target, null) : (target + i, ToLocal(ReadMicros(span, i)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Index of the first record start at or after <paramref name="from"/>: a run of
    /// <see cref="ChainLength"/> well-formed records, or a shorter run that ends exactly at the
    /// end of the file. -1 if there is none.
    /// </summary>
    internal static int FindRecord(ReadOnlySpan<byte> buffer, int from, bool endsAtEof)
    {
        for (var i = from; i + 11 <= buffer.Length; i++)
        {
            if (IsChain(buffer, i, endsAtEof))
                return i;
        }

        return -1;
    }

    /// <summary>Timestamp of the last complete record in a block that ends at the end of the file.</summary>
    internal static ulong? LastTimestamp(ReadOnlySpan<byte> tail)
    {
        ulong? last = null;
        var i = FindRecord(tail, 0, endsAtEof: true);
        while (i >= 0)
        {
            var at = i;
            ulong prev = 0;
            var first = true;
            while (at < tail.Length && TryRecord(tail, at, out var micros, out var length) &&
                   at + length <= tail.Length && (first || Follows(prev, micros)))
            {
                last = micros;
                prev = micros;
                at += length;
                first = false;
            }

            if (at >= tail.Length)
                break;
            // a damaged or truncated record: look for the records after it
            i = FindRecord(tail, at + 1, endsAtEof: true);
        }

        return last;
    }

    /// <summary>Reads the record header at <paramref name="at"/>: its timestamp and full length (timestamp + frame).</summary>
    internal static bool TryRecord(ReadOnlySpan<byte> buffer, int at, out ulong micros, out int length)
    {
        micros = 0;
        length = 0;
        if (at < 0 || at + 11 > buffer.Length)
            return false;

        micros = BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(at, 8));
        if (micros < MinMicros || micros > MaxMicros)
            return false;

        var payload = buffer[at + 9];
        switch (buffer[at + 8])
        {
            case MAVLink.MAVLINK_STX_MAVLINK1:
                // stx, len, seq, sysid, compid, msgid, payload, crc
                length = 8 + 6 + payload + 2;
                return true;
            case MAVLink.MAVLINK_STX:
            {
                // stx, len, incompat, compat, seq, sysid, compid, msgid (3), payload, crc, optional signature
                var incompat = buffer[at + 10];
                if ((incompat & ~MAVLink.MAVLINK_IFLAG_SIGNED) != 0)
                    return false;
                length = 8 + 10 + payload + 2 + ((incompat & MAVLink.MAVLINK_IFLAG_SIGNED) != 0 ? MAVLink.MAVLINK_SIGNATURE_BLOCK_LEN : 0);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool IsChain(ReadOnlySpan<byte> buffer, int at, bool endsAtEof)
    {
        ulong prev = 0;
        for (var n = 0; n < ChainLength; n++)
        {
            if (at == buffer.Length)
                return endsAtEof && n > 0;
            if (!TryRecord(buffer, at, out var micros, out var length) || at + length > buffer.Length)
                return false;
            if (n > 0 && !Follows(prev, micros))
                return false;
            prev = micros;
            at += length;
        }

        return true;
    }

    // log time moves forward; a small step back (GCS clock adjusted) is tolerated
    private static bool Follows(ulong prev, ulong next) =>
        next + MaxStepBackMicros >= prev && next <= prev + MaxStepForwardMicros;

    internal static ulong ReadMicros(ReadOnlySpan<byte> buffer, int at) =>
        BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(at, 8));

    /// <summary>Same conversion as the library's log reader (millisecond resolution, local time).</summary>
    internal static DateTime ToLocal(ulong micros) =>
        DateTime.UnixEpoch.AddMilliseconds(micros / 1000).ToLocalTime();

    private static byte[] ReadAt(Stream stream, long position, int count)
    {
        var buffer = new byte[count];
        stream.Position = position;
        var read = ReadFully(stream, buffer, count);
        return read == count ? buffer : buffer[..read];
    }

    private static int ReadFully(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, total, count - total);
            if (n <= 0)
                break;
            total += n;
        }

        return total;
    }
}
