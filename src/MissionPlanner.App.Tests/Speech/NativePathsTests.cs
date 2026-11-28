using MissionPlanner.App.Core.Speech;

namespace MissionPlanner.App.Tests.Speech;

/// <summary>Voice folders whose names are not ASCII ("OneDrive\Документы\...") on Windows.</summary>
[TestClass]
public class NativePathsTests
{
    [TestMethod]
    public void NonAsciiPathsGoToTheEngineByTheirShortName()
    {
        const string cyrillic = @"C:\Users\d\OneDrive\Документы\Mission Planner\voices\kokoro";
        Assert.IsTrue(NativePaths.IsAscii(@"C:\Users\d\Documents\Mission Planner\voices"));
        Assert.IsFalse(NativePaths.IsAscii(cyrillic));

        // ASCII paths, and every path off Windows (UTF-8 there), are passed as they are
        Assert.AreEqual(@"C:\plain\voices", NativePaths.ForNative(@"C:\plain\voices", windows: true, _ => "unused"));
        Assert.AreEqual(cyrillic, NativePaths.ForNative(cyrillic, windows: false, _ => null));

        // Windows: the short name when it is ASCII; null when there is none (8.3 names off) or it is not ASCII
        Assert.AreEqual(@"C:\Users\d\OneDrive\7496~1\MISSIO~1\voices\kokoro",
            NativePaths.ForNative(cyrillic, windows: true, _ => @"C:\Users\d\OneDrive\7496~1\MISSIO~1\voices\kokoro"));
        Assert.IsNull(NativePaths.ForNative(cyrillic, windows: true, _ => null));
        Assert.IsNull(NativePaths.ForNative(cyrillic, windows: true, p => p));
    }

    [TestMethod]
    public void ARealCyrillicFolderGetsAnAsciiPathOrTheCommonFolder()
    {
        if (!OperatingSystem.IsWindows())
            return; // only Windows hands paths to the engine as ANSI

        var dir = Path.Combine(TestEnvironment.DataRoot, "Документы-" + Guid.NewGuid().ToString("N")[..6], "voices");
        Directory.CreateDirectory(dir);
        try
        {
            var root = NativePaths.VoicesRoot(dir);
            // either this folder (the drive has short names) or ProgramData, which is ASCII
            Assert.IsNotNull(NativePaths.ForNative(root), root);
            if (root == dir)
                Assert.IsTrue(NativePaths.IsAscii(NativePaths.ForNative(dir)!));
            else
                StringAssert.EndsWith(root, Path.Combine("Mission Planner", "voices"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, true);
        }
    }
}
