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
    private bool _isStreaming;

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
        Urls = BuildUrls(settings, Streamer.RtspPort, LocalAddresses());
        StatusText = L.Format("Streaming {0}×{1} at {2} fps ({3})", settings.Width, settings.Height, settings.Fps, Streamer.EncoderName ?? "");
        Services.Notify(Urls.Count > 0 ? L.Format("Streaming: {0}", Urls[0]) : StatusText);
        return true;
    }

    public void Stop()
    {
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
