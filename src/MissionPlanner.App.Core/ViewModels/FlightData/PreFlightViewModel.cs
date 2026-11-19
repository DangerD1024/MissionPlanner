using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>A checklist line as the PreFlight tab shows it.</summary>
public sealed partial class ChecklistRow : ObservableObject
{
    internal ChecklistRow(ChecklistItem item)
    {
        Item = item;
        ApplyColor();
    }

    public ChecklistItem Item { get; }

    public string Description => Item.Description;

    /// <summary>A NONE condition is ticked by hand; the others follow the vehicle.</summary>
    public bool IsManual => Item.ConditionType == ChecklistCondition.NONE;

    public bool IsAutomatic => !IsManual;

    /// <summary>"gpsstatus ≥ 3", for the tooltip.</summary>
    public string Rule => IsManual
        ? "Tick by hand"
        : $"{(Item.Name == ChecklistEvaluator.ParamSource ? ChecklistEvaluator.ParamName(Item) ?? "PARAM" : Item.Name)} {Symbol(Item.ConditionType)} {Item.TriggerValue.ToString("0.##", CultureInfo.InvariantCulture)}" +
          (Item.Child != null ? " (and a nested condition)" : "");

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>"#rrggbb", or null for the theme's text colour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColor))]
    private string? _color;

    public bool HasColor => Color != null;

    partial void OnIsCheckedChanged(bool value) => ApplyColor();

    private void ApplyColor() => Color = ChecklistEvaluator.ColorHex(IsChecked ? Item.TrueColor : Item.FalseColor);

    public static string Symbol(ChecklistCondition c) => c switch
    {
        ChecklistCondition.LT => "<",
        ChecklistCondition.LTEQ => "≤",
        ChecklistCondition.EQ => "=",
        ChecklistCondition.GT => ">",
        ChecklistCondition.GTEQ => "≥",
        ChecklistCondition.NEQ => "≠",
        _ => "",
    };
}

/// <summary>
/// PreFlight tab, after Mission Planner's CheckListControl: the user's checklist (checklist.xml
/// in the data folder, else the default list), each line evaluated live against the vehicle
/// (GPS fix, satellites, link, battery...) or ticked by hand; editable in a dialog.
/// </summary>
public sealed partial class PreFlightViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(PreFlightViewModel));

    private const int UpdateEvery = 5; // 4 Hz is plenty for a checklist
    private long _tick;

    public PreFlightViewModel(AppServices services)
        : base("PreFlight")
    {
        Services = services;
        Reload();
    }

    public AppServices Services { get; }

    public ObservableCollection<ChecklistRow> Rows { get; } = new();

    /// <summary>Where the list came from (file path or "built-in").</summary>
    [ObservableProperty]
    private string _source = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _allPassed;

    public void Reload()
    {
        List<ChecklistItem> items;
        string source;
        try
        {
            items = ChecklistFile.Load(ChecklistFile.UserPath, ChecklistFile.DefaultPath, out source);
        }
        catch (Exception ex)
        {
            Log.Warn("checklist", ex);
            items = ChecklistFile.BuiltInDefault();
            source = "built-in";
        }

        Rows.Clear();
        foreach (var item in items)
            Rows.Add(new ChecklistRow(item));
        Source = source;
        Update();
    }

    public override void OnShown() => Update();

    public override void Tick()
    {
        if (_tick++ % UpdateEvery == 0)
            Update();
    }

    /// <summary>Re-evaluates every line.</summary>
    public void Update()
    {
        var cs = Services.Session.Cs;
        var passed = 0;
        foreach (var row in Rows)
        {
            row.Text = ChecklistEvaluator.DisplayText(row.Item, cs, Param);
            if (row.IsAutomatic)
                row.IsChecked = ChecklistEvaluator.Check(row.Item, cs, Param);
            if (row.IsChecked)
                passed++;
        }

        Summary = $"{passed} of {Rows.Count} checked";
        AllPassed = Rows.Count > 0 && passed == Rows.Count;
    }

    private double? Param(string name) => Services.Params.Has(name) ? Services.Params.Get(name) : null;

    /// <summary>Clears the hand-ticked lines, for the next flight.</summary>
    [RelayCommand]
    private void ClearTicks()
    {
        foreach (var row in Rows.Where(r => r.IsManual))
            row.IsChecked = false;
        Update();
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        var editor = new ChecklistEditorViewModel(Services, Rows.Select(r => r.Item));
        if (await Services.Windows.ShowDialogAsync(editor, 980, 640))
            Reload();
    }
}
