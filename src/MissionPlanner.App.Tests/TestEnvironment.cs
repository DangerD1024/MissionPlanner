using MissionPlanner.Utilities;

[assembly: Parallelize(Workers = 1, Scope = ExecutionScope.ClassLevel)]

namespace MissionPlanner.App.Tests;

[TestClass]
public static class TestEnvironment
{
    /// <summary>Per-run data directory, so tests never touch the user's real Mission Planner data or config.</summary>
    public static string DataRoot { get; private set; } = "";

    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "mp-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataRoot);
        Settings.CustomUserDataDirectory = DataRoot;
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        try
        {
            Directory.Delete(DataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
