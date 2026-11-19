using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

/// <summary>
/// One sensor instance on a setup page: its type, the settings that matter for it, the rest of its
/// parameters (collapsed) and a live reading.
/// </summary>
public sealed partial class InstancePanel : ObservableObject
{
    public InstancePanel(SensorInstance instance, string title, ParamField? type, IReadOnlyList<ParamField> main, IReadOnlyList<ParamField> rest)
    {
        Instance = instance;
        Title = title;
        Type = type;
        Main = main;
        Rest = rest;
        Update();
    }

    public SensorInstance Instance { get; }

    public int Number => Instance.Number;

    public string Title { get; }

    public ParamField? Type { get; }

    public IReadOnlyList<ParamField> Main { get; }

    public IReadOnlyList<ParamField> Rest { get; }

    public bool HasRest => Rest.Count > 0;

    /// <summary>A type other than 0 (None) is set.</summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>"Rangefinder 1 · SITL" for the instance list.</summary>
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _reading = "";

    [ObservableProperty]
    private bool _hasReading;

    [ObservableProperty]
    private bool _showAll;

    /// <summary>Re-reads the type after a refresh or write.</summary>
    public void Update()
    {
        var type = Type?.Value ?? 0;
        IsEnabled = type != 0;
        Summary = Type == null ? Title : $"{Title} · {(IsEnabled ? Type.ValueText : "not fitted")}";
    }

    public void SetReading(string? text)
    {
        var t = text ?? "";
        if (Reading != t)
            Reading = t;
        HasReading = t.Length > 0;
    }
}
