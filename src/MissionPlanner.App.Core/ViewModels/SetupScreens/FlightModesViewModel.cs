using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>One position of the six-position mode switch.</summary>
public sealed partial class FlightModeSlot : ObservableObject
{
    private readonly Action _changed;

    public FlightModeSlot(int index, ParamField mode, bool simple, bool superSimple, Action changed)
    {
        Index = index;
        Mode = mode;
        _simple = simple;
        _superSimple = superSimple;
        _changed = changed;
        mode.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ParamField.SelectedOption) or nameof(ParamField.NumberValue))
                OnPropertyChanged(nameof(SimpleAllowed));
        };
    }

    public int Index { get; }

    public string Title => "Flight Mode " + (Index + 1).ToString(CultureInfo.InvariantCulture);

    public string Band => FlightModeLogic.BandText[Index];

    /// <summary>The FLTMODEn / MODEn parameter, queued until Save Modes.</summary>
    public ParamField Mode { get; }

    [ObservableProperty]
    private bool _simple;

    [ObservableProperty]
    private bool _superSimple;

    /// <summary>The switch is in this position now.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Simple modes apply only to some copter modes (Mission Planner disables the boxes otherwise).</summary>
    public bool SimpleAllowed => FlightModeLogic.SupportsSimple(Mode.SelectedOption?.Label);

    partial void OnSimpleChanged(bool value) => _changed();

    partial void OnSuperSimpleChanged(bool value) => _changed();
}

/// <summary>
/// Flight Modes (ConfigFlightModes): the six switch positions, the live mode and switch PWM with
/// the active position highlighted, Simple / Super Simple on Copter. Changes are written by Save Modes.
/// </summary>
public sealed partial class FlightModesViewModel : HardwarePageViewModel
{
    public FlightModesViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal FlightModesViewModel(AppServices services, IParamSource? source)
        : base("Flight Modes", services, source)
    {
    }

    [ObservableProperty]
    private IReadOnlyList<FlightModeSlot> _slots = [];

    [ObservableProperty]
    private bool _showSimple;

    [ObservableProperty]
    private string _currentMode = "";

    [ObservableProperty]
    private string _switchText = "";

    [ObservableProperty]
    private int _activeSlot = -1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _hasChanges;

    [ObservableProperty]
    private string _saveText = "Save Modes";

    private string? _switchParam;

    protected override void Build()
    {
        Slots = [];
        var prefix = FlightModeLogic.ModeParamPrefix(Source.Has);
        if (prefix == null)
        {
            Status = "This vehicle has no flight mode parameters.";
            return;
        }

        var firmware = Services.Session.Cs.firmware;
        var modes = FlightModeLogic.Modes(firmware, Source.Meta(prefix + "1")?.Values);
        ShowSimple = firmware == Firmwares.ArduCopter2 && Source.Has("SIMPLE");
        var simple = Source.Get("SIMPLE") ?? 0;
        var superSimple = Source.Get("SUPER_SIMPLE") ?? 0;

        var slots = new List<FlightModeSlot>();
        for (var i = 0; i < FlightModeLogic.SlotCount; i++)
        {
            var field = Field(prefix + (i + 1).ToString(CultureInfo.InvariantCulture), new ParamFieldSpec
            {
                Label = "Flight Mode " + (i + 1).ToString(CultureInfo.InvariantCulture),
                Kind = ParamEditorKind.Options,
                Options = modes.Count > 0 ? modes : null,
                Mode = ParamWriteMode.Queued,
            });
            if (field == null)
                continue;
            field.Edited += (_, _) => UpdateChanges();
            slots.Add(new FlightModeSlot(i, field, FlightModeLogic.SlotBit(simple, i), FlightModeLogic.SlotBit(superSimple, i), UpdateChanges));
        }

        Slots = slots;
        _switchParam = FlightModeLogic.SwitchChannelParam(Source.Has);
        SaveText = "Save Modes";
        UpdateChanges();
    }

    protected override void UpdateLive()
    {
        var cs = Services.Session.Cs;
        CurrentMode = cs.mode ?? "";

        double pwm = 0;
        if (_switchParam != null && Source.Get(_switchParam) is { } ch)
        {
            var channel = (int)ch;
            pwm = channel is >= 1 and <= 16 ? Channels.In(cs, channel) : 0;
            SwitchText = $"{_switchParam} {channel}: {pwm:0}";
        }
        else
        {
            SwitchText = "No mode switch channel";
        }

        var slot = FlightModeLogic.PwmToSlot(pwm);
        if (slot != ActiveSlot)
        {
            ActiveSlot = slot;
            foreach (var s in Slots)
                s.IsActive = s.Index == slot;
        }
    }

    private double SimpleMask(Func<FlightModeSlot, bool> pick)
    {
        var bits = new bool[FlightModeLogic.SlotCount];
        foreach (var s in Slots)
            bits[s.Index] = pick(s);
        return FlightModeLogic.SlotMask(bits);
    }

    private void UpdateChanges()
    {
        var changed = Slots.Any(s => s.Mode.IsDirty);
        if (ShowSimple)
        {
            changed |= SimpleMask(s => s.Simple) != (Source.Get("SIMPLE") ?? 0);
            if (Source.Has("SUPER_SIMPLE"))
                changed |= SimpleMask(s => s.SuperSimple) != (Source.Get("SUPER_SIMPLE") ?? 0);
        }

        HasChanges = changed;
        if (changed)
            SaveText = "Save Modes";
    }

    private bool CanSave() => HasChanges && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        SaveCommand.NotifyCanExecuteChanged();
        var ok = true;
        try
        {
            foreach (var s in Slots)
                ok &= await s.Mode.CommitAsync();

            if (ShowSimple)
            {
                var simple = SimpleMask(s => s.Simple);
                if (simple != (Source.Get("SIMPLE") ?? 0))
                    ok &= await WriteAsync("SIMPLE", simple);
                if (Source.Has("SUPER_SIMPLE"))
                {
                    var super = SimpleMask(s => s.SuperSimple);
                    if (super != (Source.Get("SUPER_SIMPLE") ?? 0))
                        ok &= await WriteAsync("SUPER_SIMPLE", super);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        UpdateChanges();
        SaveText = ok ? "Complete" : "Save Modes";
        Status = ok ? "Flight modes saved." : "Some flight modes were not saved.";
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Revert()
    {
        foreach (var s in Slots)
            s.Mode.Revert();
        var simple = Source.Get("SIMPLE") ?? 0;
        var superSimple = Source.Get("SUPER_SIMPLE") ?? 0;
        foreach (var s in Slots)
        {
            s.Simple = FlightModeLogic.SlotBit(simple, s.Index);
            s.SuperSimple = FlightModeLogic.SlotBit(superSimple, s.Index);
        }

        UpdateChanges();
    }
}
