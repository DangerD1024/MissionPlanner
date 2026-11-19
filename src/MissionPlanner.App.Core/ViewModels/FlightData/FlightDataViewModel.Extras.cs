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

    private void InitExtras()
    {
        PreFlight = new PreFlightViewModel(Services);
        Gauges = new GaugesViewModel(Services);
        Transponder = new TransponderViewModel(Services, this);
        ServoRelay = new ServoRelayViewModel(Services, this);
        AuxFunctions = new AuxFunctionViewModel(Services, this);
        Payload = new PayloadViewModel(Services, this);

        // reader thread: only a dictionary update under a short lock
        Services.Session.Mav.OnPacketReceived += (_, msg) => Traffic.OnPacket(msg, DateTime.UtcNow);

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
