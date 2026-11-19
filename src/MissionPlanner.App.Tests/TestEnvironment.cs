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
        // the shared libraries block pool threads (sync-over-async); with many fake-vehicle sessions
        // in one run the pool would otherwise starve and background work misses test timeouts
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), io);

        // runs that were killed never reach AssemblyCleanup: clear their folders (/tmp is often RAM)
        foreach (var stale in Directory.GetDirectories(Path.GetTempPath(), "mp-app-tests-*"))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-2))
                    Directory.Delete(stale, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

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
