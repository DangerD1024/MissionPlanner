using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.FlightData;
using Conditional = MissionPlanner.Warnings.CustomWarning.Conditional;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A line of the Warning Manager: a warning, or one of the conditions chained under it.</summary>
public sealed partial class WarningRowViewModel(WarningRule rule, WarningRule root, WarningRule? parent) : ObservableObject
{
    public WarningRule Rule { get; } = rule;

    /// <summary>The warning this line belongs to.</summary>
    public WarningRule Root { get; } = root;

    /// <summary>The line above in the chain, or null for the warning itself.</summary>
    public WarningRule? Parent { get; } = parent;

    public bool IsChild => Parent != null;

    public bool IsTop => Parent == null;

    /// <summary>"and" in front of a chained condition.</summary>
    public string Prefix => IsChild ? "and" : "";

    /// <summary>The current value of the field, for the user to compare.</summary>
    [ObservableProperty]
    private string _currentText = "";

    /// <summary>The whole chain holds right now.</summary>
    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// Mission Planner's Warning Manager (Ctrl+F > "Warning Manager"): custom spoken warnings on any
/// CurrentState value, with chained "and" conditions, and Quick tile colouring. Edits apply at once;
/// Save writes Mission Planner's warnings.xml.
/// </summary>
public sealed partial class WarningManagerViewModel : ToolPageViewModel
{
    private readonly List<WarningRule> _rules = new();
    private int _ticks;

    public WarningManagerViewModel(AppServices services)
        : base("Warning Manager",
            "Custom audio warnings: speak a text (and show it on the HUD) when a value crosses a limit, or colour its Quick tile. {name}, {value} and {warning} in the text are replaced by the field, its value and the limit.",
            services)
    {
        _rules.AddRange(AppWarnings.Rules);
        Rebuild();
    }

    public override bool NeedsVehicle => false;

    public ObservableCollection<WarningRowViewModel> Rows { get; } = new();

    /// <summary>The numeric CurrentState fields a warning can watch.</summary>
    public IReadOnlyList<string> Fields { get; } = QuickValueCatalog.ListFields().Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<Conditional> Conditions => WarningLogic.Conditions;

    public IReadOnlyList<string> Colors => WarningLogic.ColorNames;

    public bool IsEmpty => Rows.Count == 0;

    public string FilePath => WarningStore.DefaultPath;

    [ObservableProperty]
    private bool _isDirty;

    [RelayCommand]
    private void Add()
    {
        var rule = new WarningRule { Field = Fields.Contains("battery_voltage") ? "battery_voltage" : Fields.FirstOrDefault() ?? "" };
        _rules.Add(rule);
        Changed();
    }

    /// <summary>Chains a further condition under a line ("+" in Mission Planner).</summary>
    [RelayCommand]
    private void AddCondition(WarningRowViewModel? row)
    {
        if (row == null)
            return;
        var rule = new WarningRule
        {
            Field = row.Rule.Field,
            Condition = Conditional.GT,
            RepeatSeconds = 0,
            Text = "",
            Child = row.Rule.Child,
        };
        row.Rule.Child = rule;
        Changed();
    }

    /// <summary>Removes a warning with its conditions, or one chained condition ("-" in Mission Planner).</summary>
    [RelayCommand]
    private void Remove(WarningRowViewModel? row)
    {
        if (row == null)
            return;
        if (row.Parent is { } parent)
            parent.Child = row.Rule.Child;
        else
            _rules.Remove(row.Rule);
        Changed();
    }

    /// <summary>Says the warning's text with the current value, whether or not speech is on.</summary>
    [RelayCommand]
    private void Test(WarningRowViewModel? row)
    {
        if (row == null)
            return;
        var value = QuickValueCatalog.GetGetter(row.Root.Field) is { } g ? g(Services.Session.Cs) : 0;
        var text = WarningLogic.SayText(row.Root, value);
        AppSpeech.Engine.SpeakNow(text);
        Report("Said: " + text);
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            WarningStore.Save(FilePath, _rules);
            IsDirty = false;
            Report(L.Format("Saved {0} warnings to {1}", _rules.Count, FilePath));
        }
        catch (Exception ex)
        {
            Report("Could not save the warnings: " + ex.Message, true);
        }
    }

    /// <summary>Discards unsaved edits: reads the file again.</summary>
    [RelayCommand]
    private void Reload()
    {
        try
        {
            _rules.Clear();
            _rules.AddRange(WarningStore.Load(FilePath));
            AppWarnings.Use(_rules);
            Rebuild();
            IsDirty = false;
            Report(L.Format("Read {0} warnings.", _rules.Count));
        }
        catch (Exception ex)
        {
            Report("Could not read the warnings: " + ex.Message, true);
        }
    }

    public override void Tick()
    {
        base.Tick();
        if (++_ticks % 5 != 0)
            return;
        var cs = Services.Session.Cs;
        double? Read(string name) => QuickValueCatalog.GetGetter(name) is { } g ? g(cs) : null;
        foreach (var row in Rows)
        {
            var v = Read(row.Rule.Field);
            row.CurrentText = v is { } d ? d.ToString("0.##", CultureInfo.CurrentCulture) : "";
            row.IsActive = row.IsTop && WarningLogic.ChainHolds(row.Rule, Read);
        }
    }

    private void Changed()
    {
        AppWarnings.Use(_rules);
        Rebuild();
        IsDirty = true;
    }

    private void Rebuild()
    {
        foreach (var row in Rows)
            row.Rule.PropertyChanged -= OnRuleChanged;
        Rows.Clear();
        foreach (var rule in _rules)
        {
            WarningRule? parent = null;
            foreach (var r in rule.Chain())
            {
                var row = new WarningRowViewModel(r, rule, parent);
                r.PropertyChanged += OnRuleChanged;
                Rows.Add(row);
                parent = r;
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WarningRule.Child))
            IsDirty = true;
    }
}
