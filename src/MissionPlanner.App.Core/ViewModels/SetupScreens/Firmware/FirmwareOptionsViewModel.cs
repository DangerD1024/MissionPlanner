using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.ArduPilot;
using Field = MissionPlanner.ArduPilot.FirmwareFilter.Field;
using FirmwareInfo = MissionPlanner.ArduPilot.APFirmware.FirmwareInfo;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

/// <summary>A manifest entry as a row of the options list.</summary>
public sealed class FirmwareRow(FirmwareInfo info)
{
    public FirmwareInfo Info { get; } = info;

    public string Platform => Info.Platform ?? "";

    public string Vehicle => FirmwareOptionsViewModel.VehicleName(Info.MavType);

    public string Release => Info.MavFirmwareVersionType ?? "";

    public string Version => string.IsNullOrEmpty(Info.MavFirmwareVersionStr)
        ? Info.MavFirmwareVersion?.ToString() ?? ""
        : Info.MavFirmwareVersionStr;

    public string BoardId => Info.BoardId.ToString();

    public string File => Info.Url == null ? "" : Path.GetFileName(Info.Url.AbsolutePath);

    public string Url => Info.Url?.AbsoluteUri ?? "";

    public string Summary => $"{Vehicle} {Version} ({Release}) for {Platform}";
}

/// <summary>
/// "All options": every flashable manifest entry, narrowed by vehicle, release, board and version
/// (the faceted <see cref="FirmwareFilter"/> the WinForms selection form uses) plus a board search.
/// </summary>
public sealed partial class FirmwareOptionsViewModel : ObservableObject
{
    public const string Any = "Any";
    public const int MaxRows = 300;

    private readonly IReadOnlyList<FirmwareInfo> _source;
    private FirmwareFilter _filter;
    private bool _updating;

    public FirmwareOptionsViewModel(IReadOnlyList<FirmwareInfo> source, string? initialPlatform = null)
    {
        _source = source;
        _filter = new FirmwareFilter(source);
        if (!string.IsNullOrEmpty(initialPlatform) && _filter.GetOptions(Field.Platform).Contains(initialPlatform))
        {
            _filter.Set(Field.Platform, initialPlatform);
            _platform = initialPlatform;
        }

        Refresh();
    }

    public ObservableCollection<string> VehicleOptions { get; } = new();
    public ObservableCollection<string> ReleaseOptions { get; } = new();
    public ObservableCollection<string> PlatformOptions { get; } = new();
    public ObservableCollection<string> VersionOptions { get; } = new();
    public ObservableCollection<FirmwareRow> Rows { get; } = new();

    [ObservableProperty] private string _vehicle = Any;
    [ObservableProperty] private string _release = Any;
    [ObservableProperty] private string _platform = Any;
    [ObservableProperty] private string _version = Any;

    /// <summary>Narrows the boards by name ("matek", "cube").</summary>
    [ObservableProperty] private string _search = "";

    [ObservableProperty] private FirmwareRow? _selected;

    [ObservableProperty] private string _resultText = "";

    public int MatchCount { get; private set; }

    partial void OnVehicleChanged(string value) => SetField(Field.MavType, FromVehicleName(value));
    partial void OnReleaseChanged(string value) => SetField(Field.VersionType, value);
    partial void OnPlatformChanged(string value) => SetField(Field.Platform, value);
    partial void OnVersionChanged(string value) => SetField(Field.Version, value);

    partial void OnSearchChanged(string value)
    {
        var text = value.Trim();
        var keep = new Dictionary<Field, string?>
        {
            [Field.MavType] = _filter.Get(Field.MavType),
            [Field.VersionType] = _filter.Get(Field.VersionType),
            [Field.Platform] = _filter.Get(Field.Platform),
            [Field.Version] = _filter.Get(Field.Version),
        };
        _filter = new FirmwareFilter(text.Length == 0
            ? _source
            : _source.Where(f => f.Platform?.Contains(text, StringComparison.OrdinalIgnoreCase) == true));
        foreach (var (field, v) in keep)
            _filter.Set(field, v);
        Refresh();
    }

    public void ClearFilters()
    {
        _filter.Clear();
        Search = "";
        Refresh();
    }

    private void SetField(Field field, string? value)
    {
        if (_updating)
            return;
        _filter.Set(field, value is null or Any ? null : value);
        Refresh();
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            Fill(VehicleOptions, Field.MavType, VehicleName);
            Fill(ReleaseOptions, Field.VersionType, s => s);
            Fill(PlatformOptions, Field.Platform, s => s);
            Fill(VersionOptions, Field.Version, s => s);
            Vehicle = SelectedValue(Field.MavType, VehicleName);
            Release = SelectedValue(Field.VersionType, s => s);
            Platform = SelectedValue(Field.Platform, s => s);
            Version = SelectedValue(Field.Version, s => s);
        }
        finally
        {
            _updating = false;
        }

        var matches = _filter.GetMatches();
        MatchCount = matches.Count;
        var rows = matches
            .OrderBy(f => f.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => VehicleRank(f.MavType))
            .ThenBy(f => ReleaseRank(f.MavFirmwareVersionType))
            .ThenByDescending(f => f.MavFirmwareVersion ?? new System.Version(0, 0))
            .Take(MaxRows)
            .Select(f => new FirmwareRow(f))
            .ToList();
        Rows.Clear();
        foreach (var r in rows)
            Rows.Add(r);
        Selected = Rows.Count == 1 ? Rows[0] : null;
        ResultText = matches.Count switch
        {
            0 => "No firmware matches these choices.",
            > MaxRows => $"{matches.Count} matches; showing the first {MaxRows}. Choose a board to narrow the list.",
            1 => "1 match.",
            _ => $"{matches.Count} matches.",
        };
    }

    private void Fill(ObservableCollection<string> target, Field field, Func<string, string> display)
    {
        var options = _filter.GetOptions(field);
        var selected = _filter.Get(field);
        // keep a selection other filters made unreachable, so it can still be cleared
        if (selected != null && !options.Contains(selected))
            options.Insert(0, selected);
        if (field == Field.Version)
            options.Reverse();
        var items = new List<string> { Any };
        items.AddRange(options.Select(display));
        if (items.SequenceEqual(target))
            return;
        target.Clear();
        foreach (var i in items)
            target.Add(i);
    }

    private string SelectedValue(Field field, Func<string, string> display) =>
        _filter.Get(field) is { } v ? display(v) : Any;

    private static int VehicleRank(string? mavType) => mavType switch
    {
        "Copter" => 0,
        "FIXED_WING" => 1,
        "GROUND_ROVER" => 2,
        "SUBMARINE" => 3,
        "HELICOPTER" => 4,
        "ANTENNA_TRACKER" => 5,
        _ => 6,
    };

    // releases first, then the archived stable versions (newest first by version)
    private static int ReleaseRank(string? type) => type switch
    {
        "OFFICIAL" => 0,
        "BETA" => 1,
        "DEV" => 2,
        _ => 3,
    };

    public static string VehicleName(string? mavType) => mavType switch
    {
        "Copter" => "Copter",
        "FIXED_WING" => "Plane",
        "GROUND_ROVER" => "Rover",
        "SUBMARINE" => "Sub",
        "HELICOPTER" => "Heli",
        "ANTENNA_TRACKER" => "Antenna Tracker",
        null => "",
        _ => mavType,
    };

    private static string? FromVehicleName(string? name) => name switch
    {
        null or Any => null,
        "Plane" => "FIXED_WING",
        "Rover" => "GROUND_ROVER",
        "Sub" => "SUBMARINE",
        "Heli" => "HELICOPTER",
        "Antenna Tracker" => "ANTENNA_TRACKER",
        _ => name,
    };
}
