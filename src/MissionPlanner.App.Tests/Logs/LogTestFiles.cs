using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MissionPlanner.App.Tests.Logs;

/// <summary>Writes small DataFlash (.bin / .log) and telemetry (.tlog) logs for the log tests.</summary>
internal static class LogTestFiles
{
    public const int GpsWeek = 2300;
    public const uint GpsMs = 345_600_000; // Thursday 00:00 of the week

    public static string TempPath(string extension) =>
        Path.Combine(TestEnvironment.DataRoot, "log-" + Guid.NewGuid().ToString("N")[..10] + extension);

    /// <summary>
    /// A binary DataFlash log: MSG with the firmware name, ATT at 1..3 s (Roll = 1.5 t, Pitch = -t),
    /// GPS with instances 0 and 1 (FMTU marks the I column as the instance), a mode change to
    /// LOITER at 1.5 s, an ERR and an EV.
    /// </summary>
    public static byte[] BinaryLog(int attSamples = 21, bool withGps = true)
    {
        var w = new BinWriter();
        w.Fmt(128, "FMT", "BBnNZ", "Type,Length,Name,Format,Columns");
        w.Fmt(129, "ATT", "Qff", "TimeUS,Roll,Pitch");
        w.Fmt(130, "GPS", "QBBIHBLLf", "TimeUS,I,Status,GMS,GWk,NSats,Lat,Lng,Alt");
        w.Fmt(131, "FMTU", "QBNN", "TimeUS,FmtType,UnitIds,MultIds");
        w.Fmt(132, "MODE", "QMBB", "TimeUS,Mode,ModeNum,Rsn");
        w.Fmt(133, "MSG", "QZ", "TimeUS,Message");
        w.Fmt(134, "ERR", "QBB", "TimeUS,Subsys,ECode");
        w.Fmt(135, "EV", "QB", "TimeUS,Id");

        w.Msg(131, "QBNN", 0UL, (byte)130, "s#-s--DUm", "F--C--GG0");
        w.Msg(133, "QZ", 500_000UL, "ArduCopter V4.5.0 (test)");

        for (var i = 0; i < attSamples; i++)
        {
            var t = 1.0 + i * 0.1;
            w.Msg(129, "Qff", (ulong)Math.Round(t * 1e6), (float)(1.5 * t), (float)-t);
            if (withGps && i % 5 == 0)
            {
                for (byte inst = 0; inst < 2; inst++)
                {
                    w.Msg(130, "QBBIHBLLf", (ulong)Math.Round(t * 1e6), inst, (byte)3, GpsMs + (uint)(i * 100), (ushort)GpsWeek,
                        (byte)12, -35.3632 + i * 1e-4 + inst * 1e-5, 149.1652 + i * 1e-4, (float)(100 + i + inst * 1000));
                }
            }
        }

        w.Msg(132, "QMBB", 1_500_000UL, (byte)5, (byte)5, (byte)1);
        w.Msg(134, "QBB", 2_000_000UL, (byte)5, (byte)1);
        w.Msg(135, "QB", 2_100_000UL, (byte)10);
        return w.ToArray();
    }

    /// <summary>The same kind of log in text form (.log).</summary>
    public static string TextLog() =>
        string.Join("\n",
            "FMT, 128, 89, FMT, BBnNZ, Type,Length,Name,Format,Columns",
            "FMT, 129, 19, ATT, Qff, TimeUS,Roll,Pitch",
            "FMT, 130, 14, MODE, QMBB, TimeUS,Mode,ModeNum,Rsn",
            "FMT, 131, 75, MSG, QZ, TimeUS,Message",
            "MSG, 500000, ArduPlane V4.5.0",
            "ATT, 1000000, 1.5, -2.25",
            "MODE, 1500000, Loiter, 12, 1",
            "ATT, 2000000, 3, -4.5",
            "ATT, 3000000, 4.5, -6.75",
            "") + "\n";

    /// <summary>
    /// A telemetry log: vehicle 1:1 (ArduCopter) and a GCS 255:190 heartbeat, ATTITUDE every 0.2 s
    /// (roll = 0.1 i), a mode change to LOITER at 1 s, the firmware STATUSTEXT, an error STATUSTEXT
    /// at 1.2 s and GLOBAL_POSITION_INT.
    /// </summary>
    public static byte[] Tlog(DateTime startUtc, int attitudeSamples = 10)
    {
        var w = new TlogWriter(startUtc);
        w.Write(0, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, Heartbeat(0), 1, 1);
        w.Write(0, MAVLink.MAVLINK_MSG_ID.HEARTBEAT,
            new MAVLink.mavlink_heartbeat_t(0, (byte)MAVLink.MAV_TYPE.GCS, (byte)MAVLink.MAV_AUTOPILOT.INVALID, 0, 0, 3), 255, 190);
        w.Write(0.1, MAVLink.MAVLINK_MSG_ID.STATUSTEXT, StatusText(MAVLink.MAV_SEVERITY.INFO, "ArduCopter V4.5.0"), 1, 1);

        for (var i = 0; i < attitudeSamples; i++)
        {
            var t = 0.2 * (i + 1);
            w.Write(t, MAVLink.MAVLINK_MSG_ID.ATTITUDE,
                new MAVLink.mavlink_attitude_t((uint)(t * 1000), 0.1f * i, -0.05f * i, 1, 0, 0, 0), 1, 1);
            w.Write(t, MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT,
                new MAVLink.mavlink_global_position_int_t((uint)(t * 1000), (int)((-35.3632 + i * 1e-4) * 1e7),
                    (int)((149.1652 + i * 1e-4) * 1e7), 584_000 + i * 1000, 10_000, 0, 0, 0, 0), 1, 1);
            if (Math.Abs(t - 1.0) < 1e-9)
                w.Write(t, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, Heartbeat(5), 1, 1);
            if (Math.Abs(t - 1.2) < 1e-9)
                w.Write(t, MAVLink.MAVLINK_MSG_ID.STATUSTEXT, StatusText(MAVLink.MAV_SEVERITY.ERROR, "PreArm: test failure"), 1, 1);
        }

        return w.ToArray();
    }

    private static MAVLink.mavlink_heartbeat_t Heartbeat(uint mode) =>
        new(mode, (byte)MAVLink.MAV_TYPE.QUADROTOR, (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA,
            (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED, (byte)MAVLink.MAV_STATE.STANDBY, 3);

    private static MAVLink.mavlink_statustext_t StatusText(MAVLink.MAV_SEVERITY severity, string text)
    {
        var bytes = new byte[50];
        Encoding.ASCII.GetBytes(text, 0, Math.Min(50, text.Length), bytes, 0);
        return new MAVLink.mavlink_statustext_t((byte)severity, bytes, 0, 0);
    }

    /// <summary>DataFlash binary records: A3 95 type, then the fields packed little-endian.</summary>
    internal sealed class BinWriter
    {
        private readonly MemoryStream _ms = new();

        public void Fmt(byte type, string name, string format, string columns)
        {
            var length = 3 + format.Sum(Size);
            Msg(128, "BBnNZ", type, (byte)length, name, format, columns);
        }

        public void Msg(byte type, string format, params object[] values)
        {
            _ms.WriteByte(0xA3);
            _ms.WriteByte(0x95);
            _ms.WriteByte(type);
            for (var i = 0; i < format.Length; i++)
                Write(format[i], values[i]);
        }

        public byte[] ToArray() => _ms.ToArray();

        private void Write(char f, object v)
        {
            Span<byte> b = stackalloc byte[8];
            switch (f)
            {
                case 'B':
                case 'M':
                    _ms.WriteByte(Convert.ToByte(v, CultureInfo.InvariantCulture));
                    break;
                case 'H':
                    BinaryPrimitives.WriteUInt16LittleEndian(b, Convert.ToUInt16(v, CultureInfo.InvariantCulture));
                    _ms.Write(b[..2]);
                    break;
                case 'I':
                    BinaryPrimitives.WriteUInt32LittleEndian(b, Convert.ToUInt32(v, CultureInfo.InvariantCulture));
                    _ms.Write(b[..4]);
                    break;
                case 'L':
                    BinaryPrimitives.WriteInt32LittleEndian(b, (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture) * 1e7));
                    _ms.Write(b[..4]);
                    break;
                case 'f':
                    BinaryPrimitives.WriteSingleLittleEndian(b, Convert.ToSingle(v, CultureInfo.InvariantCulture));
                    _ms.Write(b[..4]);
                    break;
                case 'Q':
                    BinaryPrimitives.WriteUInt64LittleEndian(b, Convert.ToUInt64(v, CultureInfo.InvariantCulture));
                    _ms.Write(b);
                    break;
                case 'n':
                case 'N':
                case 'Z':
                    var bytes = new byte[Size(f)];
                    Encoding.ASCII.GetBytes((string)v, 0, Math.Min(bytes.Length, ((string)v).Length), bytes, 0);
                    _ms.Write(bytes);
                    break;
                default:
                    throw new NotSupportedException(f.ToString());
            }
        }

        private static int Size(char f) => f switch
        {
            'b' or 'B' or 'M' => 1,
            'h' or 'H' => 2,
            'i' or 'I' or 'f' or 'L' or 'n' => 4,
            'q' or 'Q' or 'd' => 8,
            'N' => 16,
            'Z' => 64,
            _ => throw new NotSupportedException(f.ToString()),
        };
    }

    /// <summary>tlog records: 8-byte big-endian µs since 1970, then the MAVLink 2 packet.</summary>
    internal sealed class TlogWriter(DateTime startUtc)
    {
        private readonly MemoryStream _ms = new();
        private readonly MAVLink.MavlinkParse _parse = new();
        private int _seq;

        public void Write(double seconds, MAVLink.MAVLINK_MSG_ID id, object message, byte sysid, byte compid)
        {
            var us = (ulong)((startUtc.AddSeconds(seconds) - DateTime.UnixEpoch).Ticks / 10);
            Span<byte> ts = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(ts, us);
            _ms.Write(ts);
            _ms.Write(_parse.GenerateMAVLinkPacket20(id, message, false, sysid, compid, _seq++ & 0xff));
        }

        public byte[] ToArray() => _ms.ToArray();
    }
}
