using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// What Basic and Extended Tuning share: edits queued until Write Params (with Mission Planner's
/// "more than doubled" question), Refresh Screen (read this page's values again), Refresh Params,
/// and keeping the page in step with the vehicle's list and documentation.
/// </summary>
public abstract partial class TuningPageViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TuningPageViewModel));

    private bool _shown;
    private bool _dirty = true;
    private int _ticks;

    protected TuningPageViewModel(string title, AppServices services)
        : base(title)
    {
        Services = services;
        MetaSource = services.Params.Meta;
        services.Params.ParamsChanged += (_, _) => Invalidate();
        services.Params.MetadataChanged += (_, _) => Invalidate();
        Edits.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(Summary));
        };
    }

    public AppServices Services { get; }

    public ParamEditBuffer Edits { get; } = new();

    /// <summary>Parameter documentation (replaceable in tests).</summary>
    internal Func<string, ParamMeta?> MetaSource { get; set; }

    /// <summary>The acsimplepids.xml to read (null: the one next to the executable).</summary>
    internal string? XmlPath { get; set; }

    protected Firmwares Firmware => Services.Session.Cs.firmware;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshScreenCommand), nameof(RefreshParamsCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshScreenCommand), nameof(RefreshParamsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int _shownCount;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _vehicleText = "";

    public int PendingCount => Edits.Count;

    public bool HasPending => Edits.Count > 0;

    /// <summary>What <see cref="ShownCount"/> counts ("parameter", "slider").</summary>
    protected virtual string ItemNoun => "parameter";

    public string Summary
    {
        get
        {
            var s = $"{ShownCount} {ItemNoun}{(ShownCount == 1 ? "" : "s")}";
            if (Edits.Count > 0)
                s += $" · {Edits.Count} unsaved";
            return s;
        }
    }

    public virtual string EmptyText => !Services.Params.HasParams
        ? "Connect to a vehicle and download its parameters to tune it."
        : "None of the tuning parameters of this page were found on the vehicle.";

    /// <summary>The parameters on screen, for Refresh Screen.</summary>
    protected abstract IEnumerable<string> ScreenParams { get; }

    /// <summary>Builds the page from the parameter list and documentation.</summary>
    internal abstract void Rebuild();

    /// <summary>Copies the vehicle's values and the queued edits into the editors.</summary>
    internal abstract void SyncValues();

    /// <summary>The documentation of a parameter, null when unknown.</summary>
    protected ParamMeta? Meta(string name) => MetaSource(name);

    /// <summary>The queued value of a parameter, dropped if the vehicle now has it.</summary>
    protected double? PendingFor(string name, double current) =>
        Edits.TryGet(name, out var p) && Edits.Set(name, p, current) ? p : null;

    /// <summary>Doubling means nothing for lists and bitmasks (gains with suggested values still count).</summary>
    internal bool IsEnumerated(string name) =>
        Meta(name) is { } m && (m.Bitmask.Count > 0 || (m.Values.Count > 0 && !TuningEditors.IsSuggestionList(m)));

    protected static string FirmwareName(Firmwares fw) => fw switch
    {
        Firmwares.ArduCopter2 => "ArduCopter",
        Firmwares.ArduPlane => "ArduPlane",
        Firmwares.ArduRover => "ArduRover",
        _ => fw.ToString(),
    };

    public override void OnShown()
    {
        _shown = true;
        IsConnected = Services.Session.IsConnected;
        if (_dirty)
            RebuildNow();
        else
            SyncValues();
    }

    public override void OnHidden() => _shown = false;

    public override void Tick()
    {
        IsConnected = Services.Session.IsConnected;

        // values change behind the page (other pages, the transmitter tuning knob): follow them once a second
        if (++_ticks % 20 == 0 && !IsBusy)
            SyncValues();
    }

    private void Invalidate()
    {
        _dirty = true;
        if (_shown)
            RebuildNow();
    }

    private void RebuildNow()
    {
        _dirty = false;
        try
        {
            Rebuild();
        }
        catch (Exception ex)
        {
            Log.Error("tuning page " + Title, ex);
            StatusText = "Could not build the page: " + ex.Message;
        }

        OnPropertyChanged(nameof(EmptyText));
    }

    private bool CanWrite => IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        IsBusy = true;
        try
        {
            var written = await TuningWriteFlow.WriteAsync(Services, Edits, IsEnumerated, s => StatusText = s);
            SyncValues();
            if (written.Count > 0)
                StatusText = $"Wrote {written.Count} parameter{(written.Count == 1 ? "" : "s")}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reads this page's parameters from the vehicle again, one by one (Mission Planner's "Refresh Screen").</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task RefreshScreenAsync()
    {
        var names = ScreenParams.Distinct().ToList();
        if (names.Count == 0)
            return;

        IsBusy = true;
        try
        {
            var session = Services.Session;
            var (sysid, compid) = (session.Vehicle.sysid, session.Vehicle.compid);
            var dispatcher = Services.Dispatcher;
            var (read, failed) = await Task.Run(() =>
            {
                int ok = 0, missed = 0, inARow = 0;
                foreach (var name in names)
                {
                    dispatcher.Post(() => StatusText = $"Reading {name} ({ok + missed + 1}/{names.Count})…");
                    try
                    {
                        session.Mav.GetParam(sysid, compid, name);
                        ok++;
                        inARow = 0;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("read " + name, ex);
                        missed++;

                        // a vehicle that stopped answering would take three seconds per value
                        if (++inARow >= 3)
                            break;
                    }
                }

                return (ok, missed);
            });

            SyncValues();
            StatusText = failed == 0
                ? $"Read {read} parameters from the vehicle."
                : $"Read {read} of {names.Count} parameters; the vehicle did not answer for {failed}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task RefreshParamsAsync()
    {
        IsBusy = true;
        try
        {
            if (await ParamWriteFlow.RefreshAsync(Services, s => StatusText = s))
                StatusText = "Parameters refreshed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DiscardEdits()
    {
        Edits.Clear();
        SyncValues();
        StatusText = "Edits discarded.";
    }
}
