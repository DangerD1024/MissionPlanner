using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

/// <summary>A node in the DroneCAN node list.</summary>
public sealed partial class CanNodeRow : ObservableObject
{
    public CanNodeRow(byte id) => Id = id;

    public byte Id { get; }

    [ObservableProperty]
    private string _name = "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText), nameof(IsHealthy), nameof(IsWarning), nameof(IsBad))]
    private CanNodeHealth _health;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText))]
    private CanNodeMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UptimeText))]
    private uint _uptimeSec;

    [ObservableProperty]
    private ushort _vendorStatus;

    [ObservableProperty]
    private string _softwareVersion = "";

    [ObservableProperty]
    private string _hardwareVersion = "";

    [ObservableProperty]
    private string _uniqueId = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText), nameof(IsBad))]
    private bool _isStale;

    /// <summary>When the last NodeStatus came (Environment.TickCount64).</summary>
    public long LastSeen { get; set; }

    public string HealthText => Health.ToString().ToUpperInvariant();

    public bool IsHealthy => Health == CanNodeHealth.Ok;

    public bool IsWarning => Health == CanNodeHealth.Warning;

    public bool IsBad => Health is CanNodeHealth.Error or CanNodeHealth.Critical || IsStale;

    public string ModeText => IsStale ? "NOT SEEN" : Mode switch
    {
        CanNodeMode.SoftwareUpdate => "SOFTWARE UPDATE",
        _ => Mode.ToString().ToUpperInvariant(),
    };

    public string UptimeText => FormatUptime(UptimeSec);

    public static string FormatUptime(uint seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1
            ? $"{(int)t.TotalDays}d {t:hh\\:mm\\:ss}"
            : t.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
    }

    public void Apply(CanNodeStatus status, long now)
    {
        Health = status.Health;
        Mode = status.Mode;
        UptimeSec = status.UptimeSec;
        VendorStatus = status.VendorStatus;
        LastSeen = now;
        IsStale = false;
    }

    public void Apply(CanNodeInfo info)
    {
        Name = info.Name.Length > 0 ? info.Name : "?";
        SoftwareVersion = info.SoftwareVersion;
        HardwareVersion = info.HardwareVersion;
        UniqueId = info.UniqueId;
    }
}

/// <summary>A node parameter, editable as text and checked against its type.</summary>
public sealed partial class CanParamRow : ObservableObject
{
    public CanParamRow(CanParameter parameter)
    {
        Parameter = parameter;
        _valueText = parameter.ValueText;
    }

    public CanParameter Parameter { get; private set; }

    public string Name => Parameter.Name;

    public string KindText => Parameter.Kind switch
    {
        CanParamKind.Integer => "int",
        CanParamKind.Real => "real",
        CanParamKind.Boolean => "bool",
        CanParamKind.String => "string",
        _ => "empty",
    };

    public string DefaultText => Parameter.Default is { } d ? Format(d) : "";

    public string RangeText => Parameter is { Min: { } min, Max: { } max } ? $"{Format(min)} … {Format(max)}" : "";

    public bool IsReadOnly => Parameter.Kind == CanParamKind.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(IsInvalid))]
    private string _valueText;

    public bool IsChanged => !IsReadOnly && ValueText.Trim() != Parameter.ValueText;

    public bool IsInvalid => IsChanged && !TryParse(out _, out _);

    /// <summary>The edited value in the parameter's type; false if it does not parse or is out of range.</summary>
    public bool TryParse(out double number, out string text)
    {
        number = 0;
        text = ValueText.Trim();
        switch (Parameter.Kind)
        {
            case CanParamKind.Integer:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return false;
                number = l;
                break;
            case CanParamKind.Real:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    return false;
                break;
            case CanParamKind.Boolean:
                if (text is "1" or "true" or "True")
                    number = 1;
                else if (text is "0" or "false" or "False")
                    number = 0;
                else
                    return false;
                return true;
            case CanParamKind.String:
                return text.Length <= 128;
            default:
                return false;
        }

        return (Parameter.Min is not { } min || number >= min) && (Parameter.Max is not { } max || number <= max);
    }

    /// <summary>The node now has the edited value.</summary>
    public void Commit(double number, string text)
    {
        Parameter = Parameter with { Number = number, Text = text };
        ValueText = Parameter.ValueText;
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(IsInvalid));
    }

    private static string Format(double v) => v.ToString("G7", CultureInfo.InvariantCulture);
}

/// <summary>A debug LogMessage from a node.</summary>
public sealed record CanLogRow(DateTime Time, byte NodeId, string Level, string Source, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
