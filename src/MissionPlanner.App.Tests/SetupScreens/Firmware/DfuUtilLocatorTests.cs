using System.Runtime.InteropServices;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

/// <summary>Which dfu-util is used where, with fake file checks; and the copy the app ships.</summary>
[TestClass]
public class DfuUtilLocatorTests
{
    private static DfuUtilLocator Locator(HostOs os, Architecture arch, string bundledDir, string path, IEnumerable<string> files,
        Dictionary<string, string>? env = null)
    {
        var set = files.ToHashSet(StringComparer.Ordinal);
        return new DfuUtilLocator(os, arch, bundledDir, path, set.Contains, name => env?.GetValueOrDefault(name));
    }

    private static string[] Paths(DfuUtilLocator l) => l.Candidates().Select(c => c.Path).ToArray();

    [TestMethod]
    public void LinuxUsesTheBundledCopyThenPath()
    {
        const string dir = "/opt/mp/Tools/dfu-util";
        var l = Locator(HostOs.Linux, Architecture.X64, dir, "/home/u/bin:/usr/bin", [dir + "/dfu-util", "/usr/bin/dfu-util"]);

        CollectionAssert.AreEqual(new[] { dir + "/dfu-util", "/usr/bin/dfu-util" }, Paths(l));
        var bundled = l.Candidates()[0];
        Assert.IsTrue(bundled.IsBundled);
        Assert.AreEqual("bundled", bundled.SourceText);
        Assert.AreEqual(0, bundled.Environment.Count, "the x64 build has libusb linked in");
        Assert.IsFalse(l.Candidates()[1].IsBundled);

        // without the bundled copy (an unsupported architecture): PATH, then the usual folders
        l = Locator(HostOs.Linux, Architecture.Arm, dir, "/home/u/bin", ["/usr/local/bin/dfu-util"]);
        CollectionAssert.AreEqual(new[] { "/usr/local/bin/dfu-util" }, Paths(l));
        Assert.AreEqual(0, Locator(HostOs.Linux, Architecture.X64, dir, "", []).Candidates().Count);
    }

    [TestMethod]
    public void LinuxArm64LoadsTheBundledLibusb()
    {
        const string dir = "/opt/mp/Tools/dfu-util";
        string[] files = [dir + "/dfu-util", dir + "/libusb-1.0.so.0"];

        var tool = Locator(HostOs.Linux, Architecture.Arm64, dir, "/usr/bin", files).Candidates().Single();
        Assert.AreEqual(dir, tool.Environment["LD_LIBRARY_PATH"]);

        tool = Locator(HostOs.Linux, Architecture.Arm64, dir, "/usr/bin", files, new() { ["LD_LIBRARY_PATH"] = "/opt/lib" }).Candidates().Single();
        Assert.AreEqual(dir + ":/opt/lib", tool.Environment["LD_LIBRARY_PATH"]);
    }

    [TestMethod]
    public void WindowsLooksForDfuUtilExe()
    {
        const string dir = @"C:\Program Files\Mission Planner 3\Tools\dfu-util";
        // Windows paths whatever the host: the locator joins with the separator of the OS it locates for
        var bundled = dir + @"\dfu-util.exe";
        var onPath = @"D:\tools\dfu-util.exe";
        var l = Locator(HostOs.Windows, Architecture.X64, dir, @"C:\Windows\system32;""D:\tools"";", [bundled, onPath]);

        Assert.AreEqual("dfu-util.exe", l.ExecutableName);
        CollectionAssert.AreEqual(new[] { bundled, onPath }, Paths(l));
        Assert.AreEqual(0, l.Candidates()[0].Environment.Count);
    }

    [TestMethod]
    public void IntelMacUsesTheBundledCopyWithItsDylib()
    {
        const string dir = "/Applications/Mission Planner 3.app/Contents/MacOS/Tools/dfu-util";
        // apps started from the Finder have a short PATH; Homebrew's folder is looked at anyway
        var l = Locator(HostOs.MacOS, Architecture.X64, dir, "/usr/bin:/bin", [dir + "/dfu-util", "/usr/local/bin/dfu-util"]);

        CollectionAssert.AreEqual(new[] { dir + "/dfu-util", "/usr/local/bin/dfu-util" }, Paths(l));
        Assert.AreEqual(dir + ":/usr/local/lib:/usr/lib", l.Candidates()[0].Environment["DYLD_FALLBACK_LIBRARY_PATH"]);
        Assert.AreEqual(0, l.Candidates()[1].Environment.Count);

        l = Locator(HostOs.MacOS, Architecture.X64, dir, "", [dir + "/dfu-util"], new() { ["DYLD_FALLBACK_LIBRARY_PATH"] = "/opt/x" });
        Assert.AreEqual(dir + ":/opt/x", l.Candidates()[0].Environment["DYLD_FALLBACK_LIBRARY_PATH"]);
    }

    [TestMethod]
    public void AppleSiliconPrefersANativeDfuUtil()
    {
        const string dir = "/Applications/Mission Planner 3.app/Contents/MacOS/Tools/dfu-util";
        var l = Locator(HostOs.MacOS, Architecture.Arm64, dir, "/usr/bin:/bin", [dir + "/dfu-util", "/opt/homebrew/bin/dfu-util"]);
        CollectionAssert.AreEqual(new[] { "/opt/homebrew/bin/dfu-util", dir + "/dfu-util" }, Paths(l));

        // no Homebrew: the bundled Intel build, under Rosetta
        l = Locator(HostOs.MacOS, Architecture.Arm64, dir, "/usr/bin:/bin", [dir + "/dfu-util"]);
        var only = l.Candidates().Single();
        Assert.IsTrue(only.IsBundled);
        Assert.IsTrue(only.Environment.ContainsKey("DYLD_FALLBACK_LIBRARY_PATH"));
    }

    [TestMethod]
    public void TheBundledFolderOnPathIsNotListedTwice()
    {
        const string dir = "/opt/mp/Tools/dfu-util";
        var l = Locator(HostOs.Linux, Architecture.X64, dir, dir + ":" + dir + "/", [dir + "/dfu-util"]);
        Assert.AreEqual(1, l.Candidates().Count);
    }

    /// <summary>The build copies this machine's dfu-util next to the output, executable, with its licences.</summary>
    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task BundledDfuUtilIsInTheOutputAndStarts()
    {
        var dir = DfuUtilLocator.BundledDirectoryIn(AppContext.BaseDirectory);
        var locator = DfuUtilLocator.ForThisMachine();
        var bundled = Path.Combine(dir, locator.ExecutableName);
        var supported = (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64) ||
                        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        if (!supported)
            Assert.Inconclusive("no dfu-util is bundled for this platform");

        Assert.IsTrue(File.Exists(bundled), bundled);
        foreach (var name in new[] { "README.txt", "COPYING", "COPYING.libusb" })
            Assert.IsTrue(File.Exists(Path.Combine(dir, name)), name);
        if (!OperatingSystem.IsWindows())
            Assert.IsTrue((File.GetUnixFileMode(bundled) & UnixFileMode.UserExecute) != 0, "executable bit kept");
        var tool = locator.Candidates().FirstOrDefault(c => c.IsBundled);
        Assert.IsNotNull(tool);

        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
            return;

        // linux-x64: the static build itself. --version prints and exits before libusb is set up (main.c)
        var header = new byte[20];
        await using (var f = File.OpenRead(bundled))
            f.ReadExactly(header);
        CollectionAssert.AreEqual("\u007fELF"u8.ToArray(), header[..4]);
        Assert.AreEqual((ushort)0x3E, BitConverter.ToUInt16(header, 18), "x86-64");

        var lines = new List<string>();
        var code = await SystemProcessRunner.Instance.RunAsync(new ProcessRequest(tool.Path, ["--version"], tool.Environment),
            (l, _) => lines.Add(l), CancellationToken.None);
        Assert.AreEqual(0, code, string.Join("\n", lines));
        Assert.AreEqual("dfu-util 0.11", lines.First());
    }

    /// <summary>Each folder in the source tree holds the build for its platform, executable where that matters.</summary>
    [TestMethod]
    public void BundledBinariesMatchTheirFolders()
    {
        var source = Path.Combine(Repo(), "src", "MissionPlanner.App", "Tools", "dfu-util");

        AssertElf(Path.Combine(source, "linux-x64", "dfu-util"), 0x3E);
        AssertElf(Path.Combine(source, "linux-arm64", "dfu-util"), 0xB7);
        AssertElf(Path.Combine(source, "linux-arm64", "libusb-1.0.so.0"), 0xB7);
        AssertPe(Path.Combine(source, "win-x64", "dfu-util.exe"), 0x8664);
        AssertPe(Path.Combine(source, "win-x86", "dfu-util.exe"), 0x014C);
        AssertMachO(Path.Combine(source, "osx-x64", "dfu-util"));
        AssertMachO(Path.Combine(source, "osx-x64", "libusb-1.0.0.dylib"));
        foreach (var name in new[] { "README.txt", "COPYING", "COPYING.libusb", "dfu-util.targets" })
            Assert.IsTrue(File.Exists(Path.Combine(source, name)), name);

        if (!OperatingSystem.IsWindows())
        {
            foreach (var exe in new[] { "linux-x64/dfu-util", "linux-arm64/dfu-util", "osx-x64/dfu-util" })
                Assert.IsTrue((File.GetUnixFileMode(Path.Combine(source, exe)) & UnixFileMode.UserExecute) != 0, exe);
        }
    }

    private static byte[] Head(string path, int n)
    {
        Assert.IsTrue(File.Exists(path), path);
        using var f = File.OpenRead(path);
        var b = new byte[n];
        f.ReadExactly(b);
        return b;
    }

    private static void AssertElf(string path, ushort machine)
    {
        var h = Head(path, 20);
        CollectionAssert.AreEqual("\u007fELF"u8.ToArray(), h[..4], path);
        Assert.AreEqual(2, h[4], "64-bit: " + path);
        Assert.AreEqual(machine, BitConverter.ToUInt16(h, 18), path);
    }

    private static void AssertPe(string path, ushort machine)
    {
        var dos = Head(path, 0x40);
        Assert.AreEqual((byte)'M', dos[0], path);
        var pe = BitConverter.ToInt32(dos, 0x3C);
        var h = Head(path, pe + 6);
        Assert.AreEqual(0x00004550, BitConverter.ToInt32(h, pe), "PE signature: " + path);
        Assert.AreEqual(machine, BitConverter.ToUInt16(h, pe + 4), path);
    }

    private static void AssertMachO(string path)
    {
        var h = Head(path, 8);
        Assert.AreEqual(0xFEEDFACFu, BitConverter.ToUInt32(h, 0), "64-bit Mach-O: " + path);
        Assert.AreEqual(0x01000007u, BitConverter.ToUInt32(h, 4), "x86_64: " + path);
    }

    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MissionPlanner.Next.sln")))
                return dir.FullName;
        }

        throw new InvalidOperationException("repository not found");
    }
}
