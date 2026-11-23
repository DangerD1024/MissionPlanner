using System.Diagnostics;

namespace MissionPlanner.App.Core;

/// <summary>
/// Linux USB access for autopilots: serial ports belong to a group (dialout, or uucp on Arch) the
/// user must be in; a board in STM32 DFU mode (for a bootloader with dfu-util) needs a udev rule;
/// and ModemManager must leave autopilots alone, or it grabs the port for half a minute after
/// plugging in. One command, run as root through pkexec (the desktop's password prompt), does it all.
/// </summary>
public static class LinuxUsbSetup
{
    public const string RulesPath = "/etc/udev/rules.d/50-mission-planner.rules";

    /// <summary>The udev rules: DFU for everyone at the desk, and ModemManager kept off autopilot vendors.</summary>
    public const string Rules = """
        # Mission Planner: USB access for autopilots
        # STM32 in DFU mode (bootloader recovery with dfu-util)
        SUBSYSTEM=="usb", ATTRS{idVendor}=="0483", ATTRS{idProduct}=="df11", MODE="0666", TAG+="uaccess"
        # keep ModemManager away from autopilots (ArduPilot/pid.codes, 3DR, Holybro, CubePilot, ST virtual COM)
        ATTRS{idVendor}=="1209", ENV{ID_MM_DEVICE_IGNORE}="1"
        ATTRS{idVendor}=="26ac", ENV{ID_MM_DEVICE_IGNORE}="1"
        ATTRS{idVendor}=="3162", ENV{ID_MM_DEVICE_IGNORE}="1"
        ATTRS{idVendor}=="2dae", ENV{ID_MM_DEVICE_IGNORE}="1"
        ATTRS{idVendor}=="0483", ATTRS{idProduct}=="5740", ENV{ID_MM_DEVICE_IGNORE}="1"

        """;

    /// <summary>The group that owns serial ports here: the owner of an existing tty, else dialout, else uucp.</summary>
    public static string SerialGroup(string groupFile = "/etc/group", string devDir = "/dev")
    {
        var groups = ReadGroups(groupFile);
        foreach (var name in new[] { "ttyACM0", "ttyUSB0", "ttyS0" })
        {
            var owner = FileGroupName(Path.Combine(devDir, name));
            if (owner is "dialout" or "uucp" or "serial")
                return owner;
        }

        if (groups.Values.Any(g => g.Name == "dialout"))
            return "dialout";
        return groups.Values.Any(g => g.Name == "uucp") ? "uucp" : "dialout";
    }

    /// <summary>True when <paramref name="user"/> is listed in the group (or has it as primary group).</summary>
    public static bool IsMember(string user, string group, string groupFile = "/etc/group", string passwdFile = "/etc/passwd")
    {
        var groups = ReadGroups(groupFile);
        var entry = groups.Values.FirstOrDefault(g => g.Name == group);
        if (entry.Name == null)
            return false;
        if (entry.Members.Contains(user))
            return true;
        try
        {
            // primary group: user:x:uid:gid:...
            return File.ReadLines(passwdFile).Select(l => l.Split(':')).Any(p => p.Length > 3 && p[0] == user && p[3] == entry.Gid);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The root shell script: add the user to the group, install the rules, reload udev.</summary>
    public static string Script(string user, string group) =>
        $"set -e\n" +
        $"usermod -aG {Quote(group)} {Quote(user)}\n" +
        $"cat > {RulesPath} <<'MPRULES'\n{Rules}MPRULES\n" +
        "udevadm control --reload-rules\n" +
        "udevadm trigger\n";

    /// <summary>What to type in a terminal when there is no graphical password prompt.</summary>
    public static string ManualCommands(string user, string group) =>
        $"sudo usermod -aG {group} {user}\n" +
        $"sudo tee {RulesPath} > /dev/null <<'EOF'\n{Rules}EOF\n" +
        "sudo udevadm control --reload-rules && sudo udevadm trigger";

    /// <summary>A summary for the HELP page: whether serial ports and DFU will work.</summary>
    public static string Describe(string user, string group, bool member, bool rulesInstalled) =>
        (member
            ? $"Serial ports: OK ({user} is in the {group} group)."
            : $"Serial ports: {user} is not in the {group} group yet, so USB autopilots cannot be opened.") +
        (rulesInstalled ? " DFU and ModemManager rules: installed." : " DFU and ModemManager rules: not installed.");

    public static string CurrentUser => Environment.UserName;

    public static bool RulesInstalled => File.Exists(RulesPath);

    /// <summary>
    /// Runs the setup as root with pkexec (asks for the password); returns null on success or the error.
    /// A new group takes effect after logging out and in again.
    /// </summary>
    public static async Task<string?> ApplyAsync(string user, string group)
    {
        var psi = new ProcessStartInfo("pkexec")
        {
            ArgumentList = { "/bin/sh", "-c", Script(user, group) },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null)
                return "pkexec did not start";
            var err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode switch
            {
                0 => null,
                126 or 127 => "The password was not given (cancelled).",
                _ => string.IsNullOrWhiteSpace(err) ? $"Failed (exit code {p.ExitCode})." : err.Trim(),
            };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "pkexec is not installed; run the commands below in a terminal.";
        }
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private readonly record struct GroupEntry(string Name, string Gid, HashSet<string> Members);

    private static Dictionary<string, GroupEntry> ReadGroups(string groupFile)
    {
        var map = new Dictionary<string, GroupEntry>();
        try
        {
            foreach (var line in File.ReadLines(groupFile))
            {
                var p = line.Split(':');
                if (p.Length < 4)
                    continue;
                map[p[2]] = new GroupEntry(p[0], p[2], p[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet());
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return map;
    }

    /// <summary>The group owning a device file (stat -c %G), or null.</summary>
    private static string? FileGroupName(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path))
            return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("stat") { ArgumentList = { "-c", "%G", path }, RedirectStandardOutput = true, UseShellExecute = false });
            if (p == null)
                return null;
            var name = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return name.Length > 0 ? name : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
