using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// Rangefinder (ConfigHWRangeFinder): each RNGFNDn instance's type, orientation, range, pins and
/// position, with the live distance and voltage. Edits write at once; a new type needs a reboot
/// before its own settings appear.
/// </summary>
public sealed partial class RangefinderViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(RangefinderViewModel));

    /// <summary>The settings shown first for each instance (aliases: 4.6 changed MIN_CM to MIN in metres).</summary>
    internal static readonly IReadOnlyList<string[]> MainKeys =
    [
        ["ORIENT"], ["MIN", "MIN_CM"], ["MAX", "MAX_CM"], ["GNDCLR", "GNDCLEAR"], ["ADDR"], ["PIN"], ["SCALING"],
        ["OFFSET"], ["FUNCTION"], ["STOP_PIN"], ["POS_X"], ["POS_Y"], ["POS_Z"],
    ];

    public RangefinderViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal RangefinderViewModel(AppServices services, IParamSource? source)
        : base("Rangefinder", services, source)
    {
    }

    [ObservableProperty]
    private IReadOnlyList<InstancePanel> _instances = [];

    [ObservableProperty]
    private InstancePanel? _selected;

    /// <summary>Settings shared by all rangefinders (RNGFND_FILT, RNGFND_LANDING...).</summary>
    [ObservableProperty]
    private IReadOnlyList<ParamField> _general = [];

    [ObservableProperty]
    private bool _hasGeneral;

    [ObservableProperty]
    private string _distanceText = "";

    [ObservableProperty]
    private string _voltageText = "";

    /// <summary>A type was changed: the new driver's parameters arrive after a reboot and refresh.</summary>
    [ObservableProperty]
    private bool _typeChanged;

    protected override void Build()
    {
        var names = Source.Names();
        var found = SensorInstances.Find(Source.Has, "RNGFND", "TYPE");
        var panels = new List<InstancePanel>();
        var instanceParams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inst in found)
        {
            var type = Field(inst.Prefix + "TYPE", new ParamFieldSpec { Kind = ParamEditorKind.Options, Label = "Type" });
            var numbered = inst.Prefix != "RNGFND_";
            var (main, rest) = SensorInstances.Split(names, inst.Prefix, MainKeys,
                n => n == inst.Prefix + "TYPE" || (!numbered && IsGeneralKey(n)));
            var panel = new InstancePanel(inst, inst.Title("Rangefinder"), type,
                main.Select(n => Field(n)).OfType<ParamField>().ToList(),
                rest.Select(n => Field(n)).OfType<ParamField>().ToList());
            if (type != null)
                type.Written += (_, e) => OnTypeWritten(panel, e);
            panels.Add(panel);
            instanceParams.Add(inst.Prefix + "TYPE");
            instanceParams.UnionWith(main);
            instanceParams.UnionWith(rest);
        }

        var general = names
            .Where(n => n.StartsWith("RNGFND_", StringComparison.Ordinal) && !instanceParams.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => Field(n))
            .OfType<ParamField>()
            .ToList();

        var keep = Selected?.Number;
        Instances = panels;
        General = general;
        HasGeneral = general.Count > 0;
        Selected = panels.FirstOrDefault(p => p.Number == keep) ?? panels.FirstOrDefault(p => p.IsEnabled) ?? panels.FirstOrDefault();
        Status = panels.Count == 0 ? "This vehicle has no RNGFND parameters." : null;
    }

    // on firmware with an unnumbered first rangefinder, these RNGFND_ settings belong to the library
    private static bool IsGeneralKey(string name) =>
        name is "RNGFND_FILT" or "RNGFND_LANDING" or "RNGFND_GAIN" or "RNGFND_MAX_RATE" or "RNGFND_SQ_MIN";

    protected override void OnValuesRefreshed()
    {
        foreach (var p in Instances)
            p.Update();
    }

    private void OnTypeWritten(InstancePanel panel, ParamWrittenEventArgs e)
    {
        panel.Update();
        if (e.OldValue != e.NewValue)
        {
            TypeChanged = true;
            RebootRequired = true;
        }
    }

    protected override void UpdateLive()
    {
        var cs = Services.Session.Cs;
        var unit = CurrentState.AltUnit is { Length: > 0 } u ? u : "m";
        DistanceText = cs.sonarrange.ToString("0.00", CultureInfo.InvariantCulture) + " " + unit;
        VoltageText = cs.sonarvoltage.ToString("0.00", CultureInfo.InvariantCulture) + " V";
        foreach (var p in Instances)
            p.SetReading(p.IsEnabled ? ReadingFor(cs, p.Number) : null);
    }

    /// <summary>The instance's DISTANCE_SENSOR distance (sensor id = instance - 1).</summary>
    internal static string ReadingFor(CurrentState cs, int instance)
    {
        uint cm = instance switch
        {
            1 => cs.rangefinder1, 2 => cs.rangefinder2, 3 => cs.rangefinder3, 4 => cs.rangefinder4,
            5 => cs.rangefinder5, 6 => cs.rangefinder6, 7 => cs.rangefinder7, 8 => cs.rangefinder8,
            9 => cs.rangefinder9, 10 => cs.rangefinder10,
            _ => 0,
        };
        return cm == 0 ? "no reading" : (cm / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " m";
    }

    /// <summary>Downloads the parameters again (the new driver's settings after a reboot).</summary>
    [RelayCommand]
    private async Task RefreshParamsAsync()
    {
        if (!Services.Session.IsConnected)
            return;
        IsBusy = true;
        Status = "Getting parameters…";
        try
        {
            await Services.Params.RefreshAsync();
            TypeChanged = false;
            Status = null;
        }
        catch (Exception ex)
        {
            Log.Warn("refresh", ex);
            Status = "Could not refresh parameters: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
