using System.Runtime.InteropServices;
using System.Text;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;

/// <summary>
/// Linux kernel joystick devices (/dev/input/js*), like Mission Planner's JoystickLinux: a reader
/// thread decodes 8-byte js_event records. Hats show up as extra axes in this API.
/// </summary>
public sealed class LinuxJoydevBackend(string? status = null) : IJoystickBackend
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(LinuxJoydevBackend));

    public static string DeviceFolder { get; set; } = "/dev/input";

    public string Name => "joydev";

    public string Status { get; } = status ?? "Linux joystick driver (/dev/input/js*)";

    public IReadOnlyList<JoystickDeviceInfo> GetDevices()
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles(DeviceFolder, "js*").Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception)
        {
            return [];
        }

        var list = new List<JoystickDeviceInfo>();
        foreach (var path in paths)
        {
            var name = JoydevNative.TryGetName(path) ?? Path.GetFileName(path);
            var same = list.Count(d => d.Name == name || d.Name.StartsWith(name + " #", StringComparison.Ordinal));
            list.Add(new JoystickDeviceInfo(path, same == 0 ? name : $"{name} #{same + 1}", Name));
        }

        return list;
    }

    public IJoystickDevice Open(JoystickDeviceInfo device)
    {
        FileStream fs;
        try
        {
            fs = new FileStream(device.Id, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
        }
        catch (UnauthorizedAccessException)
        {
            throw new IOException($"No permission to read {device.Id} (add yourself to the 'input' group).");
        }
        catch (FileNotFoundException)
        {
            throw new IOException($"{device.Name} is no longer connected.");
        }

        return new JoydevDevice(device, fs);
    }

    public void Dispose()
    {
    }

    private sealed class JoydevDevice : IJoystickDevice
    {
        private readonly FileStream _fs;
        private readonly JoydevState _state;
        private readonly Thread _reader;
        private volatile bool _attached = true;
        private volatile bool _closed;

        public JoydevDevice(JoystickDeviceInfo info, FileStream fs)
        {
            Info = info;
            _fs = fs;
            var fd = (int)fs.SafeFileHandle.DangerousGetHandle();
            AxisCount = JoydevNative.TryGetCount(fd, JoydevNative.JSIOCGAXES) ?? 8;
            ButtonCount = JoydevNative.TryGetCount(fd, JoydevNative.JSIOCGBUTTONS) ?? 16;
            _state = new JoydevState(AxisCount, ButtonCount);
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "joydev " + Path.GetFileName(info.Id) };
            _reader.Start();
        }

        public JoystickDeviceInfo Info { get; }

        public int AxisCount { get; }

        public int ButtonCount { get; }

        public int HatCount => 0;

        public bool IsAttached => _attached;

        public JoystickSnapshot Read() => _state.Snapshot();

        private void ReadLoop()
        {
            var buf = new byte[8];
            try
            {
                while (!_closed)
                {
                    var got = 0;
                    while (got < 8)
                    {
                        var n = _fs.Read(buf, got, 8 - got);
                        if (n <= 0)
                            throw new EndOfStreamException();
                        got += n;
                    }

                    _state.Apply(buf);
                }
            }
            catch (Exception ex)
            {
                if (!_closed)
                {
                    // ENODEV when unplugged
                    Log.Info("joystick lost: " + ex.Message);
                    _attached = false;
                }
            }
        }

        public void Dispose()
        {
            _closed = true;
            _fs.Dispose();
        }
    }
}

/// <summary>Decodes Linux js_event records (u32 time, s16 value, u8 type, u8 number) into a state.</summary>
public sealed class JoydevState(int axes, int buttons)
{
    public const byte EventButton = 0x01;
    public const byte EventAxis = 0x02;
    public const byte EventInit = 0x80;

    private readonly object _gate = new();
    private short[] _axes = new short[axes];
    private bool[] _buttons = new bool[buttons];

    public void Apply(ReadOnlySpan<byte> ev)
    {
        var value = BitConverter.ToInt16(ev[4..6]);
        var type = (byte)(ev[6] & ~EventInit);
        var number = ev[7];
        lock (_gate)
        {
            if (type == EventAxis)
            {
                if (number >= _axes.Length)
                    Array.Resize(ref _axes, number + 1);
                _axes[number] = value;
            }
            else if (type == EventButton)
            {
                if (number >= _buttons.Length)
                    Array.Resize(ref _buttons, number + 1);
                _buttons[number] = value != 0;
            }
        }
    }

    public JoystickSnapshot Snapshot()
    {
        lock (_gate)
            return new JoystickSnapshot((short[])_axes.Clone(), (bool[])_buttons.Clone(), []);
    }
}

internal static class JoydevNative
{
    // _IOR('j', nr, size): dir 2 (read) << 30 | size << 16 | 'j' << 8 | nr
    private static nuint Ior(uint nr, uint size) => (nuint)((2u << 30) | (size << 16) | ((uint)'j' << 8) | nr);

    public static readonly nuint JSIOCGAXES = Ior(0x11, 1);
    public static readonly nuint JSIOCGBUTTONS = Ior(0x12, 1);

    public static nuint JSIOCGNAME(uint len) => Ior(0x13, len);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, byte[] arg);

    public static int? TryGetCount(int fd, nuint request)
    {
        try
        {
            var b = new byte[1];
            return ioctl(fd, request, b) < 0 ? null : b[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? TryGetName(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
            var buf = new byte[128];
            if (ioctl((int)fs.SafeFileHandle.DangerousGetHandle(), JSIOCGNAME((uint)buf.Length), buf) < 0)
                return null;
            var end = Array.IndexOf(buf, (byte)0);
            var name = Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end).Trim();
            return name.Length > 0 ? name : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
