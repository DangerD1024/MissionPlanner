using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.FlightData;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// One-off vehicle commands from Mission Planner's Ctrl+F screen: force calibrated, arm and take off,
/// bootloader update, DFU, lock-up test.
/// </summary>
public sealed partial class VehicleCommandsViewModel(AppServices services)
    : ToolPageViewModel("Vehicle commands",
        "Commands Mission Planner keeps on its Ctrl+F screen. Some are for developers; read the note on each before using it.",
        services)
{
    [ObservableProperty]
    private decimal _takeoffAltitude = 10;

    [RelayCommand]
    private Task ForceAccelCalibratedAsync() =>
        RunAsync("Force accel calibrated", (m, s, c) => ToolCommands.ForceAccelCalibrated.Send(m, s, c),
            "The autopilot now treats its accelerometers as calibrated.", "The autopilot refused to mark the accelerometers calibrated.");

    [RelayCommand]
    private Task ForceCompassCalibratedAsync() =>
        RunAsync("Force compass calibrated", (m, s, c) => ToolCommands.ForceCompassCalibrated.Send(m, s, c),
            "The autopilot now treats its compasses as calibrated.", "The autopilot refused to mark the compasses calibrated.");

    /// <summary>GUIDED, arm (unless armed) and TAKEOFF to the altitude, as Mission Planner's "arm and takeoff".</summary>
    [RelayCommand]
    private async Task ArmAndTakeoffAsync()
    {
        var alt = (float)TakeoffAltitude;
        if (!await Services.Dialogs.ConfirmAsync("Arm and take off",
                $"Switch to GUIDED, arm the motors and climb to {alt.ToString(CultureInfo.InvariantCulture)} m now?", "Arm and take off"))
            return;

        await RunAsync("Arm and take off", (mav, sysid, compid) =>
        {
            mav.setMode(sysid, compid, "GUIDED");
            if (!mav.MAVlist[sysid, compid].cs.armed && !mav.doARM(sysid, compid, true))
                return false;
            Thread.Sleep(300);
            return mav.doCommand(sysid, compid, MAVLink.MAV_CMD.TAKEOFF, 0, 0, 0, 0, 0, 0, alt);
        }, $"Taking off to {alt.ToString(CultureInfo.InvariantCulture)} m.", "Arm and take off failed (the vehicle refused to arm or to take off).");
    }

    [RelayCommand]
    private async Task UpdateBootloaderAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("Update bootloader",
                "Write the bootloader that this firmware carries? If power is lost while it is written the board may no longer start, " +
                "and it can then only be recovered with DFU or a debugger. Do not unplug the board.", "Continue") ||
            !await Services.Dialogs.ConfirmAsync("Update bootloader",
                "Really update the bootloader now? It can take a few minutes; wait for the answer.", "Update bootloader"))
            return;

        await RunAsync("Updating the bootloader (can take minutes)", (m, s, c) => ToolCommands.FlashBootloader.Send(m, s, c),
            "Bootloader updated.", "The bootloader was not updated.");
    }

    [RelayCommand]
    private async Task RebootToDfuAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("Reboot to DFU",
                "Reboot the autopilot into its STM32 DFU bootloader? The link drops; the board then waits for a DFU tool " +
                "(dfu-util, STM32CubeProgrammer) and needs a power cycle to start normally.", "Reboot to DFU"))
            return;

        await RunAsync("Reboot to DFU", (m, s, c) => ToolCommands.RebootToDfu.Send(m, s, c, requireAck: false),
            "Reboot to DFU sent.", "Could not send the reboot.");
    }

    [RelayCommand]
    private async Task LockupAsync()
    {
        const string text = "This deliberately hangs the autopilot to test its watchdog. A flying vehicle WILL CRASH. Only use it on the bench.";
        if (!await Services.Dialogs.ConfirmAsync("Lock up the autopilot", text, "Continue") ||
            !await Services.Dialogs.ConfirmAsync("Lock up the autopilot", "Are you absolutely sure? " + text, "Lock it up"))
            return;

        await RunAsync("Lock-up", (m, s, c) => ToolCommands.Lockup.Send(m, s, c, requireAck: false),
            "Lock-up sent.", "Could not send the lock-up command.");
    }
}

/// <summary>QNH and the baro altitude nudge (GND_ABS_PRESS / BARO1_GND_PRESS).</summary>
public sealed partial class GroundPressureViewModel(AppServices services)
    : ToolPageViewModel("Barometer",
        "Set the ground pressure the barometer altitude is measured from: type the QNH, or nudge the reported altitude up or down.",
        services)
{
    private double? _base;

    [ObservableProperty]
    private string _paramName = "";

    [ObservableProperty]
    private string _currentText = "";

    [ObservableProperty]
    private string _qnh = "";

    [ObservableProperty]
    private decimal _offsetMetres;

    [ObservableProperty]
    private bool _hasParam;

    public override void OnShown()
    {
        base.OnShown();
        Load();
    }

    private void Load()
    {
        var p = Services.Params;
        ParamName = GroundPressure.ParamName(p.Has) ?? "";
        HasParam = ParamName.Length > 0;
        _base = HasParam ? p.Get(ParamName) : null;
        OffsetMetres = 0;
        CurrentText = _base is { } v ? $"{ParamName} = {v.ToString("0", CultureInfo.InvariantCulture)} Pa ({(v / 100).ToString("0.0", CultureInfo.InvariantCulture)} hPa)" : "No ground pressure parameter (connect and load the parameters).";
        if (_base is { } b)
            Qnh = b.ToString("0", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private async Task SetQnhAsync()
    {
        if (!HasParam)
            return;
        if (GroundPressure.ParseQnh(Qnh) is not { } pa)
        {
            Report("Enter the QNH in pascals (101325) or hectopascals (1013.25).", true);
            return;
        }

        await WriteAsync(pa);
        Load();
    }

    [RelayCommand]
    private async Task ApplyOffsetAsync()
    {
        if (!HasParam || _base is not { } b)
            return;
        if (!await Services.Dialogs.ConfirmAsync("Adjust barometer height",
                "Use at your own risk: this changes the altitude the autopilot reports, which it flies by.", "Apply"))
            return;
        await WriteAsync(GroundPressure.ForOffset(b, (double)OffsetMetres));
        CurrentText = $"{ParamName} = {Services.Params.Get(ParamName)?.ToString("0.0", CultureInfo.InvariantCulture)} Pa (offset {OffsetMetres} m from {b.ToString("0", CultureInfo.InvariantCulture)} Pa)";
    }

    [RelayCommand]
    private async Task ResetOffsetAsync()
    {
        if (_base is not { } b)
            return;
        OffsetMetres = 0;
        await WriteAsync(b);
    }

    private async Task WriteAsync(double pascals)
    {
        if (!IsConnected)
        {
            Report("Connect to a vehicle first.", true);
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await Services.Params.SetAsync(ParamName, pascals);
            Report(ok ? $"{ParamName} set to {pascals.ToString("0.0", CultureInfo.InvariantCulture)} Pa." : $"Could not set {ParamName}.", !ok);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>SET_MESSAGE_INTERVAL for any message (Mission Planner's Message Interval tool).</summary>
public sealed partial class MessageIntervalViewModel(AppServices services)
    : ToolPageViewModel("Message interval",
        "Ask the autopilot to send one MAVLink message at a given rate. 0 Hz restores the default rate, -1 stops the message.",
        services)
{
    public IReadOnlyList<string> Messages => MessageIntervals.Names;

    [ObservableProperty]
    private string? _message = nameof(MAVLink.MAVLINK_MSG_ID.ATTITUDE);

    [ObservableProperty]
    private decimal _rateHz = 10;

    [RelayCommand]
    private async Task SetAsync()
    {
        if (Message == null || !Enum.TryParse<MAVLink.MAVLINK_MSG_ID>(Message, out var id))
        {
            Report("Pick a message.", true);
            return;
        }

        var cmd = MessageIntervals.Command(id, (double)RateHz);
        await RunAsync($"{Message} at {RateHz} Hz", (m, s, c) => cmd.Send(m, s, c),
            $"{Message}: interval set ({Describe((double)RateHz)}).", $"The autopilot refused the interval for {Message}.");
    }

    [RelayCommand]
    private async Task SetAllAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("Set every message",
                $"Send SET_MESSAGE_INTERVAL ({Describe((double)RateHz)}) for every MAVLink message? This floods a slow radio link.", "Set all"))
            return;

        var rate = (double)RateHz;
        await RunAsync("Setting every message", (mav, s, c) =>
        {
            foreach (var id in Enum.GetValues<MAVLink.MAVLINK_MSG_ID>())
                MessageIntervals.Command(id, rate).Send(mav, s, c, requireAck: false);
            return true;
        }, $"Interval sent for every message ({Describe(rate)}).", "Could not send the intervals.");
    }

    private static string Describe(double hz) => hz switch
    {
        0 => "default rate",
        < 0 => "stopped",
        _ => hz.ToString(CultureInfo.InvariantCulture) + " Hz",
    };
}

/// <summary>Downloads a file from the vehicle by name (e.g. @SYS/threads.txt), as Mission Planner's "grab threads.txt".</summary>
public sealed partial class OnboardFileViewModel(AppServices services)
    : ToolPageViewModel("Onboard files",
        "Read a file from the autopilot over MAVFtp. The @SYS files are live diagnostics (threads, timers, serial ports, DMA, memory).",
        services)
{
    private byte[]? _data;

    public IReadOnlyList<string> Presets { get; } =
    [
        "@SYS/threads.txt", "@SYS/timers.txt", "@SYS/uarts.txt", "@SYS/dma.txt", "@SYS/memory.txt",
        "@SYS/can_log.txt", "@SYS/can2_log.txt", "@SYS/tasks.txt", "@SYS/storage.bin", "@SYS/crash_dump.bin",
    ];

    [ObservableProperty]
    private string _path = "@SYS/threads.txt";

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    private string _savedName = "";

    public bool HasData => _data != null;

    [RelayCommand]
    private void UsePreset(string path) => Path = path;

    [RelayCommand]
    private async Task DownloadAsync()
    {
        var path = Path.Trim();
        if (path.Length == 0)
            return;
        MemoryStream? result = null;
        var ok = await RunAsync("Reading " + path, (mav, sysid, compid) =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            result = new ConfigScreens.MavFtpClient(mav, sysid, compid).GetFile(path, cancel);
            return result != null;
        }, $"Read {path}.", $"Could not read {path} (the file may not exist on this firmware).");

        _data = ok ? result!.ToArray() : null;
        Content = _data == null ? "" : Preview(_data);
        SavedName = _data == null ? "" : System.IO.Path.GetFileName(path.Replace('@', '_'));
    }

    /// <summary>The text, or a hex dump of the start when it is binary.</summary>
    internal static string Preview(byte[] data)
    {
        var text = data.Length > 0 && data.Take(4096).All(b => b is 9 or 10 or 13 or >= 32 and < 127 or >= 128);
        if (text)
            return System.Text.Encoding.UTF8.GetString(data);
        var sb = new System.Text.StringBuilder($"{data.Length} bytes (binary). First bytes:\n");
        for (var i = 0; i < Math.Min(data.Length, 512); i += 16)
            sb.AppendLine($"{i:X4}  " + string.Join(' ', data.Skip(i).Take(16).Select(b => b.ToString("X2", CultureInfo.InvariantCulture))));
        return sb.ToString();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_data == null)
            return;
        var file = await Services.Dialogs.SaveFileAsync("Save file", SavedName);
        if (file == null)
            return;
        await File.WriteAllBytesAsync(file, _data);
        Report("Saved " + file);
    }
}

/// <summary>Mission Planner's Param Restore: a saved file written to the vehicle with the ordering rules of <see cref="ParamRestorePlan"/>.</summary>
public sealed partial class ParamRestoreViewModel(AppServices services)
    : ToolPageViewModel("Parameter restore",
        "Write a whole saved parameter file to the vehicle: *ENABLE* parameters first, values that already match are skipped, and device IDs are cleared before they are written so the autopilot detects them again.",
        services)
{
    [ObservableProperty]
    private string _log = "";

    [ObservableProperty]
    private int _progress;

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (!IsConnected)
        {
            Report("Connect to a vehicle first.", true);
            return;
        }

        var file = await Services.Dialogs.OpenFileAsync("Parameter file", new FileFilter("Parameter files", "param", "parm", "txt"));
        if (file == null)
            return;

        Dictionary<string, double> values;
        try
        {
            values = Params.ParamService.LoadFile(file);
        }
        catch (Exception ex)
        {
            Report("Could not read the file: " + ex.Message, true);
            return;
        }

        var (steps, already) = ParamRestorePlan.Build(values, Services.Params.Get);
        if (!await Services.Dialogs.ConfirmAsync("Parameter restore",
                $"{values.Count} parameters in the file: {already} already match, {steps.Count} will be written. This can take a while.", "Restore"))
            return;

        IsBusy = true;
        var fails = new List<string>();
        var set = 0;
        var log = new System.Text.StringBuilder();
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                Status = $"Writing {step.Name} ({i + 1} of {steps.Count})";
                Progress = (i + 1) * 100 / steps.Count;
                if (step.ZeroFirst)
                    await Services.Params.SetAsync(step.Name, 0);
                if (await Services.Params.SetAsync(step.Name, step.Value))
                {
                    set++;
                }
                else
                {
                    fails.Add(step.Name);
                    log.AppendLine("failed: " + step.Name);
                    Log = log.ToString();
                }
            }

            Status = "Refreshing the parameter list…";
            try
            {
                await Services.Params.RefreshAsync();
            }
            catch (Exception ex)
            {
                log.AppendLine("refresh: " + ex.Message);
            }
        }
        finally
        {
            IsBusy = false;
        }

        var summary = $"Set {set} parameters, {already} already set" + (fails.Count > 0 ? $", {fails.Count} failed: {string.Join(", ", fails)}" : ".");
        log.Insert(0, summary + Environment.NewLine);
        Log = log.ToString();
        Report(summary, fails.Count > 0);
    }
}

/// <summary>Decodes typed bytes as a MAVLink packet (Mission Planner's "parse packet bytes" and "hex Mavlink decode").</summary>
public sealed partial class PacketDecoderViewModel(AppServices services)
    : ToolPageViewModel("MAVLink packet decoder",
        "Paste the bytes of a MAVLink packet — hex (fd 09 00 …, 0xfd,0x09, or fd0900…) or decimal (253 9 0 …) — to see its fields.",
        services)
{
    public override bool NeedsVehicle => false;

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private string _output = PacketDecoder.Decode("").Text;

    partial void OnInputChanged(string value)
    {
        var (_, text) = PacketDecoder.Decode(value);
        Output = text;
    }
}

/// <summary>Decodes typed device-ID numbers (Mission Planner's "decode HWIDs").</summary>
public sealed partial class DeviceIdDecoderViewModel(AppServices services)
    : ToolPageViewModel("Device ID decoder",
        "Paste device ID numbers (one per line, optionally after the parameter name, e.g. COMPASS_DEV_ID 97539) to see bus, address and sensor type.",
        services)
{
    public override bool NeedsVehicle => false;

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private string _output = DeviceIdDecoder.Decode("");

    partial void OnInputChanged(string value) => Output = DeviceIdDecoder.Decode(value);
}

/// <summary>Embeds a parameter file into an .apj firmware as its defaults (Mission Planner's APJ Tool).</summary>
public sealed partial class ApjToolViewModel(AppServices services)
    : ToolPageViewModel("APJ tool",
        "Build a firmware whose default parameters come from a .param file: pick an .apj and a parameter file; the new firmware is saved next to the original as <name>.apjnew.apj.",
        services)
{
    public override bool NeedsVehicle => false;

    [ObservableProperty]
    private string _firmware = "";

    [ObservableProperty]
    private string _paramFile = "";

    [RelayCommand]
    private async Task PickFirmwareAsync() =>
        Firmware = await Services.Dialogs.OpenFileAsync("Firmware", new FileFilter("ArduPilot firmware", "apj")) ?? Firmware;

    [RelayCommand]
    private async Task PickParamsAsync() =>
        ParamFile = await Services.Dialogs.OpenFileAsync("Parameter file", new FileFilter("Parameter files", "param", "parm")) ?? ParamFile;

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (!File.Exists(Firmware) || !File.Exists(ParamFile))
        {
            Report("Pick the firmware and the parameter file.", true);
            return;
        }

        IsBusy = true;
        try
        {
            var fw = Firmware;
            var pf = ParamFile;
            await Task.Run(() => MissionPlanner.Utilities.apj_tool.Process(fw, pf));
            Report("Saved " + fw + "new.apj");
        }
        catch (Exception ex)
        {
            Report("Could not build the firmware: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>TOOLS: Mission Planner's Ctrl+F screen, with a left menu.</summary>
public sealed class ToolsViewModel : MenuScreenViewModel
{
    public const string OutputsTitle = "Outputs";

    /// <summary>Opens a page by its menu title.</summary>
    public void Show(string title)
    {
        if (Items.FirstOrDefault(i => i.IsPage && i.Title == title) is { } item)
            SelectedItem = item;
    }

    public ToolsViewModel(AppServices s)
        : base("Tools", s)
    {
        Add(MenuItemViewModel.Header("VEHICLE"));
        Add(MenuItemViewModel.Page("Vehicle commands", () => new VehicleCommandsViewModel(s)));
        Add(MenuItemViewModel.Page("Barometer", () => new GroundPressureViewModel(s)));
        Add(MenuItemViewModel.Page("Message interval", () => new MessageIntervalViewModel(s)));
        Add(MenuItemViewModel.Page("Onboard files", () => new OnboardFileViewModel(s)));
        Add(MenuItemViewModel.Page("Parameter restore", () => new ParamRestoreViewModel(s)));
        Add(MenuItemViewModel.Header("OUTPUTS"));
        Add(MenuItemViewModel.Page(OutputsTitle, () => new OutputsViewModel(s)));
        Add(MenuItemViewModel.Header("DECODERS"));
        Add(MenuItemViewModel.Page("MAVLink packet decoder", () => new PacketDecoderViewModel(s)));
        Add(MenuItemViewModel.Page("Device ID decoder", () => new DeviceIdDecoderViewModel(s)));
        Add(MenuItemViewModel.Header("FIRMWARE"));
        Add(MenuItemViewModel.Page("APJ tool", () => new ApjToolViewModel(s)));
    }
}
