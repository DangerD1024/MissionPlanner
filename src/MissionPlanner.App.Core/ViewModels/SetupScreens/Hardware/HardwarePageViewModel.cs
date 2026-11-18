using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;

/// <summary>
/// Base of the parameter-driven hardware pages: builds <see cref="ParamField"/>s from the vehicle's
/// parameters, rebuilds them when the list or its documentation reloads, keeps them current while
/// on screen, reports failed writes and tracks whether a reboot is needed.
/// </summary>
public abstract partial class HardwarePageViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(HardwarePageViewModel));

    private readonly List<ParamField> _fields = new();
    private bool _stale = true;
    private bool _shown;
    private bool _reporting;
    private int _tick;

    protected HardwarePageViewModel(string title, AppServices services, IParamSource? source = null)
        : base(title)
    {
        Services = services;
        Source = source ?? new ParamServiceSource(services);
        services.Params.ParamsChanged += (_, _) => OnParamsReloaded();
        services.Params.MetadataChanged += (_, _) => OnParamsReloaded();
    }

    public AppServices Services { get; }

    protected IParamSource Source { get; }

    protected IReadOnlyList<ParamField> Fields => _fields;

    /// <summary>A write changed a parameter that only takes effect after the autopilot restarts.</summary>
    [ObservableProperty]
    private bool _rebootRequired;

    /// <summary>A long operation is running (parameter refresh, calibration...).</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>One line of feedback under the page title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _status;

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    /// <summary>Creates the page's fields and rows from the current parameters.</summary>
    protected abstract void Build();

    /// <summary>Copies telemetry into properties; called at about 20 Hz while on screen.</summary>
    protected virtual void UpdateLive()
    {
    }

    public override void OnShown()
    {
        _shown = true;
        if (_stale)
            Rebuild();
        else
            RefreshFields();
    }

    public override void OnHidden()
    {
        _shown = false;
        foreach (var f in _fields)
            _ = f.FlushAsync();
    }

    public override void Tick()
    {
        UpdateLive();
        // values change behind the page too (full parameter list, background PARAM_VALUEs)
        if (++_tick % 20 == 0)
            RefreshFields();
    }

    /// <summary>Discards the fields and builds them again from the current parameters.</summary>
    protected void Rebuild()
    {
        foreach (var f in _fields)
        {
            f.Written -= OnFieldWritten;
            f.WriteFailed -= OnFieldFailed;
        }

        _fields.Clear();
        _stale = false;
        try
        {
            Build();
        }
        catch (Exception ex)
        {
            Log.Error("build " + Title, ex);
            Status = "This page could not be set up: " + ex.Message;
        }
    }

    protected void RefreshFields()
    {
        foreach (var f in _fields)
            f.Refresh();
        OnValuesRefreshed();
    }

    /// <summary>Called after the fields re-read the vehicle's values (on show, after writes, once a second).</summary>
    protected virtual void OnValuesRefreshed()
    {
    }

    private void OnParamsReloaded()
    {
        if (_shown)
            Rebuild();
        else
            _stale = true;
    }

    /// <summary>A field for the parameter, or null if the vehicle does not have it.</summary>
    protected ParamField? Field(string name, ParamFieldSpec? spec = null)
    {
        if (!Source.Has(name))
            return null;
        var field = new ParamField(Source, name, spec);
        field.Written += OnFieldWritten;
        field.WriteFailed += OnFieldFailed;
        _fields.Add(field);
        return field;
    }

    /// <summary>A field for the first of several names (renamed parameters) the vehicle has.</summary>
    protected ParamField? Field(IEnumerable<string> aliases, ParamFieldSpec? spec = null)
    {
        var name = aliases.FirstOrDefault(Source.Has);
        return name == null ? null : Field(name, spec);
    }

    protected string? FirstExisting(params string[] names) => names.FirstOrDefault(Source.Has);

    protected virtual void OnFieldWritten(object? sender, ParamWrittenEventArgs e)
    {
        if (sender is ParamField { RebootRequired: true })
            RebootRequired = true;
    }

    private void OnFieldFailed(object? sender, string message) => ReportFailure(message);

    /// <summary>Writes one parameter outside a field; tells the user when it fails.</summary>
    protected async Task<bool> WriteAsync(string name, double value)
    {
        bool ok;
        try
        {
            ok = await Source.SetAsync(name, value);
        }
        catch (Exception ex)
        {
            Log.Warn("set " + name, ex);
            ok = false;
        }

        if (!ok)
            ReportFailure($"The vehicle did not accept {name} = {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        else if (Source.Meta(name)?.RebootRequired == true)
            RebootRequired = true;

        RefreshFields();
        return ok;
    }

    /// <summary>Shows a failed write to the user (one dialog at a time; later ones go to the status line).</summary>
    protected void ReportFailure(string message)
    {
        Status = message;
        if (_reporting)
            return;
        _reporting = true;
        _ = ShowFailureAsync(message);
    }

    private async Task ShowFailureAsync(string message)
    {
        try
        {
            await Services.Dialogs.ShowMessageAsync("Parameter not set",
                message + "\n\nCheck the link to the vehicle and that the value is allowed.", MessageKind.Error);
        }
        catch (Exception ex)
        {
            Log.Warn("dialog", ex);
        }
        finally
        {
            _reporting = false;
        }
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        if (!Services.Session.IsConnected)
            return;
        if (!await Services.Dialogs.ConfirmAsync("Reboot autopilot",
                "Restart the flight controller now? The link drops while it reboots.", "Reboot"))
            return;

        var mav = Services.Session.Mav;
        bool ok;
        try
        {
            ok = await Task.Run(() => mav.doReboot(false, true));
        }
        catch (Exception ex)
        {
            Log.Warn("reboot", ex);
            ok = false;
        }

        if (ok)
        {
            RebootRequired = false;
            Status = "Reboot command sent.";
        }
        else
        {
            await Services.Dialogs.ShowMessageAsync("Reboot", "The autopilot did not confirm the reboot command.", MessageKind.Warning);
        }
    }
}
