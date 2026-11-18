using MissionPlanner.App.Core.Params;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>
/// What the hardware setup pages need from the parameter store. The app uses
/// <see cref="ParamServiceSource"/>; tests substitute a dictionary.
/// </summary>
public interface IParamSource
{
    bool Has(string name);

    double? Get(string name);

    ParamMeta? Meta(string name);

    /// <summary>True when the autopilot stores the parameter as an integer type.</summary>
    bool IsInteger(string name);

    /// <summary>Names of all parameters on the vehicle (a copy).</summary>
    IReadOnlyList<string> Names();

    /// <summary>Writes one parameter; false if the vehicle rejected it or does not have it.</summary>
    Task<bool> SetAsync(string name, double value);
}

/// <summary>The live vehicle's parameters, through <see cref="ParamService"/> (writes are serialised there).</summary>
public sealed class ParamServiceSource(AppServices services) : IParamSource
{
    public bool Has(string name) => services.Params.Has(name);

    public double? Get(string name) => services.Params.Get(name) is { } v ? ParamValues.Clean(v, IsInteger(name)) : null;

    public ParamMeta? Meta(string name) => services.Params.Meta(name);

    public bool IsInteger(string name)
    {
        var p = services.Session.Vehicle.param[name];
        if (p == null)
            return false;
        return p.TypeAP is not (MAVLink.MAV_PARAM_TYPE.REAL32 or MAVLink.MAV_PARAM_TYPE.REAL64);
    }

    public IReadOnlyList<string> Names()
    {
        var list = services.Session.Vehicle.param;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return list.Keys.ToList();
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
                // the link added a parameter while copying
            }
        }
    }

    public Task<bool> SetAsync(string name, double value) => services.Params.SetAsync(name, value);
}

public static class ParamValues
{
    /// <summary>
    /// The value as the autopilot holds it. The library rounds floats to 7 significant digits in a
    /// way that turns 57 into 56.99999999999999, which breaks comparisons with documented values:
    /// integers are rounded, floats reduced to float precision.
    /// </summary>
    public static double Clean(double value, bool isInteger)
    {
        if (!double.IsFinite(value))
            return value;
        if (isInteger)
            return Math.Round(value);
        if (Math.Abs(value) >= 7.9e27)
            return value;
        // decimal(float) keeps the float's 7 significant digits exactly
        return (double)(decimal)(float)value;
    }
}
