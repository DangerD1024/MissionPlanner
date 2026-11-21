using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Session;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>A playback speed button ("x0.25" … "x10").</summary>
public sealed record TlogSpeedOption(double Value)
{
    public string Label => "x" + Value.ToString("0.##", CultureInfo.InvariantCulture);

    public override string ToString() => Label;
}

/// <summary>
/// Telemetry Logs tab of the DATA screen, after Mission Planner's: load a .tlog, play / pause,
/// speed, a position slider and the log's time. Playback goes through the app's MAVLink
/// interface (<see cref="TlogPlaybackEngine"/>), so the HUD, map and quick values show the log.
/// A log can't be played while a vehicle is connected, and playback stops when a connect starts.
/// </summary>
public sealed partial class TlogPlayerViewModel : PageViewModel
{
    private const string PageTitle = "Telemetry Logs";
    private const string IdleText = "Load a telemetry log (.tlog) to replay a flight on the HUD and map.";

    private static readonly ILog Log = LogManager.GetLogger(typeof(TlogPlayerViewModel));

    private static readonly FileFilter[] Filters =
    [
        new("Telemetry log", "tlog"),
        new("MAVLink log", "mavlog"),
        new("All files", "*"),
    ];

    private readonly TlogPlaybackEngine _engine;
    private bool _syncingPosition;
    private int _shownPermyriad = -1;
    private long _shownSecond = long.MinValue;
    private TlogInfo? _shownInfo;
    private TlogPlaybackState _shownState = TlogPlaybackState.Unloaded;

    public TlogPlayerViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal TlogPlayerViewModel(AppServices services, ITlogClock? clock)
        : base(PageTitle)
    {
        Services = services;
        _engine = new TlogPlaybackEngine(services.Session.Mav, clock);
        Speeds = TlogPlaybackEngine.Speeds.Select(v => new TlogSpeedOption(v)).ToArray();
        _selectedSpeed = Speeds.First(s => s.Value == 1);
        services.Session.StateChanged += OnSessionStateChanged;
    }

    public AppServices Services { get; }

    internal TlogPlaybackEngine Engine => _engine;

    public IReadOnlyList<TlogSpeedOption> Speeds { get; }

    [ObservableProperty]
    private TlogSpeedOption _selectedSpeed;

    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private string _detailsText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseText))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadLogCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    /// <summary>Slider position, 0 … 100 % of the file. Setting it from the UI seeks.</summary>
    [ObservableProperty]
    private double _positionPercent;

    [ObservableProperty]
    private string _percentText = "";

    [ObservableProperty]
    private string _elapsedText = "--:--";

    [ObservableProperty]
    private string _durationText = "--:--";

    /// <summary>Wall-clock time in the log at the current position.</summary>
    [ObservableProperty]
    private string _logClockText = "";

    [ObservableProperty]
    private string _statusText = IdleText;

    [ObservableProperty]
    private bool _statusIsError;

    public string PlayPauseText => IsPlaying ? "Pause" : "Play";

    /// <summary>Set by the view while the user drags the position slider, so playback doesn't move it.</summary>
    public bool IsScrubbing { get; set; }

    partial void OnSelectedSpeedChanged(TlogSpeedOption value) => _engine.Speed = value.Value;

    partial void OnPositionPercentChanged(double value)
    {
        if (_syncingPosition || !_engine.IsLoaded)
            return;
        _engine.Seek(value / 100.0);
        _shownSecond = long.MinValue;
        Refresh();
    }

    private bool CanLoad => !IsBusy;

    private bool CanPlay => IsLoaded && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadLogAsync()
    {
        if (!await EnsureDisconnectedAsync())
            return;

        var path = await Services.Dialogs.OpenFileAsync("Load a telemetry log", Filters);
        if (string.IsNullOrEmpty(path))
            return;

        await LoadFileAsync(path);
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void PlayPause()
    {
        if (_engine.State == TlogPlaybackState.Playing)
            _engine.Pause();
        else if (Services.Session.State == LinkState.Disconnected)
            _engine.Play();
        Refresh();
    }

    /// <summary>"+" while a log plays (Mission Planner's tlog keys): the next faster speed.</summary>
    [RelayCommand]
    private void Faster() => StepSpeed(+1);

    /// <summary>"-" while a log plays: the next slower speed.</summary>
    [RelayCommand]
    private void Slower() => StepSpeed(-1);

    private void StepSpeed(int step)
    {
        var i = Speeds.ToList().IndexOf(SelectedSpeed) + step;
        if (i >= 0 && i < Speeds.Count)
            SelectedSpeed = Speeds[i];
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Stop()
    {
        _engine.Stop();
        FileName = "";
        FilePath = "";
        DetailsText = "";
        SetStatus(IdleText);
        Refresh();
    }

    /// <summary>Opens a log and starts playing it, as Mission Planner does after Load Log.</summary>
    internal async Task LoadFileAsync(string path)
    {
        if (!await EnsureDisconnectedAsync())
            return;

        IsBusy = true;
        SetStatus("Opening " + Path.GetFileName(path) + " …");
        try
        {
            await Task.Run(() => _engine.Load(path));

            // a connect may have started while the file was opening
            if (Services.Session.State != LinkState.Disconnected)
            {
                _engine.Stop();
                SetStatus("Not played: a vehicle is connected.", error: true);
                return;
            }

            FileName = Path.GetFileName(path);
            FilePath = path;
            DetailsText = Describe(_engine.Info, Services.Session);
            _engine.Play();
            SetStatus("");
        }
        catch (Exception ex)
        {
            Log.Warn("tlog load " + path, ex);
            // a file that fails the check before opening leaves the log that was playing alone
            if (!_engine.IsLoaded)
            {
                FileName = "";
                FilePath = "";
                DetailsText = "";
            }

            SetStatus("Could not open " + Path.GetFileName(path) + ".", error: true);
            await Services.Dialogs.ShowMessageAsync(PageTitle, "Please load a valid telemetry log.\n\n" + ex.Message, MessageKind.Error);
        }
        finally
        {
            IsBusy = false;
            _shownSecond = long.MinValue;
            _shownPermyriad = -1;
            Refresh();
        }
    }

    public override void Tick() => Refresh();

    public override void OnShown() => Refresh();

    /// <summary>Copies the engine's state into the bound properties; cheap when nothing changed.</summary>
    private void Refresh()
    {
        var state = _engine.State;
        IsLoaded = state != TlogPlaybackState.Unloaded;
        IsPlaying = state == TlogPlaybackState.Playing;

        if (state != _shownState)
        {
            if (state == TlogPlaybackState.Ended)
                SetStatus("End of log. Play starts it again.");
            else if (_shownState == TlogPlaybackState.Ended && state != TlogPlaybackState.Unloaded)
                SetStatus("");
            _shownState = state;
        }

        if (!IsLoaded)
        {
            SetPosition(0);
            PercentText = "";
            ElapsedText = "--:--";
            DurationText = "--:--";
            LogClockText = "";
            _shownPermyriad = -1;
            _shownSecond = long.MinValue;
            _shownInfo = null;
            return;
        }

        var position = _engine.Position;
        var permyriad = (int)Math.Round(position * 10000);
        if (permyriad != _shownPermyriad)
        {
            _shownPermyriad = permyriad;
            PercentText = (permyriad / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " %";
            if (!IsScrubbing)
                SetPosition(permyriad / 100.0);
        }

        var info = _engine.Info;
        if (info == null)
            return;

        if (!ReferenceEquals(info, _shownInfo))
        {
            _shownInfo = info;
            DurationText = FormatSpan(info.Duration);
        }

        var time = _engine.CurrentTime;
        if (time == DateTime.MinValue)
            return;

        var second = (long)Math.Floor((time - DateTime.UnixEpoch).TotalSeconds);
        if (second == _shownSecond)
            return;
        _shownSecond = second;
        var elapsed = time - info.Start;
        ElapsedText = FormatSpan(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
        LogClockText = time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private void SetPosition(double percent)
    {
        _syncingPosition = true;
        try
        {
            PositionPercent = percent;
        }
        finally
        {
            _syncingPosition = false;
        }
    }

    /// <summary>
    /// Raised on the thread that changed the link. Connecting is raised before the link opens,
    /// so stopping here (synchronously) hands the interface back before it reads the port.
    /// </summary>
    private void OnSessionStateChanged(object? sender, LinkState state)
    {
        if (state == LinkState.Disconnected)
            return;

        // also when a load is under way (still Unloaded): Stop waits for it, then closes the file
        var wasLoaded = _engine.State != TlogPlaybackState.Unloaded;
        _engine.Stop();
        if (!wasLoaded)
            return;

        Services.Dispatcher.Post(() =>
        {
            FileName = "";
            FilePath = "";
            DetailsText = "";
            SetStatus("Playback stopped: a vehicle is connecting.");
            Refresh();
        });
    }

    private async Task<bool> EnsureDisconnectedAsync()
    {
        if (Services.Session.State == LinkState.Disconnected)
            return true;

        await Services.Dialogs.ShowMessageAsync(PageTitle, "Disconnect from the vehicle before playing a telemetry log.", MessageKind.Warning);
        return false;
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
    }

    internal static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static string Describe(TlogInfo? info, VehicleSession session)
    {
        if (info == null)
            return "";

        var parts = new List<string>
        {
            info.Start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            FormatSize(info.Length),
        };

        var vehicle = session.Vehicle;
        if (vehicle.sysid != 0)
        {
            var firmware = session.Cs.firmware.ToString().Replace("ArduCopter2", "ArduCopter", StringComparison.Ordinal);
            parts.Add($"{firmware} sysid {vehicle.sysid}");
        }

        return string.Join("  ·  ", parts);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 20 => (bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1 << 10 => (bytes / (double)(1 << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };
}
