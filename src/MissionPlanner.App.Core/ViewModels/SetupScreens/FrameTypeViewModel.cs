using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>A selectable frame class or type, with the motor layout it stands for.</summary>
public sealed partial class FrameTile : ObservableObject
{
    public FrameTile(int value, string label, MotorLayout? layout, Func<FrameTile, Task> select)
    {
        Value = value;
        Label = label;
        Layout = layout;
        SelectCommand = new AsyncRelayCommand(() => select(this));
    }

    public int Value { get; }

    public string Label { get; }

    public MotorLayout? Layout { get; }

    public bool HasLayout => Layout != null;

    public IAsyncRelayCommand SelectCommand { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Frame Type (ConfigFrameClassType / ConfigFrameType): FRAME_CLASS and FRAME_TYPE as tiles (Q_ on
/// QuadPlanes), or the single FRAME of old copters. Choosing a tile writes at once, after confirming.
/// </summary>
public sealed partial class FrameTypeViewModel : HardwarePageViewModel
{
    private readonly MotorLayoutCatalog _layouts;
    private double? _shownClass;
    private double? _shownType;

    public FrameTypeViewModel(AppServices services)
        : this(services, null, null)
    {
    }

    internal FrameTypeViewModel(AppServices services, IParamSource? source, MotorLayoutCatalog? layouts)
        : base("Frame Type", services, source)
    {
        _layouts = layouts ?? MotorLayoutCatalog.Default;
    }

    /// <summary>FRAME_CLASS / FRAME_TYPE (or Q_FRAME_CLASS / Q_FRAME_TYPE).</summary>
    [ObservableProperty]
    private bool _isClassType;

    /// <summary>The single FRAME parameter (Copter before 3.5).</summary>
    [ObservableProperty]
    private bool _isLegacy;

    /// <summary>Multirotor frames get motor layout drawings; rover and boat frames are plain tiles.</summary>
    [ObservableProperty]
    private bool _isMultirotor;

    [ObservableProperty]
    private string _classParam = "";

    [ObservableProperty]
    private string _typeParam = "";

    [ObservableProperty]
    private IReadOnlyList<FrameTile> _classes = [];

    [ObservableProperty]
    private IReadOnlyList<FrameTile> _types = [];

    [ObservableProperty]
    private IReadOnlyList<FrameTile> _legacyFrames = [];

    [ObservableProperty]
    private bool _hasTypes;

    [ObservableProperty]
    private string _typesNote = "";

    [ObservableProperty]
    private string _currentText = "";

    [ObservableProperty]
    private MotorLayout? _currentLayout;

    public bool HasCurrentLayout => CurrentLayout != null;

    partial void OnCurrentLayoutChanged(MotorLayout? value) => OnPropertyChanged(nameof(HasCurrentLayout));

    protected override void Build()
    {
        _shownClass = _shownType = null;
        Classes = [];
        Types = [];
        LegacyFrames = [];

        var cls = FirstExisting("FRAME_CLASS", "Q_FRAME_CLASS");
        if (cls != null)
        {
            IsClassType = true;
            IsLegacy = false;
            ClassParam = cls;
            TypeParam = cls.StartsWith("Q_", StringComparison.Ordinal) ? "Q_FRAME_TYPE" : "FRAME_TYPE";
            IsMultirotor = cls.StartsWith("Q_", StringComparison.Ordinal) || Services.Session.Cs.firmware == Firmwares.ArduCopter2;
            BuildClasses();
        }
        else if (Source.Has("FRAME"))
        {
            IsClassType = false;
            IsLegacy = true;
            IsMultirotor = true;
            ClassParam = "FRAME";
            TypeParam = "";
            LegacyFrames = FrameCatalog.LegacyFrames
                .Select(f => new FrameTile(f.Value, f.Label, _layouts.Find(f.LayoutClass, f.LayoutType), SelectLegacyAsync))
                .ToList();
        }
        else
        {
            IsClassType = IsLegacy = false;
            Status = "This vehicle has no frame parameters.";
        }

        Sync();
    }

    private void BuildClasses()
    {
        var documented = Source.Meta(ClassParam)?.Values;
        IEnumerable<(int Value, string Label)> classes = documented is { Count: > 0 }
            ? documented.Select(v => ((int)v.Key, v.Value))
            : IsMultirotor
                ? Enum.GetValues<motor_frame_class>().Select(c => ((int)c, FrameCatalog.ClassName((int)c)))
                : [];

        var list = classes
            .Select(c => new FrameTile(c.Value, c.Label, IsMultirotor ? FrameCatalog.ClassPreview(c.Value, _layouts) : null, SelectClassAsync))
            .ToList();

        // always show the vehicle's own value, documented or not
        if (Source.Get(ClassParam) is { } current && list.All(t => t.Value != (int)current))
            list.Add(new FrameTile((int)current, FrameCatalog.ClassName((int)current), FrameCatalog.ClassPreview((int)current, _layouts), SelectClassAsync));
        Classes = list;
    }

    protected override void OnValuesRefreshed() => Sync();

    /// <summary>Marks the vehicle's current class/type and lists the types valid for that class.</summary>
    private void Sync()
    {
        if (IsLegacy)
        {
            var frame = Source.Get("FRAME");
            foreach (var t in LegacyFrames)
                t.IsSelected = frame is { } f && t.Value == (int)f;
            var sel = LegacyFrames.FirstOrDefault(t => t.IsSelected);
            CurrentText = sel != null ? "Frame: " + sel.Label : frame is { } fv ? $"Frame: {fv}" : "";
            CurrentLayout = sel?.Layout;
            return;
        }

        if (!IsClassType)
            return;

        var cls = Source.Get(ClassParam);
        var typ = Source.Has(TypeParam) ? Source.Get(TypeParam) : null;
        if (cls == _shownClass && typ == _shownType && Types.Count + Classes.Count > 0)
            return;

        var classChanged = cls != _shownClass;
        _shownClass = cls;
        _shownType = typ;

        foreach (var t in Classes)
            t.IsSelected = cls is { } c && t.Value == (int)c;

        if (classChanged || Types.Count == 0)
            BuildTypes(cls is { } cv ? (int)cv : 0);

        foreach (var t in Types)
            t.IsSelected = typ is { } ty && t.Value == (int)ty;

        var classLabel = Classes.FirstOrDefault(t => t.IsSelected)?.Label ?? (cls is { } cl ? FrameCatalog.ClassName((int)cl) : "?");
        var typeLabel = HasTypes ? Types.FirstOrDefault(t => t.IsSelected)?.Label ?? (typ is { } tl ? TypeLabel((int)tl) : "") : "";
        CurrentText = HasTypes && typeLabel.Length > 0 ? $"{classLabel} {typeLabel}" : classLabel;
        CurrentLayout = IsMultirotor && cls is { } lc
            ? FrameCatalog.Preview((int)lc, typ is { } lt ? (int)lt : 0, _layouts) ?? FrameCatalog.ClassPreview((int)lc, _layouts)
            : null;
    }

    private void BuildTypes(int frameClass)
    {
        if (!Source.Has(TypeParam))
        {
            Types = [];
            HasTypes = false;
            TypesNote = "";
            return;
        }

        IReadOnlyList<int> values;
        if (IsMultirotor)
        {
            values = FrameCatalog.ValidTypes(frameClass, _layouts);
        }
        else
        {
            values = Source.Meta(TypeParam)?.Values.Select(v => (int)v.Key).ToList() ?? [];
        }

        Types = values
            .Select(v => new FrameTile(v, TypeLabel(v), IsMultirotor ? FrameCatalog.Preview(frameClass, v, _layouts) : null, SelectTypeAsync))
            .ToList();
        HasTypes = Types.Count > 0;
        TypesNote = HasTypes ? "" : "This frame class does not use a frame type.";
    }

    private string TypeLabel(int type)
    {
        var label = Source.Meta(TypeParam)?.Values.FirstOrDefault(v => (int)v.Key == type).Value;
        return string.IsNullOrEmpty(label) ? FrameCatalog.TypeName(type) : label;
    }

    private async Task SelectClassAsync(FrameTile tile)
    {
        if (IsBusy || Source.Get(ClassParam) is { } now && (int)now == tile.Value)
            return;

        var currentType = Source.Get(TypeParam) is { } t ? (int)t : (int)motor_frame_type.MOTOR_FRAME_TYPE_X;
        var newType = IsMultirotor ? FrameCatalog.TypeForClass(tile.Value, currentType, _layouts) : currentType;
        var typeNote = Source.Has(TypeParam) && newType != currentType ? $" with frame type {TypeLabel(newType)}" : "";
        if (!await Services.Dialogs.ConfirmAsync("Frame class",
                $"Set the frame class to {tile.Label}{typeNote}?\n\n{ClassParam} is written to the vehicle now and takes effect after the autopilot reboots.",
                "Set frame"))
            return;

        await WriteFrameAsync(tile.Value, Source.Has(TypeParam) ? newType : null);
    }

    private async Task SelectTypeAsync(FrameTile tile)
    {
        if (IsBusy || Source.Get(TypeParam) is { } now && (int)now == tile.Value)
            return;
        if (!await Services.Dialogs.ConfirmAsync("Frame type",
                $"Set the frame type to {tile.Label}?\n\n{TypeParam} is written to the vehicle now; reboot the autopilot afterwards.",
                "Set frame"))
            return;

        await WriteFrameAsync(Source.Get(ClassParam) is { } c ? (int)c : null, tile.Value);
    }

    private async Task SelectLegacyAsync(FrameTile tile)
    {
        if (IsBusy || Source.Get("FRAME") is { } now && (int)now == tile.Value)
            return;
        if (!await Services.Dialogs.ConfirmAsync("Frame",
                $"Set the frame to {tile.Label}?\n\nFRAME is written to the vehicle now; reboot the autopilot afterwards.", "Set frame"))
            return;

        IsBusy = true;
        try
        {
            if (await WriteAsync("FRAME", tile.Value))
            {
                RebootRequired = true;
                Status = "Frame set to " + tile.Label + ".";
            }
        }
        finally
        {
            IsBusy = false;
            Sync();
        }
    }

    /// <summary>Writes the class then the type, as Mission Planner does on every selection.</summary>
    private async Task WriteFrameAsync(int? frameClass, int? frameType)
    {
        IsBusy = true;
        try
        {
            var ok = true;
            if (frameClass is { } c)
                ok = await WriteAsync(ClassParam, c);
            if (ok && frameType is { } t && Source.Has(TypeParam))
                ok = await WriteAsync(TypeParam, t);
            if (ok)
            {
                RebootRequired = true;
                Status = "Frame saved. Reboot the autopilot for it to take effect.";
            }
        }
        finally
        {
            IsBusy = false;
            Sync();
        }
    }
}
