using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using log4net;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Hardware;
using MissionPlanner.ArduPilot.Mavlink;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>One SERIALn port.</summary>
public sealed partial class SerialPortRow(int number, ParamField? protocol, ParamField? baud, ParamField? options, ParamField? rtsCts)
    : ObservableObject
{
    public int Number { get; } = number;

    public string Title { get; } = "SERIAL" + number.ToString(CultureInfo.InvariantCulture);

    public ParamField? Protocol { get; } = protocol;

    public ParamField? Baud { get; } = baud;

    public ParamField? Options { get; } = options;

    public ParamField? RtsCts { get; } = rtsCts;

    /// <summary>The board's name for the port (from @SYS/uarts.txt), e.g. UART7.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUartName))]
    private string _uartName = "";

    public bool HasUartName => UartName.Length > 0;
}

/// <summary>
/// Serial Ports (ConfigSerial): protocol, baud, options and flow control for SERIAL1..N. Choosing a
/// protocol applies the presets in SerialOptionRules.json; changes need a reboot.
/// </summary>
public sealed partial class SerialPortsViewModel : HardwarePageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SerialPortsViewModel));

    private Dictionary<int, SerialOptionRule>? _rules;
    private Dictionary<int, string> _uartNames = new();
    private (uint SysId, byte CompId, DateTime Connected)? _uartsFor;

    public SerialPortsViewModel(AppServices services)
        : this(services, null)
    {
    }

    internal SerialPortsViewModel(AppServices services, IParamSource? source)
        : base("Serial Ports", services, source)
    {
    }

    [ObservableProperty]
    private IReadOnlyList<SerialPortRow> _ports = [];

    [ObservableProperty]
    private bool _hasFlowControl;

    /// <summary>The comment of the protocol preset applied last.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleNote))]
    private string _ruleNote = "";

    public bool HasRuleNote => RuleNote.Length > 0;

    [ObservableProperty]
    private bool _tooManyMavlink;

    public string MavlinkWarning => SerialPortLogic.MavlinkLimitWarning;

    public string RebootNote => SerialPortLogic.RebootNote;

    protected override void Build()
    {
        if (_rules == null)
        {
            var bauds = Source.Meta("SERIAL1_BAUD")?.Values.Select(v => v.Key) ?? [];
            _rules = SerialPortLogic.Sanitize(SerialPortLogic.LoadRules(), bauds);
        }

        var rows = new List<SerialPortRow>();
        foreach (var n in SerialPortLogic.PortNumbers(Source.Names()))
        {
            var p = "SERIAL" + n.ToString(CultureInfo.InvariantCulture);
            var protocol = Field(p + "_PROTOCOL", new ParamFieldSpec { Kind = ParamEditorKind.Options });
            var row = new SerialPortRow(n,
                protocol,
                Field(p + "_BAUD", new ParamFieldSpec { Kind = ParamEditorKind.Options }),
                Field(p + "_OPTIONS", new ParamFieldSpec { Kind = ParamEditorKind.Bitmask }),
                Field("BRD_SER" + n.ToString(CultureInfo.InvariantCulture) + "_RTSCTS", new ParamFieldSpec { Kind = ParamEditorKind.Options }));
            if (_uartNames.TryGetValue(n, out var uart))
                row.UartName = uart;
            if (protocol != null)
                protocol.Written += async (_, e) => await OnProtocolChangedAsync(row, e.NewValue);
            rows.Add(row);
        }

        Ports = rows;
        HasFlowControl = rows.Any(r => r.RtsCts != null);
        UpdateMavlinkWarning();
        Status = rows.Count == 0 ? "This vehicle has no SERIALn_BAUD parameters." : null;
        FetchUartNames();
    }

    protected override void OnFieldWritten(object? sender, ParamWrittenEventArgs e)
    {
        base.OnFieldWritten(sender, e);
        // every serial setting is read at boot
        RebootRequired = true;
    }

    private async Task OnProtocolChangedAsync(SerialPortRow row, double protocol)
    {
        try
        {
            var outcome = SerialPortLogic.Apply(row.Number, (int)protocol, _rules ?? new Dictionary<int, SerialOptionRule>());
            if (outcome.Baud is { } baud && row.Baud != null && row.Baud.Value != baud)
                await row.Baud.WriteAsync(baud);
            if (outcome.Options is { } options && row.Options != null && row.Options.Value != options)
                await row.Options.WriteAsync(options);
            RuleNote = outcome.Note;
        }
        catch (Exception ex)
        {
            Log.Warn("serial rules", ex);
        }

        UpdateMavlinkWarning();
    }

    private void UpdateMavlinkWarning() =>
        TooManyMavlink = SerialPortLogic.TooManyMavlink(Ports.Select(r => r.Protocol?.Value ?? 0));

    protected override void OnValuesRefreshed() => UpdateMavlinkWarning();

    /// <summary>Port names from the autopilot's @SYS/uarts.txt, once per connection, in the background; failures are ignored.</summary>
    private void FetchUartNames()
    {
        var session = Services.Session;
        if (!session.IsConnected)
            return;
        var v = session.Vehicle;
        var key = (v.sysid, v.compid, session.ConnectedAt);
        if (_uartsFor == key)
            return;
        _uartsFor = key;
        _uartNames = new Dictionary<int, string>();
        if ((session.Cs.capabilities & (uint)MAVLink.MAV_PROTOCOL_CAPABILITY.FTP) == 0)
            return;

        var mav = session.Mav;
        _ = Task.Run(() =>
        {
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var ftp = new MAVFtp(mav, key.sysid, key.compid);
                using var ms = ftp.GetFile("@SYS/uarts.txt", cancel, false);
                if (ms == null || ms.Length == 0)
                    return;
                var names = SerialPortLogic.ParseUarts(System.Text.Encoding.ASCII.GetString(ms.ToArray()));
                Services.Dispatcher.Post(() => ApplyUartNames(key, names));
            }
            catch (Exception ex)
            {
                Log.Info("uarts.txt: " + ex.Message);
            }
        });
    }

    private void ApplyUartNames((uint, byte, DateTime) key, Dictionary<int, string> names)
    {
        if (_uartsFor != key)
            return;
        _uartNames = names;
        foreach (var row in Ports)
        {
            if (names.TryGetValue(row.Number, out var uart))
                row.UartName = uart;
        }
    }
}
