using System.Globalization;
using System.Runtime.InteropServices;
using MissionPlanner.App.Core.ViewModels.Simulation;
using MissionPlanner.App.Tests.Logs;
using MavType = MissionPlanner.ArduPilot.APFirmware.MAV_TYPE;
using ReleaseType = MissionPlanner.ArduPilot.APFirmware.RELEASE_TYPES;

namespace MissionPlanner.App.Tests.Simulation;

[TestClass]
public class SitlArgumentTests
{
    [TestMethod]
    public void DefaultCopterAtCmac()
    {
        var args = SitlArguments.Build(new SitlLaunch("+", SitlHome.Cmac, 1, "/data/sitl/default_params/copter.parm"));
        Assert.AreEqual("-M+ -O-35.363261,149.16523,584,353 -s1 --serial0 tcp:0 --defaults \"/data/sitl/default_params/copter.parm\"", args);
    }

    [TestMethod]
    public void AllOptions()
    {
        var home = new SitlHome(51.5, -0.1234567891, 12.346, -10);
        var args = SitlArguments.Build(new SitlLaunch("plane", home, 5, null, "  --uartC tcp:2  ", Wipe: true));
        Assert.AreEqual("-Mplane -O51.5,-0.1234568,12.35,350 -s5 --serial0 tcp:0 --uartC tcp:2 --wipe", args);
    }

    [TestMethod]
    public void SecondInstanceListensTenPortsHigher()
    {
        var args = SitlArguments.Build(new SitlLaunch("rover", SitlHome.Cmac, 0, Instance: 1));
        StringAssert.Contains(args, "-s1 --instance 1 --serial0 tcp:0");
        Assert.AreEqual(5760, SitlArguments.TcpPort());
        Assert.AreEqual(5770, SitlArguments.TcpPort(1));
    }

    [TestMethod]
    public void HomeIsWrittenInvariantly()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.AreEqual("-35.363261,149.16523,584,353", SitlHome.Cmac.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [TestMethod]
    public void HomeValidation()
    {
        Assert.IsTrue(SitlHome.Cmac.IsValid);
        Assert.IsFalse(new SitlHome(0, 0, 0, 0).IsValid);
        Assert.IsFalse(new SitlHome(91, 10, 0, 0).IsValid);
        Assert.IsFalse(new SitlHome(double.NaN, 10, 0, 0).IsValid);
    }

    [TestMethod]
    public void WorkingDirectoryPerModel()
    {
        Assert.AreEqual(Path.Combine("/d/sitl", "+"), SitlArguments.WorkingDirectory("/d/sitl", "+"));
        Assert.AreEqual(Path.Combine("/d/sitl", "heli-dual"), SitlArguments.WorkingDirectory("/d/sitl", "heli-dual"));
        Assert.AreEqual(Path.Combine("/d/sitl", "a_b"), SitlArguments.WorkingDirectory("/d/sitl", "a/b"));
        Assert.AreEqual(Path.Combine("/d/sitl", "default"), SitlArguments.WorkingDirectory("/d/sitl", ".."));
    }

    [TestMethod]
    public void VehicleMapping()
    {
        Assert.AreEqual("plane", SitlVehicles.DefaultModel(SitlVehicle.Plane));
        Assert.AreEqual("+", SitlVehicles.DefaultModel(SitlVehicle.Copter));
        Assert.AreEqual("rover", SitlVehicles.DefaultModel(SitlVehicle.Rover));
        Assert.AreEqual("heli", SitlVehicles.DefaultModel(SitlVehicle.Heli));
        Assert.AreEqual(MavType.FIXED_WING, SitlVehicles.ManifestType(SitlVehicle.Plane));
        Assert.AreEqual(MavType.Copter, SitlVehicles.ManifestType(SitlVehicle.Copter));
        Assert.AreEqual(MavType.GROUND_ROVER, SitlVehicles.ManifestType(SitlVehicle.Rover));
        Assert.AreEqual(MavType.HELICOPTER, SitlVehicles.ManifestType(SitlVehicle.Heli));
        Assert.AreEqual("ArduHeli", SitlVehicles.BinaryName(SitlVehicle.Heli));
        Assert.AreEqual(33, SitlVehicles.ModelOverrides.Count);
    }
}

[TestClass]
public class SitlPlatformTests
{
    [TestMethod]
    public void HostDetection()
    {
        Assert.AreEqual(SitlHost.LinuxX64, SitlPlatform.Detect(true, false, Architecture.X64));
        Assert.AreEqual(SitlHost.LinuxArm, SitlPlatform.Detect(true, false, Architecture.Arm64));
        Assert.AreEqual(SitlHost.LinuxArm, SitlPlatform.Detect(true, false, Architecture.Arm));
        Assert.AreEqual(SitlHost.WindowsCygwin, SitlPlatform.Detect(false, true, Architecture.X64));
        Assert.AreEqual(SitlHost.WindowsCygwin, SitlPlatform.Detect(false, true, Architecture.Arm64));
        Assert.AreEqual(SitlHost.Unsupported, SitlPlatform.Detect(false, false, Architecture.Arm64)); // macOS
    }

    [TestMethod]
    public void ManifestPlatformsAndFileNames()
    {
        Assert.AreEqual("SITL_x86_64_linux_gnu", SitlPlatform.ManifestPlatform(SitlHost.LinuxX64));
        Assert.AreEqual("SITL_arm_linux_gnueabihf", SitlPlatform.ManifestPlatform(SitlHost.LinuxArm));
        Assert.IsNull(SitlPlatform.ManifestPlatform(SitlHost.WindowsCygwin));
        Assert.AreEqual("ArduCopter", SitlPlatform.ExecutableName(SitlHost.LinuxX64, SitlVehicle.Copter));
        Assert.AreEqual("ArduPlane.exe", SitlPlatform.ExecutableName(SitlHost.WindowsCygwin, SitlVehicle.Plane));
    }

    [TestMethod]
    public void CygwinDownloadLocations()
    {
        const string root = "https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/";
        Assert.AreEqual(root, SitlPlatform.CygwinBaseUrl(ReleaseType.DEV, SitlVehicle.Plane).AbsoluteUri);
        Assert.AreEqual(root + "Beta/", SitlPlatform.CygwinBaseUrl(ReleaseType.BETA, SitlVehicle.Rover).AbsoluteUri);
        Assert.AreEqual(root + "CopterStable/", SitlPlatform.CygwinBaseUrl(ReleaseType.OFFICIAL, SitlVehicle.Copter).AbsoluteUri);
        Assert.AreEqual(root + "CopterStable/", SitlPlatform.CygwinBaseUrl(ReleaseType.OFFICIAL, SitlVehicle.Heli).AbsoluteUri);
        Assert.AreEqual(root + "PlaneStable/", SitlPlatform.CygwinBaseUrl(ReleaseType.OFFICIAL, SitlVehicle.Plane).AbsoluteUri);
        Assert.AreEqual(root + "RoverStable/", SitlPlatform.CygwinBaseUrl(ReleaseType.OFFICIAL, SitlVehicle.Rover).AbsoluteUri);
        Assert.AreEqual(root + "Beta/ArduHeli.elf", SitlPlatform.CygwinExeUrl(ReleaseType.BETA, SitlVehicle.Heli).AbsoluteUri);
        Assert.AreEqual(10, SitlPlatform.CygwinDlls.Count);
        Assert.IsTrue(SitlPlatform.CygwinDlls.Contains("cygwin1.dll"));
    }

    [TestMethod]
    public void BinaryReuseDecision()
    {
        var build = new SitlBuild(new Uri("https://firmware.ardupilot.org/Copter/stable/SITL_x86_64_linux_gnu/arducopter"), "4.7.2", "OFFICIAL", "fb77");
        var same = build.ToInfo(DateTime.UtcNow);

        Assert.AreEqual(SitlBinaryAction.Download, SitlBinaryStore.Decide(false, null, build));
        Assert.AreEqual(SitlBinaryAction.Unavailable, SitlBinaryStore.Decide(false, null, null));
        Assert.AreEqual(SitlBinaryAction.Reuse, SitlBinaryStore.Decide(true, same, build));
        Assert.AreEqual(SitlBinaryAction.Reuse, SitlBinaryStore.Decide(true, null, null), "offline: use what is there");
        Assert.AreEqual(SitlBinaryAction.AskUpdate, SitlBinaryStore.Decide(true, null, build), "unknown local build");
        Assert.AreEqual(SitlBinaryAction.AskUpdate, SitlBinaryStore.Decide(true, same with { GitSha = "0ld" }, build), "new stable build, same URL");
        Assert.AreEqual(SitlBinaryAction.AskUpdate,
            SitlBinaryStore.Decide(true, same, build with { Url = new Uri("https://firmware.ardupilot.org/Copter/beta/SITL_x86_64_linux_gnu/arducopter") }),
            "release changed");

        // the cygwin builds have no version: the URL decides
        var cyg = new SitlBuild(new Uri("https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/ArduCopter.elf"), null, "DEV", null);
        Assert.AreEqual(SitlBinaryAction.Reuse, SitlBinaryStore.Decide(true, cyg.ToInfo(DateTime.UtcNow), cyg));
    }

    [TestMethod]
    public void BinaryNoteRoundTripsAndFileBecomesExecutable()
    {
        var dir = Path.Combine(TestEnvironment.DataRoot, "sitl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "ArduCopter");
        File.WriteAllText(exe, "not a real simulator");
        var info = new SitlBinaryInfo("https://x/arducopter", "4.7.2", "OFFICIAL", "fb77", new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc));

        SitlBinaryStore.WriteInfo(exe, info);
        Assert.AreEqual(info, SitlBinaryStore.ReadInfo(exe));
        Assert.IsNull(SitlBinaryStore.ReadInfo(Path.Combine(dir, "ArduPlane")));

        SitlBinaryStore.MakeExecutable(exe);
        if (!OperatingSystem.IsWindows())
            Assert.IsTrue(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
    }
}

[TestClass]
public class SitlDefaultsTests
{
    // excerpt of Tools/MissionPlanner/vehicleinfo.py: a Python dict with comments, trailing commas and booleans
    private const string VehicleInfo = """
    class VehicleInfo(object):

        def __init__(self):
            self.options = {
        "ArduCopter": {
            "default_frame": "quad",
            "frames": {
                # COPTER
                "+": {
                    "waf_target": "bin/arducopter",
                    "default_params_filename": "default_params/copter.parm",
                },
                "X": {
                    "waf_target": "bin/arducopter",
                    "default_params_filename": ["default_params/copter.parm",
                                                "default_params/copter-X.parm"],
                    "external": True,
                },
                "heli": {
                    "waf_target": "bin/arducopter-heli",
                    "default_params_filename": "default_params/copter-heli.parm",
                },
            },
        },
        "ArduPlane": {
            "default_frame": "plane",
            "frames": {
                "plane": {
                    "waf_target": "bin/arduplane",
                    "default_params_filename": "models/plane.parm",
                },
                "jsbsim": {
                    "waf_target": "bin/arduplane",
                    "default_params_filename": "default_params/plane-jsbsim.parm",
                    "sitl-port": False,
                },
            },
        },
    }

        def default_frame(self, vehicle):
            return self.options[vehicle]["default_frame"]
    """;

    [TestMethod]
    public void FramesFromVehicleInfo()
    {
        CollectionAssert.AreEqual(new[] { "default_params/copter.parm" }, SitlDefaults.ParseVehicleInfo(VehicleInfo, "+").ToArray());
        CollectionAssert.AreEqual(new[] { "default_params/copter.parm", "default_params/copter-X.parm" },
            SitlDefaults.ParseVehicleInfo(VehicleInfo, "X").ToArray());
        CollectionAssert.AreEqual(new[] { "default_params/copter-heli.parm" }, SitlDefaults.ParseVehicleInfo(VehicleInfo, "heli").ToArray());
        CollectionAssert.AreEqual(new[] { "models/plane.parm" }, SitlDefaults.ParseVehicleInfo(VehicleInfo, "plane").ToArray());
        CollectionAssert.AreEqual(new[] { "default_params/plane-jsbsim.parm" }, SitlDefaults.ParseVehicleInfo(VehicleInfo, "jsbsim").ToArray());
        Assert.AreEqual(0, SitlDefaults.ParseVehicleInfo(VehicleInfo, "rover").Count);
        Assert.AreEqual(0, SitlDefaults.ParseVehicleInfo("not python", "+").Count);
    }

    [TestMethod]
    public void FramesFromOldSimVehicle()
    {
        const string sim = """
            _options = {
                "rover": {
                    "waf_target": "bin/ardurover",
                    "default_params_filename": "default_params/rover.parm",
                },
                "X": {
                    "default_params_filename": ["default_params/copter.parm", "default_params/copter-X.parm"],
                },
            }
            """;
        CollectionAssert.AreEqual(new[] { "default_params/rover.parm" }, SitlDefaults.ParseSimVehicle(sim, "rover").ToArray());
        CollectionAssert.AreEqual(new[] { "default_params/copter.parm" }, SitlDefaults.ParseSimVehicle(sim, "x").ToArray());
        Assert.AreEqual(0, SitlDefaults.ParseSimVehicle(sim, "plane").Count);
    }
}

[TestClass]
public class SitlProcessTests
{
    [TestMethod]
    public void StartInfoRunsInTheModelFolder()
    {
        var psi = SitlProcess.CreateStartInfo("/d/sitl/ArduCopter", "-M+ -s1", "/d/sitl/+", "/d/sitl", windows: false);
        Assert.AreEqual("/d/sitl/ArduCopter", psi.FileName);
        Assert.AreEqual("-M+ -s1", psi.Arguments);
        Assert.AreEqual("/d/sitl/+", psi.WorkingDirectory);
        Assert.AreEqual("/d/sitl/+", psi.Environment["HOME"]);
        Assert.IsFalse(psi.UseShellExecute);
        Assert.IsTrue(psi.RedirectStandardOutput && psi.RedirectStandardError);

        var win = SitlProcess.CreateStartInfo(@"C:\sitl\ArduCopter.exe", "-M+", @"C:\sitl\+", @"C:\sitl", windows: true);
        StringAssert.StartsWith(win.Environment["PATH"], @"C:\sitl;C:\sitl\+;");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task OutputAndExitCodeAreReported()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("uses /bin/sh");

        // a system shell stands in for the simulator
        var dir = Path.Combine(TestEnvironment.DataRoot, "sitlproc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var psi = SitlProcess.CreateStartInfo("/bin/sh", "-c \"echo Starting sketch; echo $HOME; echo oops 1>&2; exit 3\"", dir, dir, false);
        var lines = new List<string>();
        var exited = new TaskCompletionSource<int>();

        using var p = SitlProcess.Start(psi, l =>
        {
            lock (lines)
                lines.Add(l);
        }, code => exited.TrySetResult(code));

        Assert.AreEqual(3, await exited.Task);
        lock (lines)
        {
            CollectionAssert.Contains(lines, "Starting sketch");
            CollectionAssert.Contains(lines, dir);
            CollectionAssert.Contains(lines, "oops");
        }

        Assert.IsTrue(p.HasExited);
    }
}

[TestClass]
public class SimulationViewModelTests
{
    [TestMethod]
    public void StartsWithCopterAtCmac()
    {
        var vm = new SimulationViewModel(new FakeUiServices().CreateServices());

        Assert.AreEqual(SitlVehicle.Copter, vm.SelectedVehicle!.Vehicle);
        Assert.IsTrue(vm.SelectedVehicle.IsSelected);
        Assert.AreEqual("+", vm.Model);
        Assert.AreEqual(SitlHome.Cmac, vm.Home);
        Assert.AreEqual(ReleaseType.OFFICIAL, vm.SelectedRelease!.Type);
        StringAssert.StartsWith(vm.CommandPreview, "-M+ -O-35.363261,149.16523,584,353 -s1 --serial0 tcp:0 --defaults");
        Assert.AreEqual(Path.Combine(MissionPlanner.Utilities.Settings.GetUserDataDirectory(), "sitl"), vm.SitlDir);
        StringAssert.StartsWith(vm.SitlDir, TestEnvironment.DataRoot);
    }

    [TestMethod]
    public void ModelOverrideAndVehicleChoice()
    {
        var vm = new SimulationViewModel(new FakeUiServices().CreateServices());

        vm.SelectVehicleCommand.Execute(vm.Vehicles.Single(v => v.Vehicle == SitlVehicle.Plane));
        Assert.AreEqual("plane", vm.Model);
        Assert.IsFalse(vm.Vehicles.Single(v => v.Vehicle == SitlVehicle.Copter).IsSelected);

        vm.SelectedModel = "quadplane";
        Assert.AreEqual("quadplane", vm.Model);
        StringAssert.StartsWith(vm.CommandPreview, "-Mquadplane ");

        vm.SelectedModel = SimulationViewModel.DefaultModelOption;
        Assert.AreEqual("plane", vm.Model);
    }

    [TestMethod]
    public void HomeFromTheMapAndValidation()
    {
        var vm = new SimulationViewModel(new FakeUiServices().CreateServices());
        var changed = 0;
        vm.HomeChanged += (_, _) => changed++;

        vm.MoveHome(-34.98106, 117.85201);
        Assert.AreEqual(-34.98106, vm.Home.Lat, 1e-9);
        Assert.AreEqual(117.85201, vm.Home.Lng, 1e-9);
        Assert.IsTrue(changed > 0);

        vm.SetHome(47.1, 8.2, 410);
        Assert.AreEqual(new SitlHome(47.1, 8.2, 410, 353), vm.Home);

        vm.LatText = "north";
        Assert.IsFalse(vm.Home.IsValid);
        Assert.IsFalse(vm.StartCommand.CanExecute(null));
        Assert.AreNotEqual("", vm.HomeError);

        vm.UseCmacCommand.Execute(null);
        Assert.AreEqual(SitlHome.Cmac, vm.Home);
        Assert.AreEqual("", vm.HomeError);
    }

    [TestMethod]
    public void OptionsShowInTheCommandLine()
    {
        var vm = new SimulationViewModel(new FakeUiServices().CreateServices());
        vm.SpeedUp = 4;
        vm.Wipe = true;
        vm.ExtraArguments = "--uartC tcp:2";
        StringAssert.Contains(vm.CommandPreview, " -s4 ");
        StringAssert.EndsWith(vm.CommandPreview, " --uartC tcp:2 --wipe");
    }
}
