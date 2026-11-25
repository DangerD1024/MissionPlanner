using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using MissionPlanner.App.Core.Updates;

namespace MissionPlanner.App.Tests.Updates;

[TestClass]
public class UpdateInstallTests
{
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