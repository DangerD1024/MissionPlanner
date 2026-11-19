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
    private long _lastPacketTicks;
    private int _ioErrors;
    private Task _monitor = Task.CompletedTask;
    private int _losing;
    private volatile bool _transportDown;

    public VehicleSession()
    {
        Mav.OnPacketReceived += (_, message) =>
        {
            Interlocked.Exchange(ref _lastPacketTicks, Environment.TickCount64);
            // before the library adds the text to cs.messages, so the Messages tab finds it
            Severities.Record(message);
        };
    }

    /// <summary>STATUSTEXT severities by text, for the Messages tab.</summary>
    public StatusTextSeverities Severities { get; } = new();

    /// <summary>A TCP peer-closed check counts only after this long without a packet (see the link monitor).</summary>
    internal static readonly TimeSpan PeerClosedQuiet = TimeSpan.FromSeconds(1);

    /// <summary>No packet for this long and the link counts as lost (shown to the user, kept open).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    public MAVLinkInterface Mav { get; } = new();

    /// <summary>Time since the last packet from the link (zero while not connected).</summary>
    public TimeSpan SinceLastPacket => IsConnected
        ? TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastPacketTicks))
        : TimeSpan.Zero;

    /// <summary>
    /// Connected, but nothing has arrived for <see cref="StaleAfter"/>. A link that is certainly gone
    /// (USB unplugged, TCP closed) disconnects instead; this covers UDP and a vehicle that went quiet,
    /// which recover by themselves when packets return.
    /// </summary>
    public bool IsLinkStale => IsConnected && SinceLastPacket > StaleAfter;

    /// <summary>A TCP or UDP link: these never disconnect by themselves, they wait for the link to return.</summary>
    public bool IsNetworkLink => Link is { Kind: LinkKind.Tcp or LinkKind.UdpListen or LinkKind.UdpConnect };

    /// <summary>
    /// A network link that dropped (TCP closed by the other end) or went silent: the session stays
    /// connected and keeps trying — TCP reopens its socket every couple of seconds — and carries on
    /// by itself when data returns. A USB serial device that disappears disconnects instead.
    /// </summary>
    public bool IsReconnecting => IsConnected && IsNetworkLink && (_transportDown || IsLinkStale);

    public LinkState State { get; private set; }

    public LinkSpec? Link { get; private set; }

    /// <summary>Telemetry log being written for the current connection.</summary>
    public string? TlogPath { get; private set; }

    public DateTime ConnectedAt { get; private set; }

    /// <summary>Raised on a worker thread whenever <see cref="State"/> changes.</summary>
    public event EventHandler<LinkState>? StateChanged;

    /// <summary>Raised when a serial link is gone for good (USB device unplugged), not on Disconnect.</summary>
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

        // The reader must already run while Open() works: Open's version request (which tells it
        // the vehicle supports MAVFTP) and the MAVFTP parameter download only wait for replies that
        // the reader delivers, as the WinForms app's always-running reader thread does. Without it
        // the capabilities stay unknown and parameters fall back to the slow one-by-one download.
        var stop = new CancellationTokenSource();
        _stop = stop;
        _reader = Task.Factory.StartNew(() => ReadLoop(stop.Token), stop.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        try
        {
            Mav.BaseStream = LinkFactory.Create(spec);
            // log the whole session, parameter download included, as the WinForms app does
            StartTlog();

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
            await FailConnectAsync(stop).ConfigureAwait(false);
            throw new ConnectException(ex.Message, ex);
        }
        catch
        {
            await FailConnectAsync(stop).ConfigureAwait(false);
            throw;
        }

        Link = spec;
        ConnectedAt = DateTime.Now;
        Interlocked.Exchange(ref _lastPacketTicks, Environment.TickCount64);
        _ioErrors = 0;
        _losing = 0;
        _transportDown = false;
        SetState(LinkState.Connected);
        _monitor = Task.Run(() => MonitorLoop(stop.Token), CancellationToken.None);
    }

    private async Task FailConnectAsync(CancellationTokenSource stop)
    {
        _stop = null;
        await stop.CancelAsync().ConfigureAwait(false);
        await WaitBounded(_reader).ConfigureAwait(false);
        var tlog = TlogPath;
        CloseQuietly();
        // a failed attempt leaves no telemetry log behind
        try
        {
            if (tlog != null && File.Exists(tlog) && new FileInfo(tlog).Length < 4096)
                File.Delete(tlog);
        }
        catch (IOException)
        {
        }

        TlogPath = null;
        SetState(LinkState.Disconnected);
    }

    public async Task DisconnectAsync()
    {
        var stop = _stop;
        _stop = null;
        if (stop != null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await WaitBounded(Task.WhenAll(_reader, _monitor)).ConfigureAwait(false);
        }

        CloseQuietly();
        _transportDown = false;
        SetState(LinkState.Disconnected);
    }

    /// <summary>A task blocked in a dead device must not hold the app: wait a few seconds at most.</summary>
    private static async Task WaitBounded(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // cancelled, failed or still stuck: the link is being torn down either way
        }
    }

    /// <summary>
    /// Watches the link while connected, independently of the reader (which can sit in a blocking
    /// call): a vanished USB device disconnects; a dropped TCP socket is reopened until it works.
    /// </summary>
    private async Task MonitorLoop(CancellationToken stop)
    {
        long nextReopen = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (State != LinkState.Connected || Link is not { } link)
                    continue;
                var stream = Mav.BaseStream;

                if (link.Kind == LinkKind.Serial)
                {
                    if (LinkHealth.Check(link, stream) is { } gone)
                    {
                        LoseLink(gone);
                        return;
                    }

                    continue;
                }

                if (link.Kind == LinkKind.Tcp)
                {
                    // "peer closed" (readable with nothing to read) races with the reader thread, which can
                    // drain the bytes between the two checks; believe it only once packets have stopped.
                    // Reopening closes the old socket, and the Windows SITL build exits when its client
                    // disconnects, so a false alarm there ends the simulator.
                    if (stream == null || !stream.IsOpen ||
                        (SinceLastPacket > PeerClosedQuiet && LinkHealth.Check(link, stream) != null))
                        _transportDown = true;

                    if (_transportDown && Environment.TickCount64 >= nextReopen)
                    {
                        nextReopen = Environment.TickCount64 + 2000;
                        if (await TryReopenAsync(link, stop).ConfigureAwait(false))
                            _transportDown = false;
                    }
                }

                // UDP has nothing to reopen: it listens on, and IsReconnecting follows the packets
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("link monitor", ex);
            }
        }
    }

    private async Task<bool> TryReopenAsync(LinkSpec link, CancellationToken stop)
    {
        var fresh = LinkFactory.Create(link);
        var opened = await Task.Run(() =>
        {
            try
            {
                fresh.Open();
                return fresh.IsOpen;
            }
            catch (Exception)
            {
                return false;
            }
        }, stop).ConfigureAwait(false);

        if (!opened || stop.IsCancellationRequested || State != LinkState.Connected)
        {
            try
            {
                fresh.Dispose();
            }
            catch (Exception)
            {
            }

            return false;
        }

        // the library closes the old socket; parameters and vehicle state carry on
        Mav.BaseStream = fresh;
        Log.Info($"link {link} reopened");
        return true;
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
                if (Mav.BaseStream == null || !Mav.BaseStream.IsOpen)
                {
                    // Open() has not opened the port yet
                    if (State == LinkState.Connecting)
                    {
                        await Task.Delay(20, stop).ConfigureAwait(false);
                        continue;
                    }

                    // a network link waits while the monitor reopens it
                    if (IsNetworkLink)
                    {
                        await Task.Delay(50, stop).ConfigureAwait(false);
                        continue;
                    }

                    LoseLink("The link closed.");
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

                if (read > 0)
                    _ioErrors = 0;

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

                // a serial port that keeps failing while nothing arrives is gone, whatever IsOpen says;
                // network links are left to the monitor, which reopens them
                if (State == LinkState.Connected && !IsNetworkLink &&
                    ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException
                        or InvalidOperationException or UnauthorizedAccessException &&
                    ++_ioErrors >= 5 && SinceLastPacket > TimeSpan.FromSeconds(1))
                {
                    LoseLink(ex.Message);
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

    /// <summary>The link is gone for good: disconnect once, from whichever task noticed first.</summary>
    private void LoseLink(string reason)
    {
        if (Interlocked.Exchange(ref _losing, 1) == 1)
            return;
        var stop = _stop;
        _stop = null;
        stop?.Cancel();
        // closing a vanished serial device is bounded (LinkFactory.SerialLink), so this cannot hang
        CloseQuietly();
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
