using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using log4net;
using MissionPlanner.App.Core.Session;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.Params;

/// <summary>A parameter value with its documentation, as screens show it.</summary>
public sealed record ParamEntry(string Name, double Value, double? Default, ParamMeta? Meta);

/// <summary>Result of writing several parameters.</summary>
public sealed record ParamWriteResult(int Written, IReadOnlyList<string> Failed, bool RebootRequired);

/// <summary>
/// The connected vehicle's parameters and their documentation. Values live in the library's
/// <c>MAVState.param</c>; writes are serialised because the library holds the port during each.
/// Events are raised on the UI thread.
/// </summary>
public sealed partial class ParamService : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ParamService));

    private readonly Services.AppServices _services;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private IReadOnlyDictionary<string, ParamMeta> _meta = new Dictionary<string, ParamMeta>();

    public ParamService(Services.AppServices services)
    {
        _services = services;
        services.Session.Mav.ParamListChanged += (_, _) => services.Dispatcher.Post(OnParamsReplaced);
        services.Session.StateChanged += (_, state) =>
        {
            if (state == LinkState.Connected)
                services.Dispatcher.Post(() => _ = LoadMetadataAsync());
            else if (state == LinkState.Disconnected)
                services.Dispatcher.Post(OnParamsReplaced);
        };
    }

    private VehicleSession Session => _services.Session;

    /// <summary>Raised on the UI thread when the whole list is (re)loaded or the vehicle changes.</summary>
    public event EventHandler? ParamsChanged;

    /// <summary>Raised on the UI thread when documentation for the current vehicle has loaded.</summary>
    public event EventHandler? MetadataChanged;

    [ObservableProperty]
    private bool _metadataLoaded;

    public int Count => Snapshot().Count;

    public bool HasParams => Count > 0;

    public bool Has(string name) => Session.Vehicle.param.ContainsKey(name);

    public double? Get(string name)
    {
        var p = Session.Vehicle.param[name];
        return p?.Value;
    }

    /// <summary>The first of several aliases (renamed parameters) that exists on this vehicle.</summary>
    public string? FirstExisting(params string[] names) => names.FirstOrDefault(Has);

    public ParamMeta? Meta(string name)
    {
        if (_meta.TryGetValue(name, out var m))
            return m;

        // instance-numbered names share docs with their first instance in some files
        return null;
    }

    /// <summary>A copy of the current list, safe to enumerate while the link updates it.</summary>
    public IReadOnlyList<ParamEntry> Snapshot()
    {
        List<MAVLink.MAVLinkParam> list;
        try
        {
            list = Session.Vehicle.param.ToList();
        }
        catch (InvalidOperationException)
        {
            // modified while copying; one retry is enough in practice
            list = Session.Vehicle.param.ToList();
        }

        return list
            .Where(p => p != null)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new ParamEntry(p.Name, p.Value, p.default_value, Meta(p.Name)))
            .ToList();
    }

    /// <summary>Writes one parameter. Returns false if the vehicle rejected it or does not have it.</summary>
    public async Task<bool> SetAsync(string name, double value)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var v = Session.Vehicle;
            return await Task.Run(() => Session.Mav.setParam(v.sysid, v.compid, name, value)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("set " + name, ex);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Writes several parameters, *_ENABLE ones first (they create others), and reports which failed
    /// and whether any needs a reboot.
    /// </summary>
    public async Task<ParamWriteResult> SetManyAsync(IReadOnlyDictionary<string, double> values, IProgress<OperationProgress>? progress = null)
    {
        var ordered = values.Keys.OrderByDescending(k => k.EndsWith("ENABLE", StringComparison.Ordinal)).ThenBy(k => k, StringComparer.Ordinal).ToList();
        var failed = new List<string>();
        var reboot = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var name = ordered[i];
            progress?.Report(new OperationProgress(i * 100 / Math.Max(1, ordered.Count), "Writing " + name));
            if (await SetAsync(name, values[name]).ConfigureAwait(false))
                reboot |= Meta(name)?.RebootRequired == true;
            else
                failed.Add(name);
        }

        progress?.Report(new OperationProgress(100, "Done"));

        // enabling a feature adds parameters: fetch the list again when the count no longer matches
        var list = Session.Vehicle.param;
        if (list.TotalReceived != list.TotalReported && !Session.Cs.armed)
            await RefreshAsync(progress).ConfigureAwait(false);

        return new ParamWriteResult(ordered.Count - failed.Count, failed, reboot);
    }

    /// <summary>Downloads the full list again (MAVFTP when available).</summary>
    public async Task RefreshAsync(IProgress<OperationProgress>? progress = null)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var v = Session.Vehicle;
            progress?.Report(new OperationProgress(-1, "Getting parameters…"));
            await Task.Run(() => Session.Mav.getParamListMavftp(v.sysid, v.compid)).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        _services.Dispatcher.Post(OnParamsReplaced);
    }

    /// <summary>Saves the current values to the vehicle's flash (normally automatic).</summary>
    public Task<bool> CommitToFlashAsync()
    {
        var v = Session.Vehicle;
        return Task.Run(() => Session.Mav.doCommand(v.sysid, v.compid, MAVLink.MAV_CMD.PREFLIGHT_STORAGE, 1, 0, 0, 0, 0, 0, 0));
    }

    public static Dictionary<string, double> LoadFile(string path) => ParamFile.loadParamFile(path);

    public void SaveFile(string path, IEnumerable<ParamEntry>? entries = null)
    {
        var table = new Hashtable();
        foreach (var e in entries ?? Snapshot())
            table[e.Name] = e.Value;
        ParamFile.SaveParamFile(path, table);
    }

    public async Task LoadMetadataAsync()
    {
        var cs = Session.Cs;
        try
        {
            _meta = await ParamMetadata.LoadAsync(cs.firmware, cs.version).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("metadata", ex);
        }

        _services.Dispatcher.Post(() =>
        {
            MetadataLoaded = _meta.Count > 0;
            MetadataChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnParamsReplaced()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasParams));
        ParamsChanged?.Invoke(this, EventArgs.Empty);
    }
}
