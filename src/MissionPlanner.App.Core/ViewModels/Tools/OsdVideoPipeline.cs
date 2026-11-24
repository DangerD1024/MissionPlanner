using System.Globalization;
using System.Runtime.InteropServices;
using log4net;
using MissionPlanner.App.Core.Video;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.App.Core.ViewModels.Video;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>What a video file holds, from its first frame.</summary>
/// <remarks><see cref="StartSeconds"/>: timestamp of the first frame; video times count from it.</remarks>
public sealed record OsdVideoInfo(int Width, int Height, int FpsNumerator, int FpsDenominator, double DurationSeconds, double StartSeconds = 0)
{
    public double Fps => FpsNumerator > 0 && FpsDenominator > 0 ? (double)FpsNumerator / FpsDenominator : 0;

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0}×{1}, {2}, {3}", Width, Height,
            Fps > 0 ? Fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps" : "variable frame rate",
            DurationSeconds > 0 ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : "length unknown");
}

public sealed record OsdRenderOptions(int Width, int Height, string Encoder, int BitrateKbps);

public readonly record struct OsdRenderProgress(long Frames, double VideoSeconds, double DurationSeconds)
{
    public double Fraction => DurationSeconds > 0 ? Math.Clamp(VideoSeconds / DurationSeconds, 0, 1) : -1;
}

/// <summary>
/// The GStreamer side of the OSD video: the recording decoded to BGRA frames (uridecodebin →
/// appsink), the HUD drawn over each one for its own time, and the result encoded to H.264 in an
/// MP4 (appsrc → encoder → mp4mux → filesink) with the source's timestamps.
/// </summary>
public static class OsdVideoPipeline
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(OsdVideoPipeline));

    public const string SinkName = "outsink";
    public const string SrcName = "src";

    /// <summary>Encoders tried for "auto", as the HUD stream.</summary>
    public static IReadOnlyList<string> EncoderOrder => StreamPipelines.EncoderOrder;

    public static bool IsAvailable(out string message)
    {
        if (!GstNative.TryInitialize(out var error))
        {
            message = error ?? "GStreamer is not available.";
            return false;
        }

        foreach (var (element, plugin) in new[]
                 {
                     ("uridecodebin", "gst-plugins-base"), ("videoconvert", "gst-plugins-base"), ("videoscale", "gst-plugins-base"),
                     ("appsrc", "gst-plugins-base"), ("mp4mux", "gst-plugins-good"), ("h264parse", "gst-plugins-bad"),
                 })
        {
            if (!GstNative.HasElement(element))
            {
                message = $"GStreamer's {element} is missing ({plugin}).";
                return false;
            }
        }

        if (InstalledEncoders().Count == 0)
        {
            message = "No H.264 encoder is installed (x264enc from gst-plugins-ugly, or openh264enc).";
            return false;
        }

        message = GstNative.Version ?? "GStreamer";
        return true;
    }

    public static IReadOnlyList<string> InstalledEncoders() =>
        GstNative.TryInitialize(out _) ? EncoderOrder.Where(GstNative.HasElement).ToList() : [];

    /// <summary>The URI of a local file for uridecodebin.</summary>
    public static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>The recording as BGRA frames; scaled to <paramref name="width"/> × <paramref name="height"/> when given.</summary>
    public static string DecodePipeline(string path, int width = 0, int height = 0)
    {
        var size = width > 0 && height > 0 ? $",width={width},height={height},pixel-aspect-ratio=1/1" : "";
        return $"uridecodebin uri=\"{FileUri(path)}\" ! queue ! videoconvert ! videoscale ! video/x-raw,format=BGRA{size} " +
               $"! appsink name={SinkName} sync=false max-buffers=4 drop=false";
    }

    /// <summary>A file encoder: quality before latency (the HUD stream's settings are for live video).</summary>
    public static string FileEncoder(string name, int kbps, int keyframeInterval) => name switch
    {
        "x264enc" => $"x264enc speed-preset=veryfast bitrate={kbps} key-int-max={keyframeInterval}",
        "openh264enc" => $"openh264enc bitrate={kbps * 1000} rate-control=bitrate gop-size={keyframeInterval}",
        "mfh264enc" => $"mfh264enc bitrate={kbps} gop-size={keyframeInterval}",
        "vtenc_h264" => $"vtenc_h264 bitrate={kbps} max-keyframe-interval={keyframeInterval}",
        _ => name,
    };

    /// <summary>BGRA frames with timestamps in, an MP4 out. The path uses forward slashes: gst-launch treats a backslash as an escape.</summary>
    public static string EncodePipeline(OsdRenderOptions o, int fpsNum, int fpsDen, string output)
    {
        if (fpsNum <= 0 || fpsDen <= 0)
        {
            fpsNum = 30;
            fpsDen = 1;
        }

        var keyframes = Math.Max(1, (int)Math.Round(2.0 * fpsNum / fpsDen));
        var frameBytes = (long)o.Width * o.Height * 4;
        return $"appsrc name={SrcName} format=time is-live=false block=true max-bytes={frameBytes * 4} " +
               $"caps=\"video/x-raw,format=BGRA,width={o.Width},height={o.Height},framerate={fpsNum}/{fpsDen},pixel-aspect-ratio=1/1\" " +
               $"! videoconvert ! {FileEncoder(o.Encoder, o.BitrateKbps, keyframes)} ! h264parse ! mp4mux " +
               $"! filesink location=\"{output.Replace('\\', '/').Replace("\"", "")}\"";
    }

    /// <summary>The output size: the source's, or scaled down to at most 1280 wide; always even (H.264 needs it).</summary>
    public static (int Width, int Height) OutputSize(int width, int height, bool fullResolution)
    {
        if (!fullResolution && width > 1280)
        {
            height = (int)Math.Round(height * 1280.0 / width);
            width = 1280;
        }

        return (Math.Max(2, width & ~1), Math.Max(2, height & ~1));
    }

    // ---- frames out of the decoder -------------------------------------------------------------

    private sealed class Decoded(byte[] pixels, int width, int height, ulong pts, ulong duration)
    {
        public byte[] Pixels { get; } = pixels;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public ulong Pts { get; } = pts;
        public ulong Duration { get; } = duration;
    }

    private static Decoded? Copy(IntPtr sample, out int fpsNum, out int fpsDen)
    {
        fpsNum = fpsDen = 0;
        var caps = GstNative.SampleGetCaps(sample);
        var buffer = GstNative.SampleGetBuffer(sample);
        if (caps == IntPtr.Zero || buffer == IntPtr.Zero)
            return null;
        var s = GstNative.CapsGetStructure(caps, 0);
        if (s == IntPtr.Zero || GstNative.StructureGetInt(s, "width", out var w) == 0 || GstNative.StructureGetInt(s, "height", out var h) == 0)
            return null;
        GstExtra.Fraction(s, "framerate", out fpsNum, out fpsDen);
        if (GstNative.BufferMap(buffer, out var info, GstNative.MapRead) == 0)
            return null;
        try
        {
            var bytes = w * h * 4;
            if ((ulong)info.Size < (ulong)bytes)
                return null;
            var pixels = new byte[bytes];
            Marshal.Copy(info.Data, pixels, 0, bytes);
            return new Decoded(pixels, w, h, GstExtra.Pts(buffer), GstExtra.Duration(buffer));
        }
        finally
        {
            GstNative.BufferUnmap(buffer, ref info);
        }
    }

    private static IntPtr Launch(string text)
    {
        var p = GstNative.ParseLaunch(text, out var error);
        if (error != IntPtr.Zero)
        {
            var message = GstNative.TakeErrorMessage(error);
            if (p != IntPtr.Zero)
                GstNative.ObjectUnref(p);
            throw new InvalidOperationException("Pipeline: " + message);
        }

        return p != IntPtr.Zero ? p : throw new InvalidOperationException("The pipeline could not be created.");
    }

    private static void Shut(ref IntPtr pipeline)
    {
        if (pipeline == IntPtr.Zero)
            return;
        GstNative.ElementSetState(pipeline, GstNative.StateNull);
        GstNative.ObjectUnref(pipeline);
        pipeline = IntPtr.Zero;
    }

    /// <summary>
    /// The next end-of-stream or error on a pipeline's bus. Both are popped together: filtering the
    /// bus for one kind throws the other away, and a lost end of stream would never be seen.
    /// </summary>
    private static string? PopEnd(IntPtr pipeline, ref bool eos, ulong timeoutNs = 0)
    {
        if (pipeline == IntPtr.Zero)
            return null;
        var bus = GstNative.ElementGetBus(pipeline);
        if (bus == IntPtr.Zero)
            return null;
        try
        {
            var msg = GstNative.BusTimedPopFiltered(bus, timeoutNs, GstNative.MessageEos | GstNative.MessageError);
            if (msg == IntPtr.Zero)
                return null;
            try
            {
                if (GstExtra.MessageType(msg) == GstNative.MessageEos)
                {
                    eos = true;
                    return null;
                }

                GstNative.MessageParseError(msg, out var gerror, out var debug);
                var text = GstNative.TakeErrorMessage(gerror);
                Log.Info("osd video: " + text + " (" + Marshal.PtrToStringUTF8(debug) + ")");
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

    private static string? PopError(IntPtr pipeline)
    {
        var eos = false;
        return PopEnd(pipeline, ref eos);
    }

    /// <summary>Starts a decoder and pulls its first frame (after a seek to <paramref name="seconds"/> when above 0).</summary>
    /// <remarks>
    /// A seek position is stream time, which starts at 0 even when the first frame's timestamp does
    /// not (an MP4 edit list); frames before <paramref name="fromPts"/> seconds are skipped.
    /// </remarks>
    private static Decoded? FirstFrame(IntPtr pipeline, IntPtr sink, double seconds, CancellationToken cancel, out int fpsNum, out int fpsDen,
        double fromPts = 0)
    {
        fpsNum = fpsDen = 0;
        if (GstNative.ElementSetState(pipeline, GstExtra.StatePaused) == GstNative.StateChangeFailure)
            throw new InvalidOperationException(PopError(pipeline) ?? "The video could not be opened.");
        if (GstExtra.WaitState(pipeline, 10_000) == GstNative.StateChangeFailure)
            throw new InvalidOperationException(PopError(pipeline) ?? "The video could not be opened.");
        if (seconds > 0)
        {
            GstExtra.Seek(pipeline, seconds);
            GstExtra.WaitState(pipeline, 10_000);
        }

        if (GstNative.ElementSetState(pipeline, GstNative.StatePlaying) == GstNative.StateChangeFailure)
            throw new InvalidOperationException(PopError(pipeline) ?? "The video did not start.");
        var target = (ulong)(Math.Max(0, Math.Max(seconds, fromPts)) * 1e9);
        for (var waited = 0; waited < 200; waited++)
        {
            cancel.ThrowIfCancellationRequested();
            var sample = GstNative.AppSinkTryPullSample(sink, 100 * GstNative.Millisecond);
            if (sample == IntPtr.Zero)
            {
                var ended = false;
                if (PopEnd(pipeline, ref ended) is { } e)
                    throw new InvalidOperationException(e);
                if (ended)
                    return null;
                continue;
            }

            try
            {
                var frame = Copy(sample, out fpsNum, out fpsDen);
                // an inaccurate seek lands on the keyframe before: read on to the asked time
                if (frame != null && (frame.Pts == GstNative.ClockTimeNone || frame.Pts + 50 * GstNative.Millisecond >= target))
                    return frame;
            }
            finally
            {
                GstNative.MiniObjectUnref(sample);
            }
        }

        throw new InvalidOperationException("No video frame came out of the file.");
    }

    /// <summary>Size, frame rate and length of a video. Blocks: call off the UI thread.</summary>
    public static OsdVideoInfo Probe(string path, CancellationToken cancel = default)
    {
        if (!IsAvailable(out var message))
            throw new InvalidOperationException(message);
        var pipeline = Launch(DecodePipeline(path));
        var sink = GstNative.BinGetByName(pipeline, SinkName);
        try
        {
            var frame = FirstFrame(pipeline, sink, 0, cancel, out var num, out var den)
                        ?? throw new InvalidOperationException("The file has no video.");
            var duration = GstExtra.DurationSeconds(pipeline);
            return new OsdVideoInfo(frame.Width, frame.Height, num, den, duration,
                frame.Pts == GstNative.ClockTimeNone ? 0 : frame.Pts / 1e9);
        }
        finally
        {
            if (sink != IntPtr.Zero)
                GstNative.ObjectUnref(sink);
            Shut(ref pipeline);
        }
    }

    /// <summary>
    /// One frame at <paramref name="seconds"/> (from the first frame, which has timestamp
    /// <paramref name="startSeconds"/>) with the HUD of <paramref name="hudFor"/> drawn over it: the alignment preview.
    /// </summary>
    public static async Task<(OsdFrame Frame, double Seconds)> PreviewAsync(string path, double seconds, double startSeconds, int width, int height,
        Func<double, HudModel> hudFor, IHudCompositor compositor, CancellationToken cancel = default)
    {
        if (!IsAvailable(out var message))
            throw new InvalidOperationException(message);
        var decoded = await Task.Run(() =>
        {
            var pipeline = Launch(DecodePipeline(path, width, height));
            var sink = GstNative.BinGetByName(pipeline, SinkName);
            try
            {
                return FirstFrame(pipeline, sink, seconds, cancel, out _, out _, seconds + startSeconds);
            }
            finally
            {
                if (sink != IntPtr.Zero)
                    GstNative.ObjectUnref(sink);
                Shut(ref pipeline);
            }
        }, cancel).ConfigureAwait(false) ?? throw new InvalidOperationException("No frame at that time (past the end?).");

        var t = decoded.Pts == GstNative.ClockTimeNone ? seconds : decoded.Pts / 1e9 - startSeconds;
        await compositor.ComposeAsync(decoded.Pixels, decoded.Width, decoded.Height, hudFor(t), cancel).ConfigureAwait(false);
        return (new OsdFrame(decoded.Pixels, decoded.Width, decoded.Height), t);
    }

    /// <summary>
    /// Writes <paramref name="output"/>: every frame of <paramref name="video"/> with the HUD of
    /// <paramref name="hudFor"/> (seconds into the video → HUD) over it. On cancel the file is
    /// closed properly up to the last frame written; returns the frames written.
    /// </summary>
    public static Task<long> RenderAsync(string video, string output, OsdRenderOptions options, Func<double, HudModel> hudFor,
        IHudCompositor compositor, IProgress<OsdRenderProgress>? progress, CancellationToken cancel) =>
        Task.Run(() => Render(video, output, options, hudFor, compositor, progress, cancel));

    private static async Task<long> Render(string video, string output, OsdRenderOptions options, Func<double, HudModel> hudFor,
        IHudCompositor compositor, IProgress<OsdRenderProgress>? progress, CancellationToken cancel)
    {
        if (!IsAvailable(out var message))
            throw new InvalidOperationException(message);
        if (!GstNative.HasElement(options.Encoder))
            throw new InvalidOperationException($"The encoder {options.Encoder} is not installed.");

        var decoder = Launch(DecodePipeline(video, options.Width, options.Height));
        var sink = GstNative.BinGetByName(decoder, SinkName);
        IntPtr encoder = IntPtr.Zero, appsrc = IntPtr.Zero;
        long frames = 0;
        try
        {
            var first = FirstFrame(decoder, sink, 0, cancel, out var fpsNum, out var fpsDen)
                        ?? throw new InvalidOperationException("The file has no video.");
            var duration = GstExtra.DurationSeconds(decoder);
            var frameNs = fpsNum > 0 && fpsDen > 0 ? (ulong)(1e9 * fpsDen / fpsNum) : 33_333_333UL;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            encoder = Launch(EncodePipeline(options, fpsNum, fpsDen, output));
            appsrc = GstNative.BinGetByName(encoder, SrcName);
            if (appsrc == IntPtr.Zero)
                throw new InvalidOperationException("The encoder's appsrc was not created.");
            if (GstNative.ElementSetState(encoder, GstNative.StatePlaying) == GstNative.StateChangeFailure)
                throw new InvalidOperationException(PopError(encoder) ?? "The encoder did not start.");

            var firstPts = first.Pts == GstNative.ClockTimeNone ? 0UL : first.Pts;
            var frame = first;
            while (frame != null)
            {
                // the frame's time in the recording (from its first frame), as the old OSD video used the sample time
                var pts = frame.Pts == GstNative.ClockTimeNone ? firstPts + (ulong)frames * frameNs : frame.Pts;
                var seconds = pts >= firstPts ? (pts - firstPts) / 1e9 : 0;
                // not cancelled half way: a cancel ends the file after this frame
                await compositor.ComposeAsync(frame.Pixels, frame.Width, frame.Height, hudFor(seconds), CancellationToken.None).ConfigureAwait(false);

                if (PopError(encoder) is { } encodeError)
                    throw new InvalidOperationException("Encoding: " + encodeError);
                Push(appsrc, frame.Pixels, pts >= firstPts ? pts - firstPts : 0, frame.Duration == GstNative.ClockTimeNone ? frameNs : frame.Duration);
                frames++;
                progress?.Report(new OsdRenderProgress(frames, seconds, duration));

                if (cancel.IsCancellationRequested)
                    break;
                frame = Next(decoder, sink, cancel);
            }

            // end of stream, so mp4mux writes its index; a cancelled render keeps what was written
            GstNative.AppSrcEndOfStream(appsrc);
            for (var i = 0; i < 600; i++)
            {
                var ended = false;
                if (PopEnd(encoder, ref ended, 100 * GstNative.Millisecond) is { } e)
                    throw new InvalidOperationException("Encoding: " + e);
                if (ended)
                    break;
            }

            return frames;
        }
        finally
        {
            if (appsrc != IntPtr.Zero)
                GstNative.ObjectUnref(appsrc);
            Shut(ref encoder);
            if (sink != IntPtr.Zero)
                GstNative.ObjectUnref(sink);
            Shut(ref decoder);
        }
    }

    /// <summary>The next decoded frame; null at the end (or when cancelled).</summary>
    private static Decoded? Next(IntPtr decoder, IntPtr sink, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var sample = GstNative.AppSinkTryPullSample(sink, 100 * GstNative.Millisecond);
            if (sample != IntPtr.Zero)
            {
                try
                {
                    if (Copy(sample, out _, out _) is { } frame)
                        return frame;
                }
                finally
                {
                    GstNative.MiniObjectUnref(sample);
                }

                continue;
            }

            var ended = false;
            if (PopEnd(decoder, ref ended) is { } e)
                throw new InvalidOperationException("Decoding: " + e);
            if (ended)
                return null;
        }

        return null;
    }

    private static void Push(IntPtr appsrc, byte[] bgra, ulong pts, ulong duration)
    {
        var buffer = GstNative.BufferNewAllocate(IntPtr.Zero, (UIntPtr)bgra.Length, IntPtr.Zero);
        if (buffer == IntPtr.Zero)
            throw new InvalidOperationException("Out of memory for a video frame.");
        var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            GstNative.BufferFill(buffer, UIntPtr.Zero, pin.AddrOfPinnedObject(), (UIntPtr)bgra.Length);
        }
        finally
        {
            pin.Free();
        }

        GstExtra.SetTimes(buffer, pts, duration);
        // takes the buffer; blocks while the encoder is behind (block=true)
        var flow = GstNative.AppSrcPushBuffer(appsrc, buffer);
        if (flow < 0)
            throw new InvalidOperationException($"The encoder refused a frame (flow {flow}).");
    }

    /// <summary>The few calls the OSD video needs beyond <see cref="GstNative"/>, bound from the same library.</summary>
    private static class GstExtra
    {
        public const int StatePaused = 3;
        private const int FormatTime = 3;
        private const int SeekFlush = 1;
        private const int SeekAccurate = 2;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetFractionFn(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int num, out int den);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int QueryDurationFn(IntPtr element, int format, out long duration);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SeekSimpleFn(IntPtr element, int format, int flags, long position);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetStateFn(IntPtr element, out int state, out int pending, ulong timeout);

        /// <summary>The head of struct GstBuffer (GstMiniObject, pool, then the timestamps), for PTS and duration.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct BufferHead
        {
            public IntPtr Type;
            public int RefCount;
            public int LockState;
            public uint Flags;
            public IntPtr Copy;
            public IntPtr Dispose;
            public IntPtr Free;
            public uint PrivUint;
            public IntPtr PrivPointer;
            public IntPtr Pool;
            public ulong Pts;
            public ulong Dts;
            public ulong Duration;
        }

        private static readonly int PtsOffset = (int)Marshal.OffsetOf<BufferHead>(nameof(BufferHead.Pts));
        private static readonly int DurationOffset = (int)Marshal.OffsetOf<BufferHead>(nameof(BufferHead.Duration));

        private static GetFractionFn? _fraction;
        private static QueryDurationFn? _duration;
        private static SeekSimpleFn? _seek;
        private static GetStateFn? _getState;

        private static T Fn<T>(ref T? slot, string name) where T : Delegate =>
            slot ??= GstNative.Bind<T>(GstNative.CoreHandle, name);

        public static bool Fraction(IntPtr structure, string field, out int num, out int den)
        {
            num = den = 0;
            return Fn(ref _fraction, "gst_structure_get_fraction")(structure, field, out num, out den) != 0;
        }

        public static double DurationSeconds(IntPtr element) =>
            Fn(ref _duration, "gst_element_query_duration")(element, FormatTime, out var d) != 0 && d > 0 ? d / 1e9 : 0;

        public static void Seek(IntPtr element, double seconds) =>
            Fn(ref _seek, "gst_element_seek_simple")(element, FormatTime, SeekFlush | SeekAccurate, (long)(seconds * 1e9));

        public static int WaitState(IntPtr element, int timeoutMs) =>
            Fn(ref _getState, "gst_element_get_state")(element, out _, out _, (ulong)timeoutMs * GstNative.Millisecond);

        /// <summary>struct GstMiniObject, which heads GstMessage too (its type follows).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MiniObject
        {
            public IntPtr Type;
            public int RefCount;
            public int LockState;
            public uint Flags;
            public IntPtr Copy;
            public IntPtr Dispose;
            public IntPtr Free;
            public uint PrivUint;
            public IntPtr PrivPointer;
        }

        private static readonly int MessageTypeOffset = Marshal.SizeOf<MiniObject>();

        public static int MessageType(IntPtr message) => Marshal.ReadInt32(message, MessageTypeOffset);

        public static ulong Pts(IntPtr buffer) => (ulong)Marshal.ReadInt64(buffer, PtsOffset);

        public static ulong Duration(IntPtr buffer) => (ulong)Marshal.ReadInt64(buffer, DurationOffset);

        public static void SetTimes(IntPtr buffer, ulong pts, ulong duration)
        {
            Marshal.WriteInt64(buffer, PtsOffset, (long)pts);
            Marshal.WriteInt64(buffer, DurationOffset, (long)duration);
        }
    }
}
