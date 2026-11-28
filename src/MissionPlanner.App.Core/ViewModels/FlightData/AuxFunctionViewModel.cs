using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>An RC switch position for MAV_CMD_DO_AUX_FUNCTION.</summary>
public enum AuxSwitchPosition
{
    Low = 0,
    Middle = 1,
    High = 2,
}

public static class AuxCommands
{
    /// <summary>MAV_CMD_DO_AUX_FUNCTION: p1 the RCn_OPTION function, p2 the switch position (0 low, 1 middle, 2 high).</summary>
    public static VehicleCommand AuxFunction(int function, AuxSwitchPosition position) =>
        new(MAVLink.MAV_CMD.DO_AUX_FUNCTION, function, (int)position);

    /// <summary>The RCn_OPTION table without "Do Nothing", by number.</summary>
    public static IReadOnlyList<KeyValuePair<int, string>> Clean(IEnumerable<KeyValuePair<int, string>> options) =>
        options.Where(o => o.Key > 0 && !string.IsNullOrWhiteSpace(o.Value))
            .GroupBy(o => o.Key)
            .Select(g => g.First())
            .OrderBy(o => o.Key)
            .ToList();

    /// <summary>A few common functions, for when no parameter documentation is available.</summary>
    public static IReadOnlyList<KeyValuePair<int, string>> Fallback { get; } =
    [
        new(4, "RTL"), new(9, "Camera Trigger"), new(11, "Fence"), new(16, "Auto"), new(19, "Gripper"),
        new(28, "Relay On/Off"), new(31, "Parachute Release"), new(41, "ArmDisarm"), new(56, "Loiter"),
        new(153, "ArmDisarm (4.2 and higher)"), new(166, "Camera Record Video"), new(174, "Camera Image Tracking"),
    ];
}

/// <summary>One aux function: Low / Mid / High, the last position sent, and a pin to keep it on top.</summary>
public sealed partial class AuxFunctionRow : ObservableObject
{
    private readonly AuxFunctionViewModel _owner;

    internal AuxFunctionRow(AuxFunctionViewModel owner, int id, string name, bool pinned)
    {
        _owner = owner;
        Id = id;
        Name = name;
        _isPinned = pinned;
    }

    public int Id { get; }

    public string Name { get; }

    public string IdText => Id.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLow), nameof(IsMiddle), nameof(IsHigh))]
    private AuxSwitchPosition? _lastPosition;

    public bool IsLow => LastPosition == AuxSwitchPosition.Low;

    public bool IsMiddle => LastPosition == AuxSwitchPosition.Middle;

    public bool IsHigh => LastPosition == AuxSwitchPosition.High;

    [RelayCommand]
    private Task Low() => _owner.Send(this, AuxSwitchPosition.Low);

    [RelayCommand]
    private Task Middle() => _owner.Send(this, AuxSwitchPosition.Middle);

    [RelayCommand]
    private Task High() => _owner.Send(this, AuxSwitchPosition.High);

    [RelayCommand]
    private void TogglePin()
    {
        IsPinned = !IsPinned;
        _owner.OnPinChanged();
    }
}

/// <summary>
/// Aux Function tab, after Mission Planner's: every RC auxiliary function (the RCn_OPTION values
/// from the parameter documentation) with Low / Mid / High buttons that send
/// MAV_CMD_DO_AUX_FUNCTION, as if an RC switch had moved. Searchable; pinned ones stay on top.
/// </summary>
public sealed partial class AuxFunctionViewModel : PageViewModel
{
    private const string PinnedKey = "next_aux_pinned";

    private static readonly ILog Log = LogManager.GetLogger(typeof(AuxFunctionViewModel));

    private readonly IFlightCommands _commands;
    private List<AuxFunctionRow> _all = new();
    private Firmwares? _loadedFor;
    private bool _fromVehicleDocs;
    private int _request;
    private long _tick;

    public AuxFunctionViewModel(AppServices services, IFlightCommands commands)
        : base("Aux Function")
    {
        Services = services;
        _commands = commands;
        services.Params.MetadataChanged += (_, _) => Load(force: true);
    }

    public AppServices Services { get; }

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private IReadOnlyList<AuxFunctionRow> _rows = [];

    [ObservableProperty]
    private string _sourceText = "";

    public bool CanCommand => _commands.CanCommand;

    partial void OnFilterChanged(string value) => ApplyFilter();

    public override void OnShown()
    {
        Load(force: false);
        OnPropertyChanged(nameof(CanCommand));
    }

    public override void Tick()
    {
        if (_tick++ % 10 != 0)
            return;
        OnPropertyChanged(nameof(CanCommand));
        if (_loadedFor != Services.Session.Cs.firmware)
            Load(force: false);
    }

    /// <summary>Reads the function table: the connected vehicle's documentation, else the offline copy shipped with the app.</summary>
    private void Load(bool force)
    {
        var fw = Services.Session.Cs.firmware;
        if (!force && _loadedFor == fw && _all.Count > 0)
            return;
        _loadedFor = fw;

        var fromVehicle = FromParamService(Services.Params);
        if (fromVehicle.Count > 0)
        {
            _fromVehicleDocs = true;
            Apply(fromVehicle, L.Tr("Functions from the vehicle's parameter documentation (RCn_OPTION)."));
            return;
        }

        if (_fromVehicleDocs && !force)
            return;

        var request = ++_request;
        _ = Task.Run(() =>
        {
            try
            {
                return AuxCommands.Clean(ParameterMetaDataRepository.GetParameterOptionsInt("RC6_OPTION", fw.ToString()));
            }
            catch (Exception ex)
            {
                Log.Info("aux functions", ex);
                return (IReadOnlyList<KeyValuePair<int, string>>)[];
            }
        }).ContinueWith(t => Services.Dispatcher.Post(() =>
        {
            if (request != _request)
                return;
            var list = t.IsCompletedSuccessfully ? t.Result : [];
            if (list.Count > 0)
                Apply(list, "Functions from the offline parameter documentation.");
            else
                Apply(AuxCommands.Fallback, "Parameter documentation not available: showing common functions.");
        }), TaskScheduler.Default);
    }

    /// <summary>The RCn_OPTION values of the first RC channel the documentation describes.</summary>
    internal static IReadOnlyList<KeyValuePair<int, string>> FromParamService(ParamService p)
    {
        for (var n = 1; n <= 16; n++)
        {
            if (p.Meta($"RC{n}_OPTION") is { Values.Count: > 0 } meta)
                return AuxCommands.Clean(meta.Values.Select(v => new KeyValuePair<int, string>((int)v.Key, v.Value)));
        }

        return [];
    }

    private void Apply(IReadOnlyList<KeyValuePair<int, string>> functions, string source)
    {
        var pinned = PinnedIds();
        var last = _all.ToDictionary(r => r.Id, r => r.LastPosition);
        _all = functions.Select(f => new AuxFunctionRow(this, f.Key, f.Value, pinned.Contains(f.Key))
        {
            LastPosition = last.GetValueOrDefault(f.Key),
        }).ToList();
        SourceText = source;
        ApplyFilter();
    }

    private void ApplyFilter() => Rows = Arrange(_all, Filter);

    /// <summary>Pinned functions first, then by number; the filter matches the number or the name.</summary>
    public static IReadOnlyList<AuxFunctionRow> Arrange(IEnumerable<AuxFunctionRow> rows, string? filter)
    {
        var f = filter?.Trim() ?? "";
        return rows.Where(r => f.Length == 0 || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                               r.IdText == f)
            .OrderByDescending(r => r.IsPinned)
            .ThenBy(r => r.Id)
            .ToList();
    }

    internal void OnPinChanged()
    {
        Settings.Instance[PinnedKey] = string.Join(",", _all.Where(r => r.IsPinned).Select(r => r.IdText));
        ApplyFilter();
    }

    private static HashSet<int> PinnedIds()
    {
        var set = new HashSet<int>();
        foreach (var part in (Settings.Instance[PinnedKey] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                set.Add(id);
        }

        return set;
    }

    internal async Task Send(AuxFunctionRow row, AuxSwitchPosition position)
    {
        var cmd = AuxCommands.AuxFunction(row.Id, position);
        var ok = false;
        await _commands.RunAsync($"{row.Name} {position}", (mav, sysid, compid, _) => ok = cmd.Send(mav, sysid, compid),
            $"{row.Name}: {position}", $"{row.Name}: the vehicle rejected DO_AUX_FUNCTION");
        if (ok)
            row.LastPosition = position;
    }
}
