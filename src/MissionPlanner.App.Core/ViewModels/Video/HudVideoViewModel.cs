using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using log4net;
using MissionPlanner.App.Core.Video;

namespace MissionPlanner.App.Core.ViewModels.Video;

/// <summary>
/// Video behind the HUD: the saved sources, the running stream and its status line, and the
/// actions of the HUD's Video menu (start, stop, pick a source, settings dialog, raw GStreamer
/// pipeline). One per app (<see cref="For"/>), so the stream keeps running while the Flight Data
/// view is rebuilt.
/// </summary>
public sealed partial class HudVideoViewModel : ObservableObject, IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(HudVideoViewModel));
    private static readonly ConditionalWeakTable<AppServices, HudVideoViewModel> Instances = new();

    private readonly IVideoSettingsStore _store;
    private readonly VideoSettings _settings;
    private VideoSourceConfig? _playing;
    private string? _startError;

    public HudVideoViewModel(AppServices services, IVideoSettingsStore store, IVideoStream stream)
    {
        Services = services;
        _store = store;
        Stream = stream;
        _settings = VideoSettings.Load(store);
        stream.StateChanged += (_, _) => services.Dispatcher.Post(SyncState);
        _threeD = bool.TryParse(store.Get(ThreeDKey), out var on) && on;
        _threeDFallback = bool.TryParse(store.Get(ThreeDFallbackKey), out var fallback) && fallback;
    }

    /// <summary>The app's instance, created (and auto-started if so configured) on first use.</summary>
    public static HudVideoViewModel For(AppServices services) =>
        Instances.GetValue(services, s =>
        {
            var vm = new HudVideoViewModel(s, new AppVideoSettingsStore(), new VideoStream());
            if (vm.AutoStart)
                vm.Start();
            return vm;
        });

    public AppServices Services { get; }

    public IVideoStream Stream { get; }

    public IReadOnlyList<VideoSourceConfig> Sources => _settings.Sources;

    public VideoSourceConfig? Selected => _settings.Selected;

    /// <summary>The source currently running (or trying to), if any.</summary>
    public VideoSourceConfig? Playing => _playing;

    /// <summary>The pipeline last typed into "Set GStreamer source…".</summary>
    public string? LastPipeline => _settings.LastPipeline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(ShowsFrame), nameof(UsesSynthetic))]
    private VideoStreamState _state;

    /// <summary>"Connecting video…", "No video", the error...; empty while playing or stopped.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>True from Start until Stop (including while reconnecting or failed).</summary>
    public bool IsActive => State != VideoStreamState.Stopped;

    /// <summary>Whether the HUD should draw the last frame.</summary>
    public bool ShowsFrame => VideoLayout.ShowsFrame(State);

    public bool AutoStart
    {
        get => _settings.AutoStart;
        set
        {
            if (_settings.AutoStart == value)
                return;
            _settings.AutoStart = value;
            Save();
            OnPropertyChanged();
        }
    }

    public VideoAspect Aspect
    {
        get => _settings.Aspect;
        set
        {
            if (_settings.Aspect == value)
                return;
            _settings.Aspect = value;
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Starts the selected source.</summary>
    public bool Start() => Start(Selected);

    /// <summary>Builds the source's pipeline and starts it; an invalid source shows as a video error.</summary>
    public bool Start(VideoSourceConfig? source)
    {
        if (source == null)
        {
            Fail("no video source is set up");
            return false;
        }

        string pipeline;
        try
        {
            pipeline = VideoPipelineBuilder.Build(source);
        }
        catch (ArgumentException ex)
        {
            Stream.Stop();
            _playing = source.Clone();
            Fail(ex.Message);
            return false;
        }

        // the camera picture replaces the 3D view
        ThreeD = false;
        Log.Info($"video: {source.DisplayName}: {pipeline}");
        _startError = null;
        _playing = source.Clone();
        Stream.Start(pipeline);
        SyncState();
        return true;
    }

    public void Stop()
    {
        _startError = null;
        _playing = null;
        Stream.Stop();
        SyncState();
    }

    /// <summary>Makes a saved source the selected one and starts it (the menu's source list).</summary>
    public void Select(string id, bool start = true)
    {
        if (_settings.Sources.All(s => s.Id != id))
            return;
        _settings.SelectedId = id;
        Save();
        OnPropertyChanged(nameof(Selected));
        if (start)
            Start();
    }

    /// <summary>
    /// Replaces the saved sources and options (the settings dialog's Save). A running stream
    /// restarts if its source changed; <paramref name="play"/> starts the selected source.
    /// </summary>
    public void Apply(IEnumerable<VideoSourceConfig> sources, string? selectedId, bool autoStart, VideoAspect aspect, bool play)
    {
        _settings.Sources = sources.Select(s => s.Clone()).ToList();
        _settings.SelectedId = _settings.Sources.Any(s => s.Id == selectedId) ? selectedId : _settings.Sources.FirstOrDefault()?.Id;
        _settings.AutoStart = autoStart;
        _settings.Aspect = aspect;
        Save();
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(AutoStart));
        OnPropertyChanged(nameof(Aspect));

        if (play)
        {
            Start();
        }
        else if (IsActive && _playing != null)
        {
            // keep showing what was on; restart if it was edited, stop if it was deleted
            var now = _settings.Sources.FirstOrDefault(s => s.Id == _playing.Id);
            if (now == null)
                Stop();
            else if (!now.SameStream(_playing))
                Start(now);
            else
                _playing = now.Clone();
        }
    }

    /// <summary>The settings dialog (sources, options); applies what the user saves.</summary>
    public Task OpenSettingsAsync() => Services.Windows.ShowDialogAsync(new VideoSettingsViewModel(this), 960, 660);

    /// <summary>
    /// Mission Planner's "Set GStreamer source": asks for a raw pipeline, keeps it as the
    /// "GStreamer pipeline" source, selects and starts it.
    /// </summary>
    public async Task SetGStreamerSourceAsync()
    {
        var initial = _settings.LastPipeline;
        if (string.IsNullOrWhiteSpace(initial))
        {
            initial = Selected is { } sel && VideoPipelineBuilder.Validate(sel) == null
                ? VideoPipelineBuilder.Build(sel)
                : VideoPipelineBuilder.ExamplePipeline;
        }

        var text = await Services.Dialogs.PromptAsync("GStreamer source",
            "Enter the source pipeline. It must end with\n" + VideoPipelineBuilder.SinkTail +
            "\n(added for you when there is no appsink named outsink).", initial);
        if (string.IsNullOrWhiteSpace(text))
            return;
        UsePipeline(text.Trim());
    }

    /// <summary>Saves a raw pipeline as the "GStreamer pipeline" source, selects and starts it.</summary>
    public void UsePipeline(string pipeline)
    {
        var source = _settings.Sources.FirstOrDefault(s => s.Id == VideoSettings.QuickPipelineId);
        if (source == null)
        {
            source = new VideoSourceConfig { Id = VideoSettings.QuickPipelineId, Name = "GStreamer pipeline", Type = VideoSourceType.Custom };
            _settings.Sources.Add(source);
        }

        source.Type = VideoSourceType.Custom;
        source.Pipeline = pipeline;
        _settings.LastPipeline = pipeline;
        _settings.SelectedId = source.Id;
        Save();
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(Selected));
        Start(source);
    }

    public void Dispose() => Stream.Dispose();

    private void Fail(string message)
    {
        _startError = message;
        SyncState();
    }

    private void SyncState()
    {
        if (_startError != null)
        {
            State = VideoStreamState.Failed;
            StatusText = VideoLayout.StatusText(VideoStreamState.Failed, _startError);
            return;
        }

        var state = Stream.State;
        State = state;
        StatusText = VideoLayout.StatusText(state, Stream.Error);
    }

    private void Save() => _settings.Save(_store);
}
