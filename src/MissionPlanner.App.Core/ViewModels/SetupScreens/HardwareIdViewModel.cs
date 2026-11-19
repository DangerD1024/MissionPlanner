using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// HW ID (ConfigHWIDs): the sensors the autopilot found, from its device-id parameters
/// (INS_ACC_ID, COMPASS_DEV_ID, BARO1_DEVID...), decoded into bus type, bus, address and device type.
/// Read-only.
/// </summary>
public sealed partial class HardwareIdViewModel : HardwarePageViewModel
{
    private IReadOnlyList<HardwareIdRow> _all = [];

    public HardwareIdViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal HardwareIdViewModel(AppServices services, IParamSource? source)
        : base("HW ID", services, source)
    {
    }

    [ObservableProperty]
    private IReadOnlyList<HardwareIdRow> _rows = [];

    /// <summary>Also list parameters whose id is 0 (no device).</summary>
    [ObservableProperty]
    private bool _showUnset;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _summary = "";

    partial void OnShowUnsetChanged(bool value) => ApplyFilter();

    partial void OnFilterChanged(string value) => ApplyFilter();

    protected override void Build()
    {
        var values = Source.Names()
            .Where(HardwareIds.IsDeviceIdParam)
            .Select(n => new KeyValuePair<string, double>(n, Source.Get(n) ?? 0));
        _all = HardwareIds.Rows(values);
        ApplyFilter();
    }

    protected override void OnValuesRefreshed()
    {
        // ids change only with the parameter list, which rebuilds the page
    }

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        Rows = _all
            .Where(r => ShowUnset || r.IsSet)
            .Where(r => f.Length == 0
                        || r.ParamName.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || r.DevType.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || r.BusType.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var set = _all.Count(r => r.IsSet);
        Summary = $"{set} device id{(set == 1 ? "" : "s")} set, {_all.Count - set} unset";
        Status = _all.Count == 0 ? "This vehicle reports no device-id parameters." : null;
    }
}
