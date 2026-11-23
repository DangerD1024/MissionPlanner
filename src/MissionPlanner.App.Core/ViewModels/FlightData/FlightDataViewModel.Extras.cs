using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>
/// The remaining Flight Data tabs (PreFlight, Gauges, Transponder, Servo/Relay, Aux Function,
/// Payload), ADS-B traffic on the map and speech. Each tab has its own view-model, ticked only
/// while its tab is selected; traffic is gathered on the reader thread and drawn at 2 Hz.
/// </summary>
public sealed partial class FlightDataViewModel : IFlightCommands
{
    private const int TrafficEvery = 10; // 2 Hz

    private PageViewModel? _activeTab;
    private long _trafficVersion = -1;
    private bool _hadTraffic;

    public PreFlightViewModel PreFlight { get; private set; } = null!;

    public GaugesViewModel Gauges { get; private set; } = null!;

    public TransponderViewModel Transponder { get; private set; } = null!;

    public ServoRelayViewModel ServoRelay { get; private set; } = null!;

    public AuxFunctionViewModel AuxFunctions { get; private set; } = null!;

    public PayloadViewModel Payload { get; private set; } = null!;

    /// <summary>ADS-B traffic heard on the link (ADSB_VEHICLE).</summary>
    public AdsbTrafficStore Traffic { get; } = new();

    /// <summary>Photos the vehicle took this flight (Mission Planner's camera overlap).</summary>
    public PhotoStore Photos { get; } = new();

    private long _photosVersion = -1;

    /// <summary>Map right-click > Camera Overlap: draws what each photo covers (remembered).</summary>
    public bool ShowCameraOverlap
    {
        get => MapModel.ShowPhotos;
        set
        {
            MapModel.ShowPhotos = value;
            Settings.Instance["next_cam_overlap"] = value.ToString();
            MapModel.Version++;
            OnPropertyChanged();
        }
    }

    /// <summary>Asks for the camera's field of view (horizontal;vertical degrees); new photos use it.</summary>
    public async Task SetCameraFovAsync()
    {
        var current = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{CameraFootprints.HorizontalFov:0.#};{CameraFootprints.VerticalFov:0.#}");
        var answer = await Services.Dialogs.PromptAsync("Camera field of view",
            "Horizontal;vertical field of view in degrees (e.g. 62.2;48.8). New photos use it.", current);
        if (answer == null)
            return;
        var parts = answer.Split(';', ',');
        if (parts.Length != 2 || !TryParseNumber(parts[0], out var h) || !TryParseNumber(parts[1], out var v) || h is <= 1 or >= 179 || v is <= 1 or >= 179)
        {
            Report("Enter two angles between 1 and 179 degrees, as horizontal;vertical", true);
            return;
        }

        CameraFootprints.HorizontalFov = h;
        CameraFootprints.VerticalFov = v;
    }

    private void UpdatePhotos()
    {
        if (Photos.Version == _photosVersion)
            return;
        _photosVersion = Photos.Version;
        MapModel.Photos = Photos.Snapshot();
        MapModel.Version++;
    }

    private void InitExtras()
    {
        PreFlight = new PreFlightViewModel(Services);
        Gauges = new GaugesViewModel(Services);
        Transponder = new TransponderViewModel(Services, this);
        ServoRelay = new ServoRelayViewModel(Services, this);
        AuxFunctions = new AuxFunctionViewModel(Services, this);
        Payload = new PayloadViewModel(Services, this);

        // reader thread: only a dictionary update under a short lock
        MapModel.ShowPhotos = Settings.Instance.GetBoolean("next_cam_overlap", false);
        Services.Session.Mav.OnPacketReceived += (_, msg) =>
        {
            Traffic.OnPacket(msg, DateTime.UtcNow);
            Photos.OnPacket(msg);
        };

        AppSpeech.Install(Services.Session);
        _speechEnabled = SpeechOptions.Enabled;
    }

    /// <summary>The view-model of a tab that has one (the extra tabs and the logs).</summary>
    private PageViewModel? TabPage(int index) => (FlightDataTab)index switch
    {
        FlightDataTab.PreFlight => PreFlight,
        FlightDataTab.Gauges => Gauges,
        FlightDataTab.Transponder => Transponder,
        FlightDataTab.Servo => ServoRelay,
        FlightDataTab.AuxFunction => AuxFunctions,
        FlightDataTab.Payload => Payload,
        _ => null,
    };

    private void SwitchTab(int index)
    {
        var next = TabPage(index);
        if (ReferenceEquals(next, _activeTab))
            return;
        if (_shown)
            _activeTab?.OnHidden();
        _activeTab = next;
        if (_shown)
            _activeTab?.OnShown();
    }

    private void ShowExtras(bool shown)
    {
        if (shown)
            _activeTab?.OnShown();
        else
            _activeTab?.OnHidden();
    }

    private void TickExtras(long tick)
    {
        _activeTab?.Tick();
        if (tick % TrafficEvery == 3)
            UpdateTraffic();
    }

    /// <summary>Copies the traffic into the map model when it changed (positions, ageing, removal).</summary>
    private void UpdateTraffic()
    {
        UpdatePhotos();
        var version = Traffic.Version;
        var any = Traffic.Count > 0;
        if (!any && !_hadTraffic && version == _trafficVersion)
            return;

        var list = Traffic.Snapshot(DateTime.UtcNow);
        _trafficVersion = Traffic.Version;
        _hadTraffic = list.Count > 0;
        MapModel.Traffic = list;
        MapModel.Version++;
    }

    // ---- IFlightCommands ----------------------------------------------------------------------

    Task IFlightCommands.RunAsync(string what, Func<MAVLinkInterface, uint, byte, CurrentState, bool> action, string okText,
        string failText) => RunAsync(what, action, okText, failText);

    void IFlightCommands.Report(string text, bool isError) => Report(text, isError);

    // ---- map ----------------------------------------------------------------------------------

    /// <summary>Map "Point Camera Here".</summary>
    public Task PointCameraHereAsync(double lat, double lng) => Payload.PointCameraAtAsync(lat, lng);

    // ---- speech -------------------------------------------------------------------------------

    private bool _speechEnabled;

    /// <summary>Mission Planner's "Enable Speech" (settings key speechenable).</summary>
    public bool SpeechEnabled
    {
        get => _speechEnabled;
        set
        {
            if (!SetProperty(ref _speechEnabled, value))
                return;
            Settings.Instance[SpeechOptions.EnableKey] = value.ToString();
            AppSpeech.ApplySettings();
            if (!value)
                AppSpeech.Engine.SpeakAsyncCancelAll();
        }
    }

    [RelayCommand]
    private async Task OpenSpeechSettingsAsync()
    {
        await Services.Windows.ShowDialogAsync(new SpeechSettingsViewModel(Services), 760, 820);
        _speechEnabled = SpeechOptions.Enabled;
        OnPropertyChanged(nameof(SpeechEnabled));
    }
}
