using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>
/// TOOLS: Mission Planner's Ctrl+F screen. Shown with a left menu by default; the classic layout is the
/// WinForms Ctrl+F form: two columns of buttons with what each does, in the same order, and the
/// SYS_STATUS sensor grid on the right.
/// </summary>
public sealed partial class ToolsViewModel : MenuScreenViewModel
{
    public const string OutputsTitle = "Outputs";

    public const string MapCacheTitle = "Map cache";

    public const string DeviceOpTitle = "Bus registers";

    public const string VehicleCommandsTitle = "Vehicle commands";

    public const string BarometerTitle = "Barometer";

    public const string LogToolsTitle = "Log tools";

    public const string PacketDecoderTitle = "MAVLink packet decoder";

    /// <summary>Setting: the classic wall of buttons instead of the menu.</summary>
    public const string ClassicKey = "next_tools_classic";

    private readonly AppServices _s;
    private ClassicActionsViewModel? _actions;
    private IReadOnlyList<ClassicToolButton>? _left;
    private IReadOnlyList<ClassicToolButton>? _right;
    private int _tick;

    public ToolsViewModel(AppServices s)
        : base("Tools", s)
    {
        _s = s;
        Add(MenuItemViewModel.Header("VEHICLE"));
        Add(MenuItemViewModel.Page(VehicleCommandsTitle, () => new VehicleCommandsViewModel(s)));
        Add(MenuItemViewModel.Page(BarometerTitle, () => new GroundPressureViewModel(s)));
        Add(MenuItemViewModel.Page("Message interval", () => new MessageIntervalViewModel(s)));
        Add(MenuItemViewModel.Page("Onboard files", () => new OnboardFileViewModel(s)));
        Add(MenuItemViewModel.Page("Parameter restore", () => new ParamRestoreViewModel(s)));
        Add(MenuItemViewModel.Page("Compass from a log", () => new MagLogCalibrationViewModel(s)));
        Add(MenuItemViewModel.Page(DeviceOpTitle, () => new DeviceOpViewModel(s)));
        Add(MenuItemViewModel.Header("OUTPUTS"));
        Add(MenuItemViewModel.Page(OutputsTitle, () => new OutputsViewModel(s)));
        Add(MenuItemViewModel.Header("LOGS"));
        Add(MenuItemViewModel.Page(LogToolsTitle, () => new LogToolsViewModel(s)));
        Add(MenuItemViewModel.Page("Log download (SSH)", () => new ScpLogDownloadViewModel(s)));
        Add(MenuItemViewModel.Header("MAPS"));
        Add(MenuItemViewModel.Page(MapCacheTitle, () => new MapCacheViewModel(s)));
        Add(MenuItemViewModel.Header("DECODERS"));
        Add(MenuItemViewModel.Page(PacketDecoderTitle, () => new PacketDecoderViewModel(s)));
        Add(MenuItemViewModel.Page("Device ID decoder", () => new DeviceIdDecoderViewModel(s)));
        Add(MenuItemViewModel.Header("FIRMWARE"));
        Add(MenuItemViewModel.Page("APJ tool", () => new ApjToolViewModel(s)));
        Add(MenuItemViewModel.Page("Download all firmware", () => new FirmwareArchiveViewModel(s)));

        _classicLayout = Settings.Instance.GetBoolean(ClassicKey, false);
    }

    protected override bool AutoSelectFirst => !ClassicLayout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMenu), nameof(ShowButtonWall), nameof(ShowClassicPage))]
    private bool _classicLayout;

    /// <summary>Classic layout: a page opened from its button is shown instead of the buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowButtonWall), nameof(ShowClassicPage))]
    private bool _classicPageOpen;

    public bool ShowMenu => !ClassicLayout;

    public bool ShowButtonWall => ClassicLayout && !ClassicPageOpen;

    public bool ShowClassicPage => ClassicLayout && ClassicPageOpen;

    /// <summary>The one-click actions without a page of their own.</summary>
    public ClassicActionsViewModel Actions => _actions ??= new ClassicActionsViewModel(_s);

    /// <summary>The classic screen's left column, in Mission Planner's order.</summary>
    public IReadOnlyList<ClassicToolButton> ClassicLeft => _left ??= BuildLeft();

    /// <summary>The classic screen's right column, in Mission Planner's order.</summary>
    public IReadOnlyList<ClassicToolButton> ClassicRight => _right ??= BuildRight();

    /// <summary>Both columns.</summary>
    public IEnumerable<ClassicToolButton> ClassicButtons => ClassicLeft.Concat(ClassicRight);

    /// <summary>SYS_STATUS sensors: enabled, present, healthy.</summary>
    public IReadOnlyList<SensorStatusRow> Sensors { get; } = SensorStatusRow.All();

    private VehicleCommandsViewModel Commands =>
        (VehicleCommandsViewModel)Items.First(i => i.Title == VehicleCommandsTitle).GetPage();

    private ClassicToolButton Open(string title, string description, string page) =>
        new(title, description, OpenClassicPageCommand, Items.First(i => i.Title == page));

    private static ClassicToolButton Do(string title, string description, System.Windows.Input.ICommand command, bool danger = false) =>
        new(title, description, command, null, danger);

    private IReadOnlyList<ClassicToolButton> BuildLeft()
    {
        var a = Actions;
        var c = Commands;
        return
        [
            Open("NMEA", "outputs the mav location in nmea", OutputsTitle),
            Open("MicroDrone", "outputs the mav location in microdrone format", OutputsTitle),
            Open("Mavlink", "mirrors the mavlink stream received by mp", OutputsTitle),
            Open("Anon Log", "anonymise a log (remove positions)", LogToolsTitle),
            Open("Sort TLogs", "Sort TLogs into their type and sysid directories", LogToolsTitle),
            Open("rip all fw", "download all current fw's", "Download all firmware"),
            Do("structtest", "struct conversion speed test", a.StructTestCommand),
            Open("DashWare", "Create dashware date input file", LogToolsTitle),
            Do("arm and takeoff", "quad: arm and takeoff", c.ArmAndTakeoffCommand),
            Do("logindex", "tlog browser", a.OpenLogBrowserCommand),
            Open("APJ Tool", "sign, inspect and edit APJ firmware files", "APJ tool"),
            Open("mag calb log", "get mag offsets from a log", "Compass from a log"),
            Open("CoT", "Outputs Cursor-on-Target", OutputsTitle),
            Do("Force Accel Cal", "Mark accel as cal'd after param restore", c.ForceAccelCalibratedCommand),
            Do("Force Compass Cal", "Mark mag as cal'd after param restore", c.ForceCompassCalibratedCommand),
        ];
    }

    private IReadOnlyList<ClassicToolButton> BuildRight()
    {
        var a = Actions;
        var c = Commands;
        return
        [
            Open("hex Mavlink decode", "debug a hex string mavlink packet", PacketDecoderTitle),
            Do("driver clean", "remove installed drivers", a.CleanDriversCommand),
            Do("Toggle Safety Switch", "virtual press the safety button", a.ToggleSafetyCommand),
            Open("Message Interval", "set custom message intervals for messages", "Message interval"),
            Do("MAVLink Inspector", "Inspect all mavlink packets being transmitted", a.OpenInspectorCommand),
            Do("Bootloader Upgrade", "update the bootloader", c.UpdateBootloaderCommand),
            Do("3D Map", "3d map testing", a.Show3DMapCommand),
            Open("decode HWIDs", "display info about a hardware id typed in", "Device ID decoder"),
            Open("parse packet bytes", "debug a hex string mavlink packet", PacketDecoderTitle),
            Open("adjust aircraft baro height", "modify baro alt reference alt", BarometerTitle),
            Do("Lockup MAV", "cause the autopilot to lockup", c.LockupCommand, danger: true),
            Open("logdownload scp", "logdownload via scp - ssh (apsync)", "Log download (SSH)"),
            Do("ReSort All logs", "resort all the logs in the MP logging folder", a.ResortLogsCommand),
            Do("sitl streamcombiner", "every local SITL on one link, each with its own sysid", a.SitlCombinerCommand),
            Open("Param Restore", "restore parameters from a backup onto the same board", "Parameter restore"),
            Do("grab threads.txt", "fetch @SYS/threads.txt from the autopilot", a.GrabThreadsCommand),
            Do("reboot pixhawk", "reboot the autopilot", a.RebootCommand),
            Open("QNH", "adjust the qnh", BarometerTitle),
            Do("vlc", "display video stream (now through GStreamer)", a.PlayUrlCommand),
            Open("Age Map Data", "remove image tiles older than 30 days", MapCacheTitle),
            Open("Split DFLog", "split dflog into x pieces", LogToolsTitle),
            Open("extract gps_inject", "extract rtcm data from tlog", LogToolsTitle),
            Do("DFU Mode", "DFU Mode", c.RebootToDfuCommand),
        ];
    }

    partial void OnClassicLayoutChanged(bool value)
    {
        Settings.Instance[ClassicKey] = value.ToString();
        Settings.Instance.Save();
        ClassicPageOpen = false;
        SelectedItem = value ? null : Items.FirstOrDefault(i => i.IsPage && i.IsVisible);
    }

    [RelayCommand]
    private void OpenClassicPage(MenuItemViewModel? item)
    {
        if (item == null)
            return;
        SelectedItem = item;
        ClassicPageOpen = true;
    }

    /// <summary>A hotkey opens the page in either layout.</summary>
    public override bool Show(string title)
    {
        if (!base.Show(title))
            return false;
        if (ClassicLayout)
            ClassicPageOpen = true;
        return true;
    }

    /// <summary>Classic layout: back from a page to the buttons.</summary>
    [RelayCommand]
    private void BackToButtons()
    {
        ClassicPageOpen = false;
        SelectedItem = null;
    }

    public override void Tick()
    {
        base.Tick();
        if (ShowButtonWall && ++_tick % 5 == 0)
            UpdateSensors();
    }

    public override void OnShown()
    {
        base.OnShown();
        UpdateSensors();
    }

    private void UpdateSensors()
    {
        var cs = _s.Session.Cs;
        foreach (var row in Sensors)
            row.Update(cs.sensors_enabled.Value, cs.sensors_present.Value, cs.sensors_health.Value);
    }
}
