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

    // send side (streaming the HUD)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr BufferNewAllocateFn(IntPtr allocator, UIntPtr size, IntPtr allocParams);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate UIntPtr BufferFillFn(IntPtr buffer, UIntPtr offset, IntPtr src, UIntPtr size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int PushBufferFn(IntPtr appsrc, IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr FactoryFindFn([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    // device discovery (capture devices: EasyCap, UVC cameras)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr NewFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint AddFilterFn(IntPtr monitor, [MarshalAs(UnmanagedType.LPUTF8Str)] string classes, IntPtr caps);

    // ---- bound functions (valid after a successful TryInitialize) ------------------------------

    public static ParseLaunchFn ParseLaunch = null!;
    public static BinGetByNameFn BinGetByName = null!;
    public static SetStateFn ElementSetState = null!;
    public static PtrFn ElementGetBus = null!;
    public static VoidFn ObjectUnref = null!;
    public static VoidFn MiniObjectUnref = null!;
    public static BusPopFn BusTimedPopFiltered = null!;

    /// <summary>gst_element_send_event (element, event): true when handled.</summary>
    public static PushBufferFn ElementSendEvent = null!;

    /// <summary>gst_event_new_eos: ends a stream so muxers finish their files.</summary>
    public static NewFn EventNewEos = null!;
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
    public static BufferNewAllocateFn BufferNewAllocate = null!;
    public static BufferFillFn BufferFill = null!;
    public static PushBufferFn AppSrcPushBuffer = null!;
    public static PtrFn AppSrcEndOfStream = null!;
    public static FactoryFindFn ElementFactoryFind = null!;
    public static NewFn DeviceMonitorNew = null!;
    public static AddFilterFn DeviceMonitorAddFilter = null!;
    public static PtrFn DeviceMonitorGetDevices = null!;
    public static PtrFn DeviceGetDisplayName = null!;
    public static PtrFn DeviceGetProperties = null!;
    public static VoidFn StructureFree = null!;
    public static VoidFn GListFree = null!;

    /// <summary>glib, for the RTSP server's main loop (zero if it could not be loaded separately).</summary>
    public static IntPtr GlibHandle { get; private set; }

    /// <summary>GStreamer core, where glib symbols can also be found on macOS.</summary>
    public static IntPtr CoreHandle { get; private set; }

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
        ElementSendEvent = Bind<PushBufferFn>(core, "gst_element_send_event");
        EventNewEos = Bind<NewFn>(core, "gst_event_new_eos");
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
        BufferNewAllocate = Bind<BufferNewAllocateFn>(core, "gst_buffer_new_allocate");
        BufferFill = Bind<BufferFillFn>(core, "gst_buffer_fill");
        ElementFactoryFind = Bind<FactoryFindFn>(core, "gst_element_factory_find");
        AppSrcPushBuffer = Bind<PushBufferFn>(app, "gst_app_src_push_buffer");
        AppSrcEndOfStream = Bind<PtrFn>(app, "gst_app_src_end_of_stream");
        DeviceMonitorNew = Bind<NewFn>(core, "gst_device_monitor_new");
        DeviceMonitorAddFilter = Bind<AddFilterFn>(core, "gst_device_monitor_add_filter");
        DeviceMonitorGetDevices = Bind<PtrFn>(core, "gst_device_monitor_get_devices");
        DeviceGetDisplayName = Bind<PtrFn>(core, "gst_device_get_display_name");
        DeviceGetProperties = Bind<PtrFn>(core, "gst_device_get_properties");
        StructureFree = Bind<VoidFn>(core, "gst_structure_free");
        CoreHandle = core;
        GlibHandle = glib;
        var gl2 = glib != IntPtr.Zero ? glib : core;
        GListFree = NativeLibrary.TryGetExport(gl2, "g_list_free", out var listFree)
            ? Marshal.GetDelegateForFunctionPointer<VoidFn>(listFree)
            : _ => { };

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

    /// <summary>Whether an element (an encoder, a plugin's element) is installed.</summary>
    public static bool HasElement(string name)
    {
        var factory = ElementFactoryFind(name);
        if (factory == IntPtr.Zero)
            return false;
        ObjectUnref(factory);
        return true;
    }

    public static void GFree(IntPtr p)
    {
        if (p != IntPtr.Zero)
            _gFree?.Invoke(p);
    }

    internal static T Bind<T>(IntPtr lib, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    internal static IntPtr LoadFirst(IEnumerable<string> candidates)
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

    internal static IEnumerable<string> Candidates(string linux, string windows, string mac)
    {
        if (OperatingSystem.IsWindows())
        {
            // the official (MSVC) runtime names its DLLs without the "lib" prefix the MinGW build uses
            var msvc = windows.StartsWith("lib", StringComparison.Ordinal) ? windows[3..] : windows;
            foreach (var dir in WindowsBinDirs())
            {
                yield return Path.Combine(dir, msvc);
                yield return Path.Combine(dir, windows);
            }

            yield return msvc;
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

    /// <summary>The GStreamer the Windows installer ships next to the app (<c>gstreamerin</c>, <c>lib\gstreamer-1.0</c>).</summary>
    public static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "gstreamer");

    private static List<string> WindowsBinDirs()
    {
        var dirs = new List<string>();
        var bundledBin = Path.Combine(BundledRoot, "bin");
        if (Directory.Exists(bundledBin))
        {
            dirs.Add(bundledBin);
            UseBundledPlugins();
        }

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

    /// <summary>
    /// Points GStreamer at the bundled plugins (and its plugin scanner and a registry in the data
    /// folder) before gst_init, so a separately installed GStreamer is not mixed in.
    /// </summary>
    private static void UseBundledPlugins()
    {
        var plugins = Path.Combine(BundledRoot, "lib", "gstreamer-1.0");
        if (!Directory.Exists(plugins) || Environment.GetEnvironmentVariable("GST_PLUGIN_SYSTEM_PATH_1_0") != null)
            return;
        Environment.SetEnvironmentVariable("GST_PLUGIN_SYSTEM_PATH_1_0", plugins);
        Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", "");
        var scanner = Path.Combine(BundledRoot, "libexec", "gstreamer-1.0", "gst-plugin-scanner.exe");
        if (File.Exists(scanner))
            Environment.SetEnvironmentVariable("GST_PLUGIN_SCANNER_1_0", scanner);
        try
        {
            // the install folder is read-only; the registry cache goes with the app's data
            var registry = Path.Combine(MissionPlanner.Utilities.Settings.GetDataDirectory(), "gstreamer-registry.bin");
            Environment.SetEnvironmentVariable("GST_REGISTRY_1_0", registry);
        }
        catch (Exception)
        {
            // no data folder: GStreamer uses its default location
        }
    }

    private static IEnumerable<string> MacLibDirs() => new[]
    {
        "/Library/Frameworks/GStreamer.framework/Versions/1.0/lib",
        "/opt/homebrew/lib",
        "/usr/local/lib",
        "/opt/local/lib",
    };
}
