using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.Logs;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>The REMOTE_LOG traffic of one vehicle; faked in tests.</summary>
public interface IRemoteLogTransport
{
    /// <summary>Sends REMOTE_LOG_BLOCK_STATUS: a block number, or the START / STOP command.</summary>
    void SendStatus(uint seqno, MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_STATUSES status);

    /// <summary>Calls <paramref name="handler"/> (on a worker thread) for each REMOTE_LOG_DATA_BLOCK, until disposed.</summary>
    IDisposable Subscribe(Action<uint, byte[]> handler);
}

/// <summary>REMOTE_LOG through the session's MAVLink connection, to and from the selected vehicle.</summary>
public sealed class MavlinkRemoteLogTransport : IRemoteLogTransport
{
    private readonly MAVLinkInterface _mav;
    private readonly uint _sysid;
    private readonly byte _compid;

    public MavlinkRemoteLogTransport(VehicleSession session)
    {
        _mav = session.Mav;
        _sysid = session.Vehicle.sysid;
        _compid = session.Vehicle.compid;
    }

    public void SendStatus(uint seqno, MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_STATUSES status) =>
        _mav.sendPacket(new MAVLink.mavlink_remote_log_block_status_t(seqno, (byte)_sysid, _compid, (byte)status), _sysid, _compid);

    public IDisposable Subscribe(Action<uint, byte[]> handler)
    {
        var id = _mav.SubscribeToPacketType(MAVLink.MAVLINK_MSG_ID.REMOTE_LOG_DATA_BLOCK, m =>
        {
            var block = m.ToStructure<MAVLink.mavlink_remote_log_data_block_t>();
            handler(block.seqno, block.data);
            return true;
        }, _sysid, _compid);
        return new Unsubscriber(() => _mav.UnSubscribeToPacketType(id));
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>
/// The vehicle's DataFlash log streamed over MAVLink into a .bin file, as Mission Planner's
/// RemoteLog: START asks the vehicle to send, each REMOTE_LOG_DATA_BLOCK (200 bytes) is written at
/// seqno × 200 and acknowledged, STOP ends it. Blocks resent by the vehicle land in their place.
/// </summary>
public sealed class RemoteLogWriter : IDisposable
{
    public const int BlockSize = 200;

    private readonly IRemoteLogTransport _transport;
    private readonly object _gate = new();
    private FileStream? _file;
    private IDisposable? _subscription;
    private long _blocks;
    private long _length;
    private long _lastBlockTicks;

    public RemoteLogWriter(IRemoteLogTransport transport, string path)
    {
        _transport = transport;
        Path = path;
    }

    public string Path { get; }

    public bool IsRunning { get; private set; }

    /// <summary>Blocks received (resent ones count again).</summary>
    public long Blocks => Interlocked.Read(ref _blocks);

    /// <summary>Highest seqno seen, -1 before the first block.</summary>
    public long LastSeqno { get; private set; } = -1;

    /// <summary>Size of the file so far.</summary>
    public long Length => Interlocked.Read(ref _length);

    /// <summary>When the last block came (Environment.TickCount64), 0 before the first.</summary>
    public long LastBlockTicks => Interlocked.Read(ref _lastBlockTicks);

    public void Start()
    {
        if (IsRunning)
            return;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _file = new FileStream(Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _subscription = _transport.Subscribe(OnBlock);
        IsRunning = true;
        _transport.SendStatus((uint)MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_COMMANDS.MAV_REMOTE_LOG_DATA_BLOCK_START,
            MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_STATUSES.MAV_REMOTE_LOG_DATA_BLOCK_ACK);
    }

    /// <summary>Writes one block and acknowledges it (the transport calls this; tests too).</summary>
    public void OnBlock(uint seqno, byte[] data)
    {
        lock (_gate)
        {
            if (_file == null || data == null)
                return;
            var offset = (long)seqno * BlockSize;
            if (_file.Position != offset)
                _file.Seek(offset, SeekOrigin.Begin);
            _file.Write(data, 0, Math.Min(data.Length, BlockSize));
            Interlocked.Exchange(ref _length, _file.Length);
            if (seqno > LastSeqno)
                LastSeqno = seqno;
        }

        Interlocked.Increment(ref _blocks);
        Interlocked.Exchange(ref _lastBlockTicks, Environment.TickCount64);
        _transport.SendStatus(seqno, MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_STATUSES.MAV_REMOTE_LOG_DATA_BLOCK_ACK);
    }

    public void Stop()
    {
        if (!IsRunning)
            return;
        IsRunning = false;
        try
        {
            _transport.SendStatus((uint)MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_COMMANDS.MAV_REMOTE_LOG_DATA_BLOCK_STOP,
                MAVLink.MAV_REMOTE_LOG_DATA_BLOCK_STATUSES.MAV_REMOTE_LOG_DATA_BLOCK_ACK);
        }
        catch
        {
            // the link is already down
        }

        _subscription?.Dispose();
        _subscription = null;
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// TOOLS > Remote DataFlash logger (Mission Planner's Ctrl+F > Start Remote df logger): the vehicle
/// streams its DataFlash log over MAVLink into a .bin in the log folder, for the log browser.
/// </summary>
public sealed partial class RemoteLoggerViewModel : ToolPageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(RemoteLoggerViewModel));

    private RemoteLogWriter? _writer;

    public RemoteLoggerViewModel(AppServices services)
        : base("Remote DataFlash logger",
            "Stream the vehicle's DataFlash log over MAVLink into a .bin file in the log folder. The vehicle must log to MAVLink: LOG_BACKEND_TYPE includes MAVLink (2), and the link must be fast enough for the log rate.",
            services)
    {
        services.Session.StateChanged += (_, state) =>
        {
            if (state != LinkState.Connected)
                services.Dispatcher.Post(() =>
                {
                    if (_writer != null)
                    {
                        StopWriter();
                        Report("The vehicle disconnected; the remote log stopped.", true);
                    }
                });
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText), nameof(CanOpen))]
    private bool _isRunning;

    public string StartStopText => IsRunning ? "Stop" : "Start remote log";

    /// <summary>The file being written, or the last one written.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen), nameof(HasFile))]
    private string? _filePath;

    public bool HasFile => FilePath != null;

    public bool CanOpen => !IsRunning && FilePath != null && File.Exists(FilePath);

    [ObservableProperty]
    private string _progress = "";

    /// <summary>LOG_BACKEND_TYPE of the vehicle, when known: a warning if it has no MAVLink bit.</summary>
    public string BackendWarning
    {
        get
        {
            if (!IsConnected || Services.Params.Get("LOG_BACKEND_TYPE") is not { } backend)
                return "";
            var value = (int)backend;
            return (value & 2) != 0 ? "" : L.Format("LOG_BACKEND_TYPE is {0}: add MAVLink (2) or the vehicle sends nothing.", value);
        }
    }

    internal RemoteLogWriter? Writer => _writer;

    public override void OnShown()
    {
        base.OnShown();
        OnPropertyChanged(nameof(BackendWarning));
    }

    /// <summary>Starts logging through a transport (tests pass a fake; the page uses the vehicle's).</summary>
    internal bool Start(IRemoteLogTransport transport, string path)
    {
        var writer = new RemoteLogWriter(transport, path);
        try
        {
            writer.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("remote log", ex);
            writer.Dispose();
            Report($"Could not start the remote log: {ex.Message}", true);
            return false;
        }

        _writer = writer;
        FilePath = path;
        IsRunning = true;
        UpdateProgress();
        Report($"Remote log started: writing {path}.");
        return true;
    }

    [RelayCommand]
    private void StartStop()
    {
        if (IsRunning)
        {
            var w = _writer;
            StopWriter();
            Report(w is { Blocks: > 0 }
                ? $"Remote log stopped: {w.Length:N0} bytes in {w.Path}."
                : "Remote log stopped. Nothing came: check LOG_BACKEND_TYPE and that the vehicle is logging (armed, or LOG_DISARMED).", w is not { Blocks: > 0 });
            return;
        }

        if (!IsConnected)
        {
            Report("Connect to a vehicle first.", true);
            return;
        }

        var name = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".bin";
        Start(new MavlinkRemoteLogTransport(Services.Session), Path.Combine(Settings.Instance.LogDir, name));
    }

    private void StopWriter()
    {
        var w = Interlocked.Exchange(ref _writer, null);
        w?.Stop();
        IsRunning = false;
        UpdateProgress(w);
        OnPropertyChanged(nameof(CanOpen));
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (FilePath is not { } path || !File.Exists(path))
            return;
        var browser = new LogBrowserViewModel(Services);
        Services.Windows.Show(browser, 1320, 840);
        _ = browser.OpenFileAsync(path);
    }

    public override void Tick()
    {
        base.Tick();
        if (_writer != null)
            UpdateProgress();
    }

    private void UpdateProgress(RemoteLogWriter? w = null)
    {
        w ??= _writer;
        if (w == null)
            return;
        Progress = w.LastBlockTicks == 0
            ? L.Format("{0:N0} blocks, {1:N0} bytes", w.Blocks, w.Length)
            : L.Format("{0:N0} blocks, {1:N0} bytes, last block {2:0.0} s ago", w.Blocks, w.Length, (Environment.TickCount64 - w.LastBlockTicks) / 1000.0);
    }
}
