using CommunityToolkit.Mvvm.ComponentModel;
using log4net;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// A page of the TOOLS screen (Mission Planner's Ctrl+F screen): a status line, a busy flag and a
/// runner for one blocking library call on the selected vehicle at a time.
/// </summary>
public abstract partial class ToolPageViewModel : PageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ToolPageViewModel));

    private int _tick;

    protected ToolPageViewModel(string title, string description, AppServices services)
        : base(title)
    {
        Description = description;
        Services = services;
    }

    protected AppServices Services { get; }

    /// <summary>One line under the title saying what the page is for.</summary>
    public string Description { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    [ObservableProperty]
    private bool _statusIsError;

    public bool HasStatus => Status.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommand))]
    private bool _isBusy;

    public bool IsConnected => Services.Session.IsConnected;

    /// <summary>The page talks to the vehicle (false for the offline decoders and file tools).</summary>
    public virtual bool NeedsVehicle => true;

    /// <summary>Shows the "not connected" note.</summary>
    public bool ShowNotConnected => NeedsVehicle && !IsConnected;

    /// <summary>Connected and nothing running.</summary>
    public bool CanCommand => IsConnected && !IsBusy;

    public override void Tick()
    {
        if (++_tick % 10 == 0)
        {
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(CanCommand));
            OnPropertyChanged(nameof(ShowNotConnected));
        }
    }

    public override void OnShown()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanCommand));
        OnPropertyChanged(nameof(ShowNotConnected));
    }

    protected void Report(string text, bool isError = false)
    {
        Status = text;
        StatusIsError = isError;
        if (text.Length > 0)
            Services.Notify(text, isError ? MessageKind.Error : MessageKind.Info);
    }

    /// <summary>
    /// Runs a blocking call with the selected vehicle's ids on a worker thread and reports the
    /// outcome. Returns whether it succeeded.
    /// </summary>
    protected async Task<bool> RunAsync(string what, Func<MAVLinkInterface, uint, byte, bool> action, string okText, string failText)
    {
        if (!IsConnected)
        {
            Report("Connect to a vehicle first.", true);
            return false;
        }

        if (IsBusy)
            return false;

        IsBusy = true;
        Status = what + "…";
        StatusIsError = false;
        try
        {
            var session = Services.Session;
            var v = session.Vehicle;
            var ok = await Task.Run(() => action(session.Mav, v.sysid, v.compid));
            Report(ok ? okText : failText, !ok);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn(what, ex);
            Report($"{failText} ({ex.Message})", true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
