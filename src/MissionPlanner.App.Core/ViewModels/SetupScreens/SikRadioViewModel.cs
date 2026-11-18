using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>
/// SiK Radio (Mission Planner's Radio/Sikradio.cs): reads and writes the settings of a 3DR / RFD /
/// HopeRF SiK telemetry radio pair through the AT commands of the radio plugged into this computer —
/// ATI5 for its own settings, RTI5 for the radio on the vehicle, side by side.
/// </summary>
public sealed partial class SikRadioViewModel : PageViewModel
{
    private readonly Func<string, int, IByteLink> _openLink;
    private readonly Func<IByteLink, SikAtClient> _newClient;
    private readonly Queue<string> _log = new();
    private SikRadioInfo? _local;
    private SikRadioInfo? _remote;

    public SikRadioViewModel(AppServices services)
        : this(services, CommsByteLink.Serial, link => new SikAtClient(link))
    {
    }

    /// <summary>Tests pass a scripted radio and a client with short guard times.</summary>
    internal SikRadioViewModel(AppServices services, Func<string, int, IByteLink> openLink, Func<IByteLink, SikAtClient> newClient)
        : base("SiK Radio")
    {
        Services = services;
        _openLink = openLink;
        _newClient = newClient;
        Bauds = [9600, 19200, 38400, 57600, 115200, 230400, 460800];
        _selectedBaud = 57600;
        RefreshPorts();
        var last = Settings.Instance["sik_port"] ?? Settings.Instance["comport"];
        // a port typed in earlier (a symlink, a pty) is kept even though the OS list lacks it
        if (last != null && !Ports.Contains(last) && File.Exists(last))
            Ports.Insert(0, last);
        _selectedPort = last != null && Ports.Contains(last) ? last : Ports.FirstOrDefault();
        if (int.TryParse(Settings.Instance["sik_baud"], out var baud) && baud > 0)
            _selectedBaud = baud;
    }

    public AppServices Services { get; }

    public ObservableCollection<string> Ports { get; } = new();

    public IReadOnlyList<int> Bauds { get; }

    [ObservableProperty]
    private string? _selectedPort;

    [ObservableProperty]
    private int _selectedBaud;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(SaveCommand), nameof(ResetDefaultsCommand), nameof(CopyLocalToRemoteCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string _status = "Plug the ground radio into this computer, pick its port and press Load settings.";

    public ObservableCollection<SikSettingRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CopyLocalToRemoteCommand))]
    private bool _hasSettings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyLocalToRemoteCommand))]
    private bool _hasRemote;

    [ObservableProperty]
    private string _localVersion = "";

    [ObservableProperty]
    private string _localHardware = "";

    [ObservableProperty]
    private string _rssi = "";

    [ObservableProperty]
    private string _remoteVersion = "";

    [ObservableProperty]
    private string _remoteHardware = "";

    [ObservableProperty]
    private string _localAesKey = "";

    [ObservableProperty]
    private string _remoteAesKey = "";

    [ObservableProperty]
    private bool _hasLocalKey;

    [ObservableProperty]
    private bool _hasRemoteKey;

    [ObservableProperty]
    private string _commsLog = "";

    public bool HasRssi => Rssi.Length > 0;

    partial void OnRssiChanged(string value) => OnPropertyChanged(nameof(HasRssi));

    partial void OnSelectedPortChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            Settings.Instance["sik_port"] = value;
    }

    partial void OnSelectedBaudChanged(int value) => Settings.Instance["sik_baud"] = value.ToString();

    [RelayCommand]
    private void RefreshPorts()
    {
        var current = SelectedPort;
        Ports.Clear();
        foreach (var p in LinkFactory.GetSerialPortNames().OrderBy(p => p, StringComparer.Ordinal))
            Ports.Add(p);
        if (current != null && !Ports.Contains(current))
            Ports.Insert(0, current);
        if (current != null)
            SelectedPort = current;
    }

    private bool CanRun() => !IsBusy;

    private bool CanSave() => !IsBusy && HasSettings;

    private bool CanCopy() => !IsBusy && HasSettings && HasRemote;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task LoadAsync()
    {
        if (!await PrepareAsync())
            return;

        var port = SelectedPort!;
        var baud = SelectedBaud;
        IsBusy = true;
        Status = $"Connecting to the radio on {port}…";
        try
        {
            var (local, remote) = await Task.Run(() =>
            {
                using var link = _openLink(port, baud);
                link.Open();
                var at = NewClient(link);
                EnterCommandMode(at);
                at.Command("AT&T", timeoutMs: 300); // RF test modes off, as Mission Planner does first
                Progress("Reading the local radio (ATI5)…");
                var l = at.ReadInfo(remote: false) ?? throw new SikException("The radio entered command mode but did not answer ATI.");
                Progress("Reading the remote radio (RTI5)…");
                var r = at.ReadInfo(remote: true);
                at.ExitCommandMode();
                return (l, r);
            });

            Show(local, remote);
            Status = remote == null
                ? $"Local radio read ({local.Parameters.Count} settings). The remote radio did not answer: is it powered and linked (solid green LED)?"
                : "Local and remote radios read.";
        }
        catch (Exception ex)
        {
            Status = "Load failed: " + ex.Message;
            await Services.Dialogs.ShowMessageAsync("SiK Radio", "Could not read the radio.\n\n" + ex.Message, MessageKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var localChanges = Changes(remote: false);
        var remoteChanges = HasRemote ? Changes(remote: true) : [];
        var localKey = KeyChange(_local, LocalAesKey);
        var remoteKey = HasRemote ? KeyChange(_remote, RemoteAesKey) : null;

        var problems = Validate();
        if (problems.Count > 0)
        {
            await Services.Dialogs.ShowMessageAsync("SiK Radio", "Not saved:\n\n" + string.Join("\n", problems), MessageKind.Warning);
            return;
        }

        if (localChanges.Count == 0 && remoteChanges.Count == 0 && localKey == null && remoteKey == null)
        {
            Status = "Nothing changed.";
            return;
        }

        var mismatched = Rows.Where(r => r.Mismatch).Select(r => r.Label).ToList();
        var question = $"Write {localChanges.Count + (localKey != null ? 1 : 0)} local and {remoteChanges.Count + (remoteKey != null ? 1 : 0)} remote " +
                       "setting(s) and restart the radios?";
        if (mismatched.Count > 0)
            question += "\n\nThese differ between the radios, which then will not link: " + string.Join(", ", mismatched) + ".";
        if (!await Services.Dialogs.ConfirmAsync("Save radio settings", question, "Save"))
            return;

        if (!await PrepareAsync())
            return;

        var port = SelectedPort!;
        var baud = SelectedBaud;
        IsBusy = true;
        Status = "Writing settings…";
        try
        {
            var failed = await Task.Run(() =>
            {
                var errors = new List<string>();
                using var link = _openLink(port, baud);
                link.Open();
                var at = NewClient(link);
                EnterCommandMode(at);
                at.Command("AT&T", timeoutMs: 300);

                // as Mission Planner: remote first, then local, then store and restart remote, then local
                foreach (var (des, name, value) in remoteChanges)
                {
                    Progress($"Remote {name} = {value}");
                    if (!at.SetParameter(remote: true, des, value))
                        errors.Add($"remote {name}");
                }

                foreach (var (des, name, value) in localChanges)
                {
                    Progress($"Local {name} = {value}");
                    if (!at.SetParameter(remote: false, des, value))
                        errors.Add($"local {name}");
                }

                if (remoteKey != null && !at.SetAesKey(remote: true, remoteKey))
                    errors.Add("remote encryption key");
                if (localKey != null && !at.SetAesKey(remote: false, localKey))
                    errors.Add("local encryption key");

                if (remoteChanges.Count > 0 || remoteKey != null)
                {
                    Progress("Storing the remote settings (RT&W) and restarting it");
                    if (!at.Save(remote: true))
                        errors.Add("remote save (RT&W)");
                    at.Reboot(remote: true);
                }

                Progress("Storing the local settings (AT&W) and restarting it");
                if (!at.Save(remote: false))
                    errors.Add("local save (AT&W)");
                at.Reboot(remote: false);
                return errors;
            });

            foreach (var row in Rows)
            {
                if (row.Local != null && !failed.Contains("local " + row.Name))
                    row.Local.Commit();
                if (row.Remote != null && !failed.Contains("remote " + row.Name))
                    row.Remote.Commit();
            }

            if (failed.Count == 0)
            {
                Status = "Saved. The radios restart with the new settings; Load settings again to check them.";
            }
            else
            {
                Status = "Saved with errors: " + string.Join(", ", failed);
                await Services.Dialogs.ShowMessageAsync("SiK Radio", "The radio refused:\n\n" + string.Join("\n", failed), MessageKind.Warning);
            }
        }
        catch (Exception ex)
        {
            Status = "Save failed: " + ex.Message;
            await Services.Dialogs.ShowMessageAsync("SiK Radio", "Could not write the settings.\n\n" + ex.Message, MessageKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ResetDefaultsAsync()
    {
        var both = HasRemote;
        var what = both ? "both radios (local and remote)" : "the local radio";
        if (!await Services.Dialogs.ConfirmAsync("Reset to defaults",
                $"Reset {what} to factory defaults (AT&F), store and restart? Net ID, frequencies and speeds go back to their defaults.",
                "Reset"))
            return;
        if (!await PrepareAsync())
            return;

        var port = SelectedPort!;
        var baud = SelectedBaud;
        IsBusy = true;
        Status = "Resetting…";
        try
        {
            var failed = await Task.Run(() =>
            {
                var errors = new List<string>();
                using var link = _openLink(port, baud);
                link.Open();
                var at = NewClient(link);
                EnterCommandMode(at);
                if (both)
                {
                    Progress("Remote: factory defaults");
                    if (!at.FactoryReset(remote: true))
                        errors.Add("remote AT&F");
                    if (!at.Save(remote: true))
                        errors.Add("remote AT&W");
                    at.Reboot(remote: true);
                }

                Progress("Local: factory defaults");
                if (!at.FactoryReset(remote: false))
                    errors.Add("local AT&F");
                if (!at.Save(remote: false))
                    errors.Add("local AT&W");
                at.Reboot(remote: false);
                return errors;
            });

            Clear();
            Status = failed.Count == 0
                ? "Reset to factory defaults. Load settings to see them."
                : "Reset with errors: " + string.Join(", ", failed);
        }
        catch (Exception ex)
        {
            Status = "Reset failed: " + ex.Message;
            await Services.Dialogs.ShowMessageAsync("SiK Radio", "Could not reset the radio.\n\n" + ex.Message, MessageKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Mission Planner's "Copy required to remote": the settings both radios must share.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void CopyLocalToRemote()
    {
        var copied = 0;
        foreach (var row in Rows.Where(r => r.MustMatch && r.Local != null && r.Remote != null))
        {
            if (row.Remote!.Value != row.Local!.Value)
            {
                row.Remote.Value = row.Local.Value;
                copied++;
            }
        }

        if (HasLocalKey && HasRemoteKey && RemoteAesKey != LocalAesKey)
        {
            RemoteAesKey = LocalAesKey;
            copied++;
        }

        Status = copied == 0 ? "The remote already matches the local radio." : $"Copied {copied} setting(s) to the remote column; Save settings to write them.";
    }

    /// <summary>Clears the comms log.</summary>
    [RelayCommand]
    private void ClearLog()
    {
        lock (_log)
            _log.Clear();
        CommsLog = "";
    }

    /// <summary>The port must not be the vehicle link's; offers to disconnect it, as Mission Planner closes it.</summary>
    private async Task<bool> PrepareAsync()
    {
        if (string.IsNullOrEmpty(SelectedPort))
        {
            await Services.Dialogs.ShowMessageAsync("SiK Radio", "Pick the serial port of the radio first.", MessageKind.Warning);
            return false;
        }

        var session = Services.Session;
        if (session.IsConnected && session.Link is { Kind: LinkKind.Serial } link && SamePort(link.Address, SelectedPort))
        {
            if (!await Services.Dialogs.ConfirmAsync("SiK Radio",
                    $"The vehicle link uses {SelectedPort}. Disconnect it to talk to the radio?", "Disconnect"))
                return false;
            await session.DisconnectAsync();
        }

        return true;
    }

    internal static bool SamePort(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;
        try
        {
            return string.Equals(Real(a), Real(b), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }

        static string Real(string path) =>
            File.Exists(path) && new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is { } target ? target.FullName : path;
    }

    private SikAtClient NewClient(IByteLink link)
    {
        var at = _newClient(link);
        at.Log = AddLog;
        return at;
    }

    private static void EnterCommandMode(SikAtClient at)
    {
        if (!at.EnterCommandMode())
            throw new SikException("No answer to +++. Check the port and baud rate (SiK radios default to 57600), " +
                                   "and that nothing else is using the port. Power-cycling the radio can help.");
    }

    private void Progress(string text) => Services.Dispatcher.Post(() => Status = text);

    private void AddLog(string line)
    {
        string text;
        lock (_log)
        {
            _log.Enqueue(line);
            while (_log.Count > 400)
                _log.Dequeue();
            text = string.Join("\n", _log);
        }

        Services.Dispatcher.Post(() => CommsLog = text);
    }

    private void Show(SikRadioInfo local, SikRadioInfo? remote)
    {
        _local = local;
        _remote = remote;
        LocalVersion = local.Version;
        LocalHardware = Join(local.BoardName, local.FrequencyName);
        Rssi = local.Rssi;
        RemoteVersion = remote?.Version ?? "no answer";
        RemoteHardware = remote == null ? "" : Join(remote.BoardName, remote.FrequencyName);
        LocalAesKey = local.AesKey ?? "";
        HasLocalKey = local.AesKey != null;
        RemoteAesKey = remote?.AesKey ?? "";
        HasRemoteKey = remote?.AesKey != null;

        Rows.Clear();
        var names = local.Parameters.Select(p => p.Name).ToList();
        if (remote != null)
            names.AddRange(remote.Parameters.Select(p => p.Name).Where(n => !names.Contains(n)));
        foreach (var name in names)
        {
            var lp = local.Find(name);
            var rp = remote?.Find(name);
            Rows.Add(new SikSettingRow(name,
                lp == null ? null : new SikValue(lp, SikCatalog.Describe(lp, local), remote: false),
                rp == null ? null : new SikValue(rp, SikCatalog.Describe(rp, remote!), remote: true)));
        }

        HasSettings = true;
        HasRemote = remote != null;

        static string Join(string a, string b) => string.Join(" · ", new[] { a, b }.Where(s => s.Length > 0));
    }

    private void Clear()
    {
        Rows.Clear();
        _local = null;
        _remote = null;
        HasSettings = false;
        HasRemote = false;
        LocalVersion = LocalHardware = RemoteVersion = RemoteHardware = Rssi = "";
        LocalAesKey = RemoteAesKey = "";
        HasLocalKey = HasRemoteKey = false;
    }

    private List<(string Designator, string Name, int Value)> Changes(bool remote) =>
        Rows.Select(r => (Row: r, Value: remote ? r.Remote : r.Local))
            .Where(x => x.Value is { IsChanged: true })
            .Select(x => (x.Value!.Designator, x.Row.Name, x.Value.Value))
            .ToList();

    /// <summary>The key to send when the user changed it; padded to the full length as Mission Planner does.</summary>
    private static string? KeyChange(SikRadioInfo? radio, string text)
    {
        if (radio?.AesKey == null)
            return null;
        text = text.Trim();
        if (string.Equals(text, radio.AesKey, StringComparison.OrdinalIgnoreCase) || text.Length == 0)
            return null;
        return text.PadRight(radio.AesKey.Length, '0');
    }

    /// <summary>What Mission Planner's TSettings.CheckValid catches, plus the key format.</summary>
    internal List<string> Validate()
    {
        var problems = new List<string>();
        foreach (var remote in new[] { false, true })
        {
            if (remote && !HasRemote)
                continue;
            var side = remote ? "Remote" : "Local";
            int? Get(string name) => Rows.FirstOrDefault(r => r.Name == name) is { } row ? (remote ? row.Remote : row.Local)?.Value : null;
            if (Get("MIN_FREQ") is { } lo && Get("MAX_FREQ") is { } hi && lo >= hi)
                problems.Add($"{side}: Min freq must be below Max freq.");
            var radio = remote ? _remote : _local;
            var key = (remote ? RemoteAesKey : LocalAesKey).Trim();
            if (radio?.AesKey != null && key.Length > 0 && (!key.All(Uri.IsHexDigit) || key.Length > radio.AesKey.Length))
                problems.Add($"{side}: the encryption key must be at most {radio.AesKey.Length} hex digits.");
        }

        return problems;
    }
}

/// <summary>A radio problem worth showing as is.</summary>
public sealed class SikException(string message) : Exception(message);
