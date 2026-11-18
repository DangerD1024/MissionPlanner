using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using log4net;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Core.Video;

public enum VideoStreamState
{
    /// <summary>Not running.</summary>
    Stopped,

    /// <summary>Pipeline starting, no frame yet.</summary>
    Connecting,

    /// <summary>Running, but no frame for a while (nothing sent yet, or a short stall).</summary>
    Waiting,

    /// <summary>Frames are arriving.</summary>
    Playing,

    /// <summary>The stream failed or stalled; the pipeline is rebuilt after a short pause.</summary>
    Reconnecting,

    /// <summary>Cannot run (GStreamer missing, pipeline invalid); stopped until started again.</summary>
    Failed,
}

/// <summary>A running video source as the UI sees it.</summary>
public interface IVideoStream : IDisposable
{
    VideoStreamState State { get; }

    /// <summary>Why the stream is reconnecting or failed; null otherwise.</summary>
    string? Error { get; }

    /// <summary>The pipeline last passed to <see cref="Start"/>.</summary>
    string? Pipeline { get; }

    VideoFrameBuffer Frames { get; }

    /// <summary>Raised on the decoder thread.</summary>
    event EventHandler? StateChanged;

    /// <summary>Raised on the decoder thread after each frame lands in <see cref="Frames"/>.</summary>
    event EventHandler? FrameArrived;

    /// <summary>Starts (or restarts with) a GStreamer pipeline ending in <c>appsink name=outsink</c>.</summary>
    void Start(string pipeline);

    void Stop();
}

/// <summary>
/// Runs a GStreamer pipeline on its own background thread and keeps the newest BGRA frame from
/// its <c>appsink name=outsink</c>. Rebuilds the pipeline when it errors, ends or stalls, with a
/// growing pause (1 s to 5 s). Pipelines that cannot work (syntax, missing element, no appsink,
/// wrong pixel format) stop with <see cref="VideoStreamState.Failed"/> instead of retrying.
/// </summary>
public sealed class VideoStream : IVideoStream
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(VideoStream));
    private static readonly ConcurrentDictionary<VideoStream, byte> Running = new();

    private readonly object _lock = new();
    private Run? _run;
    private VideoStreamState _state;
    private string? _error;
    private string? _pipeline;
    private long _framesReceived;

    static VideoStream()
    {
        // tear pipelines down properly (RTSP TEARDOWN, sockets) when the process ends
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var s in Running.Keys)
                s.Stop();
        };
    }

    public VideoFrameBuffer Frames { get; } = new();

    /// <summary>After this long without a frame the state drops from Playing to Waiting.</summary>
    public TimeSpan NoFrameNotice { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>After this long without a frame (having had some) the pipeline is rebuilt.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MinRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    public VideoStreamState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    public string? Error
    {
        get
        {
            lock (_lock)
                return _error;
        }
    }

    public string? Pipeline
    {
        get
        {
            lock (_lock)
                return _pipeline;
        }
    }

    public event EventHandler? StateChanged;

    public event EventHandler? FrameArrived;

    /// <summary>True when GStreamer can be loaded; <paramref name="message"/> has its version or why not.</summary>
    public static bool IsAvailable(out string message)
    {
        if (GstNative.TryInitialize(out var error))
        {
            message = GstNative.Version ?? "GStreamer";
            return true;
        }

        message = error ?? "GStreamer is not available";
        return false;
    }

    public void Start(string pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        Stop();
        var run = new Run(pipeline);
        lock (_lock)
        {
            _run = run;
            _pipeline = pipeline;
        }

        Running[this] = 0;
        SetState(run, VideoStreamState.Connecting, null);
        run.Thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "video" };
        run.Thread.Start();
    }

    public void Stop()
    {
        Run? run;
        lock (_lock)
        {
            run = _run;
            _run = null;
        }

        Running.TryRemove(this, out _);
        if (run == null)
            return;

        run.StopRequested = true;
        run.Wake.Set();
        if (run.Thread != null && run.Thread != Thread.CurrentThread && !run.Thread.Join(TimeSpan.FromSeconds(3)))
            Log.Warn("video thread did not stop in time");

        Frames.Clear();
        lock (_lock)
        {
            _state = VideoStreamState.Stopped;
            _error = null;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();

    private void SetState(Run run, VideoStreamState state, string? error)
    {
        lock (_lock)
        {
            if (_run != run || (_state == state && _error == error))
                return;
            _state = state;
            _error = error;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private VideoStreamState StateOf(Run run)
    {
        lock (_lock)
            return _run == run ? _state : VideoStreamState.Stopped;
    }

    private void Worker(Run run)
    {
        try
        {
            if (!GstNative.TryInitialize(out var initError))
            {
                SetState(run, VideoStreamState.Failed, initError);
                return;
            }

            var delay = MinRetryDelay;
            while (!run.StopRequested)
            {
                var result = RunOnce(run, out var error, out var gotFrames);
                if (run.StopRequested)
                    break;
                if (result == Outcome.Fatal)
                {
                    Log.Warn("video pipeline failed: " + error);
                    SetState(run, VideoStreamState.Failed, error);
                    return;
                }

                if (gotFrames)
                    delay = MinRetryDelay;
                Log.Info($"video stream lost ({error}); retrying in {delay.TotalSeconds:0.#} s");
                Frames.Clear();
                SetState(run, VideoStreamState.Reconnecting, error);
                run.Wake.Wait(delay);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
                // stays Reconnecting (with the last error) until frames come or the new pipeline idles
            }
        }
        catch (Exception ex)
        {
            // a bad native library or binding: report it, never take the app down
            Log.Error("video thread", ex);
            SetState(run, VideoStreamState.Failed, ex.Message);
        }
    }

    private enum Outcome
    {
        Stopped,
        Retry,
        Fatal,
    }

    private Outcome RunOnce(Run run, out string? error, out bool gotFrames)
    {
        error = null;
        gotFrames = false;

        var pipeline = GstNative.ParseLaunch(run.PipelineText, out var parseError);
        if (parseError != IntPtr.Zero)
        {
            error = "Pipeline error: " + GstNative.TakeErrorMessage(parseError);
            if (pipeline != IntPtr.Zero)
                GstNative.ObjectUnref(pipeline);
            return Outcome.Fatal;
        }

        if (pipeline == IntPtr.Zero)
        {
            error = "The pipeline could not be created.";
            return Outcome.Fatal;
        }

        var sink = GstNative.BinGetByName(pipeline, VideoPipelineBuilder.SinkName);
        if (sink == IntPtr.Zero)
        {
            GstNative.ObjectUnref(pipeline);
            error = $"The pipeline has no appsink named {VideoPipelineBuilder.SinkName} (end it with \"{VideoPipelineBuilder.SinkTail}\").";
            return Outcome.Fatal;
        }

        // only the newest frame matters: never queue behind a slow UI
        GstNative.AppSinkSetDrop(sink, 1);
        GstNative.AppSinkSetMaxBuffers(sink, 1);

        var bus = GstNative.ElementGetBus(pipeline);
        try
        {
            if (GstNative.ElementSetState(pipeline, GstNative.StatePlaying) == GstNative.StateChangeFailure)
            {
                error = PopError(bus) ?? "The pipeline could not start.";
                return Outcome.Retry;
            }

            var clock = Stopwatch.StartNew();
            var lastFrame = TimeSpan.Zero;
            while (!run.StopRequested)
            {
                var sample = GstNative.AppSinkTryPullSample(sink, 100 * GstNative.Millisecond);
                var now = clock.Elapsed;
                if (sample != IntPtr.Zero)
                {
                    string? formatError;
                    try
                    {
                        formatError = CopySample(sample);
                    }
                    finally
                    {
                        GstNative.MiniObjectUnref(sample);
                    }

                    if (formatError != null)
                    {
                        error = formatError;
                        return Outcome.Fatal;
                    }

                    gotFrames = true;
                    lastFrame = now;
                    Interlocked.Increment(ref _framesReceived);
                    SetState(run, VideoStreamState.Playing, null);
                    FrameArrived?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    var state = StateOf(run);
                    if (gotFrames)
                    {
                        if (now - lastFrame > StallTimeout)
                        {
                            error = $"No video for {StallTimeout.TotalSeconds:0} s";
                            return Outcome.Retry;
                        }

                        if (now - lastFrame > NoFrameNotice && state == VideoStreamState.Playing)
                            SetState(run, VideoStreamState.Waiting, null);
                    }
                    else if (now > NoFrameNotice && state is VideoStreamState.Connecting or VideoStreamState.Reconnecting)
                    {
                        SetState(run, VideoStreamState.Waiting, null);
                    }
                }

                if (PopError(bus) is { } busError)
                {
                    error = busError;
                    return Outcome.Retry;
                }

                var eos = GstNative.BusTimedPopFiltered(bus, 0, GstNative.MessageEos);
                if (eos != IntPtr.Zero)
                {
                    GstNative.MiniObjectUnref(eos);
                    error = "The stream ended";
                    return Outcome.Retry;
                }
            }

            return Outcome.Stopped;
        }
        finally
        {
            GstNative.ElementSetState(pipeline, GstNative.StateNull);
            GstNative.ObjectUnref(sink);
            if (bus != IntPtr.Zero)
                GstNative.ObjectUnref(bus);
            GstNative.ObjectUnref(pipeline);
        }
    }

    /// <summary>Copies one sample into <see cref="Frames"/>; returns an error if it is not BGRA.</summary>
    private string? CopySample(IntPtr sample)
    {
        var caps = GstNative.SampleGetCaps(sample);
        var buffer = GstNative.SampleGetBuffer(sample);
        if (caps == IntPtr.Zero || buffer == IntPtr.Zero)
            return null;

        var s = GstNative.CapsGetStructure(caps, 0);
        if (s == IntPtr.Zero)
            return null;
        var format = Marshal.PtrToStringUTF8(GstNative.StructureGetString(s, "format"));
        if (format is not ("BGRA" or "BGRx"))
            return $"The appsink receives {format ?? "non-video"} frames, but BGRA is needed: end the pipeline with \"{VideoPipelineBuilder.SinkTail}\".";
        if (GstNative.StructureGetInt(s, "width", out var width) == 0 || GstNative.StructureGetInt(s, "height", out var height) == 0 ||
            width <= 0 || height <= 0)
            return null;

        if (GstNative.BufferMap(buffer, out var info, GstNative.MapRead) == 0)
            return null;
        try
        {
            var stride = width * 4;
            if ((ulong)info.Size >= (ulong)stride * (ulong)height)
                Frames.Write(info.Data, width, height, stride);
        }
        finally
        {
            GstNative.BufferUnmap(buffer, ref info);
        }

        return null;
    }

    private static string? PopError(IntPtr bus)
    {
        if (bus == IntPtr.Zero)
            return null;
        var msg = GstNative.BusTimedPopFiltered(bus, 0, GstNative.MessageError);
        if (msg == IntPtr.Zero)
            return null;
        try
        {
            GstNative.MessageParseError(msg, out var gerror, out var debug);
            var text = GstNative.TakeErrorMessage(gerror);
            var detail = Marshal.PtrToStringUTF8(debug);
            GstNative.GFree(debug);
            Log.Info($"video: {text} ({detail})");
            // the summary alone ("Could not get/set settings from/on resource") does not say why
            if (detail?.Contains("already in use", StringComparison.OrdinalIgnoreCase) == true)
                return "the port is already in use by another program";
            return text;
        }
        finally
        {
            GstNative.MiniObjectUnref(msg);
        }
    }

    /// <summary>One Start: its thread, its stop flag and the event that cuts retry pauses short.</summary>
    private sealed class Run(string pipelineText)
    {
        public string PipelineText { get; } = pipelineText;

        public volatile bool StopRequested;

        public ManualResetEventSlim Wake { get; } = new();

        public Thread? Thread { get; set; }
    }
}
