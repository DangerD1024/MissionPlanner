using System.Collections.Concurrent;
using log4net;
using MissionPlanner.App.Core.Session;
using MissionPlanner.Comms;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Rtk;

/// <summary>Where GPS_RTCM_DATA packets go.</summary>
public interface IRtcmSink
{
    /// <summary>Sends one packet; false when there is no vehicle link to send it on.</summary>
    bool Send(MAVLink.mavlink_gps_rtcm_data_t packet);
}

/// <summary>
/// Sends to the vehicles on the session's link. GPS_RTCM_DATA has no target, so like Mission
/// Planner it goes out once per MAVLink flavour in use (v1, v2, signed) rather than once per vehicle.
/// </summary>
public sealed class SessionRtcmSink(VehicleSession session) : IRtcmSink
{
    public bool Send(MAVLink.mavlink_gps_rtcm_data_t packet)
    {
        var mav = session.Mav;
        if (!session.IsConnected || mav.BaseStream == null || !mav.BaseStream.IsOpen)
            return false;

        List<MAVState> targets;
        try
        {
            targets = mav.MAVlist.GroupBy(m => (m.mavlinkv2, m.signing)).Select(g => g.First()).ToList();
        }
        catch (InvalidOperationException)
        {
            // the reader added a vehicle while we looked
            targets = new List<MAVState>();
        }

        if (targets.Count == 0)
            targets.Add(session.Vehicle);
        foreach (var t in targets)
            mav.sendPacket(packet, t.sysid, t.compid);
        return true;
    }
}

public enum RtkState
{
    Stopped,
    Connecting,
    Running,
    Reconnecting,
    Failed,
}

public sealed record RtkInjectorStatus(
    RtkState State,
    string Message,
    long BytesIn,
    long BytesForwarded,
    long PacketsSent,
    long FramesForwarded,
    long FramesNotSent,
    long CrcErrors,
    DateTime? LastDataUtc,
    int Reconnects);

/// <summary>
/// Reads a correction source on a worker task, picks the RTCM 3 frames out of the stream and
/// sends each to the vehicle as GPS_RTCM_DATA. Reconnects on errors and silence, logs the raw
/// stream like Mission Planner's .gpsbase file, and can configure a u-blox base on the way.
/// </summary>
public sealed class RtkInjector : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(RtkInjector));

    private readonly IRtcmSink _sink;
    private readonly Func<DateTime> _now;
    private readonly GpsRtcmFragmenter _fragmenter = new();
    private readonly RtcmFramer _framer = new();
    private readonly ConcurrentQueue<Action<ICommsSerial>> _serialActions = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;

    private RtkState _state;
    private string _message = "";
    private long _bytesIn;
    private long _bytesForwarded;
    private long _packetsSent;
    private long _framesForwarded;
    private long _framesNotSent;
    private DateTime? _lastData;
    private int _reconnects;

    public RtkInjector(IRtcmSink sink, Func<DateTime>? now = null)
    {
        _sink = sink;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(3);

    public RtcmStatistics Statistics { get; } = new();

    /// <summary>UBX status of a serial base (survey-in, mode); null for other sources.</summary>
    public UbloxMonitor? Ublox { get; private set; }

    public IRtcmSource? Source { get; private set; }

    public bool IsRunning => _stop != null;

    /// <summary>Raised on the worker when a 1005/1006 frame gives the base position.</summary>
    public event EventHandler<RtcmBasePosition>? BasePositionReceived;

    /// <summary>Raised on the worker after a u-blox configuration step finished or failed (null = success).</summary>
    public event EventHandler<string?>? SerialActionCompleted;

    public RtkInjectorStatus GetStatus()
    {
        lock (_gate)
        {
            return new RtkInjectorStatus(_state, _message, _bytesIn, _bytesForwarded, _packetsSent, _framesForwarded,
                _framesNotSent, _framer.CrcErrors, _lastData, _reconnects);
        }
    }

    /// <summary>Starts reading <paramref name="source"/>; <paramref name="ublox"/> configures a serial u-blox base first.</summary>
    public void Start(IRtcmSource source, UbloxBaseSettings? ublox = null, string? rawLogPath = null)
    {
        if (_stop != null)
            throw new InvalidOperationException("already running");

        Source = source;
        Ublox = source is SerialRtcmSource ? new UbloxMonitor() : null;
        Statistics.Clear();
        _framer.Reset();
        lock (_gate)
        {
            _bytesIn = _bytesForwarded = _packetsSent = _framesForwarded = _framesNotSent = 0;
            _lastData = null;
            _reconnects = 0;
        }

        while (_serialActions.TryDequeue(out _))
        {
        }

        if (ublox != null)
            _serialActions.Enqueue(port => UbloxBase.Configure(port, ublox));

        var stop = new CancellationTokenSource();
        _stop = stop;
        SetState(RtkState.Connecting, "Connecting to " + source.Description);
        _worker = Task.Factory.StartNew(() => RunAsync(source, rawLogPath, stop.Token), stop.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Runs a u-blox command on the worker, between reads (serial sources only).</summary>
    public bool EnqueueSerialAction(Action<ICommsSerial> action)
    {
        if (Source is not SerialRtcmSource || _stop == null)
            return false;
        _serialActions.Enqueue(action);
        return true;
    }

    public async Task StopAsync()
    {
        var stop = _stop;
        if (stop == null)
            return;
        _stop = null;
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        stop.Dispose();
        lock (_gate)
        {
            if (_state != RtkState.Failed)
            {
                _state = RtkState.Stopped;
                _message = "Stopped";
            }
        }
    }

    private async Task RunAsync(IRtcmSource source, string? rawLogPath, CancellationToken stop)
    {
        var buffer = new byte[4096];
        var frames = new List<RtcmFrame>();
        FileStream? raw = null;
        try
        {
            if (rawLogPath != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(rawLogPath)!);
                    raw = new FileStream(rawLogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                catch (Exception ex)
                {
                    Log.Warn("gpsbase log", ex);
                }
            }

            var first = true;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (!first)
                        SetState(RtkState.Connecting, "Connecting to " + source.Description);
                    await source.OpenAsync(stop).ConfigureAwait(false);
                    first = false;
                    SetState(RtkState.Running, "Waiting for data from " + source.Description);

                    var lastData = _now();
                    var lastFlush = lastData;
                    var gotData = false;
                    while (!stop.IsCancellationRequested)
                    {
                        RunSerialActions(source);

                        var n = await source.ReadAsync(buffer, stop).ConfigureAwait(false);
                        var now = _now();
                        if (n <= 0)
                        {
                            if (source.SilenceTimeout is { } quiet && now - lastData > quiet)
                                throw new TimeoutException($"No data from {source.Description} for {quiet.TotalSeconds:0} s.");
                            continue;
                        }

                        lastData = now;
                        if (!gotData)
                        {
                            gotData = true;
                            SetState(RtkState.Running, "Receiving from " + source.Description);
                        }

                        lock (_gate)
                        {
                            _bytesIn += n;
                            _lastData = now;
                        }

                        if (raw != null)
                        {
                            raw.Write(buffer, 0, n);
                            if (now - lastFlush > TimeSpan.FromSeconds(1))
                            {
                                raw.Flush();
                                lastFlush = now;
                            }
                        }

                        Ublox?.Push(buffer.AsSpan(0, n));
                        frames.Clear();
                        _framer.Push(buffer.AsSpan(0, n), frames);
                        foreach (var frame in frames)
                            Forward(frame, now);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (NtripException ex) when (ex.Fatal)
                {
                    SetState(RtkState.Failed, ex.Message);
                    break;
                }
                catch (Exception ex)
                {
                    Log.Info("rtk source: " + ex.Message);
                    await CloseQuietly(source).ConfigureAwait(false);
                    lock (_gate)
                        _reconnects++;
                    SetState(RtkState.Reconnecting, $"{Describe(ex)} Retrying in {ReconnectDelay.TotalSeconds:0} s.");
                    try
                    {
                        await Task.Delay(ReconnectDelay, stop).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            await CloseQuietly(source).ConfigureAwait(false);
            if (raw != null)
                await raw.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Forward(RtcmFrame frame, DateTime now)
    {
        var basePos = Statistics.Record(frame, now);

        var sent = true;
        var packets = _fragmenter.Fragment(frame.Bytes);
        foreach (var p in packets)
        {
            try
            {
                sent &= _sink.Send(p);
            }
            catch (Exception ex)
            {
                Log.Warn("rtcm send", ex);
                sent = false;
            }
        }

        lock (_gate)
        {
            if (sent)
            {
                _framesForwarded++;
                _bytesForwarded += frame.Length;
                _packetsSent += packets.Count;
            }
            else
            {
                _framesNotSent++;
            }
        }

        if (basePos != null)
            BasePositionReceived?.Invoke(this, basePos);
    }

    private void RunSerialActions(IRtcmSource source)
    {
        if (_serialActions.IsEmpty || source is not SerialRtcmSource { Port: { } port } serial)
            return;

        while (_serialActions.TryDequeue(out var action))
        {
            string? error = null;
            try
            {
                SetState(RtkState.Running, "Configuring the u-blox receiver...");
                action(port);
                // the configuration moves the receiver (and the port) to 460800 baud
                serial.Baud = port.BaudRate;
                SetState(RtkState.Running, "Receiving from " + source.Description);
            }
            catch (Exception ex)
            {
                Log.Warn("ublox config", ex);
                error = "u-blox configuration failed: " + ex.Message;
                SetState(RtkState.Running, error);
            }

            SerialActionCompleted?.Invoke(this, error);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException => ex.Message,
        NtripException => ex.Message,
        UnauthorizedAccessException => "Access denied: " + ex.Message,
        _ => ex.Message.TrimEnd('.') + ".",
    };

    private static async Task CloseQuietly(IRtcmSource source)
    {
        try
        {
            await source.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug("rtk close", ex);
        }
    }

    private void SetState(RtkState state, string message)
    {
        lock (_gate)
        {
            _state = state;
            _message = message;
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
