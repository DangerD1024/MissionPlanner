using System.Runtime.InteropServices;
using log4net;

namespace MissionPlanner.App.Core.Video;

/// <summary>
/// gst-rtsp-server, bound at run time like <see cref="GstNative"/>: one server, one mount whose media
/// is a shared launch line, served from a glib main loop on its own thread. Optional: without the
/// library the stream can still go out over UDP.
/// </summary>
internal sealed class RtspServerNative : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(RtspServerNative));
    private static readonly object LoadLock = new();
    private static bool _tried;
    private static string? _loadError;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NewFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetStringFn(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetBoolFn(IntPtr obj, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr PtrFn(IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidFn(IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AddFactoryFn(IntPtr mounts, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AttachFn(IntPtr server, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetIntFn(IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LoopNewFn(IntPtr context, int running);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FindSourceFn(IntPtr context, uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClientFilterFunc(IntPtr server, IntPtr client, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ClientFilterFn(IntPtr server, ClientFilterFunc? func, IntPtr userData);

    private static NewFn _serverNew = null!;
    private static SetStringFn _setAddress = null!;
    private static SetStringFn _setService = null!;
    private static PtrFn _getMountPoints = null!;
    private static NewFn _factoryNew = null!;
    private static SetStringFn _factorySetLaunch = null!;
    private static SetBoolFn _factorySetShared = null!;
    private static AddFactoryFn _addFactory = null!;
    private static AttachFn _attach = null!;
    private static GetIntFn _getBoundPort = null!;
    private static ClientFilterFn _clientFilter = null!;
    private static NewFn _contextNew = null!;
    private static LoopNewFn _loopNew = null!;
    private static VoidFn _loopRun = null!;
    private static VoidFn _loopQuit = null!;
    private static VoidFn _loopUnref = null!;
    private static VoidFn _contextUnref = null!;
    private static FindSourceFn _findSource = null!;
    private static VoidFn _sourceDestroy = null!;

    // removes every client (GST_RTSP_FILTER_REMOVE = 0); kept alive for the process
    private static readonly ClientFilterFunc RemoveAll = (_, _, _) => 0;

    private IntPtr _server;
    private IntPtr _context;
    private IntPtr _loop;
    private uint _sourceId;
    private Thread? _thread;

    private RtspServerNative()
    {
    }

    public int Port { get; private set; }

    /// <summary>Loads gst-rtsp-server once (after <see cref="GstNative.TryInitialize"/>).</summary>
    public static bool IsAvailable(out string? error)
    {
        lock (LoadLock)
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    _loadError = Load();
                }
                catch (Exception ex)
                {
                    _loadError = "The RTSP server library could not be loaded: " + ex.Message;
                }

                if (_loadError != null)
                    Log.Warn(_loadError);
            }

            error = _loadError;
            return _loadError == null;
        }
    }

    private static string? Load()
    {
        if (!GstNative.TryInitialize(out var gstError))
            return gstError;
        var lib = GstNative.LoadFirst(GstNative.Candidates("libgstrtspserver-1.0.so.0", "libgstrtspserver-1.0-0.dll", "libgstrtspserver-1.0.0.dylib"));
        if (lib == IntPtr.Zero)
        {
            return OperatingSystem.IsLinux()
                ? "The GStreamer RTSP server is not installed (package gstreamer1.0-rtsp / libgstrtspserver)."
                : "The GStreamer RTSP server library (gst-rtsp-server) was not found.";
        }

        _serverNew = GstNative.Bind<NewFn>(lib, "gst_rtsp_server_new");
        _setAddress = GstNative.Bind<SetStringFn>(lib, "gst_rtsp_server_set_address");
        _setService = GstNative.Bind<SetStringFn>(lib, "gst_rtsp_server_set_service");
        _getMountPoints = GstNative.Bind<PtrFn>(lib, "gst_rtsp_server_get_mount_points");
        _factoryNew = GstNative.Bind<NewFn>(lib, "gst_rtsp_media_factory_new");
        _factorySetLaunch = GstNative.Bind<SetStringFn>(lib, "gst_rtsp_media_factory_set_launch");
        _factorySetShared = GstNative.Bind<SetBoolFn>(lib, "gst_rtsp_media_factory_set_shared");
        _addFactory = GstNative.Bind<AddFactoryFn>(lib, "gst_rtsp_mount_points_add_factory");
        _attach = GstNative.Bind<AttachFn>(lib, "gst_rtsp_server_attach");
        _getBoundPort = GstNative.Bind<GetIntFn>(lib, "gst_rtsp_server_get_bound_port");
        _clientFilter = GstNative.Bind<ClientFilterFn>(lib, "gst_rtsp_server_client_filter");

        var glib = GstNative.GlibHandle != IntPtr.Zero ? GstNative.GlibHandle : GstNative.CoreHandle;
        _contextNew = GstNative.Bind<NewFn>(glib, "g_main_context_new");
        _loopNew = GstNative.Bind<LoopNewFn>(glib, "g_main_loop_new");
        _loopRun = GstNative.Bind<VoidFn>(glib, "g_main_loop_run");
        _loopQuit = GstNative.Bind<VoidFn>(glib, "g_main_loop_quit");
        _loopUnref = GstNative.Bind<VoidFn>(glib, "g_main_loop_unref");
        _contextUnref = GstNative.Bind<VoidFn>(glib, "g_main_context_unref");
        _findSource = GstNative.Bind<FindSourceFn>(glib, "g_main_context_find_source_by_id");
        _sourceDestroy = GstNative.Bind<VoidFn>(glib, "g_source_destroy");
        return null;
    }

    /// <summary>
    /// Serves <paramref name="launch"/> (a bin "( ... ! rtph264pay name=pay0 )") at
    /// rtsp://host:<paramref name="port"/>/<paramref name="path"/>, shared by all clients.
    /// Throws if the library is missing or the port cannot be bound.
    /// </summary>
    public static RtspServerNative Start(int port, string path, string launch)
    {
        if (!IsAvailable(out var error))
            throw new InvalidOperationException(error);

        var s = new RtspServerNative();
        try
        {
            s._server = _serverNew();
            _setAddress(s._server, "0.0.0.0");
            _setService(s._server, port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var mounts = _getMountPoints(s._server);
            var factory = _factoryNew();
            _factorySetLaunch(factory, launch);
            _factorySetShared(factory, 1);
            _addFactory(mounts, "/" + path.Trim('/'), factory); // takes the factory
            GstNative.ObjectUnref(mounts);

            s._context = _contextNew();
            s._loop = _loopNew(s._context, 0);
            s._sourceId = _attach(s._server, s._context);
            if (s._sourceId == 0)
                throw new InvalidOperationException($"RTSP port {port} could not be opened (another program may be using it).");
            s.Port = _getBoundPort(s._server);

            var loop = s._loop;
            s._thread = new Thread(() => _loopRun(loop)) { IsBackground = true, Name = "rtsp-server" };
            s._thread.Start();
            Log.Info($"RTSP server on port {s.Port}, /{path.Trim('/')}");
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_loop != IntPtr.Zero)
        {
            _loopQuit(_loop);
            _thread?.Join(TimeSpan.FromSeconds(3));
        }

        if (_server != IntPtr.Zero)
        {
            // disconnect the clients, stop listening, free the port
            _clientFilter(_server, RemoveAll, IntPtr.Zero);
            if (_sourceId != 0 && _context != IntPtr.Zero && _findSource(_context, _sourceId) is var src && src != IntPtr.Zero)
                _sourceDestroy(src);
            GstNative.ObjectUnref(_server);
            _server = IntPtr.Zero;
        }

        if (_loop != IntPtr.Zero)
        {
            _loopUnref(_loop);
            _loop = IntPtr.Zero;
        }

        if (_context != IntPtr.Zero)
        {
            _contextUnref(_context);
            _context = IntPtr.Zero;
        }
    }
}
