using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.FlightData;

/// <summary>One live value in the raw sensor window.</summary>
public sealed partial class RawValue : ObservableObject
{
    private readonly PropertyInfo _property;

    internal RawValue(string label, PropertyInfo property, double min, double max)
    {
        Label = label;
        _property = property;
        Min = min;
        Max = max;
    }

    public string Label { get; }

    public double Min { get; }

    public double Max { get; }

    [ObservableProperty]
    private double _value;

    [ObservableProperty]
    private string _text = "";

    internal void Read(CurrentState cs)
    {
        var v = Convert.ToDouble(_property.GetValue(cs) ?? 0, CultureInfo.InvariantCulture);
        Value = Math.Clamp(v, Min, Max);
        Text = Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Mission Planner's Raw Sensor View: accelerometers, gyros and magnetometers of every IMU, the RC
/// inputs and the servo outputs, as the vehicle reports them.
/// </summary>
public sealed partial class RawSensorsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private int _tick;

    public RawSensorsViewModel(AppServices services)
        : base("Raw sensors")
    {
        _services = services;
        var t = typeof(CurrentState);
        RawValue? V(string label, string name, double min, double max) =>
            t.GetProperty(name) is { } p && (p.PropertyType == typeof(float) || p.PropertyType == typeof(double) || p.PropertyType == typeof(int))
                ? new RawValue(label, p, min, max)
                : null;

        foreach (var (suffix, title) in new[] { ("", "IMU 1"), ("2", "IMU 2"), ("3", "IMU 3") })
        {
            var group = new List<RawValue?>
            {
                V("Accel X", "ax" + suffix, -2000, 2000), V("Accel Y", "ay" + suffix, -2000, 2000), V("Accel Z", "az" + suffix, -2000, 2000),
                V("Gyro X", "gx" + suffix, -2000, 2000), V("Gyro Y", "gy" + suffix, -2000, 2000), V("Gyro Z", "gz" + suffix, -2000, 2000),
                V("Mag X", "mx" + suffix, -1000, 1000), V("Mag Y", "my" + suffix, -1000, 1000), V("Mag Z", "mz" + suffix, -1000, 1000),
            }.Where(v => v != null).Select(v => v!).ToList();
            if (group.Count > 0)
                Imus.Add(new RawGroup(title, group));
        }

        for (var ch = 1; ch <= 16; ch++)
        {
            if (V($"RC {ch}", $"ch{ch}in", 800, 2200) is { } rc)
                RcIn.Add(rc);
            if (V($"Servo {ch}", $"ch{ch}out", 800, 2200) is { } so)
                ServoOut.Add(so);
        }
    }

    public sealed record RawGroup(string Title, IReadOnlyList<RawValue> Values);

    public List<RawGroup> Imus { get; } = new();

    public List<RawValue> RcIn { get; } = new();

    public List<RawValue> ServoOut { get; } = new();

    [ObservableProperty]
    private string _note = "";

    public override void OnShown() => Read();

    public override void Tick()
    {
        if (++_tick % 2 == 0)
            Read();
    }

    private void Read()
    {
        var cs = _services.Session.Cs;
        foreach (var v in Imus.SelectMany(g => g.Values).Concat(RcIn).Concat(ServoOut))
            v.Read(cs);
        Note = _services.Session.IsConnected
            ? "Raw IMU values come from RAW_IMU / SCALED_IMU2/3 (enable the RAW_SENS stream rate if they stay at 0)."
            : "Not connected.";
    }
}
