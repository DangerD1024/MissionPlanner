using MissionPlanner.App.Core;

namespace MissionPlanner.App.Tests;

[TestClass]
public class DesktopEntryTests
{
    [TestMethod]
    public void ExecStartsThisCopyOfTheApp()
    {
        Assert.AreEqual("/opt/mp/MissionPlanner.App", DesktopEntry.ExecCommand("/opt/mp/MissionPlanner.App", "/opt/mp/MissionPlanner.App.dll"));
        Assert.AreEqual("\"/home/me/Mission Planner/MissionPlanner.App\"",
            DesktopEntry.ExecCommand("/home/me/Mission Planner/MissionPlanner.App", "x.dll"), "paths with spaces are quoted");
        Assert.AreEqual("/usr/bin/dotnet /opt/mp/MissionPlanner.App.dll",
            DesktopEntry.ExecCommand("/usr/bin/dotnet", "/opt/mp/MissionPlanner.App.dll"), "started with dotnet: run the dll");
    }

    [TestMethod]
    public void EntryNamesTheIconAndTheWindowClass()
    {
        var text = DesktopEntry.Content("/opt/mp/MissionPlanner.App", "/icons/mp.png");
        StringAssert.StartsWith(text, "[Desktop Entry]");
        StringAssert.Contains(text, "\nExec=/opt/mp/MissionPlanner.App\n");
        StringAssert.Contains(text, "\nIcon=/icons/mp.png\n");
        StringAssert.Contains(text, "\nStartupWMClass=MissionPlanner.App\n");
        Assert.IsFalse(text.Contains("\n "), "no indentation leaks into the file");
    }
}
