using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Calibration;

public enum StatusKind
{
    None,
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A one-line result shown inline on a page ("COMPASS_USE2 set to 0", "Command failed").</summary>
public sealed partial class StatusLine : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(IsInfo), nameof(IsSuccess), nameof(IsWarning), nameof(IsError))]
    private StatusKind _kind;

    public bool IsVisible => Kind != StatusKind.None && Text.Length > 0;

    public bool IsInfo => Kind == StatusKind.Info;

    public bool IsSuccess => Kind == StatusKind.Success;

    public bool IsWarning => Kind == StatusKind.Warning;

    public bool IsError => Kind == StatusKind.Error;

    public void Set(StatusKind kind, string text)
    {
        Text = text;
        Kind = kind;
    }

    public void Clear() => Set(StatusKind.None, "");
}

internal static class ParamFormat
{
    public static string Value(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// A checkbox bound to a parameter, like Mission Planner's MavlinkCheckBox: the first existing
/// name of several (renamed parameters) with its own on/off values; a value that is neither shows
/// as indeterminate. A change by the user is confirmed, written, and reported in a <see cref="StatusLine"/>.
/// </summary>
public sealed partial class ParamToggleViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly StatusLine _status;
    private readonly (string Name, double On, double Off)[] _candidates;
    private double _on = 1;
    private double _off;
    private bool _syncing;

    public ParamToggleViewModel(AppServices services, StatusLine status, string label, double on, double off, params string[] names)
        : this(services, status, label, names.Select(n => (n, on, off)).ToArray())
    {
    }

    public ParamToggleViewModel(AppServices services, StatusLine status, string label, (string Name, double On, double Off)[] candidates)
    {
        _services = services;
        _status = status;
        _candidates = candidates;
        Label = label;
    }

    public string Label { get; }

    /// <summary>Ask before writing (default); the reverse toggles on the radio page do too.</summary>
    public bool ConfirmWrites { get; init; } = true;

    /// <summary>Raised on the UI thread after a successful write.</summary>
    public event EventHandler? Written;

    [ObservableProperty]
    private string? _paramName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    [ObservableProperty]
    private bool? _isChecked;

    public bool CanEdit => IsAvailable && !IsBusy;

    /// <summary>Whether the current value is the "on" value.</summary>
    public bool IsOn => IsChecked == true;

    /// <summary>Re-reads the parameter (call on the UI thread).</summary>
    public void Refresh()
    {
        var hit = _candidates.FirstOrDefault(c => _services.Params.Has(c.Name));
        _syncing = true;
        try
        {
            if (hit.Name == null)
            {
                ParamName = null;
                IsAvailable = false;
                IsChecked = false;
                return;
            }

            ParamName = hit.Name;
            _on = hit.On;
            _off = hit.Off;
            IsAvailable = true;
            var v = _services.Params.Get(hit.Name);
            IsChecked = v == _on ? true : v == _off ? false : null;
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(IsOn));
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_syncing)
            return;
        _ = WriteAsync(value == true);
    }

    private async Task WriteAsync(bool on)
    {
        if (ParamName is not { } name)
            return;

        var value = on ? _on : _off;
        if (ConfirmWrites && !await _services.Dialogs.ConfirmAsync(Label,
                $"{Label}: set {name} to {ParamFormat.Value(value)}?", "Write"))
        {
            Refresh();
            return;
        }

        IsBusy = true;
        var ok = await _services.Params.SetAsync(name, value);
        IsBusy = false;
        if (ok)
            _status.Set(StatusKind.Success, $"{name} set to {ParamFormat.Value(value)}");
        else
            _status.Set(StatusKind.Error, $"Failed to set {name}");
        Refresh();
        if (ok)
            Written?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>An option of a parameter with a list of values.</summary>
public sealed record ParamOption(double Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A combo box bound to a parameter, like Mission Planner's MavlinkComboBox: the options come
/// from the parameter's documentation (a fallback list until it loads); a value outside them is
/// shown as its own entry. Changes are confirmed and written.
/// </summary>
public sealed partial class ParamChoiceViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly StatusLine _status;
    private readonly IReadOnlyList<ParamOption> _fallback;
    private bool _syncing;

    public ParamChoiceViewModel(AppServices services, StatusLine status, string label, string name, IReadOnlyList<ParamOption>? fallback = null)
    {
        _services = services;
        _status = status;
        _fallback = fallback ?? [];
        Label = label;
        Name = name;
    }

    public string Label { get; }

    public string Name { get; }

    public ObservableCollection<ParamOption> Options { get; } = new();

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private ParamOption? _selected;

    public void Refresh()
    {
        _syncing = true;
        try
        {
            IsAvailable = _services.Params.Has(Name);
            var meta = _services.Params.Meta(Name);
            var options = meta is { Values.Count: > 0 }
                ? meta.Values.Select(v => new ParamOption(v.Key, v.Value)).ToList()
                : _fallback.ToList();

            var value = _services.Params.Get(Name);
            if (value is { } v && options.All(o => o.Value != v))
                options.Add(new ParamOption(v, ParamFormat.Value(v)));

            Options.Clear();
            foreach (var o in options)
                Options.Add(o);
            Selected = value is { } cur ? Options.FirstOrDefault(o => o.Value == cur) : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnSelectedChanged(ParamOption? value)
    {
        if (_syncing || value == null)
            return;
        _ = WriteAsync(value);
    }

    private async Task WriteAsync(ParamOption option)
    {
        if (!await _services.Dialogs.ConfirmAsync(Label, L.Format("Set {0} to {1} ({2})?", Name, ParamFormat.Value(option.Value), option.Label), "Write"))
        {
            Refresh();
            return;
        }

        IsBusy = true;
        var ok = await _services.Params.SetAsync(Name, option.Value);
        IsBusy = false;
        if (ok)
            _status.Set(StatusKind.Success, $"{Name} set to {ParamFormat.Value(option.Value)} ({option.Label})");
        else
            _status.Set(StatusKind.Error, $"Failed to set {Name}");
        Refresh();
    }
}
