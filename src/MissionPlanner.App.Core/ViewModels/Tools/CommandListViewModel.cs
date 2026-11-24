using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.FlightPlan;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A row of the command list: the ID, the name and the names of the seven values.</summary>
public sealed partial class PlannerCommandRow : ObservableObject
{
    public PlannerCommandRow(PlannerCommand c)
    {
        Id = c.Id;
        _name = c.Name;
        var p = PlannerExtraCommands.Seven(c.ParamNames);
        _p1 = p[0] ?? "";
        _p2 = p[1] ?? "";
        _p3 = p[2] ?? "";
        _p4 = p[3] ?? "";
        _p5 = p[4] ?? "";
        _p6 = p[5] ?? "";
        _p7 = p[6] ?? "";
    }

    public ushort Id { get; }

    /// <summary>A MAV_CMD keeps its library name; an unknown ID is named by the user.</summary>
    public bool IsKnown => PlannerExtraCommands.IsKnownId(Id);

    public bool CanRename => !IsKnown;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _p1;

    [ObservableProperty]
    private string _p2;

    [ObservableProperty]
    private string _p3;

    [ObservableProperty]
    private string _p4;

    [ObservableProperty]
    private string _p5;

    [ObservableProperty]
    private string _p6;

    [ObservableProperty]
    private string _p7;

    public PlannerCommand ToCommand() =>
        new(Id, Name.Trim(), new[] { P1, P2, P3, P4, P5, P6, P7 }.Select(p => string.IsNullOrWhiteSpace(p) ? null : p.Trim()).ToArray());
}

/// <summary>
/// Mission Planner's Manage Command List (Ctrl+F > "Manage Command List"): MAVLink commands added to
/// the command choices of the PLAN grid, by ID. A known ID takes its MAV_CMD name; an ID the MAVLink
/// library does not define yet gets the name typed for it. Saving updates the planner at once.
/// </summary>
public sealed partial class CommandListViewModel : ToolPageViewModel
{
    public CommandListViewModel(AppServices services)
        : base("Planner command list",
            "Add MAVLink commands to the command choices of the PLAN grid. Enter the command ID: the name is filled in when the MAVLink library defines it; an ID it does not define yet needs a name. The parameter names become the grid's column headers.",
            services)
    {
        Load();
    }

    public override bool NeedsVehicle => false;

    public ObservableCollection<PlannerCommandRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewIdName), nameof(NeedsName))]
    private string _newId = "";

    [ObservableProperty]
    private string _newName = "NEW_COMMAND";

    [ObservableProperty]
    private bool _isDirty;

    public bool IsEmpty => Rows.Count == 0;

    private ushort? ParsedId => ushort.TryParse(NewId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The MAV_CMD name of the typed ID, or a note that it is unknown.</summary>
    public string NewIdName => ParsedId is not { } id ? "" : PlannerExtraCommands.KnownName(id) ?? L.Tr("not defined in the MAVLink library: give it a name");

    public bool NeedsName => ParsedId is { } id && !PlannerExtraCommands.IsKnownId(id);

    [RelayCommand]
    private void Add()
    {
        if (ParsedId is not { } id)
        {
            Report("Enter the command ID (0 to 65535).", true);
            return;
        }

        var name = PlannerExtraCommands.KnownName(id) ?? NewName.Trim();
        if (name.Length == 0)
        {
            Report("Enter a name for the command.", true);
            return;
        }

        var existing = Rows.Select(r => r.ToCommand()).ToList();
        if (PlannerExtraCommands.Conflict(existing, id, name) is { } why)
        {
            Report(why, true);
            return;
        }

        Rows.Add(Watch(new PlannerCommandRow(new PlannerCommand(id, name, [null, null, null, null, "Lat", "Long", "Alt"]))));
        NewId = "";
        NewName = "NEW_COMMAND";
        IsDirty = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Remove(PlannerCommandRow? row)
    {
        if (row == null || !Rows.Remove(row))
            return;
        IsDirty = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Save()
    {
        var list = Rows.Select(r => r.ToCommand()).Where(c => c.Name.Length > 0).ToList();
        var duplicate = list.GroupBy(c => c.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            Report(L.Format("Two commands are named {0}.", duplicate.Key), true);
            return;
        }

        try
        {
            PlannerExtraCommands.Save(list);
            IsDirty = false;
            Report(L.Format("Saved {0} commands; the PLAN grid offers them now.", list.Count));
        }
        catch (Exception ex)
        {
            Report("Could not save the command list: " + ex.Message, true);
        }
    }

    [RelayCommand]
    private void Load()
    {
        Rows.Clear();
        foreach (var c in PlannerExtraCommands.Load())
            Rows.Add(Watch(new PlannerCommandRow(c)));
        IsDirty = false;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private PlannerCommandRow Watch(PlannerCommandRow row)
    {
        row.PropertyChanged += (_, _) => IsDirty = true;
        return row;
    }
}
