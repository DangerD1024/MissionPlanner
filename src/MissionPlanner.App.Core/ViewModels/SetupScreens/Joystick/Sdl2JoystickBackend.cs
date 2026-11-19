using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Joystick;

/// <summary>The SDL2 joystick functions, bound at run time to whichever SDL2 library is installed.</summary>
internal sealed class Sdl2Native
{
    public const uint InitJoystick = 0x00000200;

    [StructLayout(LayoutKind.Sequential)]
    public struct SdlVersion
    {
        public byte Major;
        public byte Minor;
        public byte Patch;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int InitFn(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void QuitSubSystemFn(uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetErrorFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SetHintFn([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GetVersionFn(out SdlVersion version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int IntFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VoidFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr PtrOfIndexFn(int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int IntOfIndexFn(int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CloseFn(IntPtr joystick);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int IntOfJoystickFn(IntPtr joystick);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate short AxisFn(IntPtr joystick, int axis);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte ByteOfIndexFn(IntPtr joystick, int index);

    public InitFn Init = null!;
    public QuitSubSystemFn QuitSubSystem = null!;
    public GetErrorFn GetErrorPtr = null!;
    public SetHintFn SetHint = null!;
    public GetVersionFn GetVersion = null!;
    public IntFn NumJoysticks = null!;
    public PtrOfIndexFn JoystickNameForIndex = null!;
    public IntOfIndexFn JoystickGetDeviceInstanceID = null!;
    public PtrOfIndexFn JoystickOpen = null!;
    public CloseFn JoystickClose = null!;
    public IntOfJoystickFn JoystickNumAxes = null!;
    public IntOfJoystickFn JoystickNumButtons = null!;
    public IntOfJoystickFn JoystickNumHats = null!;
    public IntOfJoystickFn JoystickGetAttached = null!;
    public AxisFn JoystickGetAxis = null!;
    public ByteOfIndexFn JoystickGetButton = null!;
    public ByteOfIndexFn JoystickGetHat = null!;
    public VoidFn JoystickUpdate = null!;
    public IntOfIndexFn JoystickEventState = null!;

    public string LibraryPath { get; private set; } = "";

    public string GetError() => Marshal.PtrToStringUTF8(GetErrorPtr()) ?? "";

    /// <summary>Library names to try, the application folder first.</summary>
    public static IEnumerable<string> Candidates()
    {
        string[] names;
        if (OperatingSystem.IsWindows())
            names = ["SDL2.dll"];
        else if (OperatingSystem.IsMacOS())
            names = ["libSDL2-2.0.0.dylib", "libSDL2.dylib"];
        else
            names = ["libSDL2-2.0.so.0", "libSDL2-2.0.so", "libSDL2.so"];

        foreach (var n in names)
            yield return Path.Combine(AppContext.BaseDirectory, n);
        if (OperatingSystem.IsMacOS())
        {
            foreach (var dir in new[] { "/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib" })
            foreach (var n in names)
                yield return Path.Combine(dir, n);
        }

        foreach (var n in names)
            yield return n;
    }

    public static Sdl2Native? TryLoad(out string? error)
    {
        foreach (var candidate in Candidates())
        {
            if (Path.IsPathRooted(candidate) && !File.Exists(candidate))
                continue;
            if (!NativeLibrary.TryLoad(candidate, out var handle))
                continue;

            var native = new Sdl2Native { LibraryPath = candidate };
            try
            {
                native.Bind(handle);
                error = null;
                return native;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or InvalidOperationException)
            {
                error = $"{candidate} is not a usable SDL2 library ({ex.Message}).";
                return null;
            }
        }

        error = JoystickBackends.MissingSdlHint();
        return null;
    }

    private void Bind(IntPtr lib)
    {
        T F<T>(string name) where T : Delegate =>
            NativeLibrary.TryGetExport(lib, name, out var p)
                ? Marshal.GetDelegateForFunctionPointer<T>(p)
                : throw new EntryPointNotFoundException(name);

        Init = F<InitFn>("SDL_Init");
        QuitSubSystem = F<QuitSubSystemFn>("SDL_QuitSubSystem");
        GetErrorPtr = F<GetErrorFn>("SDL_GetError");
        SetHint = F<SetHintFn>("SDL_SetHint");
        GetVersion = F<GetVersionFn>("SDL_GetVersion");
        NumJoysticks = F<IntFn>("SDL_NumJoysticks");
        JoystickNameForIndex = F<PtrOfIndexFn>("SDL_JoystickNameForIndex");
        JoystickGetDeviceInstanceID = F<IntOfIndexFn>("SDL_JoystickGetDeviceInstanceID");
        JoystickOpen = F<PtrOfIndexFn>("SDL_JoystickOpen");
        JoystickClose = F<CloseFn>("SDL_JoystickClose");
        JoystickNumAxes = F<IntOfJoystickFn>("SDL_JoystickNumAxes");
        JoystickNumButtons = F<IntOfJoystickFn>("SDL_JoystickNumButtons");
        JoystickNumHats = F<IntOfJoystickFn>("SDL_JoystickNumHats");
        JoystickGetAttached = F<IntOfJoystickFn>("SDL_JoystickGetAttached");
        JoystickGetAxis = F<AxisFn>("SDL_JoystickGetAxis");
        JoystickGetButton = F<ByteOfIndexFn>("SDL_JoystickGetButton");
        JoystickGetHat = F<ByteOfIndexFn>("SDL_JoystickGetHat");
        JoystickUpdate = F<VoidFn>("SDL_JoystickUpdate");
        JoystickEventState = F<IntOfIndexFn>("SDL_JoystickEventState");
    }
}

/// <summary>
/// Joysticks through SDL2. Every SDL call runs on one dedicated thread (SDL wants that, and on
/// macOS its HID run loop lives there); that thread also samples the open devices at 100 Hz so
/// <see cref="IJoystickDevice.Read"/> never blocks.
/// </summary>
public sealed class Sdl2JoystickBackend : IJoystickBackend
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(Sdl2JoystickBackend));

    private readonly Sdl2Native _sdl;
    private readonly BlockingCollection<Action> _work = new();
    private readonly List<SdlDevice> _open = new();
    private readonly Thread _thread;
    private volatile bool _stop;

    private Sdl2JoystickBackend(Sdl2Native sdl)
    {
        _sdl = sdl;
        _thread = new Thread(Run) { IsBackground = true, Name = "SDL joystick" };
    }

    public string Name => "SDL2";

    public string Status { get; private set; } = "";

    /// <summary>Loads SDL2 and starts its joystick subsystem; false (with the reason) when that is not possible.</summary>
    public static bool TryCreate(out IJoystickBackend? backend, out string? error)
    {
        backend = null;
        var sdl = Sdl2Native.TryLoad(out error);
        if (sdl == null)
            return false;

        var b = new Sdl2JoystickBackend(sdl);
        var started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        b._work.Add(() =>
        {
            try
            {
                // joysticks must keep reporting while another window (ours) has focus, and SDL must
                // not take over SIGINT/SIGTERM from .NET
                sdl.SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
                sdl.SetHint("SDL_NO_SIGNAL_HANDLERS", "1");
                sdl.SetHint("SDL_JOYSTICK_MFI", "0");
                if (sdl.Init(Sdl2Native.InitJoystick) != 0)
                {
                    started.SetResult("SDL could not start its joystick support: " + sdl.GetError());
                    return;
                }

                // nobody drains SDL's event queue: read the devices directly instead
                sdl.JoystickEventState(0);
                sdl.GetVersion(out var v);
                b.Status = $"SDL {v.Major}.{v.Minor}.{v.Patch} ({Path.GetFileName(sdl.LibraryPath)})";
                started.SetResult(null);
            }
            catch (Exception ex)
            {
                started.SetResult("SDL failed: " + ex.Message);
            }
        });
        b._thread.Start();

        if (!started.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            error = "SDL did not start.";
            b._stop = true;
            return false;
        }

        if (started.Task.Result is { } failed)
        {
            error = failed;
            b._stop = true;
            return false;
        }

        backend = b;
        return true;
    }

    private void Run()
    {
        var next = DateTime.UtcNow;
        while (!_stop)
        {
            try
            {
                var wait = next - DateTime.UtcNow;
                if (_work.TryTake(out var job, wait > TimeSpan.Zero ? wait : TimeSpan.Zero))
                {
                    job();
                    continue;
                }

                next = DateTime.UtcNow.AddMilliseconds(10);
                if (Status.Length == 0)
                    continue;
                _sdl.JoystickUpdate();
                foreach (var d in _open)
                    d.Sample();
            }
            catch (Exception ex)
            {
                Log.Warn("sdl loop", ex);
                Thread.Sleep(100);
            }
        }

        try
        {
            foreach (var d in _open)
                d.CloseOnSdlThread();
            _open.Clear();
            if (Status.Length > 0)
                _sdl.QuitSubSystem(Sdl2Native.InitJoystick);
        }
        catch (Exception ex)
        {
            Log.Warn("sdl quit", ex);
        }
    }

    private T Invoke<T>(Func<T> func)
    {
        if (Thread.CurrentThread == _thread)
            return func();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("SDL did not answer.");
        return tcs.Task.Result;
    }

    public IReadOnlyList<JoystickDeviceInfo> GetDevices() => Invoke(() =>
    {
        // detects plugged and unplugged devices
        _sdl.JoystickUpdate();
        var list = new List<JoystickDeviceInfo>();
        var n = _sdl.NumJoysticks();
        for (var i = 0; i < n; i++)
        {
            var name = Marshal.PtrToStringUTF8(_sdl.JoystickNameForIndex(i));
            if (string.IsNullOrWhiteSpace(name))
                name = "Joystick " + (i + 1);
            var id = _sdl.JoystickGetDeviceInstanceID(i);
            // two identical pads need distinct names (and profiles)
            var same = list.Count(d => d.Name == name || d.Name.StartsWith(name + " #", StringComparison.Ordinal));
            list.Add(new JoystickDeviceInfo("sdl:" + id, same == 0 ? name : $"{name} #{same + 1}", Name));
        }

        return (IReadOnlyList<JoystickDeviceInfo>)list;
    });

    public IJoystickDevice Open(JoystickDeviceInfo device) => Invoke<IJoystickDevice>(() =>
    {
        _sdl.JoystickUpdate();
        var n = _sdl.NumJoysticks();
        for (var i = 0; i < n; i++)
        {
            if ("sdl:" + _sdl.JoystickGetDeviceInstanceID(i) != device.Id)
                continue;
            var handle = _sdl.JoystickOpen(i);
            if (handle == IntPtr.Zero)
                throw new IOException("SDL could not open the joystick: " + _sdl.GetError());
            var dev = new SdlDevice(this, device, handle);
            _open.Add(dev);
            dev.Sample();
            return dev;
        }

        throw new IOException($"{device.Name} is no longer connected.");
    });

    public void Dispose()
    {
        _stop = true;
        _work.Add(() => { });
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class SdlDevice : IJoystickDevice
    {
        private readonly Sdl2JoystickBackend _owner;
        private IntPtr _handle;
        private volatile JoystickSnapshot _last = JoystickSnapshot.Empty;
        private volatile bool _attached = true;

        public SdlDevice(Sdl2JoystickBackend owner, JoystickDeviceInfo info, IntPtr handle)
        {
            _owner = owner;
            _handle = handle;
            Info = info;
            var sdl = owner._sdl;
            AxisCount = Math.Max(0, sdl.JoystickNumAxes(handle));
            ButtonCount = Math.Max(0, sdl.JoystickNumButtons(handle));
            HatCount = Math.Max(0, sdl.JoystickNumHats(handle));
        }

        public JoystickDeviceInfo Info { get; }

        public int AxisCount { get; }

        public int ButtonCount { get; }

        public int HatCount { get; }

        public bool IsAttached => _attached;

        public JoystickSnapshot Read() => _last;

        /// <summary>On the SDL thread.</summary>
        public void Sample()
        {
            if (_handle == IntPtr.Zero)
                return;
            var sdl = _owner._sdl;
            if (sdl.JoystickGetAttached(_handle) == 0)
            {
                _attached = false;
                return;
            }

            var axes = new short[AxisCount];
            for (var i = 0; i < axes.Length; i++)
                axes[i] = sdl.JoystickGetAxis(_handle, i);
            var buttons = new bool[ButtonCount];
            for (var i = 0; i < buttons.Length; i++)
                buttons[i] = sdl.JoystickGetButton(_handle, i) != 0;
            var hats = new HatDirection[HatCount];
            for (var i = 0; i < hats.Length; i++)
                hats[i] = (HatDirection)(sdl.JoystickGetHat(_handle, i) & 0x0f);
            _last = new JoystickSnapshot(axes, buttons, hats);
        }

        public void CloseOnSdlThread()
        {
            if (_handle != IntPtr.Zero)
                _owner._sdl.JoystickClose(_handle);
            _handle = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_owner._stop)
                return;
            try
            {
                _owner.Invoke(() =>
                {
                    CloseOnSdlThread();
                    _owner._open.Remove(this);
                    return 0;
                });
            }
            catch (TimeoutException)
            {
            }
        }
    }
}
