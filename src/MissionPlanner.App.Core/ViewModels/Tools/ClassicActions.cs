using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;
using MissionPlanner.App.Core.ViewModels.Video;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>A row of the classic Ctrl+F screen: a button and what it does, as in Mission Planner's temp form.</summary>
public sealed record ClassicToolButton(string Title, string Description, System.Windows.Input.ICommand Command, object? Parameter = null, bool IsDanger = false);

/// <summary>One sensor of SYS_STATUS for the classic screen's status grid (enabled / present / healthy).</summary>
public sealed partial class SensorStatusRow(string name, uint bit) : ObservableObject
{
    public string Name { get; } = name;

    public uint Bit { get; } = bit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnabledText))]
    private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresentText))]
    private bool _present;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    private bool _healthy;

    public string EnabledText => Enabled ? "En" : "Dis";

    public string PresentText => Present ? "Present" : "No";

    public string HealthText => Healthy ? "Ok" : "Bad";

    /// <summary>All MAV_SYS_STATUS_SENSOR bits, named as Mission Planner shows them ("3D GYRO").</summary>
    public static IReadOnlyList<SensorStatusRow> All() =>
        Enum.GetValues<MAVLink.MAV_SYS_STATUS_SENSOR>()
            .Where(v => (uint)v != 0 && ((uint)v & ((uint)v - 1)) == 0)
            .Select(v => new SensorStatusRow(Display(v.ToString()), (uint)v))
            .ToList();

    private static string Display(string name) => name.Replace("MAV_SYS_STATUS", "").Replace('_', ' ').Trim();

    public void Update(uint enabled, uint present, uint health)
    {
        Enabled = (enabled & Bit) != 0;
        Present = (present & Bit) != 0;
        Healthy = (health & Bit) != 0;
    }
}

/// <summary>
/// The classic Ctrl+F screen's one-click actions that have no page of their own (reboot, safety switch,
/// re-sorting logs, driver clean-up, threads.txt, the struct speed test, a video URL, SITL combiner...).
/// Reports through toasts; not shown as a page.
/// </summary>
public sealed partial class ClassicActionsViewModel(AppServices services)
    : ToolPageViewModel("Classic actions", "One-click actions of the classic Ctrl+F screen.", services)
{
    [RelayCommand]
    private async Task RebootAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("Reboot", "Reboot the autopilot now? The link drops while it restarts.", "Reboot"))
            return;
        await RunAsync("Reboot", (mav, _, _) =>
        {
            mav.doReboot(false, true);
            return true;
        }, "Reboot sent.", "Could not send the reboot.");
    }

    /// <summary>Mission Planner's "Toggle Safety Switch": SET_MODE with SAFETY_ARMED, as a press of the safety button.</summary>
    [RelayCommand]
    private async Task ToggleSafetyAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("Toggle safety switch", "Press the safety switch virtually? With it off the motors can be armed.", "Toggle"))
            return;
        await RunAsync("Toggle safety switch", (mav, sysid, _) =>
        {
            if (sysid == 0)
                return false;
            var cs = mav.MAV.cs;
            mav.setMode(new MAVLink.mavlink_set_mode_t
            {
                custom_mode = SafetyCustomMode(cs.sensors_enabled.motor_control, cs.sensors_enabled.seen),
                target_system = (byte)sysid,
            }, MAVLink.MAV_MODE_FLAG.SAFETY_ARMED);
            return true;
        }, "Safety switch toggled.", "Could not toggle the safety switch.");
    }

    /// <summary>custom_mode 1 turns safety off (motor outputs enabled now), 0 turns it on.</summary>
    internal static uint SafetyCustomMode(bool motorOutputsEnabled, bool seen) => motorOutputsEnabled && seen ? 1u : 0u;

    /// <summary>Re-sorts every log in the log folder into vehicle type / sysid folders.</summary>
    [RelayCommand]
    private async Task ResortLogsAsync()
    {
        var dir = Settings.Instance.LogDir;
        if (!Directory.Exists(dir))
        {
            Report("The log folder does not exist yet.", true);
            return;
        }

        IsBusy = true;
        try
        {
            var count = await Task.Run(() =>
            {
                var n = 0;
                foreach (var pattern in new[] { "*.tlog", "*.bin", "*.log", "*.rlog" })
                {
                    var files = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
                    n += files.Length;
                    MissionPlanner.Log.LogSort.SortLogs(files, dir);
                }

                return n;
            });
            Report($"Sorted {count} logs in {dir}.");
        }
        catch (Exception ex)
        {
            Report("Sorting failed: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Mission Planner's "driver clean": removes the ArduPilot/3DR/ProfiCNC USB drivers installed by its driver installer (Windows).</summary>
    [RelayCommand]
    private async Task CleanDriversAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Report("Driver clean-up is for Windows only; Linux and macOS need no drivers.", true);
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync("Driver clean",
                "Remove the USB drivers Mission Planner's driver installer added (3D Robotics, Hex, and those signed by Michael Oborne)? " +
                "Windows asks for administrator rights. Boards then use the drivers built into Windows.", "Remove drivers"))
            return;

        IsBusy = true;
        try
        {
            var removed = await Task.Run(DriverCleaner.Clean);
            Report(removed.Count == 0 ? "No Mission Planner drivers were installed." : "Removed: " + string.Join(", ", removed));
        }
        catch (Exception ex)
        {
            Report("Driver clean-up failed: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Fetches a file (default @SYS/threads.txt) from the autopilot over MAVFTP into the data folder.</summary>
    [RelayCommand]
    private async Task GrabThreadsAsync()
    {
        var path = await Services.Dialogs.PromptAsync("Grab file", "Autopilot file to fetch over MAVFTP:", "@SYS/threads.txt");
        if (string.IsNullOrWhiteSpace(path))
            return;
        path = path.Trim();
        var output = Path.Combine(Settings.GetUserDataDirectory(), Path.GetFileName(path));
        await RunAsync("Fetching " + path, (mav, sysid, compid) =>
        {
            var ftp = new MavFtpClient(mav, sysid, compid);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var data = ftp.GetFile(path, cancel);
            if (data == null)
                return false;
            File.WriteAllBytes(output, data.ToArray());
            return true;
        }, "Saved " + output, "Could not fetch " + path + ".");
    }

    /// <summary>Mission Planner's "structtest": how fast each MAVLink struct conversion runs.</summary>
    [RelayCommand]
    private async Task StructTestAsync()
    {
        IsBusy = true;
        try
        {
            var text = await Task.Run(() => StructSpeedTest.Run(1_000_000));
            await Services.Dialogs.ShowMessageAsync("Struct conversion speed (1,000,000 HEARTBEATs)", text);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The old "vlc" button: plays a stream URL — now through GStreamer on the HUD.</summary>
    [RelayCommand]
    private async Task PlayUrlAsync()
    {
        var url = await Services.Dialogs.PromptAsync("Play video URL",
            "Stream or file URL (rtsp://, http://, udp://, file:///). It plays behind the HUD through GStreamer.",
            Settings.Instance["next_vlc_url", "rtsp://192.168.0.10:8554/H264Video"]);
        if (string.IsNullOrWhiteSpace(url))
            return;
        Settings.Instance["next_vlc_url"] = url.Trim();
        HudVideoViewModel.For(Services).UsePipeline(UrlPipeline(url.Trim()));
        Services.ShowScreen(Screen.Data);
    }

    internal static string UrlPipeline(string url) =>
        $"uridecodebin uri={url} ! {VideoPipelineBuilder.SinkTail}";

    /// <summary>The 3D map of the DATA screen.</summary>
    [RelayCommand]
    private void Show3DMap()
    {
        Services.RequestMapView(FlightData.MapViewMode.Map3D);
        Services.ShowScreen(Screen.Data);
    }

    /// <summary>The old "logindex" (tlog browser): the log browser window.</summary>
    [RelayCommand]
    private void OpenLogBrowser() => Services.Windows.Show(new Logs.LogBrowserViewModel(Services));

    /// <summary>The old "MAVLink Inspector": CONFIG > MAVLink Inspector.</summary>
    [RelayCommand]
    private void OpenInspector() => Services.RequestMenuPage(Screen.Config, "MAVLink Inspector");

    /// <summary>The old "sitl streamcombiner": every local SITL (TCP 5760, 5770...) on one link with its own sysid.</summary>
    [RelayCommand]
    private async Task SitlCombinerAsync()
    {
        if (Services.Session.State != Session.LinkState.Disconnected)
        {
            Report("Disconnect first: the combiner connects to every local SITL itself.", true);
            return;
        }

        IsBusy = true;
        try
        {
            var count = await SitlCombiner.Instance.StartAsync(msg => Services.Dispatcher.Post(() => Status = msg));
            if (count == 0)
            {
                Report("No SITL answered on TCP 5760, 5770, 5780...", true);
                return;
            }

            Report($"Combined {count} SITL vehicles on TCP 127.0.0.1:{SitlCombiner.Port}; connecting.");
            await Services.Session.ConnectAsync(Links.LinkSpec.Parse($"tcp:127.0.0.1:{SitlCombiner.Port}"), getParams: true);
        }
        catch (Exception ex)
        {
            Report("SITL combiner: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Removes the USB drivers Mission Planner's installer added, with its <c>CleanDrivers</c> rules (pnputil).</summary>
internal static class DriverCleaner
{
    /// <summary>Providers Mission Planner removes; drivers signed by "Michael Oborne" go too.</summary>
    internal static readonly string[] Providers = ["3D Robotics", "Laser Navigation", "Hex Technology Limited"];

    internal const string Signer = "Michael Oborne";

    public static IReadOnlyList<string> Clean()
    {
        var removed = new List<string>();
        foreach (var inf in Find(Run("pnputil", "/enum-drivers")))
        {
            // elevated, as Mission Planner: deleting a driver package needs administrator rights
            using var p = Process.Start(new ProcessStartInfo("pnputil", $"-f -u -d {inf}") { Verb = "runas", UseShellExecute = true });
            p?.WaitForExit(60000);
            removed.Add(inf);
        }

        return removed;
    }

    /// <summary>The published names (oemNN.inf) to remove from <c>pnputil /enum-drivers</c> output.</summary>
    internal static IReadOnlyList<string> Find(string enumOutput)
    {
        // pnputil prints one "Name: value" block per driver package, separated by blank lines
        var list = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in enumOutput.Split('\n').Append(""))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (fields.TryGetValue("Published Name", out var inf) &&
                    ((fields.TryGetValue("Signer Name", out var signer) && signer == Signer) ||
                     (fields.TryGetValue("Provider Name", out var provider) && Providers.Contains(provider))))
                    list.Add(inf);
                fields.Clear();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon > 0)
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return list;
    }

    private static string Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60000);
        return text;
    }
}

/// <summary>Mission Planner's struct conversion speed test.</summary>
internal static class StructSpeedTest
{
    public static string Run(int count)
    {
        var array = new byte[100];
        for (var i = 0; i < array.Length; i++)
            array[i] = (byte)i;
        var sb = new StringBuilder();

        Time("ByteArrayToStructure", () =>
        {
            var obj = (object)new MAVLink.mavlink_heartbeat_t();
            MavlinkUtil.ByteArrayToStructure(array, ref obj, 6, 5);
        });
        Time("ByteArrayToStructureT<T>", () => MavlinkUtil.ByteArrayToStructureT<MAVLink.mavlink_heartbeat_t>(array, 6));
        Time("ReadUsingPointer<T>", () => MavlinkUtil.ReadUsingPointer<MAVLink.mavlink_heartbeat_t>(array, 6));
        Time("ByteArrayToStructureGC<T>", () => MavlinkUtil.ByteArrayToStructureGC<MAVLink.mavlink_heartbeat_t>(array, 6));
        return sb.ToString();

        void Time(string name, Action action)
        {
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
                action();
            sb.AppendLine($"{name}: {sw.Elapsed.TotalMilliseconds:0} ms");
        }
    }
}
