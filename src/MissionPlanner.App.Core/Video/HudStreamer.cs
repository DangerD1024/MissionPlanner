using System.Runtime.InteropServices;
using log4net;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Core.Video;

/// <summary>Sends HUD frames out as H.264 (RTSP server and/or RTP over UDP); the app uses <see cref="HudStreamer"/>.</summary>
public interface IHudStreamer : IDisposable
{
    bool IsRunning { get; }

    /// <summary>The last error from a running pipeline; null while all is well.</summary>
    string? Error { get; }

    /// <summary>The encoder element in use ("x264enc").</summary>
    string? EncoderName { get; }

    /// <summary>The port the RTSP server listens on; 0 when not serving RTSP.</summary>
    int RtspPort { get; }

    /// <summary>Raised (on a worker thread) when <see cref="Error"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Starts; throws <see cref="InvalidOperationException"/> with a message for the user.</summary>
    void Start(StreamSettings settings);

    /// <summary>Queues one BGRA frame of the stream size (width × height × 4 bytes); dropped if not running.</summary>
    void Push(byte[] bgra);

    void Stop();

    /// <summary>Recording the stream to a file (needs <see cref="Start"/> first).</summary>
    bool IsRecording { get; }

    /// <summary>Starts writing the HUD to <paramref name="path"/>; throws with a message for the user.</summary>
    void StartRecording(string path);

    /// <summary>Ends the file properly (end of stream) and stops recording.</summary>
    void StopRecording();
}

/// <summary>
/// The GStreamer side of streaming: a producer pipeline fed through appsrc, a UDP sender and/or an
/// RTSP server reading it through an inter-pipeline channel (<see cref="StreamPipelines"/>).
/// </summary>
public sealed class HudStreamer : IHudStreamer
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(HudStreamer));

    private readonly object _gate = new();
    private IntPtr _producer;
    private IntPtr _appsrc;
    private IntPtr _udp;
    private IntPtr _recorder;
    private StreamSettings? _settings;
    private RtspServerNative? _rtsp;
    private ShmFrameWriter? _shm;
    private int _shmWidth;
    private int _shmHeight;
    private int _frameBytes;
    private Thread? _monitor;
    private volatile bool _stopping;

    public bool IsRunning { get; private set; }

    public string? Error { get; private set; }

    public string? EncoderName { get; private set; }

    public int RtspPort { get; private set; }

    public event EventHandler? StateChanged;

    /// <summary>Whether streaming can work here: GStreamer, the inter elements and an H.264 encoder.</summary>
    public static bool IsAvailable(out string message)
    {
        if (!GstNative.TryInitialize(out var error))
        {
            message = error ?? "GStreamer is not available.";
            return false;
        }

        if (!GstNative.HasElement("intervideosink") || !GstNative.HasElement("intervideosrc"))
        {
            message = "GStreamer's inter elements are missing (gst-plugins-bad).";
            return false;
        }

        if (StreamPipelines.EncoderOrder.All(e => !GstNative.HasElement(e)))
        {
            message = "No H.264 encoder is installed (x264enc from gst-plugins-ugly, or openh264enc).";
            return false;
        }

        message = GstNative.Version ?? "GStreamer";
        return true;
    }

    /// <summary>The encoders installed here, in <see cref="StreamPipelines.EncoderOrder"/>.</summary>
    public static IReadOnlyList<string> InstalledEncoders() =>
        GstNative.TryInitialize(out _) ? StreamPipelines.EncoderOrder.Where(GstNative.HasElement).ToList() : [];

    public void Start(StreamSettings settings)
    {
        lock (_gate)
        {
            StopLocked();
            if (settings.Validate() is { } invalid)
                throw new InvalidOperationException(invalid);
            if (settings.UsesShm)
            {
                // Same-machine raw frames: no GStreamer, no encoder, no network.
                _shm = new ShmFrameWriter();
                _shmWidth = settings.Width;
                _shmHeight = settings.Height;
                _frameBytes = settings.Width * settings.Height * 4;
                EncoderName = "shm raw BGRA";
                Error = null;
                IsRunning = true;
                _stopping = false;
                Log.Info($"streaming {settings.Width}x{settings.Height}@{settings.Fps} raw BGRA via {StreamSettings.ShmPath}");
                return;
            }
            if (!IsAvailable(out var message))
                throw new InvalidOperationException(message);
            var encoder = StreamPipelines.PickEncoder(settings, GstNative.HasElement)
                          ?? throw new InvalidOperationException($"The encoder {settings.Encoder} is not installed.");

            try
            {
                _producer = Launch(StreamPipelines.Producer(settings));
                _appsrc = GstNative.BinGetByName(_producer, StreamPipelines.AppSrcName);
                if (_appsrc == IntPtr.Zero)
                    throw new InvalidOperationException("The stream's appsrc was not created.");
                Play(_producer);

                if (settings.UsesUdp)
                {
                    _udp = Launch(StreamPipelines.UdpSender(settings, encoder));
                    Play(_udp);
                }

                if (settings.UsesRtsp)
                {
                    _rtsp = RtspServerNative.Start(settings.RtspPort, settings.RtspPath, StreamPipelines.RtspLaunch(settings, encoder));
                    RtspPort = _rtsp.Port;
                }
            }
            catch
            {
                StopLocked();
                throw;
            }

            _frameBytes = settings.Width * settings.Height * 4;
            _settings = settings;
            EncoderName = encoder;
            Error = null;
            IsRunning = true;
            _stopping = false;
            _monitor = new Thread(Monitor) { IsBackground = true, Name = "stream-monitor" };
            _monitor.Start();
            Log.Info($"streaming {settings.Width}x{settings.Height}@{settings.Fps} {encoder} ({settings.Mode})");
        }
    }

    public void Push(byte[] bgra)
    {
        lock (_gate)
        {
            if (!IsRunning)
                return;
            if (_shm != null)
            {
                _shm.Write(bgra, _shmWidth, _shmHeight);
                return;
            }
            if (_appsrc == IntPtr.Zero || bgra.Length < _frameBytes)
                return;
            var buffer = GstNative.BufferNewAllocate(IntPtr.Zero, (UIntPtr)_frameBytes, IntPtr.Zero);
            if (buffer == IntPtr.Zero)
                return;
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                GstNative.BufferFill(buffer, UIntPtr.Zero, pin.AddrOfPinnedObject(), (UIntPtr)_frameBytes);
            }
            finally
            {
                pin.Free();
            }

            // the appsrc takes the buffer; with block=false and a leaky queue it never waits
            GstNative.AppSrcPushBuffer(_appsrc, buffer);
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopLocked();
    }

    public bool IsRecording => _recorder != IntPtr.Zero;

    public void StartRecording(string path)
    {
        lock (_gate)
        {
            if (!IsRunning || _settings == null || EncoderName == null)
                throw new InvalidOperationException("The HUD frame source is not running.");
            if (!GstNative.HasElement("matroskamux"))
                throw new InvalidOperationException("GStreamer's matroskamux is missing (gst-plugins-good).");
            StopRecordingLocked();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _recorder = Launch(StreamPipelines.FileRecorder(_settings, EncoderName, path));
            try
            {
                Play(_recorder);
            }
            catch
            {
                Shut(ref _recorder);
                throw;
            }

            Log.Info("recording the HUD to " + path);
        }
    }

    public void StopRecording()
    {
        lock (_gate)
            StopRecordingLocked();
    }

    /// <summary>End of stream first, so the muxer writes its index; then the pipeline is shut.</summary>
    private void StopRecordingLocked()
    {
        if (_recorder == IntPtr.Zero)
            return;
        try
        {
            GstNative.ElementSendEvent(_recorder, GstNative.EventNewEos());
            var bus = GstNative.ElementGetBus(_recorder);
            if (bus != IntPtr.Zero)
            {
                var msg = GstNative.BusTimedPopFiltered(bus, 3_000_000_000UL, GstNative.MessageEos | GstNative.MessageError);
                if (msg != IntPtr.Zero)
                    GstNative.MiniObjectUnref(msg);
                GstNative.ObjectUnref(bus);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("end of recording", ex);
        }

        Shut(ref _recorder);
        Log.Info("recording stopped");
    }

    public void Dispose() => Stop();

    private void StopLocked()
    {
        _stopping = true;
        var wasRunning = IsRunning;
        IsRunning = false;
        _shm?.Dispose();
        _shm = null;
        StopRecordingLocked();
        _rtsp?.Dispose();
        _rtsp = null;
        RtspPort = 0;
        Shut(ref _udp);
        if (_appsrc != IntPtr.Zero)
        {
            GstNative.ObjectUnref(_appsrc);
            _appsrc = IntPtr.Zero;
        }

        Shut(ref _producer);
        if (wasRunning)
            Log.Info("streaming stopped");
    }

    private static IntPtr Launch(string pipeline)
    {
        var p = GstNative.ParseLaunch(pipeline, out var error);
        if (error != IntPtr.Zero)
        {
            var text = GstNative.TakeErrorMessage(error);
            if (p != IntPtr.Zero)
                GstNative.ObjectUnref(p);
            throw new InvalidOperationException("Stream pipeline: " + text);
        }

        return p != IntPtr.Zero ? p : throw new InvalidOperationException("Stream pipeline could not be created.");
    }

    private static void Play(IntPtr pipeline)
    {
        if (GstNative.ElementSetState(pipeline, GstNative.StatePlaying) == GstNative.StateChangeFailure)
            throw new InvalidOperationException("The stream pipeline did not start: " + (PopError(pipeline) ?? "unknown error"));
    }

    private static void Shut(ref IntPtr pipeline)
    {
        if (pipeline == IntPtr.Zero)
            return;
        GstNative.ElementSetState(pipeline, GstNative.StateNull);
        GstNative.ObjectUnref(pipeline);
        pipeline = IntPtr.Zero;
    }

    /// <summary>Reports errors from the producer and the UDP sender while streaming.</summary>
    private void Monitor()
    {
        while (!_stopping)
        {
            Thread.Sleep(250);
            string? error;
            lock (_gate)
            {
                if (_stopping)
                    return;
                error = PopError(_producer) ?? PopError(_udp);
            }

            if (error != null && error != Error)
            {
                Error = error;
                Log.Warn("stream: " + error);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static string? PopError(IntPtr pipeline)
    {
        if (pipeline == IntPtr.Zero)
            return null;
        var bus = GstNative.ElementGetBus(pipeline);
        if (bus == IntPtr.Zero)
            return null;
        try
        {
            var msg = GstNative.BusTimedPopFiltered(bus, 0, GstNative.MessageError);
            if (msg == IntPtr.Zero)
                return null;
            try
            {
                GstNative.MessageParseError(msg, out var gerror, out var debug);
                var text = GstNative.TakeErrorMessage(gerror);
                Log.Info("stream detail: " + Marshal.PtrToStringUTF8(debug));
                GstNative.GFree(debug);
                return text;
            }
            finally
            {
                GstNative.MiniObjectUnref(msg);
            }
        }
        finally
        {
            GstNative.ObjectUnref(bus);
        }
    }
}
