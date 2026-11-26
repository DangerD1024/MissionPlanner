using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using MissionPlanner.App.Core.Updates;

namespace MissionPlanner.App.Tests.Updates;

[TestClass]
public class UpdateInstallTests
{
    private const string Release = """
        {
          "tag_name": "v3.0.6b", "draft": false, "html_url": "https://example.invalid/r", "body": "",
          "assets": [
            { "name": "MissionPlanner-3.0.6b-win-x64.msi", "size": 9, "browser_download_url": "https://example.invalid/a.msi" },
            { "name": "MissionPlanner-3.0.6b-win-x64-update.zip", "size": 8, "browser_download_url": "https://example.invalid/w.zip" },
            { "name": "MissionPlanner-3.0.6b-osx-arm64.dmg", "size": 7, "browser_download_url": "https://example.invalid/a.dmg" },
            { "name": "MissionPlanner-3.0.6b-osx-arm64-update.zip", "size": 6, "browser_download_url": "https://example.invalid/m.zip" },
            { "name": "MissionPlanner-3.0.6b-osx-x64.dmg", "size": 5, "browser_download_url": "https://example.invalid/x.dmg" }
          ]
        }
        """;

    [TestMethod]
    public void TheInPlaceUpdatePackageIsPreferredOverTheInstaller()
    {
        var win = UpdateCheck.AssetSuffixes(System.Runtime.InteropServices.OSPlatform.Windows, System.Runtime.InteropServices.Architecture.X64);
        CollectionAssert.AreEqual(new[] { "win-x64-update.zip", "win-x64.msi" }, win.ToArray());
        Assert.AreEqual("MissionPlanner-3.0.6b-win-x64-update.zip", UpdateCheck.ParseBest(Release, win)!.AssetName);

        var mac = UpdateCheck.AssetSuffixes(System.Runtime.InteropServices.OSPlatform.OSX, System.Runtime.InteropServices.Architecture.Arm64);
        Assert.AreEqual("MissionPlanner-3.0.6b-osx-arm64-update.zip", UpdateCheck.ParseBest(Release, mac)!.AssetName);

        // a release without the package: the installer, as before
        var intel = UpdateCheck.AssetSuffixes(System.Runtime.InteropServices.OSPlatform.OSX, System.Runtime.InteropServices.Architecture.X64);
        Assert.AreEqual("MissionPlanner-3.0.6b-osx-x64.dmg", UpdateCheck.ParseBest(Release, intel)!.AssetName);
        var older = Release.Replace("win-x64-update.zip", "win-x64-other.zip");
        Assert.AreEqual("MissionPlanner-3.0.6b-win-x64.msi", UpdateCheck.ParseBest(older, win)!.AssetName);
        Assert.AreEqual("3.0.6b", UpdateCheck.ParseBest(Release, [])!.Version);
    }

    [TestMethod]
    public void WindowsZipIsCopiedOverTheInstalledFiles()
    {
        if (!OperatingSystem.IsWindows())
            return; // the updater is PowerShell + robocopy

        var root = Path.Combine(Path.GetTempPath(), "mpupd-win-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = Path.Combine(root, "Mission Planner 3");
            Directory.CreateDirectory(Path.Combine(target, "gstreamer"));
            File.WriteAllText(Path.Combine(target, "MissionPlanner.App.dll"), "OLD");
            File.WriteAllText(Path.Combine(target, "gstreamer", "keep.dll"), "kept");

            var pkg = Path.Combine(root, "pkg");
            Directory.CreateDirectory(Path.Combine(pkg, "Tools"));
            File.WriteAllText(Path.Combine(pkg, "MissionPlanner.App.dll"), "NEW");
            File.WriteAllText(Path.Combine(pkg, "Tools", "added.exe"), "added");
            var zip = Path.Combine(root, "update.zip");
            ZipFile.CreateFromDirectory(pkg, zip);

            var script = UpdateViewModel.StageWindowsUpdate(zip, target, pid: 999999, admin: false);
            var text = File.ReadAllText(script);
            StringAssert.Contains(text, "Wait-Process -Id 999999");
            StringAssert.Contains(text, "Start-Process -FilePath");
            Assert.IsFalse(text.Contains("RunAs"), "a writable folder needs no administrator");
            Assert.IsTrue(UpdateViewModel.WindowsUpdateScript("c.ps1", "a.exe", 1, admin: true).Contains("-Verb RunAs"));

            // run only the copy step: the full updater would start the app at the end
            var copy = Path.Combine(Path.GetDirectoryName(script)!, "copy.ps1");
            using var p = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", copy },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.IsTrue(p!.WaitForExit(60000));
            Assert.AreEqual(0, p.ExitCode);
            Assert.AreEqual("NEW", File.ReadAllText(Path.Combine(target, "MissionPlanner.App.dll")));
            Assert.AreEqual("added", File.ReadAllText(Path.Combine(target, "Tools", "added.exe")));
            Assert.AreEqual("kept", File.ReadAllText(Path.Combine(target, "gstreamer", "keep.dll")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void MacScriptSwapsTheBundleSignsItAndReopensIt()
    {
        Assert.AreEqual("/Applications/Mission Planner 3.app",
            UpdateViewModel.MacBundlePath("/Applications/Mission Planner 3.app/Contents/MacOS/MissionPlanner.App"));
        Assert.IsNull(UpdateViewModel.MacBundlePath("/home/me/mp/MissionPlanner.App"));

        var s = UpdateViewModel.MacUpdateScript("/Users/o'neil/Downloads/u.zip", "/Applications/Mission Planner 3.app", 4242);
        StringAssert.Contains(s, "kill -0 4242");
        StringAssert.Contains(s, "ditto -x -k '/Users/o'\\''neil/Downloads/u.zip'", "paths are quoted for sh, apostrophes too");
        StringAssert.Contains(s, "codesign --force --deep -s -");
        StringAssert.Contains(s, "xattr -cr");
        StringAssert.Contains(s, "with administrator privileges");
        StringAssert.Contains(s, "mv '/Applications/Mission Planner 3.app' '/Applications/Mission Planner 3.app.old'");
        Assert.IsTrue(s.TrimEnd().EndsWith("open '/Applications/Mission Planner 3.app'"));
    }
    [TestMethod]
    public void LinuxTarGzUnpacksAndReplacesCurrentFiles()
    {
        if (!OperatingSystem.IsLinux())
            return; // the updater script is /bin/sh

        var root = Path.Combine(Path.GetTempPath(), "mpupd-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // current install: an old app dll plus a user file that must survive
            var target = Path.Combine(root, "app");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "MissionPlanner.App.dll"), "OLD");
            File.WriteAllText(Path.Combine(target, "keep.txt"), "user");

            // package: one wrapper folder holding the new app dll + an added file
            var pkg = Path.Combine(root, "pkg", "MissionPlanner");
            Directory.CreateDirectory(pkg);
            File.WriteAllText(Path.Combine(pkg, "MissionPlanner.App.dll"), "NEW");
            File.WriteAllText(Path.Combine(pkg, "added.txt"), "added");
            var tar = Path.Combine(root, "update.tar");
            TarFile.CreateFromDirectory(Path.Combine(root, "pkg"), tar, includeBaseDirectory: true);
            var tarGz = Path.Combine(root, "update.tar.gz");
            using (var src = File.OpenRead(tar))
            using (var dst = File.Create(tarGz))
            using (var gz = new GZipStream(dst, CompressionLevel.Optimal))
                src.CopyTo(gz);

            var script = UpdateViewModel.StageLinuxUpdate(tarGz, target);
            Assert.IsTrue(File.Exists(script), "updater script written");
            var text = File.ReadAllText(script);
            StringAssert.Contains(text, "cp -a");
            StringAssert.Contains(text, target);

            // run the updater (it sleeps for the app to exit, copies, relaunches)
            using var p = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { script }, UseShellExecute = false });
            p!.WaitForExit(15000);

            Assert.AreEqual("NEW", File.ReadAllText(Path.Combine(target, "MissionPlanner.App.dll")), "app dll replaced");
            Assert.AreEqual("added", File.ReadAllText(Path.Combine(target, "added.txt")), "new file copied");
            Assert.AreEqual("user", File.ReadAllText(Path.Combine(target, "keep.txt")), "user file kept");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}