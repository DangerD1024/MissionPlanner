using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Localization;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Video;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>
/// Streaming the HUD picture to other apps (one per app): the settings, start/stop, the addresses
/// to give other apps, and errors. The frames themselves come from the UI layer, which renders the
/// HUD off screen at the stream size and calls <see cref="Push"/> at <see cref="StreamSettings.Fps"/>.
/// </summary>
public sealed partial class StreamingViewModel : ObservableObject, IDisposable
{
    private static readonly ConditionalWeakTable<AppServices, StreamingViewModel> Instances = new();

    private readonly IVideoSettingsStore _store;

    public StreamingViewModel(AppServices services, IVideoSettingsStore store, IHudStreamer streamer)
    {
        Services = services;
        _store = store;
        Streamer = streamer;
        Settings = StreamSettings.Load(store);
        streamer.StateChanged += (_, _) => services.Dispatcher.Post(SyncError);
    }

    public static StreamingViewModel For(AppServices services) =>
        Instances.GetValue(services, s => new StreamingViewModel(s, new AppVideoSettingsStore(), new HudStreamer()));

    public AppServices Services { get; }

    public IHudStreamer Streamer { get; }

    /// <summary>The saved settings (a running stream keeps the ones it started with).</summary>
    public StreamSettings Settings { get; private set; }

    /// <summary>The settings of the running stream (size, fps and OSD for the frame source).</summary>
    public StreamSettings? Active { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProducing))]
    private bool _isStreaming;

    /// <summary>Writing the HUD to <see cref="RecordingPath"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProducing))]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingPath = "";

    /// <summary>Frames are wanted: streaming, recording, or both.</summary>
    public bool IsProducing => IsStreaming || IsRecording;

    // started only for a recording: stopping the recording stops it too
    private bool _recordOnly;

    /// <summary>Mission Planner's "Record HUD to AVI": the HUD as drawn (with the OSD) into an .mkv in the log folder.</summary>
    public bool StartRecording(string? path = null)
    {
        if (IsRecording)
            return true;
        path ??= Path.Combine(MissionPlanner.Utilities.Settings.Instance.LogDir,
            "HUD " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + ".mkv");
        try
        {
            if (!Streamer.IsRunning)
            {
                var settings = Settings.Clone();
                settings.Mode = StreamMode.RecordOnly;
                Streamer.Start(settings);
                Active = settings;
                _recordOnly = true;
            }

            Streamer.StartRecording(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            if (_recordOnly)
            {
                Streamer.Stop();
                Active = null;
                _recordOnly = false;
            }

            Services.Notify(L.Tr("Recording failed: ") + ex.Message, MessageKind.Error);
            return false;
        }

        RecordingPath = path;
        IsRecording = true;
        Services.Notify(L.Format("Recording the HUD to {0}", path));
        return true;
    }

    public void StopRecording()
    {
        if (!IsRecording)
            return;
        Streamer.StopRecording();
        IsRecording = false;
        if (_recordOnly)
        {
            _recordOnly = false;
            Streamer.Stop();
            Active = null;
        }

        Services.Notify(L.Format("Saved {0}", RecordingPath));
    }

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>Addresses other apps open: rtsp://… for each of this computer's IPv4 addresses, then udp://….</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _urls = [];

    /// <summary>Draw the HUD (OSD) over the picture in the stream; applies at once, also while streaming.</summary>
    public bool ShowOsd
    {
        get => Settings.ShowOsd;
        set
        {
            if (Settings.ShowOsd == value)
                return;
            Settings.ShowOsd = value;
            if (Active != null)
                Active.ShowOsd = value;
            Settings.Save(_store);
            OnPropertyChanged();
        }
    }

    /// <summary>Starts with the saved settings; problems are shown as a notification and in <see cref="StatusText"/>.</summary>
    public bool Start()
    {
        // a recording on its own frame source is saved; a new one carries on with the stream
        var resumeRecording = IsRecording && _recordOnly;
        if (resumeRecording)
            StopRecording();

        var settings = Settings.Clone();
        try
        {
            Streamer.Start(settings);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Active = null;
            IsStreaming = false;
            StatusText = L.Tr("Streaming failed: ") + ex.Message;
            Services.Notify(StatusText, MessageKind.Error);
            return false;
        }

        Active = settings;
        IsStreaming = true;
        if (resumeRecording)
            StartRecording();
        Urls = BuildUrls(settings, Streamer.RtspPort, LocalAddresses());
        StatusText = L.Format("Streaming {0}×{1} at {2} fps ({3})", settings.Width, settings.Height, settings.Fps, Streamer.EncoderName ?? "");
        Services.Notify(Urls.Count > 0 ? L.Format("Streaming: {0}", Urls[0]) : StatusText);
        return true;
    }

    public void Stop()
    {
        // the recording reads the stream: it ends (and is saved) with it
        StopRecording();
        Streamer.Stop();
        Active = null;
        IsStreaming = false;
        Urls = [];
        StatusText = L.Tr("Streaming stopped.");
    }

    /// <summary>One BGRA frame of <see cref="Active"/>'s size (from the UI layer's renderer).</summary>
    public void Push(byte[] bgra) => Streamer.Push(bgra);

    /// <summary>Saves new settings; a running stream restarts with them.</summary>
    public void Apply(StreamSettings settings)
    {
        var osdChanged = settings.ShowOsd != Settings.ShowOsd;
        Settings = settings.Clone();
        Settings.Save(_store);
        if (osdChanged)
            OnPropertyChanged(nameof(ShowOsd));
        if (IsStreaming)
        {
            Stop();
            Start();
        }
    }

    public Task OpenSettingsAsync() => Services.Windows.ShowDialogAsync(new StreamSettingsViewModel(this), 640, 640);

    private void SyncError()
    {
        if (IsStreaming && Streamer.Error is { } error)
            StatusText = L.Tr("Stream error: ") + error;
    }

    /// <summary>rtsp://address:port/path for each address (localhost last), then the UDP target.</summary>
    public static IReadOnlyList<string> BuildUrls(StreamSettings s, int rtspPort, IEnumerable<string> addresses)
    {
        if (s.UsesShm)
            return [$"shm://{StreamSettings.ShmPath} (raw BGRA, same machine only)"];
        var urls = new List<string>();
        if (s.UsesRtsp)
        {
            var port = rtspPort > 0 ? rtspPort : s.RtspPort;
            foreach (var a in addresses.Append("127.0.0.1").Distinct())
                urls.Add($"rtsp://{a}:{port}/{s.RtspPath.Trim('/')}");
        }

        if (s.UsesUdp)
            urls.Add($"udp://{s.UdpHost}:{s.UdpPort} (RTP H.264)");
        return urls;
    }

    /// <summary>This computer's IPv4 addresses on interfaces that are up (not loopback).</summary>
    public static IEnumerable<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        list.Add(ua.Address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // no interface list: localhost only
        }

        return list;
    }

    public void Dispose() => Streamer.Dispose();
}
