using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Params;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>One difference in the compare dialog; ticked rows are taken over as edits.</summary>
public sealed partial class ParamCompareRow(ParamDifference diff, ParamMeta? meta) : ObservableObject
{
    public string Name { get; } = diff.Name;

    public double Current { get; } = diff.Current;

    public double FromFile { get; } = diff.FromFile;

    public string CurrentText => ParamValues.Format(Current);

    public string FromFileText => ParamValues.Format(FromFile);

    public string Description { get; } = meta?.DisplayName is { Length: > 0 } d ? d : meta?.Description ?? "";

    public bool IsReadOnly { get; } = meta?.ReadOnly == true;

    [ObservableProperty]
    private bool _use = meta?.ReadOnly != true;
}

/// <summary>
/// Compare Params: the differences between the current list and a .param file, each with a
/// tick box (like Mission Planner's ParamCompare form). Shown modally; OK takes the ticked
/// values over as edits.
/// </summary>
public sealed partial class ParamCompareViewModel : PageViewModel, IDialogPage
{
    private bool _settingAll;

    public ParamCompareViewModel(string fileName, IReadOnlyList<ParamDifference> differences, int onlyInFile,
        Func<string, ParamMeta?> meta)
        : base("Compare Params")
    {
        FileName = fileName;
        Rows = differences.Select(d => new ParamCompareRow(d, meta(d.Name))).ToList();
        OnlyInFile = onlyInFile;
        foreach (var r in Rows)
            r.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ParamCompareRow.Use) && !_settingAll)
                    OnPropertyChanged(nameof(Summary));
            };
        _allChecked = Rows.All(r => r.Use);
    }

    public event EventHandler<bool>? CloseRequested;

    public string FileName { get; }

    public IReadOnlyList<ParamCompareRow> Rows { get; }

    public int OnlyInFile { get; }

    public string Summary =>
        $"{Rows.Count} difference{(Rows.Count == 1 ? "" : "s")}, {Rows.Count(r => r.Use)} selected" +
        (OnlyInFile > 0
            ? OnlyInFile == 1
                ? " · 1 parameter in the file is not in the current list"
                : $" · {OnlyInFile} parameters in the file are not in the current list"
            : "");

    [ObservableProperty]
    private bool _allChecked;

    partial void OnAllCheckedChanged(bool value)
    {
        _settingAll = true;
        try
        {
            foreach (var r in Rows)
                r.Use = value && !r.IsReadOnly;
        }
        finally
        {
            _settingAll = false;
        }

        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>The ticked values, to queue as edits.</summary>
    public IReadOnlyDictionary<string, double> Selected =>
        Rows.Where(r => r.Use).ToDictionary(r => r.Name, r => r.FromFile, StringComparer.Ordinal);

    [RelayCommand]
    private void Apply() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
