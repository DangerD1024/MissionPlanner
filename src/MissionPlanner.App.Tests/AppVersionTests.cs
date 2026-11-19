using System.Text.RegularExpressions;
using MissionPlanner.App.Core;

namespace MissionPlanner.App.Tests;

[TestClass]
public class AppVersionTests
{
    [TestMethod]
    public void DisplayedVersionIsTheOneInDirectoryBuildProps()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "src/Directory.Build.props not found above the test output");

        var props = File.ReadAllText(Path.Combine(dir.FullName, "Directory.Build.props"));
        var expected = Regex.Match(props, "<MissionPlannerVersion>([^<]+)</MissionPlannerVersion>").Groups[1].Value;

        Assert.AreEqual(expected, AppVersion.Display, "no build metadata (+commit) or other suffix");
        Assert.AreEqual("Mission Planner " + expected, AppVersion.ProductName);
    }
}
