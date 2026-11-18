using System.Runtime.InteropServices;
using log4net;

namespace MissionPlanner.App.Core.Video;

/// <summary>
/// The few GStreamer calls the video engine needs (parse a pipeline, pull BGRA frames from an
/// appsink, read bus errors), bound at run time with <see cref="NativeLibrary"/> so a missing
/// GStreamer install is a normal "not available" answer instead of a crash. Looks in the usual
/// places on Linux, Windows (official installer, Mission Planner's own download) and macOS
/// (GStreamer.framework, Homebrew).
/// </summary>
/// <remarks>
/// ExtLibs' <c>MissionPlanner.Utilities.GStreamer</c> was not reused: it allocates a bitmap per frame,
/// reports through WinForms message boxes, never reads bus errors while running, and has no macOS
/// library names.
/// </remarks>
internal static class GstNative
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(GstNative));
    private static readonly object InitLock = new();
    private static bool _tried;
    private static string? _error;

    public const ulong ClockTimeNone = ulong.MaxValue;
    public const ulong Millisecond = 1_000_000;

    public const int StateNull = 1;
    public const int StatePlaying = 4;
    public const int StateChangeFailure = 0;

    public const int MessageEos = 1 << 0;
    public const int MessageError = 1 << 1;

    public const int MapRead = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct GstMapInfo
    {
        public IntPtr Memory;
        public int Flags;
        public IntPtr Data;
        public UIntPtr Size;
        public UIntPtr MaxSize;
        public IntPtr User0, User1, User2, User3;
        public IntPtr Reserved0, Reserved1, Reserved2, Reserved3;
    }

    // ---- signatures ---------------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int InitCheckFn(IntPtr argc, IntPtr argv, out IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VersionFn(out uint major, out uint minor, out uint micro, out uint nano);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr ParseLaunchFn([MarshalAs(UnmanagedType.LPUTF8Str)] string pipeline, out IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr BinGetByNameFn(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SetStateFn(IntPtr element, int state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr PtrFn(IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VoidFn(IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr BusPopFn(IntPtr bus, ulong timeout, int types);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ParseErrorFn(IntPtr message, out IntPtr error, out IntPtr debug);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr PullSampleFn(IntPtr appsink, ulong timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SetUIntFn(IntPtr appsink, uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SetBoolFn(IntPtr appsink, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr CapsGetStructureFn(IntPtr caps, uint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int StructureGetIntFn(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr StructureGetStringFn(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int BufferMapFn(IntPtr buffer, out GstMapInfo info, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void BufferUnmapFn(IntPtr buffer, ref GstMapInfo info);

    // ---- bound functions (valid after a successful TryInitialize) ------------------------------

    public static ParseLaunchFn ParseLaunch = null!;
    public static BinGetByNameFn BinGetByName = null!;
    public static SetStateFn ElementSetState = null!;
    public static PtrFn ElementGetBus = null!;
    public static VoidFn ObjectUnref = null!;
    public static VoidFn MiniObjectUnref = null!;
    public static BusPopFn BusTimedPopFiltered = null!;
    public static ParseErrorFn MessageParseError = null!;
    public static PullSampleFn AppSinkTryPullSample = null!;
    public static SetUIntFn AppSinkSetMaxBuffers = null!;
    public static SetBoolFn AppSinkSetDrop = null!;
    public static PtrFn SampleGetCaps = null!;
    public static PtrFn SampleGetBuffer = null!;
    public static CapsGetStructureFn CapsGetStructure = null!;
    public static StructureGetIntFn StructureGetInt = null!;
    public static StructureGetStringFn StructureGetString = null!;
    public static BufferMapFn BufferMap = null!;
    public static BufferUnmapFn BufferUnmap = null!;
    private static VoidFn? _gFree;
    private static VoidFn? _gErrorFree;

    /// <summary>"GStreamer 1.28.2" once initialised.</summary>
    public static string? Version { get; private set; }

    /// <summary>Loads and initialises GStreamer once; later calls return the first answer.</summary>
    public static bool TryInitialize(out string? error)
    {
        lock (InitLock)
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    _error = Initialize();
                }
                catch (Exception ex)
                {
                    _error = "GStreamer could not be loaded: " + ex.Message;
                }

                if (_error != null)
                    Log.Warn(_error);
                else
                    Log.Info(Version);
            }

            error = _error;
            return _error == null;
        }
    }

    private static string? Initialize()
    {
        var core = LoadFirst(CoreCandidates());
        if (core == IntPtr.Zero)
            return "GStreamer was not found. Install GStreamer 1.x (runtime, with the good, bad, ugly and libav plugins).";
        var app = LoadFirst(AppCandidates());
        if (app == IntPtr.Zero)
            return "GStreamer was found, but not its app library (libgstapp). Install the GStreamer base plugins.";
        var glib = LoadFirst(GlibCandidates());

        var initCheck = Bind<InitCheckFn>(core, "gst_init_check");
        if (initCheck(IntPtr.Zero, IntPtr.Zero, out var initError) == 0)
            return "GStreamer failed to initialise: " + TakeErrorMessage(initError);

        Bind<VersionFn>(core, "gst_version")(out var major, out var minor, out var micro, out _);
        Version = $"GStreamer {major}.{minor}.{micro}";

        ParseLaunch = Bind<ParseLaunchFn>(core, "gst_parse_launch");
        BinGetByName = Bind<BinGetByNameFn>(core, "gst_bin_get_by_name");
        ElementSetState = Bind<SetStateFn>(core, "gst_element_set_state");
        ElementGetBus = Bind<PtrFn>(core, "gst_element_get_bus");
        ObjectUnref = Bind<VoidFn>(core, "gst_object_unref");
        MiniObjectUnref = Bind<VoidFn>(core, "gst_mini_object_unref");
        BusTimedPopFiltered = Bind<BusPopFn>(core, "gst_bus_timed_pop_filtered");
        MessageParseError = Bind<ParseErrorFn>(core, "gst_message_parse_error");
        SampleGetCaps = Bind<PtrFn>(core, "gst_sample_get_caps");
        SampleGetBuffer = Bind<PtrFn>(core, "gst_sample_get_buffer");
        CapsGetStructure = Bind<CapsGetStructureFn>(core, "gst_caps_get_structure");
        StructureGetInt = Bind<StructureGetIntFn>(core, "gst_structure_get_int");
        StructureGetString = Bind<StructureGetStringFn>(core, "gst_structure_get_string");
        BufferMap = Bind<BufferMapFn>(core, "gst_buffer_map");
        BufferUnmap = Bind<BufferUnmapFn>(core, "gst_buffer_unmap");
        AppSinkTryPullSample = Bind<PullSampleFn>(app, "gst_app_sink_try_pull_sample");
        AppSinkSetMaxBuffers = Bind<SetUIntFn>(app, "gst_app_sink_set_max_buffers");
        AppSinkSetDrop = Bind<SetBoolFn>(app, "gst_app_sink_set_drop");

        // glib is a dependency of GStreamer, so it is normally loaded already; the symbols may
        // also be re-exported by the macOS framework binary
        var gl = glib != IntPtr.Zero ? glib : core;
        if (NativeLibrary.TryGetExport(gl, "g_free", out var gFree))
            _gFree = Marshal.GetDelegateForFunctionPointer<VoidFn>(gFree);
        if (NativeLibrary.TryGetExport(gl, "g_error_free", out var gErrorFree))
            _gErrorFree = Marshal.GetDelegateForFunctionPointer<VoidFn>(gErrorFree);
        return null;
    }

    /// <summary>Reads a GError's message and frees it.</summary>
    public static string TakeErrorMessage(IntPtr gerror)
    {
        if (gerror == IntPtr.Zero)
            return "unknown error";
        // struct GError { GQuark domain; gint code; gchar *message; }
        var text = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(gerror, 8)) ?? "unknown error";
        _gErrorFree?.Invoke(gerror);
        return text;
    }

    public static void GFree(IntPtr p)
    {
        if (p != IntPtr.Zero)
            _gFree?.Invoke(p);
    }

    private static T Bind<T>(IntPtr lib, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    private static IntPtr LoadFirst(IEnumerable<string> candidates)
    {
        foreach (var c in candidates)
        {
            // bare names go through the system search (ldconfig, PATH); paths must exist
            if (Path.IsPathRooted(c) && !File.Exists(c))
                continue;
            if (NativeLibrary.TryLoad(c, out var handle))
            {
                Log.Info("loaded " + c);
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    // ---- where to look --------------------------------------------------------------------------

    private static IEnumerable<string> CoreCandidates() => Candidates("libgstreamer-1.0.so.0", "libgstreamer-1.0-0.dll", "libgstreamer-1.0.0.dylib");

    private static IEnumerable<string> AppCandidates() => Candidates("libgstapp-1.0.so.0", "libgstapp-1.0-0.dll", "libgstapp-1.0.0.dylib");

    private static IEnumerable<string> GlibCandidates() => Candidates("libglib-2.0.so.0", "libglib-2.0-0.dll", "libglib-2.0.0.dylib");

    private static IEnumerable<string> Candidates(string linux, string windows, string mac)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var dir in WindowsBinDirs())
                yield return Path.Combine(dir, windows);
            yield return windows;
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var dir in MacLibDirs())
                yield return Path.Combine(dir, mac);
            // the framework's single umbrella binary exports everything
            yield return "/Library/Frameworks/GStreamer.framework/GStreamer";
            yield return mac;
        }
        else
        {
            yield return linux;
            foreach (var dir in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib/arm-linux-gnueabihf", "/usr/lib64", "/usr/lib", "/usr/local/lib" })
                yield return Path.Combine(dir, linux);
        }
    }

    private static List<string> WindowsBinDirs()
    {
        var dirs = new List<string>();
        foreach (var env in new[] { "GSTREAMER_1_0_ROOT_MSVC_X86_64", "GSTREAMER_1_0_ROOT_X86_64", "GSTREAMER_1_0_ROOT_MINGW_X86_64", "GSTREAMER_ROOT" })
        {
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } root)
                dirs.Add(Path.Combine(root, "bin"));
        }

        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\" })
        {
            dirs.Add(Path.Combine(pf, "gstreamer", "1.0", "msvc_x86_64", "bin"));
            dirs.Add(Path.Combine(pf, "gstreamer", "1.0", "mingw_x86_64", "bin"));
            dirs.Add(Path.Combine(pf, "gstreamer", "1.0", "x86_64", "bin"));
        }

        // the copy the WinForms Mission Planner downloads into its data folder
        try
        {
            dirs.Add(Path.Combine(MissionPlanner.Utilities.Settings.GetDataDirectory(), "gstreamer", "1.0", "x86_64", "bin"));
        }
        catch (Exception)
        {
            // no data folder yet
        }

        var existing = dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // plugins load their own dependencies from bin/: make sure the loader finds them
        if (existing.Count > 0)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Contains(existing[0], StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", existing[0] + ";" + path);
        }

        return existing;
    }

    private static IEnumerable<string> MacLibDirs() => new[]
    {
        "/Library/Frameworks/GStreamer.framework/Versions/1.0/lib",
        "/opt/homebrew/lib",
        "/usr/local/lib",
        "/opt/local/lib",
    };
}
