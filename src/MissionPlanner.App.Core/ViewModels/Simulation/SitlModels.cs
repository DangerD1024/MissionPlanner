using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MavType = MissionPlanner.ArduPilot.APFirmware.MAV_TYPE;
using ReleaseType = MissionPlanner.ArduPilot.APFirmware.RELEASE_TYPES;

namespace MissionPlanner.App.Core.ViewModels.Simulation;

public enum SitlVehicle
{
    Plane,
    Copter,
    Rover,
    Heli,
}

/// <summary>Vehicle → SITL model, binary and manifest type, as GCSViews/SITL.cs maps them.</summary>
public static class SitlVehicles
{
    public static IReadOnlyList<SitlVehicle> All { get; } = [SitlVehicle.Plane, SitlVehicle.Copter, SitlVehicle.Rover, SitlVehicle.Heli];

    /// <summary>The -M model each vehicle picture starts.</summary>
    public static string DefaultModel(SitlVehicle v) => v switch
    {
        SitlVehicle.Plane => "plane",
        SitlVehicle.Rover => "rover",
        SitlVehicle.Heli => "heli",
        _ => "+",
    };

    /// <summary>Local file name of the simulator (SITL.cs: "ArduCopter.elf" without the extension).</summary>
    public static string BinaryName(SitlVehicle v) => v switch
    {
        SitlVehicle.Plane => "ArduPlane",
        SitlVehicle.Rover => "ArduRover",
        SitlVehicle.Heli => "ArduHeli",
        _ => "ArduCopter",
    };

    public static MavType ManifestType(SitlVehicle v) => v switch
    {
        SitlVehicle.Plane => MavType.FIXED_WING,
        SitlVehicle.Rover => MavType.GROUND_ROVER,
        SitlVehicle.Heli => MavType.HELICOPTER,
        _ => MavType.Copter,
    };

    /// <summary>The model override list of Mission Planner's SITL page (cmb_model).</summary>
    public static IReadOnlyList<string> ModelOverrides { get; } =
    [
        "quadplane", "xplane", "xplane-heli", "firefly", "+", "quad", "copter", "x", "hexa", "octa", "tri", "y6", "heli",
        "heli-dual", "heli-compound", "singlecopter", "coaxcopter", "rover", "crrcsim", "jsbsim", "flightaxis", "gazebo",
        "last_letter", "tracker", "balloon", "plane", "calibration", "plane-jet", "sailboat", "motorboat", "morse-rover",
        "rover-skid", "plane-3d",
    ];
}

public enum SitlHost
{
    LinuxX64,
    LinuxArm,
    WindowsCygwin,
    Unsupported,
}

/// <summary>Which SITL build this machine runs and where it comes from (GCSViews/SITL.cs CheckandGetSITLImage).</summary>
public static class SitlPlatform
{
    public const string LinuxX64Platform = "SITL_x86_64_linux_gnu";
    public const string LinuxArmPlatform = "SITL_arm_linux_gnueabihf";

    private static readonly Uri CygwinMaster = new("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/");
    private static readonly Uri CygwinBeta = new("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/Beta/");
    private static readonly Uri CygwinCopterStable = new("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/CopterStable/");
    private static readonly Uri CygwinPlaneStable = new("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/PlaneStable/");
    private static readonly Uri CygwinRoverStable = new("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/RoverStable/");

    /// <summary>The cygwin runtime the Windows builds need next to the .exe.</summary>
    public static IReadOnlyList<string> CygwinDlls { get; } =
    [
        "cygatomic-1.dll", "cyggcc_s-1.dll", "cyggcc_s-seh-1.dll", "cyggomp-1.dll", "cygiconv-2.dll", "cygintl-8.dll",
        "cygquadmath-0.dll", "cygssp-0.dll", "cygstdc++-6.dll", "cygwin1.dll",
    ];

    public static SitlHost Current => Detect(RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows), RuntimeInformation.OSArchitecture);

    public static SitlHost Detect(bool linux, bool windows, Architecture arch)
    {
        if (linux)
        {
            return arch switch
            {
                Architecture.X64 or Architecture.X86 => SitlHost.LinuxX64,
                Architecture.Arm or Architecture.Arm64 => SitlHost.LinuxArm,
                _ => SitlHost.Unsupported,
            };
        }

        // the cygwin builds are x86-64; Windows on ARM runs them under emulation
        return windows ? SitlHost.WindowsCygwin : SitlHost.Unsupported;
    }

    /// <summary>The manifest platform of the Linux builds; null where the manifest has none.</summary>
    public static string? ManifestPlatform(SitlHost host) => host switch
    {
        SitlHost.LinuxX64 => LinuxX64Platform,
        SitlHost.LinuxArm => LinuxArmPlatform,
        _ => null,
    };

    public static string ExecutableName(SitlHost host, SitlVehicle vehicle) =>
        SitlVehicles.BinaryName(vehicle) + (host == SitlHost.WindowsCygwin ? ".exe" : "");

    /// <summary>The cygwin build's folder for a release (DEV = master, BETA, else the vehicle's stable folder).</summary>
    public static Uri CygwinBaseUrl(ReleaseType release, SitlVehicle vehicle) => release switch
    {
        ReleaseType.DEV => CygwinMaster,
        ReleaseType.BETA => CygwinBeta,
        _ => vehicle switch
        {
            SitlVehicle.Plane => CygwinPlaneStable,
            SitlVehicle.Rover => CygwinRoverStable,
            _ => CygwinCopterStable, // heli included
        },
    };

    /// <summary>The cygwin executable is published as "ArduCopter.elf" and saved as "ArduCopter.exe".</summary>
    public static Uri CygwinExeUrl(ReleaseType release, SitlVehicle vehicle) =>
        new(CygwinBaseUrl(release, vehicle), SitlVehicles.BinaryName(vehicle) + ".elf");

    public static string Describe(SitlHost host) => host switch
    {
        SitlHost.LinuxX64 => "Linux x86-64 (" + LinuxX64Platform + ")",
        SitlHost.LinuxArm => "Linux ARM (" + LinuxArmPlatform + ")",
        SitlHost.WindowsCygwin => "Windows (cygwin build)",
        _ => "not available on this system",
    };
}

/// <summary>A start location: what -O takes.</summary>
public readonly record struct SitlHome(double Lat, double Lng, double Alt, double Heading)
{
    /// <summary>Canberra Model Aircraft Club, ArduPilot's usual SITL home.</summary>
    public static SitlHome Cmac { get; } = new(-35.363261, 149.165230, 584, 353);

    public bool IsValid => Lat is >= -90 and <= 90 && Lng is >= -180 and <= 180 && !(Lat == 0 && Lng == 0) &&
                           !double.IsNaN(Alt) && !double.IsNaN(Heading);

    /// <summary>"lat,lng,alt,heading" in invariant culture (SITL.cs BuildHomeLocation).</summary>
    public override string ToString()
    {
        var inv = CultureInfo.InvariantCulture;
        var heading = ((Heading % 360) + 360) % 360;
        return string.Join(",", Lat.ToString("0.#######", inv), Lng.ToString("0.#######", inv), Alt.ToString("0.##", inv),
            Math.Round(heading).ToString("0", inv));
    }
}

public sealed record SitlLaunch(
    string Model,
    SitlHome Home,
    int SpeedUp = 1,
    string? DefaultsFile = null,
    string? ExtraArguments = null,
    bool Wipe = false,
    int Instance = 0);

/// <summary>The simulator's command line (SITL.cs StartSITL).</summary>
public static class SitlArguments
{
    /// <summary>serial0 tcp:0 listens on 5760 + 10 × instance.</summary>
    public static int TcpPort(int instance = 0) => 5760 + 10 * instance;

    public static string Build(SitlLaunch l)
    {
        var sb = new StringBuilder();
        sb.Append("-M").Append(l.Model);
        sb.Append(" -O").Append(l.Home);
        sb.Append(" -s").Append(Math.Max(1, l.SpeedUp).ToString(CultureInfo.InvariantCulture));
        if (l.Instance > 0)
            sb.Append(" --instance ").Append(l.Instance.ToString(CultureInfo.InvariantCulture));
        sb.Append(" --serial0 tcp:0");
        if (!string.IsNullOrEmpty(l.DefaultsFile))
            sb.Append(" --defaults \"").Append(l.DefaultsFile).Append('"');
        var extra = l.ExtraArguments?.Trim();
        if (!string.IsNullOrEmpty(extra))
            sb.Append(' ').Append(extra);
        if (l.Wipe)
            sb.Append(" --wipe");
        return sb.ToString();
    }

    /// <summary>Folder the simulator runs in (its eeprom.bin and logs live there): sitl/&lt;model&gt;/.</summary>
    public static string WorkingDirectory(string sitlDir, string model) =>
        Path.Combine(sitlDir, SafeName(model));

    /// <summary>A model name as a folder name ("+" stays, as in Mission Planner; path characters do not).</summary>
    public static string SafeName(string model)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var name = new string(model.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return name.Length == 0 || name == "." || name == ".." ? "default" : name;
    }
}
