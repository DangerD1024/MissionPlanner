using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// CompassMot (ConfigCompassMot): compass/motor interference calibration. Start sends
/// MAV_CMD_PREFLIGHT_CALIBRATION with param6 = 1; the vehicle then streams COMPASSMOT_STATUS while
/// the pilot raises the throttle, shown as text and as interference / current against throttle.
/// Finish sends a COMMAND_ACK, which ends the calibration and saves the compensation.
/// </summary>
public sealed partial class CompassMotViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(CompassMotViewModel));

    private readonly ICalibrationLink _link;
    private readonly bool _needsSession;
    private readonly List<IDisposable> _subs = new();
    private readonly object _gate = new();
    private readonly List<string> _messages = new();
    private int _messageVersion;
    private int _shownMessageVersion = -1;
    private int _shownCount = -1;

    public CompassMotViewModel(AppServices services)
        : this(services, null, null)
    {
    }

    internal CompassMotViewModel(AppServices services, ICalibrationLink? link, IParamSource? source)
        : base("CompassMot", services, source)
    {
        _link = link ?? new VehicleCalibrationLink(services.Session);
        _needsSession = link == null;
    }

    public CompassMotRecorder Recorder { get; } = new();

    /// <summary>The calibration is running on the vehicle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool _isRunning;

    public string ButtonText => IsRunning ? "Finish" : "Start";

    [ObservableProperty]
    private string _statusText = "Compass Motor Calibration";

    [ObservableProperty]
    private string _messagesText = "";

    /// <summary>Samples by throttle for the graph; replaced when new data arrives.</summary>
    [ObservableProperty]
    private IReadOnlyList<CompassMotSample> _points = [];

    [ObservableProperty]
    private string _ratingText = "";

    [ObservableProperty]
    private InterferenceRating _rating;

    [ObservableProperty]
    private ParamField? _compensationType;

    [ObservableProperty]
    private IReadOnlyList<ParamField> _compensation = [];

    protected override void Build()
    {
        CompensationType = Field("COMPASS_MOTCT", new ParamFieldSpec { Kind = ParamEditorKind.Options, Label = "Compensation type" });
        Compensation = new[] { "COMPASS_MOT_X", "COMPASS_MOT_Y", "COMPASS_MOT_Z", "COMPASS_MOT2_X", "COMPASS_MOT2_Y", "COMPASS_MOT2_Z" }
            .Select(n => Field(n, new ParamFieldSpec { Kind = ParamEditorKind.Number }))
            .OfType<ParamField>()
            .ToList();
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsRunning)
            await FinishAsync();
        else
            await StartAsync();
    }

    private async Task StartAsync()
    {
        if (_needsSession && !Services.Session.IsConnected)
            return;
        if (!await Services.Dialogs.ConfirmAsync("Compass/motor calibration",
                "The motors run at the throttle you give them.\n\n" +
                "• Propellers on, vehicle tied down or held firmly; keep everyone clear of the propellers.\n" +
                "• After Start, raise the throttle slowly to 50-75 % over 5-10 seconds, hold a few seconds, then lower it to zero.\n" +
                "• Press Finish to end the calibration and save the compensation.",
                "Start"))
            return;

        Recorder.Clear();
        lock (_gate)
        {
            _messages.Clear();
            _messageVersion++;
        }

        Unsubscribe();
        _subs.Add(_link.Subscribe(MAVLink.MAVLINK_MSG_ID.COMPASSMOT_STATUS,
            msg => Recorder.Add(CompassMotSample.From(msg.ToStructure<MAVLink.mavlink_compassmot_status_t>()))));
        _subs.Add(_link.Subscribe(MAVLink.MAVLINK_MSG_ID.STATUSTEXT, msg =>
        {
            var text = StatusTextDecoder.Decode(msg);
            lock (_gate)
            {
                _messages.Add(text);
                if (_messages.Count > 200)
                    _messages.RemoveAt(0);
                _messageVersion++;
            }
        }));

        try
        {
            // the library sends the command twice and does not wait: the vehicle answers with the status stream
            await _link.CommandAsync(MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 0, 0, 0, 0, 0, 1, 0);
            IsRunning = true;
            StatusText = "Waiting for the vehicle… raise the throttle slowly.";
        }
        catch (Exception ex)
        {
            Log.Warn("compassmot start", ex);
            Unsubscribe();
            await Services.Dialogs.ShowMessageAsync("CompassMot", "Compassmot requires AC 3.2+ (" + ex.Message + ")", MessageKind.Error);
        }
    }

    private async Task FinishAsync()
    {
        IsRunning = false;
        try
        {
            await SendAckAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("compassmot finish", ex);
            Status = "Could not send Finish: " + ex.Message;
        }

        // the vehicle saves COMPASS_MOT_* when it stops; read them again
        if (Services.Session.IsConnected)
            _ = RereadCompensationAsync();
    }

    /// <summary>Any COMMAND_ACK ends compassmot on the vehicle (Mission Planner's SendAck, sent twice).</summary>
    private async Task SendAckAsync()
    {
        var ack = new MAVLink.mavlink_command_ack_t
        {
            command = (ushort)MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION,
            result = 0,
        };
        await _link.SendAsync(ack);
        await Task.Delay(20);
        await _link.SendAsync(ack);
    }

    private async Task RereadCompensationAsync()
    {
        await Task.Delay(1500);
        var v = Services.Session.Vehicle;
        var mav = Services.Session.Mav;
        foreach (var f in Compensation.Prepend(CompensationType).OfType<ParamField>())
        {
            try
            {
                await Task.Run(() => mav.GetParam(v.sysid, v.compid, f.Name));
            }
            catch (Exception ex)
            {
                Log.Debug("reread " + f.Name, ex);
            }
        }

        Services.Dispatcher.Post(RefreshFields);
    }

    public override void OnHidden()
    {
        // leaving the page stops a running calibration, as Mission Planner does
        if (IsRunning)
        {
            IsRunning = false;
            _ = SendAckSafeAsync();
        }

        Unsubscribe();
        base.OnHidden();
    }

    private async Task SendAckSafeAsync()
    {
        try
        {
            await SendAckAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("compassmot stop", ex);
        }
    }

    private void Unsubscribe()
    {
        foreach (var s in _subs)
            s.Dispose();
        _subs.Clear();
    }

    protected override void UpdateLive()
    {
        var count = Recorder.Count;
        if (count != _shownCount)
        {
            _shownCount = count;
            Points = Recorder.ByThrottle();
            if (Recorder.Latest is { } latest)
                StatusText = CompassMotRecorder.Describe(latest);
            Rating = CompassMotRecorder.Rate(Recorder.MaxInterference, count > 0);
            RatingText = count > 0
                ? $"Highest interference: {Recorder.MaxInterference.ToString("0", CultureInfo.InvariantCulture)} %. {CompassMotRecorder.RatingText(Rating)}"
                : "";
        }

        lock (_gate)
        {
            if (_messageVersion != _shownMessageVersion)
            {
                _shownMessageVersion = _messageVersion;
                MessagesText = string.Join("\n", _messages);
            }
        }
    }
}
