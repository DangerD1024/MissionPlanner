using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>One radio's value of one setting, with the editor that fits it.</summary>
public sealed partial class SikValue : ObservableObject
{
    public SikValue(SikParam param, SikParamInfo info, bool remote)
    {
        Param = param;
        Info = info;
        IsRemote = remote;
        Original = param.Value;
        Load(param.Value);
    }

    public SikParam Param { get; }

    public SikParamInfo Info { get; }

    public bool IsRemote { get; }

    public string Designator => Param.Designator;

    public SikEditorKind Kind => Info.Kind;

    public bool IsFlag => Kind == SikEditorKind.Flag;

    public bool IsChoice => Kind == SikEditorKind.Choice;

    public bool IsNumber => Kind == SikEditorKind.Number;

    public bool IsReadOnly => Kind == SikEditorKind.ReadOnly;

    public IReadOnlyList<SikChoice> Choices => Info.Choices;

    public decimal Minimum => Info.Min;

    public decimal Maximum => Info.Max;

    public decimal Step => Info.Step;

    /// <summary>The value read from the radio (or last written to it).</summary>
    public int Original { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value), nameof(IsChanged))]
    private SikChoice? _selectedChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value), nameof(IsChanged))]
    private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value), nameof(IsChanged))]
    private decimal? _number;

    public string ReadOnlyText => Param.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The value the editor holds now.</summary>
    public int Value
    {
        get => Kind switch
        {
            SikEditorKind.Flag => IsChecked ? 1 : 0,
            SikEditorKind.Choice => SelectedChoice?.Value ?? Original,
            SikEditorKind.Number => Number is { } n ? (int)Math.Round(n) : Original,
            _ => Original,
        };
        set => Load(value);
    }

    public bool IsChanged => Kind != SikEditorKind.ReadOnly && Value != Original;

    /// <summary>The value is now what the radio has.</summary>
    public void Commit()
    {
        Original = Value;
        OnPropertyChanged(nameof(IsChanged));
    }

    private void Load(int value)
    {
        switch (Kind)
        {
            case SikEditorKind.Flag:
                IsChecked = value != 0;
                break;
            case SikEditorKind.Choice:
                SelectedChoice = Choices.FirstOrDefault(c => c.Value == value) ??
                                 Choices.OrderBy(c => Math.Abs(c.Value - value)).FirstOrDefault();
                break;
            case SikEditorKind.Number:
                Number = Math.Clamp(value, Info.Min, Info.Max);
                break;
        }

        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsChanged));
    }
}

/// <summary>A setting, side by side on both radios.</summary>
public sealed partial class SikSettingRow : ObservableObject
{
    public SikSettingRow(string name, SikValue? local, SikValue? remote)
    {
        Name = name;
        Local = local;
        Remote = remote;
        var info = (local ?? remote)?.Info;
        Label = info?.Label ?? SikCatalog.Label(name);
        Description = info?.Description is { Length: > 0 } d ? d : SikCatalog.Description(name);
        MustMatch = SikCatalog.MustMatch.Contains(name);
        if (local != null)
            local.PropertyChanged += OnValueChanged;
        if (remote != null)
            remote.PropertyChanged += OnValueChanged;
    }

    public string Name { get; }

    public string Label { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public SikValue? Local { get; }

    public SikValue? Remote { get; }

    public bool HasLocal => Local != null;

    public bool HasRemote => Remote != null;

    /// <summary>Settings both radios need equal to link (air speed, net ID, frequencies...).</summary>
    public bool MustMatch { get; }

    /// <summary>Both radios have the setting and the values differ where they must not.</summary>
    public bool Mismatch => MustMatch && Local != null && Remote != null && Local.Value != Remote.Value;

    private void OnValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SikValue.Value))
            OnPropertyChanged(nameof(Mismatch));
    }
}
