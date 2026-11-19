namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

/// <summary>
/// Packs RTCM data into GPS_RTCM_DATA messages the way Mission Planner and ArduPilot
/// (AP_GPS::handle_gps_rtcm_fragment) agree on:
/// <list type="bullet">
/// <item>data under 180 bytes goes in one unfragmented message (flags bit 0 clear);</item>
/// <item>longer data is split into up to 4 fragments of 180 bytes, flags = 1 | fragment &lt;&lt; 1 | sequence &lt;&lt; 3;</item>
/// <item>the receiver knows the last fragment because it is shorter than 180, so an exact multiple of
/// 180 bytes gets an extra empty fragment (except 4 full fragments, which ArduPilot detects);</item>
/// <item>the 5-bit sequence number advances once per block.</item>
/// </list>
/// A block is at most 720 bytes; larger RTCM frames (up to 1029) are sent as consecutive blocks,
/// which the autopilot writes to the GPS one after the other, so the receiver sees the frame intact.
/// </summary>
public sealed class GpsRtcmFragmenter
{
    public const int FragmentSize = 180;
    public const int MaxFragments = 4;
    public const int MaxBlock = FragmentSize * MaxFragments;

    private int _sequence;

    /// <summary>The sequence number the next block will use (0-31).</summary>
    public int NextSequence
    {
        get => _sequence & 0x1f;
        set => _sequence = value & 0x1f;
    }

    public List<MAVLink.mavlink_gps_rtcm_data_t> Fragment(ReadOnlySpan<byte> data)
    {
        var packets = new List<MAVLink.mavlink_gps_rtcm_data_t>();
        do
        {
            var block = data[..Math.Min(data.Length, MaxBlock)];
            AddBlock(block, packets);
            data = data[block.Length..];
        }
        while (data.Length > 0);

        return packets;
    }

    private void AddBlock(ReadOnlySpan<byte> block, List<MAVLink.mavlink_gps_rtcm_data_t> packets)
    {
        var seq = _sequence & 0x1f;
        _sequence = (_sequence + 1) & 0x1f;

        if (block.Length < FragmentSize)
        {
            packets.Add(Packet((byte)(seq << 3), block));
            return;
        }

        // full fragments, then the short (possibly empty) one that ends the block
        var count = block.Length / FragmentSize + 1;
        if (count > MaxFragments)
            count = MaxFragments;

        for (var frag = 0; frag < count; frag++)
        {
            var start = frag * FragmentSize;
            var len = Math.Min(block.Length - start, FragmentSize);
            var flags = (byte)(1 | (frag << 1) | (seq << 3));
            packets.Add(Packet(flags, len > 0 ? block.Slice(start, len) : ReadOnlySpan<byte>.Empty));
        }
    }

    private static MAVLink.mavlink_gps_rtcm_data_t Packet(byte flags, ReadOnlySpan<byte> data)
    {
        var buf = new byte[FragmentSize];
        data.CopyTo(buf);
        return new MAVLink.mavlink_gps_rtcm_data_t { flags = flags, len = (byte)data.Length, data = buf };
    }
}
