using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.Params;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>Which editor a parameter gets.</summary>
public enum ParamEditorKind
{
    /// <summary>Numeric up/down within the documented (or page-given) range.</summary>
    Number,

    /// <summary>Drop-down of the documented values.</summary>
    Options,

    /// <summary>Check box: on/off values (1/0 unless the page says otherwise).</summary>
    Toggle,

    /// <summary>One check box per documented bit.</summary>
    Bitmask,
}

/// <summary>When an edit reaches the vehicle.</summary>
public enum ParamWriteMode
{
    /// <summary>As soon as it is made (numbers and bitmasks after a short pause), like Mission Planner's Mavlink controls.</summary>
    Immediate,

    /// <summary>Held until the page commits it (e.g. Flight Modes' "Save Modes").</summary>
    Queued,
}

public sealed record ParamOption(double Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>How a page wants a parameter edited. Anything left null comes from the parameter documentation.</summary>
public sealed record ParamFieldSpec
{
    public string? Label { get; init; }

    public ParamEditorKind? Kind { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Increment { get; init; }

    public string? Units { get; init; }

    /// <summary>Replaces the documented values of an <see cref="ParamEditorKind.Options"/> field.</summary>
    public IReadOnlyList<ParamOption>? Options { get; init; }

    public double ToggleOn { get; init; } = 1;

    public double ToggleOff { get; init; }

    public ParamWriteMode Mode { get; init; } = ParamWriteMode.Immediate;

    /// <summary>Pause after the last change before an immediate write (defaults: numbers 0.6 s, bitmasks 0.8 s, others none).</summary>
    public TimeSpan? Debounce { get; init; }
}

/// <summary>One bit of a bitmask field.</summary>
public sealed class BitOption : ObservableObject
{
    private readonly ParamField _owner;
    private bool _isChecked;

    internal BitOption(ParamField owner, int bit, string label)
    {
        _owner = owner;
        Bit = bit;
        Label = label;
    }

    public int Bit { get; }

    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                _owner.OnBitToggled();
        }
    }

    internal void Sync(bool on) => SetProperty(ref _isChecked, on, nameof(IsChecked));
}

public sealed class ParamWrittenEventArgs(double? oldValue, double newValue) : EventArgs
{
    public double? OldValue { get; } = oldValue;

    public double NewValue { get; } = newValue;
}

/// <summary>
/// One parameter as an editor: label, units and range from the documentation, the vehicle's value,
/// and the write path. Create and use on the UI thread.
/// </summary>
public sealed partial class ParamField : ObservableObject
{
    public static readonly TimeSpan NumberDebounce = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan BitmaskDebounce = TimeSpan.FromMilliseconds(800);

    private const double WideRange = 1e7;

    private readonly IParamSource _source;
    private readonly IReadOnlyList<ParamOption> _documentedOptions;
    private bool _syncing;
    private CancellationTokenSource? _debounce;
    private double? _scheduled;
    private int _writes;

    public ParamField(IParamSource source, string name, ParamFieldSpec? spec = null)
    {
        _source = source;
        Name = name;
        Spec = spec ?? new ParamFieldSpec();
        Meta = source.Meta(name);

        Label = Spec.Label ?? (string.IsNullOrWhiteSpace(Meta?.DisplayName) ? name : Meta!.DisplayName);
        Description = Meta?.Description ?? "";
        Units = Spec.Units ?? Meta?.Units ?? "";
        IsInteger = source.IsInteger(name);
        IsReadOnly = Meta?.ReadOnly == true;
        Mode = Spec.Mode;

        _documentedOptions = Spec.Options ?? Meta?.Values.Select(v => new ParamOption(v.Key, v.Value)).ToList() ?? [];
        Bits = Meta?.Bitmask.Select(b => new BitOption(this, b.Key, b.Value)).ToList() ?? [];

        Kind = Spec.Kind ?? (Bits.Count > 0 ? ParamEditorKind.Bitmask
            : _documentedOptions.Count > 0 ? ParamEditorKind.Options
            : ParamEditorKind.Number);
        if (Kind == ParamEditorKind.Bitmask && Bits.Count == 0)
            Kind = ParamEditorKind.Number;
        if (Kind == ParamEditorKind.Options && _documentedOptions.Count == 0)
            Kind = ParamEditorKind.Number;

        Debounce = Spec.Debounce ?? Kind switch
        {
            ParamEditorKind.Number => NumberDebounce,
            ParamEditorKind.Bitmask => BitmaskDebounce,
            _ => TimeSpan.Zero,
        };

        RangeMin = Spec.Min ?? Meta?.Min;
        RangeMax = Spec.Max ?? Meta?.Max;
        var inc = Spec.Increment ?? Meta?.Increment ?? (IsInteger ? 1 : 0.01);
        Increment = inc > 0 ? (decimal)inc : 1m;
        FormatString = IsInteger && inc >= 1 ? "0" : "0.######";

        _options = _documentedOptions;
        Refresh(force: true);
    }

    public string Name { get; }

    public ParamFieldSpec Spec { get; }

    public ParamMeta? Meta { get; }

    public string Label { get; }

    public string Description { get; }

    public string Units { get; }

    public bool HasUnits => Units.Length > 0;

    public ParamEditorKind Kind { get; }

    public bool IsNumber => Kind == ParamEditorKind.Number;

    public bool IsOptions => Kind == ParamEditorKind.Options;

    public bool IsToggle => Kind == ParamEditorKind.Toggle;

    public bool IsBitmask => Kind == ParamEditorKind.Bitmask;

    public ParamWriteMode Mode { get; }

    public TimeSpan Debounce { get; }

    public bool IsInteger { get; }

    public bool IsReadOnly { get; }

    public bool IsEditable => !IsReadOnly;

    public bool RebootRequired => Meta?.RebootRequired == true;

    public double? RangeMin { get; }

    public double? RangeMax { get; }

    public decimal Increment { get; }

    public string FormatString { get; }

    public IReadOnlyList<BitOption> Bits { get; }

    /// <summary>Name, documentation and range, for a tooltip.</summary>
    public string ToolTip
    {
        get
        {
            var parts = new List<string> { Name };
            if (Description.Length > 0)
                parts.Add(Description);
            if (RangeMin is { } a && RangeMax is { } b)
                parts.Add($"Range {Format(a)} to {Format(b)}{(HasUnits ? " " + Units : "")}");
            if (RebootRequired)
                parts.Add("Takes effect after a reboot.");
            return string.Join("\n", parts);
        }
    }

    /// <summary>The value the vehicle reported last (null if it does not have the parameter).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private double? _value;

    [ObservableProperty]
    private bool _exists;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error != null;

    /// <summary>Queued mode: the edited value not yet written.</summary>
    [ObservableProperty]
    private double? _pendingValue;

    [ObservableProperty]
    private bool _isDirty;

    // ---- number editor ----

    [ObservableProperty]
    private decimal _minimum = (decimal)-WideRange;

    [ObservableProperty]
    private decimal _maximum = (decimal)WideRange;

    [ObservableProperty]
    private decimal? _numberValue;

    partial void OnNumberValueChanged(decimal? value)
    {
        if (_syncing || value is null)
            return;
        Edit((double)value.Value);
    }

    // ---- options editor ----

    private IReadOnlyList<ParamOption> _options;

    public IReadOnlyList<ParamOption> Options
    {
        get => _options;
        private set => SetProperty(ref _options, value);
    }

    [ObservableProperty]
    private ParamOption? _selectedOption;

    partial void OnSelectedOptionChanged(ParamOption? value)
    {
        if (_syncing || value is null)
            return;
        Edit(value.Value);
    }

    // ---- toggle editor ----

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value)
    {
        if (_syncing)
            return;
        Edit(value ? Spec.ToggleOn : Spec.ToggleOff);
    }

    // ---- bitmask editor ----

    [ObservableProperty]
    private string _bitSummary = "None";

    internal void OnBitToggled()
    {
        if (_syncing)
            return;
        var basis = _scheduled ?? PendingValue ?? Value ?? 0;
        var value = BitmaskCodec.Encode(basis, Bits.Select(b => new KeyValuePair<int, bool>(b.Bit, b.IsChecked)));
        BitSummary = BitmaskCodec.Summary(value, Bits.Select(b => new KeyValuePair<int, string>(b.Bit, b.Label)));
        Edit(value);
    }

    /// <summary>The value as text, with the option label or bit names where documented.</summary>
    public string ValueText
    {
        get
        {
            if (Value is not { } v)
                return "—";
            return Kind switch
            {
                ParamEditorKind.Options => LabelFor(v),
                ParamEditorKind.Bitmask => BitmaskCodec.Summary(v, Bits.Select(b => new KeyValuePair<int, string>(b.Bit, b.Label))),
                ParamEditorKind.Toggle => v == Spec.ToggleOn ? "On" : "Off",
                _ => Format(v) + (HasUnits ? " " + Units : ""),
            };
        }
    }

    public string LabelFor(double value) =>
        _documentedOptions.FirstOrDefault(o => o.Value == value)?.Label ?? Format(value);

    public string Format(double v) => v.ToString(FormatString, CultureInfo.InvariantCulture);

    /// <summary>Raised on the UI thread after the vehicle accepted a write.</summary>
    public event EventHandler<ParamWrittenEventArgs>? Written;

    /// <summary>Raised on the UI thread when a write failed; the argument is a message for the user.</summary>
    public event EventHandler<string>? WriteFailed;

    /// <summary>Raised when the user changes the editor (before any write).</summary>
    public event EventHandler? Edited;

    /// <summary>
    /// Reads the vehicle's value again and updates the editors. Skipped while an edit is waiting
    /// or being written, unless <paramref name="force"/> is set.
    /// </summary>
    public void Refresh(bool force = false)
    {
        if (!force && (_scheduled != null || _writes > 0 || IsDirty))
            return;

        var exists = _source.Has(Name);
        var value = exists && _source.Get(Name) is { } raw ? ParamValues.Clean(raw, IsInteger) : (double?)null;
        Exists = exists;
        if (!force && value == Value)
            return;
        Value = value;
        if (_scheduled == null && !IsDirty)
            SyncEditors(value);
    }

    /// <summary>Writes a value now (bypassing the queue and pause). False if the vehicle did not take it.</summary>
    public async Task<bool> WriteAsync(double value)
    {
        CancelScheduled();
        if (IsReadOnly)
        {
            Fail($"{Name} is read-only.");
            return false;
        }

        var old = Value;
        _writes++;
        IsBusy = true;
        Error = null;
        bool ok;
        try
        {
            ok = await _source.SetAsync(Name, value);
        }
        catch (Exception)
        {
            ok = false;
        }
        finally
        {
            _writes--;
            IsBusy = _writes > 0;
        }

        if (ok)
        {
            if (PendingValue == value)
            {
                PendingValue = null;
                IsDirty = false;
            }

            AfterWrite();
            Written?.Invoke(this, new ParamWrittenEventArgs(old, value));
        }
        else
        {
            PendingValue = null;
            IsDirty = false;
            AfterWrite();
            Fail($"The vehicle did not accept {Name} = {Format(value)}.");
        }

        return ok;
    }

    /// <summary>Queued mode: writes the pending edit. True when there was nothing to write.</summary>
    public Task<bool> CommitAsync() => PendingValue is { } v && IsDirty ? WriteAsync(v) : Task.FromResult(true);

    /// <summary>Drops a queued or waiting edit and shows the vehicle's value again.</summary>
    public void Revert()
    {
        CancelScheduled();
        PendingValue = null;
        IsDirty = false;
        Refresh(force: true);
    }

    /// <summary>Writes a waiting immediate edit now instead of after its pause (e.g. when the page closes).</summary>
    public Task FlushAsync()
    {
        if (_scheduled is { } v)
            return WriteAsync(v);
        return Task.CompletedTask;
    }

    /// <summary>Sets the editor as if the user had (queued fields stay pending, immediate ones write).</summary>
    public void Edit(double value)
    {
        Error = null;

        if (Mode == ParamWriteMode.Queued)
        {
            PendingValue = value;
            IsDirty = Value != value;
            SyncEditors(value);
            Edited?.Invoke(this, EventArgs.Empty);
            return;
        }

        Edited?.Invoke(this, EventArgs.Empty);
        CancelScheduled();
        if (Debounce <= TimeSpan.Zero)
        {
            _ = WriteAsync(value);
            return;
        }

        _scheduled = value;
        var cts = new CancellationTokenSource();
        _debounce = cts;
        _ = WriteAfterPauseAsync(value, cts.Token);
    }

    private async Task WriteAfterPauseAsync(double value, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(Debounce, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cancel.IsCancellationRequested)
            await WriteAsync(value);
    }

    private void CancelScheduled()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
        _scheduled = null;
    }

    private void AfterWrite()
    {
        // a newer edit may be waiting for its pause; leave the editors showing it
        if (_scheduled != null)
            return;
        var exists = _source.Has(Name);
        Exists = exists;
        Value = exists && _source.Get(Name) is { } raw ? ParamValues.Clean(raw, IsInteger) : null;
        if (!IsDirty)
            SyncEditors(Value);
    }

    private void Fail(string message)
    {
        Error = message;
        WriteFailed?.Invoke(this, message);
    }

    private void SyncEditors(double? value)
    {
        _syncing = true;
        try
        {
            if (value is not { } v)
            {
                NumberValue = null;
                SelectedOption = null;
                IsOn = false;
                foreach (var b in Bits)
                    b.Sync(false);
                BitSummary = "None";
                return;
            }

            switch (Kind)
            {
                case ParamEditorKind.Number:
                    // widen the range to the value so the editor never clamps (and silently rewrites) it
                    var lo = Math.Min(RangeMin ?? -WideRange, v);
                    var hi = Math.Max(RangeMax ?? WideRange, v);
                    Minimum = ToDecimal(lo);
                    Maximum = ToDecimal(hi);
                    NumberValue = ToDecimal(v);
                    break;

                case ParamEditorKind.Options:
                    var match = _documentedOptions.FirstOrDefault(o => o.Value == v);
                    if (match == null)
                    {
                        match = new ParamOption(v, $"{Format(v)} (undocumented)");
                        Options = _documentedOptions.Append(match).ToList();
                    }
                    else if (!ReferenceEquals(Options, _documentedOptions))
                    {
                        Options = _documentedOptions;
                    }

                    SelectedOption = match;
                    break;

                case ParamEditorKind.Toggle:
                    IsOn = v == Spec.ToggleOn;
                    break;

                case ParamEditorKind.Bitmask:
                    foreach (var b in Bits)
                        b.Sync(BitmaskCodec.IsSet(v, b.Bit));
                    BitSummary = BitmaskCodec.Summary(v, Bits.Select(b => new KeyValuePair<int, string>(b.Bit, b.Label)));
                    break;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private decimal ToDecimal(double v)
    {
        if (double.IsNaN(v))
            return 0;
        var clamped = Math.Clamp(v, -7.9e27, 7.9e27);
        // float parameters carry noise beyond 7 significant digits (0.15f is 0.1500000059...)
        return IsInteger ? Math.Round((decimal)clamped) : (decimal)Math.Round(clamped, 6);
    }
}

/// <summary>A titled card of parameter fields.</summary>
public sealed class ParamGroup(string title, IReadOnlyList<ParamField> fields)
{
    public string Title { get; } = title;

    public IReadOnlyList<ParamField> Fields { get; } = fields;
}
