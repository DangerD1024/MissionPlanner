using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// ADSB (ConfigADSB): every ADSB_ (receiver / transponder) and AVD_ (avoidance) parameter with its
/// documentation. As in Mission Planner, edits are held until Write Params; ADSB_TYPE first since
/// enabling it adds the others.
/// </summary>
public sealed partial class AdsbSetupViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AdsbSetupViewModel));

    private static readonly ParamFieldSpec Queued = new() { Mode = ParamWriteMode.Queued };

    public AdsbSetupViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal AdsbSetupViewModel(AppServices services, IParamSource? source)
        : base("ADSB", services, source)
    {
    }

    [ObservableProperty]
    private IReadOnlyList<ParamGroup> _groups = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(WriteText))]
    private int _changeCount;

    public bool HasChanges => ChangeCount > 0;

    public string WriteText => ChangeCount > 0 ? $"Write Params ({ChangeCount})" : "Write Params";

    partial void OnFilterChanged(string value) => Group();

    protected override void Build()
    {
        foreach (var name in Source.Names()
                     .Where(n => n.StartsWith("ADSB_", StringComparison.Ordinal) || n.StartsWith("AVD_", StringComparison.Ordinal))
                     .OrderBy(Order).ThenBy(n => n, StringComparer.Ordinal))
        {
            if (Field(name, Queued) is { } f)
                f.Edited += OnEdited;
        }

        Group();
        Count();
        Status = Fields.Count == 0 ? "This vehicle has no ADSB_ parameters." : null;
    }

    // the switches first: ADSB_TYPE (and AVD_ENABLE) decide whether the rest matters
    private static int Order(string name) => name switch
    {
        "ADSB_TYPE" or "ADSB_ENABLE" => 0,
        "AVD_ENABLE" => 1,
        _ when name.StartsWith("ADSB_", StringComparison.Ordinal) => 2,
        _ => 3,
    };

    private void Group()
    {
        var f = Filter.Trim();
        bool Match(ParamField p) => f.Length == 0
                                    || p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                                    || p.Label.Contains(f, StringComparison.OrdinalIgnoreCase);
        var adsb = Fields.Where(p => p.Name.StartsWith("ADSB_", StringComparison.Ordinal) && Match(p)).ToList();
        var avd = Fields.Where(p => p.Name.StartsWith("AVD_", StringComparison.Ordinal) && Match(p)).ToList();
        var groups = new List<ParamGroup>();
        if (adsb.Count > 0)
            groups.Add(new ParamGroup("ADS-B receiver / transponder", adsb));
        if (avd.Count > 0)
            groups.Add(new ParamGroup("Avoidance", avd));
        Groups = groups;
    }

    private void OnEdited(object? sender, EventArgs e) => Count();

    private void Count() => ChangeCount = Fields.Count(f => f.IsDirty);

    protected override void OnValuesRefreshed() => Count();

    [RelayCommand]
    private async Task WriteAsync()
    {
        var dirty = Fields.Where(f => f.IsDirty).OrderBy(f => Order(f.Name)).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        if (dirty.Count == 0)
        {
            await Services.Dialogs.ShowMessageAsync("ADSB", "No changes to write.");
            return;
        }

        IsBusy = true;
        var failed = new List<string>();
        var enabled = false;
        try
        {
            foreach (var f in dirty)
            {
                var wasOff = f.Value is 0;
                if (!await f.CommitAsync())
                    failed.Add(f.Name);
                else if (f.Name is "ADSB_TYPE" or "ADSB_ENABLE" && wasOff && f.Value is > 0)
                    enabled = true;
            }
        }
        finally
        {
            IsBusy = false;
            Count();
        }

        if (failed.Count == 0)
            Status = $"{dirty.Count} parameter{(dirty.Count == 1 ? "" : "s")} saved.";

        // switching ADS-B on adds its other parameters
        if (enabled && Services.Session.IsConnected)
            await RefreshParamsCoreAsync();
    }

    [RelayCommand]
    private void Discard()
    {
        foreach (var f in Fields.Where(f => f.IsDirty))
            f.Revert();
        Count();
    }

    [RelayCommand]
    private async Task RefreshParamsAsync()
    {
        if (!Services.Session.IsConnected)
            return;
        if (Services.Session.Cs.armed &&
            !await Services.Dialogs.ConfirmAsync("Refresh Params", "The vehicle is armed. Download all parameters again anyway?", "Refresh"))
            return;
        await RefreshParamsCoreAsync();
    }

    private async Task RefreshParamsCoreAsync()
    {
        IsBusy = true;
        Status = "Getting parameters…";
        try
        {
            await Services.Params.RefreshAsync();
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
