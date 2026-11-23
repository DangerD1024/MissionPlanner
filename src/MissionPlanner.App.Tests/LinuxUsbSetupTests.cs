using MissionPlanner.App.Core;

namespace MissionPlanner.App.Tests;

[TestClass]
public class LinuxUsbSetupTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(TestEnvironment.DataRoot, "usb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "dev"));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    private string Write(string name, string text)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    [TestMethod]
    public void GroupMembershipFromGroupAndPasswd()
    {
        var group = Write("group", "root:x:0:\ndialout:x:20:alice, bob\nuucp:x:14:\npilot:x:1001:\n");
        var passwd = Write("passwd", "alice:x:1000:1000::/home/alice:/bin/bash\ncarol:x:1002:20::/home/carol:/bin/bash\n");
        Assert.IsTrue(LinuxUsbSetup.IsMember("alice", "dialout", group, passwd));
        Assert.IsTrue(LinuxUsbSetup.IsMember("bob", "dialout", group, passwd));
        Assert.IsTrue(LinuxUsbSetup.IsMember("carol", "dialout", group, passwd)); // primary group 20
        Assert.IsFalse(LinuxUsbSetup.IsMember("dave", "dialout", group, passwd));
        Assert.IsFalse(LinuxUsbSetup.IsMember("alice", "nosuchgroup", group, passwd));
        Assert.IsFalse(LinuxUsbSetup.IsMember("alice", "dialout", Path.Combine(_dir, "missing"), passwd));
    }

    [TestMethod]
    public void SerialGroupIsDialoutOrUucpOnArch()
    {
        var dev = Path.Combine(_dir, "dev");
        Assert.AreEqual("dialout", LinuxUsbSetup.SerialGroup(Write("g1", "root:x:0:\ndialout:x:20:\nuucp:x:10:\n"), dev));
        Assert.AreEqual("uucp", LinuxUsbSetup.SerialGroup(Write("g2", "root:x:0:\nuucp:x:14:\n"), dev));
        Assert.AreEqual("dialout", LinuxUsbSetup.SerialGroup(Write("g3", "root:x:0:\n"), dev));
    }

    [TestMethod]
    public void ScriptAddsTheUserInstallsRulesAndReloadsUdev()
    {
        var script = LinuxUsbSetup.Script("alice", "dialout");
        StringAssert.StartsWith(script, "set -e\n");
        StringAssert.Contains(script, "usermod -aG 'dialout' 'alice'");
        StringAssert.Contains(script, $"cat > {LinuxUsbSetup.RulesPath} <<'MPRULES'\n# Mission Planner");
        StringAssert.Contains(script, "\nMPRULES\nudevadm control --reload-rules\nudevadm trigger\n");
        StringAssert.Contains(LinuxUsbSetup.Rules, "ATTRS{idVendor}==\"0483\", ATTRS{idProduct}==\"df11\", MODE=\"0666\", TAG+=\"uaccess\"");
        StringAssert.Contains(LinuxUsbSetup.Rules, "ATTRS{idVendor}==\"1209\", ENV{ID_MM_DEVICE_IGNORE}=\"1\"");
        // a name with a quote cannot break out of the shell word
        StringAssert.Contains(LinuxUsbSetup.Script("o'neil", "dialout"), "'o'\\''neil'");

        var manual = LinuxUsbSetup.ManualCommands("alice", "uucp");
        StringAssert.StartsWith(manual, "sudo usermod -aG uucp alice\n");
        StringAssert.Contains(manual, $"sudo tee {LinuxUsbSetup.RulesPath}");
        StringAssert.EndsWith(manual, "sudo udevadm control --reload-rules && sudo udevadm trigger");
    }

    [TestMethod]
    public void ScriptIsValidShell()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("needs /bin/sh");
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(LinuxUsbSetup.Script("o'neil", "dialout"));
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.AreEqual(0, p.ExitCode, err);
    }

    [TestMethod]
    public void StatusText()
    {
        Assert.AreEqual("Serial ports: OK (alice is in the dialout group). DFU and ModemManager rules: installed.",
            LinuxUsbSetup.Describe("alice", "dialout", true, true));
        StringAssert.Contains(LinuxUsbSetup.Describe("alice", "uucp", false, false), "alice is not in the uucp group yet");
        StringAssert.EndsWith(LinuxUsbSetup.Describe("alice", "uucp", false, false), "rules: not installed.");
    }
}
