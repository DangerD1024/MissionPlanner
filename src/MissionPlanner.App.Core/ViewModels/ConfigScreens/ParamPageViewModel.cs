using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.App.Core.Params;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// What the Full Parameter List, Full Parameter Tree and User Params pages share: rows with
/// the right editor per parameter, edits queued until Write Params, refresh and reboot.
/// </summary>
public abstract partial class ParamPageViewModel : PageViewModel, IParamRowHost
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ParamPageViewModel));

    private readonly ParamEditBuffer _edits = new();
    private Dictionary<string, ParamRow> _rows = new(StringComparer.Ordinal);

    protected ParamPageViewModel(string title, AppServices services)
        : base(title)
    {
        Services = services;
        _edits.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(HasPending));
            OnEditsChanged();
        };
    }

    public AppServices Services { get; }

    /// <summary>Edits waiting for Write Params.</summary>
    public ParamEditBuffer Edits => _edits;

    public int PendingCount => _edits.Count;

    public bool HasPending => _edits.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshCommand), nameof(RebootCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshCommand), nameof(RebootCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>The rows edits apply to, by name.</summary>
    protected IReadOnlyDictionary<string, ParamRow> RowsByName => _rows;

    public override void Tick() => IsConnected = Services.Session.IsConnected;

    /// <summary>After the pending set changed.</summary>
    protected virtual void OnEditsChanged()
    {
    }

    /// <summary>After <see cref="IsBusy"/> changed.</summary>
    protected virtual void OnBusyChanged()
    {
    }

    /// <summary>After a row's edit was queued, dropped, written or discarded.</summary>
    protected virtual void OnRowsEdited()
    {
    }

    partial void OnIsBusyChanged(bool value) => OnBusyChanged();

    /// <summary>
    /// Makes <paramref name="rows"/> the page's rows: their editors report here, queued edits
    /// show on them again, and edits for parameters no longer listed are dropped.
    /// </summary>
    protected void AttachRows(IEnumerable<ParamRow> rows)
    {
        var byName = new Dictionary<string, ParamRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            byName[row.Name] = row;
            row.Host = this;
            if (_edits.TryGet(row.Name, out var pending))
                row.PendingValue = _edits.Set(row.Name, pending, row.Value) ? pending : null;
        }

        _edits.RemoveAll(_edits.Pending.Keys.Where(k => !byName.ContainsKey(k)).ToList());
        _rows = byName;
    }

    /// <summary>
    /// Takes in values the vehicle reported since the rows were built (writes from other pages).
    /// Returns false when a row's parameter is gone, so the page must be rebuilt.
    /// </summary>
    protected bool SyncRowValues(IReadOnlyList<ParamEntry> snapshot)
    {
        var values = new Dictionary<string, double>(snapshot.Count, StringComparer.Ordinal);
        foreach (var e in snapshot)
            values[e.Name] = e.Value;

        foreach (var row in _rows.Values)
        {
            if (!values.TryGetValue(row.Name, out var v))
                return false;
            if (ParamValues.Same(row.Value, v))
                continue;
            row.Value = v;
            if (row.PendingValue is { } p)
                row.PendingValue = _edits.Set(row.Name, p, row.Value) ? p : null;
        }

        return true;
    }

    /// <summary>
    /// Checks a value typed in the value cell and queues it: numbers (or a documented label) only,
    /// read-only parameters are refused, values outside the documented range need confirmation.
    /// Returns true if an edit is queued.
    /// </summary>
    public async Task<bool> CommitEditAsync(ParamRow row, string? text)
    {
        if (text == null || text.Trim() == row.ValueText)
        {
            row.IsInvalid = false;
            return false;
        }

        return await QueueCheckedAsync(row, ParamEditBuffer.Validate(row.Name, text, row.Meta));
    }

    /// <inheritdoc />
    public Task<bool> QueueValueAsync(ParamRow row, double value) =>
        QueueCheckedAsync(row, ParamEditBuffer.Validate(row.Name, value, row.Meta));

    private async Task<bool> QueueCheckedAsync(ParamRow row, EditValidation check)
    {
        switch (check.Result)
        {
            case EditCheck.NotANumber:
                row.IsInvalid = true;
                StatusText = $"{row.Name}: {check.Message}";
                return false;
            case EditCheck.ReadOnly:
                row.IsInvalid = false;
                await Services.Dialogs.ShowMessageAsync("ReadOnly", check.Message, MessageKind.Warning);
                return false;
            case EditCheck.OutOfRange:
                if (!await Services.Dialogs.ConfirmAsync("Out of range", check.Message, "Use value"))
                    return false;
                break;
        }

        row.IsInvalid = false;
        QueueEdit(row, check.Value);
        StatusText = row.IsModified ? $"{row.Name} = {row.ValueText} (not written yet)" : $"{row.Name} unchanged";
        OnRowsEdited();
        return row.IsModified;
    }

    /// <summary>Queues a value without checks (it was checked, or comes from a file the user picked).</summary>
    protected void QueueEdit(ParamRow row, double value) =>
        row.PendingValue = _edits.Set(row.Name, value, row.Value) ? value : null;

    /// <inheritdoc />
    public async Task<bool> EditBitmaskAsync(ParamRow row)
    {
        if (row.IsReadOnly)
        {
            await Services.Dialogs.ShowMessageAsync("ReadOnly", L.Format("{0} is marked as ReadOnly, and will not be changed.", row.Name),
                MessageKind.Warning);
            return false;
        }

        var editor = new BitmaskEditorViewModel(row.Name, row.Meta, row.DisplayValue, ParamTypeOf(row.Name));
        if (!await Services.Windows.ShowDialogAsync(editor, BitmaskEditorViewModel.DialogWidth, editor.DialogHeight))
            return false;
        return await QueueValueAsync(row, editor.Value);
    }

    /// <summary>
    /// The parameter's real type (so a signed bitmask with its top bit set is sent negative).
    /// Mission Planner assumes INT32 when the vehicle does not have it (a file shown offline).
    /// </summary>
    protected MAVLink.MAV_PARAM_TYPE ParamTypeOf(string name)
    {
        try
        {
            if (Services.Session.Vehicle.param[name] is { } p)
                return p.TypeAP;
        }
        catch (Exception ex)
        {
            Log.Debug("param type " + name, ex);
        }

        return MAVLink.MAV_PARAM_TYPE.INT32;
    }

    /// <summary>Whether Write Params is possible now (pages can add conditions).</summary>
    protected virtual bool CanWriteNow => IsConnected && !IsBusy;

    private bool CanWrite => CanWriteNow;

    private bool CanRefresh => IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        IsBusy = true;
        try
        {
            var written = await ParamWriteFlow.WriteAsync(Services, _edits, s => StatusText = s);
            foreach (var name in written)
            {
                if (_rows.TryGetValue(name, out var row))
                {
                    row.Value = Services.Params.Get(name) ?? row.DisplayValue;
                    row.PendingValue = null;
                }
            }

            if (written.Count > 0)
            {
                StatusText = $"Wrote {written.Count} parameter{(written.Count == 1 ? "" : "s")}.";
                OnRowsEdited();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            if (await ParamWriteFlow.RefreshAsync(Services, s => StatusText = s))
                StatusText = "Parameters refreshed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DiscardEdits()
    {
        foreach (var name in _edits.Pending.Keys.ToList())
        {
            if (_rows.TryGetValue(name, out var row))
                row.PendingValue = null;
        }

        _edits.Clear();
        StatusText = "Edits discarded.";
        OnRowsEdited();
    }

    /// <summary>The confirmation text for Reboot: stronger when the vehicle is armed.</summary>
    public static (string Title, string Message) RebootConfirmText(bool armed, int pendingEdits)
    {
        var pending = pendingEdits > 0
            ? $"\n\n{pendingEdits} edit{(pendingEdits == 1 ? " has" : "s have")} not been written; {(pendingEdits == 1 ? "it stays" : "they stay")} queued."
            : "";
        if (armed)
        {
            return ("Reboot an ARMED vehicle",
                "The vehicle is ARMED.\n\nRebooting stops the motors at once: a vehicle in the air will fall.\n" +
                "DON'T DO THIS IF YOU ARE IN THE AIR.\n\nReboot the autopilot anyway?" + pending);
        }

        return ("Reboot",
            "Reboot the autopilot?\n\nThe link usually drops while it restarts; connect again once it is back." + pending);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RebootAsync()
    {
        if (!Services.Session.IsConnected)
        {
            await Services.Dialogs.ShowMessageAsync("Not connected", "Connect to the vehicle to reboot it.", MessageKind.Error);
            return;
        }

        var (title, message) = RebootConfirmText(Services.Session.Cs.armed, _edits.Count);
        if (!await Services.Dialogs.ConfirmAsync(title, message, "Reboot"))
            return;

        IsBusy = true;
        bool sent;
        try
        {
            // MAV_CMD_PREFLIGHT_REBOOT_SHUTDOWN p1=1, sent twice without waiting for an ACK
            var mav = Services.Session.Mav;
            sent = await Task.Run(() => mav.doReboot(false, true));
        }
        catch (Exception ex)
        {
            Log.Error("reboot", ex);
            await Services.Dialogs.ShowMessageAsync("Reboot failed", "Sending the reboot command failed: " + ex.Message, MessageKind.Error);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        if (sent)
        {
            StatusText = "Reboot command sent. The autopilot restarts; the link may drop until it is back.";
        }
        else
        {
            StatusText = "The autopilot did not accept the reboot command.";
            await Services.Dialogs.ShowMessageAsync("Reboot failed", "The autopilot did not accept the reboot command.", MessageKind.Error);
        }
    }
}
