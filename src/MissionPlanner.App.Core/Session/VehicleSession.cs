using log4net;
using MissionPlanner.App.Core.Links;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Session;

public enum LinkState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>Thrown when a connect attempt fails; the message is meant for the user.</summary>
public sealed class ConnectException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The vehicle link, replacing the WinForms app's static <c>MainV2.comPort</c> and its polling
/// loops. One <see cref="MAVLinkInterface"/> lives for the whole app (so subscriptions survive
/// reconnects, as in the WinForms app); this class owns its single reader task, the GCS
/// heartbeat and the telemetry log. Events are raised on worker threads.
/// </summary>
public sealed class VehicleSession : IAsyncDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(VehicleSession));

    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task _reader = Task.CompletedTask;
    private BufferedStream? _tlog;

    public MAVLinkInterface Mav { get; } = new();

    public LinkState State { get; private set; }

    public LinkSpec? Link { get; private set; }

    /// <summary>Telemetry log being written for the current connection.</summary>
    public string? TlogPath { get; private set; }

    public DateTime ConnectedAt { get; private set; }

    /// <summary>Raised on a worker thread whenever <see cref="State"/> changes.</summary>
    public event EventHandler<LinkState>? StateChanged;

    /// <summary>Raised when an open link drops by itself (cable pulled, vehicle off), not on Disconnect.</summary>
    public event EventHandler<string>? LinkLost;

    /// <summary>The vehicle the screens show; switchable when several share the link.</summary>
    public MAVState Vehicle => Mav.MAV;

    public CurrentState Cs => Mav.MAV.cs;

    public bool IsConnected => State == LinkState.Connected;

    /// <summary>
    /// Opens the link, waits for the vehicle's heartbeats and (optionally) downloads parameters.
    /// Throws <see cref="ConnectException"/> on failure and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public async Task ConnectAsync(LinkSpec spec, bool getParams, IProgress<OperationProgress>? progress = null,
        CancellationToken cancel = default)
    {
        lock (_gate)
        {
            if (State != LinkState.Disconnected)
                throw new InvalidOperationException("already " + State.ToString().ToLowerInvariant());
            State = LinkState.Connecting;
        }

        RaiseState();

        var reporter = new BackgroundProgressReporter(progress, cancel);
        try
        {
            Mav.BaseStream = LinkFactory.Create(spec);

            await Task.Run(() =>
            {
                using (BackgroundProgressReporter.Scope.Use(reporter))
                    Mav.Open(getParams, skipconnectedcheck: false, showui: true);
            }, CancellationToken.None).ConfigureAwait(false);

            if (reporter.Cancelled || cancel.IsCancellationRequested)
                throw new OperationCanceledException(cancel);
            if (reporter.Error != null || !Mav.BaseStream.IsOpen)
                throw new ConnectException(reporter.ErrorMessage ?? "The link closed while connecting.", reporter.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ConnectException)
        {
            CloseQuietly();
            SetState(LinkState.Disconnected);
            throw new ConnectException(ex.Message, ex);
        }
        catch
        {
            CloseQuietly();
            SetState(LinkState.Disconnected);
            throw;
        }

        Link = spec;
        ConnectedAt = DateTime.Now;
        StartTlog();

        var stop = new CancellationTokenSource();
        _stop = stop;
        _reader = Task.Factory.StartNew(() => ReadLoop(stop.Token), stop.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        SetState(LinkState.Connected);
    }

    public async Task DisconnectAsync()
    {
        var stop = _stop;
        _stop = null;
        if (stop != null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            stop.Dispose();
        }

        CloseQuietly();
        SetState(LinkState.Disconnected);
    }

    /// <summary>Selects which vehicle on the link the screens show.</summary>
    public void SelectVehicle(byte sysid, byte compid)
    {
        Mav.sysidcurrent = sysid;
        Mav.compidcurrent = compid;
        StateChanged?.Invoke(this, State);
    }

    public IReadOnlyList<MAVState> Vehicles => Mav.MAVlist.ToList();

    private async Task ReadLoop(CancellationToken stop)
    {
        long lastHeartbeat = 0;
        long lastStreams = 0;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!Mav.BaseStream.IsOpen)
                {
                    OnLinkLost("The link closed.");
                    return;
                }

                var now = Environment.TickCount64;
                if (now - lastHeartbeat >= 1000)
                {
                    SendHeartbeats();
                    lastHeartbeat = now;
                }

                // parameter, mission and log transfers read the port themselves while they hold it
                if (Mav.giveComport)
                {
                    await Task.Delay(20, stop).ConfigureAwait(false);
                    continue;
                }

                var read = 0;
                while (Mav.BaseStream.BytesToRead > 0 && !Mav.giveComport && !stop.IsCancellationRequested &&
                       Environment.TickCount64 - now < 100)
                {
                    await Mav.readPacketAsync().ConfigureAwait(false);
                    read++;
                }

                // keeps derived values current and re-requests telemetry streams when they lapse
                if (now - lastStreams >= 200)
                {
                    foreach (var mav in Mav.MAVlist)
                        mav.cs.UpdateCurrentSettings(null, false, Mav, mav);
                    lastStreams = now;
                }

                if (read == 0)
                    await Task.Delay(5, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("read loop", ex);
                if (!Mav.BaseStream.IsOpen)
                {
                    OnLinkLost(ex.Message);
                    return;
                }

                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One GCS heartbeat per link flavour in use (MAVLink 1, MAVLink 2, signed), as the WinForms app does.</summary>
    private void SendHeartbeats()
    {
        var hb = new MAVLink.mavlink_heartbeat_t
        {
            type = (byte)MAVLink.MAV_TYPE.GCS,
            autopilot = (byte)MAVLink.MAV_AUTOPILOT.INVALID,
            mavlink_version = 3,
        };

        bool signed = false, v2 = false, v1 = false;
        foreach (var mav in Mav.MAVlist)
        {
            try
            {
                if (mav.mavlinkv2)
                {
                    if (mav.signing)
                    {
                        if (signed)
                            continue;
                        signed = true;
                    }
                    else
                    {
                        if (v2)
                            continue;
                        v2 = true;
                    }
                }
                else
                {
                    if (v1)
                        continue;
                    v1 = true;
                }

                Mav.sendPacket(hb, mav.sysid, mav.compid);
            }
            catch (Exception ex)
            {
                Log.Warn("heartbeat", ex);
            }
        }
    }

    private void OnLinkLost(string reason)
    {
        CloseQuietly();
        _stop = null;
        SetState(LinkState.Disconnected);
        LinkLost?.Invoke(this, reason);
    }

    private void StartTlog()
    {
        try
        {
            var dir = Settings.Instance.LogDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".tlog");
            _tlog = new BufferedStream(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read));
            Mav.logfile = _tlog;
            TlogPath = path;
        }
        catch (Exception ex)
        {
            // flying without a telemetry log is better than not connecting
            Log.Error("tlog", ex);
            TlogPath = null;
        }
    }

    private void CloseQuietly()
    {
        try
        {
            Mav.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("close", ex);
        }

        try
        {
            Mav.logfile = null;
            _tlog?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("tlog close", ex);
        }

        _tlog = null;
    }

    private void SetState(LinkState state)
    {
        lock (_gate)
            State = state;
        RaiseState();
    }

    private void RaiseState() => StateChanged?.Invoke(this, State);

    public async ValueTask DisposeAsync()
    {
        if (State != LinkState.Disconnected)
            await DisconnectAsync().ConfigureAwait(false);
    }
}
