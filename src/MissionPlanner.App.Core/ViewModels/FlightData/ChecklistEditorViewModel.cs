using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>One editable checklist line.</summary>
public sealed partial class ChecklistEditRow : ObservableObject
{
    private readonly ChecklistItem? _child;

    public ChecklistEditRow(ChecklistItem item)
    {
        _description = item.Description;
        _source = item.Name;
        _condition = item.ConditionType;
        _trigger = (decimal)item.TriggerValue;
        _text = item.Text;
        _trueColor = item.TrueColor;
        _falseColor = item.FalseColor;
        _child = item.Child?.Clone();
    }

    [ObservableProperty]
    private string _description;

    /// <summary>CurrentState property name, or PARAM.</summary>
    [ObservableProperty]
    private string _source;

    [ObservableProperty]
    private ChecklistCondition _condition;

    [ObservableProperty]
    private decimal? _trigger;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private string _trueColor;

    [ObservableProperty]
    private string _falseColor;

    public bool HasChild => _child != null;

    public ChecklistItem ToItem() => new()
    {
        Description = Description?.Trim() ?? "",
        Name = string.IsNullOrWhiteSpace(Source) ? "roll" : Source.Trim(),
        ConditionType = Condition,
        TriggerValue = (double)(Trigger ?? 0),
        Text = Text ?? "",
        TrueColor = string.IsNullOrWhiteSpace(TrueColor) ? "Green" : TrueColor,
        FalseColor = string.IsNullOrWhiteSpace(FalseColor) ? "Red" : FalseColor,
        Child = _child?.Clone(),
    };
}

/// <summary>
/// Checklist editor (Mission Planner's CheckListEditor), shown as a dialog: add, remove, reorder
/// and change lines; Save writes checklist.xml in the data folder.
/// </summary>
public sealed partial class ChecklistEditorViewModel : PageViewModel, IDialogPage
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ChecklistEditorViewModel));

    public ChecklistEditorViewModel(AppServices services, IEnumerable<ChecklistItem> items)
        : base("Edit Pre-Flight Checklist")
    {
        Services = services;
        foreach (var item in items)
            Rows.Add(new ChecklistEditRow(item.Clone()));
    }

    public AppServices Services { get; }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<ChecklistEditRow> Rows { get; } = new();

    public IReadOnlyList<string> Sources => ChecklistEvaluator.Sources;

    public IReadOnlyList<ChecklistCondition> Conditions { get; } = Enum.GetValues<ChecklistCondition>();

    public IReadOnlyList<string> Colors => ChecklistEvaluator.ColorNames;

    public string FilePath => ChecklistFile.UserPath;

    [ObservableProperty]
    private string _error = "";

    [RelayCommand]
    private void Add() => Rows.Add(new ChecklistEditRow(new ChecklistItem
    {
        Description = "New check",
        Name = "battery_remaining",
        ConditionType = ChecklistCondition.GT,
        TriggerValue = 50,
        Text = "{value}%",
    }));

    [RelayCommand]
    private void Remove(ChecklistEditRow? row)
    {
        if (row != null)
            Rows.Remove(row);
    }

    [RelayCommand]
    private void MoveUp(ChecklistEditRow? row)
    {
        var i = row == null ? -1 : Rows.IndexOf(row);
        if (i > 0)
            Rows.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveDown(ChecklistEditRow? row)
    {
        var i = row == null ? -1 : Rows.IndexOf(row);
        if (i >= 0 && i < Rows.Count - 1)
            Rows.Move(i, i + 1);
    }

    /// <summary>Replaces the list with Mission Planner's default (saved only on Save).</summary>
    [RelayCommand]
    private async Task ResetToDefaultAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("Checklist", "Replace the list with the default checklist?", "Reset"))
            return;
        var items = File.Exists(ChecklistFile.DefaultPath)
            ? ChecklistFile.Load(ChecklistFile.DefaultPath, ChecklistFile.DefaultPath, out _)
            : ChecklistFile.BuiltInDefault();
        Rows.Clear();
        foreach (var item in items)
            Rows.Add(new ChecklistEditRow(item));
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            ChecklistFile.Save(Rows.Select(r => r.ToItem()).ToList());
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            Log.Warn("checklist save", ex);
            Error = "Could not save the checklist: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
