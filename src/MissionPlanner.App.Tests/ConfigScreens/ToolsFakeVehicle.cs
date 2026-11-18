using System.Collections.Concurrent;
using System.Text;
using MissionPlanner.Utilities;
using static MissionPlanner.ArduPilot.Mavlink.MAVFtp;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// A vehicle stand-in for the MAVFtp and inspector pages: telemetry at several rates, a short
/// parameter list, and a MAVFTP server over an in-memory file system (list, read, write, create,
/// remove, rename, CRC32). Works on any stream; the tests use TCP loopback.
/// </summary>
public sealed class ToolsFakeVehicle : IAsyncDisposable
{
    public const byte SysId = 1;
    public const byte CompId = 1;

    private readonly Stream _stream;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sender;
    private readonly Task _receiver;
    private readonly MAVLink.MavlinkParse _parse = new();
    private readonly object _writeLock = new();
    private readonly object _fsLock = new();
    private int _seq;

    public ToolsFakeVehicle(Stream stream)
    {
        _stream = stream;
        Dirs.Add("/");
        _sender = Task.Run(SendLoop);
        _receiver = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
    }

    /// <summary>Folders, as full paths without a trailing slash ("/" is the root).</summary>
    public HashSet<string> Dirs { get; } = new(StringComparer.Ordinal);

    /// <summary>Files by full path.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public DateTime FileTime { get; set; } = new(2025, 3, 14, 15, 9, 26, DateTimeKind.Utc);

    /// <summary>Answer CRC requests with a wrong value (to test upload verification).</summary>
    public bool CorruptCrc { get; set; }

    public ConcurrentQueue<string> FtpLog { get; } = new();

    public void AddDir(string path)
    {
        lock (_fsLock)
            Dirs.Add(path);
    }

    public void AddFile(string path, byte[] data)
    {
        lock (_fsLock)
            Files[path] = data;
    }

    public byte[]? GetFile(string path)
    {
        lock (_fsLock)
            return Files.TryGetValue(path, out var d) ? d : null;
    }

    public bool HasDir(string path)
    {
        lock (_fsLock)
            return Dirs.Contains(path);
    }

    // ---- telemetry ----

    private async Task SendLoop()
    {
        var t = 0;
        var boot = 0u;
        try
        {
            Write(MAVLink.MAVLINK_MSG_ID.STATUSTEXT, new MAVLink.mavlink_statustext_t
            {
                severity = (byte)MAVLink.MAV_SEVERITY.INFO,
                text = Encoding.ASCII.GetBytes("ArduCopter V4.6.0 (fake)").MakeSize(50),
            });
            while (!_stop.IsCancellationRequested)
            {
                var a = t * 0.05;
                if (t % 10 == 0)
                {
                    Write(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, new MAVLink.mavlink_heartbeat_t(0, (byte)MAVLink.MAV_TYPE.QUADROTOR,
                        (byte)MAVLink.MAV_AUTOPILOT.ARDUPILOTMEGA, (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED,
                        (byte)MAVLink.MAV_STATE.STANDBY, 3));
                    Write(MAVLink.MAVLINK_MSG_ID.SYSTEM_TIME, new MAVLink.mavlink_system_time_t(1_741_964_966_000_000UL + (ulong)t * 100_000, boot));
                    Write(MAVLink.MAVLINK_MSG_ID.SYS_STATUS, new MAVLink.mavlink_sys_status_t
                    {
                        voltage_battery = (ushort)(12_400 - t % 300),
                        current_battery = 870,
                        battery_remaining = 81,
                        load = 312,
                    });
                }

                Write(MAVLink.MAVLINK_MSG_ID.ATTITUDE, new MAVLink.mavlink_attitude_t(boot, (float)(0.2 * Math.Sin(a)),
                    (float)(0.1 * Math.Cos(a * 0.7)), (float)(a % (2 * Math.PI) - Math.PI), 0.01f, -0.02f, 0.05f));
                if (t % 2 == 0)
                {
                    Write(MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT, new MAVLink.mavlink_global_position_int_t(boot,
                        -353632610, 1491652300, 584_000 + t % 1000, 12_000 + (int)(500 * Math.Sin(a)), 120, -35, 4, 1800));
                    Write(MAVLink.MAVLINK_MSG_ID.VFR_HUD, new MAVLink.mavlink_vfr_hud_t(4.2f, 4.5f, 12.0f + (float)Math.Sin(a), 0.4f,
                        180, 52));
                    Write(MAVLink.MAVLINK_MSG_ID.SERVO_OUTPUT_RAW, new MAVLink.mavlink_servo_output_raw_t
                    {
                        time_usec = boot * 1000,
                        servo1_raw = (ushort)(1500 + 100 * Math.Sin(a)),
                        servo2_raw = 1520,
                        servo3_raw = 1480,
                        servo4_raw = 1510,
                    });
                }

                if (t % 5 == 0)
                {
                    Write(MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT, new MAVLink.mavlink_gps_raw_int_t
                    {
                        time_usec = boot * 1000UL,
                        lat = -353632610,
                        lon = 1491652300,
                        alt = 584_000,
                        eph = 121,
                        epv = 200,
                        fix_type = 3,
                        satellites_visible = 14,
                    });
                }

                await _stream.FlushAsync().ConfigureAwait(false);
                t++;
                boot += 50;
                await Task.Delay(50, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Write(MAVLink.MAVLINK_MSG_ID id, object msg)
    {
        lock (_writeLock)
        {
            var bytes = _parse.GenerateMAVLinkPacket20(id, msg, false, SysId, CompId, _seq++ & 0xff);
            _stream.Write(bytes, 0, bytes.Length);
        }
    }

    private void ReceiveLoop()
    {
        var parse = new MAVLink.MavlinkParse();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var msg = parse.ReadPacket(_stream);
                if (msg == null || msg.Length == 0)
                    continue;
                switch ((MAVLink.MAVLINK_MSG_ID)msg.msgid)
                {
                    case MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_LIST:
                        SendParams();
                        break;
                    case MAVLink.MAVLINK_MSG_ID.FILE_TRANSFER_PROTOCOL:
                        var ftp = (MAVLink.mavlink_file_transfer_protocol_t)msg.data;
                        if (ftp.target_system == SysId)
                            HandleFtp(ftp.payload);
                        break;
                }
            }
            catch (TimeoutException)
            {
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void SendParams()
    {
        var names = new[] { ("FRAME_CLASS", 1f), ("ARMING_CHECK", 1f), ("SYSID_THISMAV", 1f) };
        for (var i = 0; i < names.Length; i++)
        {
            Write(MAVLink.MAVLINK_MSG_ID.PARAM_VALUE, new MAVLink.mavlink_param_value_t(names[i].Item2, (ushort)names.Length, (ushort)i,
                Encoding.ASCII.GetBytes(names[i].Item1).MakeSize(16), (byte)MAVLink.MAV_PARAM_TYPE.INT8));
        }
    }

    // ---- MAVFTP server ----

    private void HandleFtp(byte[] raw)
    {
        FTPPayloadHeader req = raw;
        var reply = new FTPPayloadHeader
        {
            seq_number = (ushort)(req.seq_number + 1),
            session = req.session,
            req_opcode = req.opcode,
            opcode = FTPOpcode.kRspAck,
            offset = req.offset,
        };
        var path = Encoding.UTF8.GetString(req.data, 0, Math.Min(req.size, req.data.Length)).TrimEnd('\0');
        FtpLog.Enqueue(req.opcode + " " + path);

        lock (_fsLock)
        {
            switch (req.opcode)
            {
                case FTPOpcode.kCmdNone:
                case FTPOpcode.kCmdResetSessions:
                case FTPOpcode.kCmdTerminateSession:
                case FTPOpcode.kCmdTruncateFile:
                    break;
                case FTPOpcode.kCmdListDirectoryWithTime:
                case FTPOpcode.kCmdListDirectory:
                {
                    var dir = Normalize(path);
                    if (!Dirs.Contains(dir))
                    {
                        Nak(ref reply, FTPErrorCode.kErrFileNotFound);
                        break;
                    }

                    var withTime = req.opcode == FTPOpcode.kCmdListDirectoryWithTime;
                    var entries = Children(dir).Skip((int)req.offset).ToList();
                    if (entries.Count == 0)
                    {
                        Nak(ref reply, FTPErrorCode.kErrEOF);
                        break;
                    }

                    var data = new List<byte>();
                    var stamp = ((long)(FileTime - DateTime.UnixEpoch).TotalSeconds).ToString();
                    foreach (var (name, isDir, size) in entries)
                    {
                        var text = isDir
                            ? "D" + name + (withTime ? "\t0\t" + stamp : "")
                            : "F" + name + "\t" + size + (withTime ? "\t" + stamp : "");
                        var bytes = Encoding.UTF8.GetBytes(text + "\0");
                        if (data.Count + bytes.Length > 239)
                            break;
                        data.AddRange(bytes);
                    }

                    reply.data = data.ToArray();
                    break;
                }

                case FTPOpcode.kCmdOpenFileRO:
                    if (!Files.TryGetValue(path, out var file))
                        Nak(ref reply, FTPErrorCode.kErrFileNotFound);
                    else
                    {
                        _reading = path;
                        reply.session = 1;
                        reply.data = BitConverter.GetBytes(file.Length);
                    }

                    break;
                case FTPOpcode.kCmdReadFile:
                {
                    if (_reading == null || !Files.TryGetValue(_reading, out var open) || req.offset >= open.Length)
                    {
                        Nak(ref reply, FTPErrorCode.kErrEOF);
                        break;
                    }

                    reply.data = open.Skip((int)req.offset).Take(Math.Min((int)req.size, 239)).ToArray();
                    break;
                }

                case FTPOpcode.kCmdCreateFile:
                case FTPOpcode.kCmdOpenFileWO:
                    if (!Dirs.Contains(Parent(path)))
                    {
                        Nak(ref reply, FTPErrorCode.kErrFileNotFound);
                        break;
                    }

                    _writing = path;
                    if (req.opcode == FTPOpcode.kCmdCreateFile || !Files.ContainsKey(path))
                        Files[path] = [];
                    reply.session = 0;
                    break;
                case FTPOpcode.kCmdWriteFile:
                    if (_writing == null || !Files.TryGetValue(_writing, out var current))
                    {
                        Nak(ref reply, FTPErrorCode.kErrInvalidSession);
                        break;
                    }

                    var end = (int)req.offset + req.size;
                    if (current.Length < end)
                        Array.Resize(ref current, end);
                    Array.Copy(req.data, 0, current, (int)req.offset, req.size);
                    Files[_writing] = current;
                    break;
                case FTPOpcode.kCmdRemoveFile:
                    if (!Files.Remove(path))
                        Nak(ref reply, FTPErrorCode.kErrFileNotFound);
                    break;
                case FTPOpcode.kCmdCreateDirectory:
                    if (Dirs.Contains(path) || Files.ContainsKey(path))
                        Nak(ref reply, FTPErrorCode.kErrFailFileExists);
                    else if (!Dirs.Contains(Parent(path)))
                        Nak(ref reply, FTPErrorCode.kErrFileNotFound);
                    else
                        Dirs.Add(path);
                    break;
                case FTPOpcode.kCmdRemoveDirectory:
                    if (!Dirs.Contains(path))
                        Nak(ref reply, FTPErrorCode.kErrFileNotFound);
                    else if (Children(path).Any(c => c.Name is not ("." or "..")))
                        Nak(ref reply, FTPErrorCode.kErrFailErrno, 13); // EACCES, as FatFs answers for a non-empty folder
                    else
                        Dirs.Remove(path);
                    break;
                case FTPOpcode.kCmdRename:
                {
                    var parts = Encoding.UTF8.GetString(req.data, 0, req.size).Split('\0');
                    var from = parts[0];
                    var to = parts.Length > 1 ? parts[1] : "";
                    if (Files.Remove(from, out var moved))
                        Files[to] = moved;
                    else if (Dirs.Remove(from))
                    {
                        Dirs.Add(to);
                        foreach (var d in Dirs.Where(d => d.StartsWith(from + "/")).ToList())
                        {
                            Dirs.Remove(d);
                            Dirs.Add(to + d[from.Length..]);
                        }

                        foreach (var (k, v) in Files.Where(kv => kv.Key.StartsWith(from + "/")).ToList())
                        {
                            Files.Remove(k);
                            Files[to + k[from.Length..]] = v;
                        }
                    }
                    else
                        Nak(ref reply, FTPErrorCode.kErrFileNotFound);

                    break;
                }

                case FTPOpcode.kCmdCalcFileCRC32:
                    if (!Files.TryGetValue(path, out var content))
                        Nak(ref reply, FTPErrorCode.kErrFileNotFound);
                    else
                        reply.data = BitConverter.GetBytes(crc_crc32(0, content) ^ (CorruptCrc ? 0xdeadbeefu : 0u));
                    break;
                default:
                    Nak(ref reply, FTPErrorCode.kErrUnknownCommand);
                    break;
            }
        }

        reply.data ??= [];
        reply.size = (byte)reply.data.Length;
        Write(MAVLink.MAVLINK_MSG_ID.FILE_TRANSFER_PROTOCOL, new MAVLink.mavlink_file_transfer_protocol_t
        {
            target_network = 0,
            target_system = 255,
            target_component = 190,
            payload = reply,
        });
    }

    private string? _writing;
    private string? _reading;

    private static void Nak(ref FTPPayloadHeader reply, FTPErrorCode code, byte errno = 0)
    {
        reply.opcode = FTPOpcode.kRspNak;
        reply.data = code == FTPErrorCode.kErrFailErrno ? [(byte)code, errno] : [(byte)code];
    }

    private static string Normalize(string dir)
    {
        if (dir.Length > 1)
            dir = dir.TrimEnd('/');
        return dir.Length == 0 ? "/" : dir;
    }

    private static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private IEnumerable<(string Name, bool IsDir, int Size)> Children(string dir)
    {
        var prefix = dir == "/" ? "/" : dir + "/";
        var dirs = Dirs.Where(d => d != dir && d.StartsWith(prefix, StringComparison.Ordinal) && !d[prefix.Length..].Contains('/'))
            .Select(d => (d[prefix.Length..], true, 0));
        var files = Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && !f.Key[prefix.Length..].Contains('/'))
            .Select(f => (f.Key[prefix.Length..], false, f.Value.Length));
        // SITL lists "." and ".." too; the browser must hide them
        return new[] { (".", true, 0), ("..", true, 0) }.Concat(dirs.Concat(files).OrderBy(e => e.Item1, StringComparer.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _sender.ConfigureAwait(false);
        await Task.WhenAny(_receiver, Task.Delay(2000)).ConfigureAwait(false);
        _stop.Dispose();
    }
}
