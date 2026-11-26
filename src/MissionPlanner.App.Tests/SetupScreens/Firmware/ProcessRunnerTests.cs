using System.Diagnostics;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

/// <summary>The real process runner, with /bin/sh standing in for dfu-util (no USB involved).</summary>
[TestClass]
public class ProcessRunnerTests
{
    [TestMethod]
    public void LinesSplitAtCarriageReturns()
    {
        var lines = new List<string>();
        var s = new LineSplitter(lines.Add);
        s.Add("a\rb");
        s.Add("c\r\n\nd");
        s.Flush();
        CollectionAssert.AreEqual(new[] { "a", "bc", "d" }, lines);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task OutputErrorsExitCodeAndEnvironment()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            Assert.Inconclusive("needs /bin/sh");
        var lines = new List<(string, bool)>();
        var code = await SystemProcessRunner.Instance.RunAsync(
            new ProcessRequest("/bin/sh", ["-c", "printf 'Erase 10%%\\rErase 20%%\\n'; echo \"lib=$MP_TEST_VAR\"; echo oops >&2; exit 3"],
                new Dictionary<string, string> { ["MP_TEST_VAR"] = "here" }),
            (l, e) =>
            {
                lock (lines)
                    lines.Add((l, e));
            }, CancellationToken.None);

        Assert.AreEqual(3, code);
        CollectionAssert.AreEqual(new[] { "Erase 10%", "Erase 20%", "lib=here" }, lines.Where(l => !l.Item2).Select(l => l.Item1).ToArray());
        CollectionAssert.AreEqual(new[] { "oops" }, lines.Where(l => l.Item2).Select(l => l.Item1).ToArray());
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task CancellingKillsTheProgram()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            Assert.Inconclusive("needs /bin/sh");
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var watch = Stopwatch.StartNew();
        var run = SystemProcessRunner.Instance.RunAsync(new ProcessRequest("/bin/sh", ["-c", "echo started; exec sleep 30"]),
            (l, _) =>
            {
                if (l == "started")
                    started.TrySetResult();
            }, cts.Token);

        await started.Task;
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), "killed, not waited for");
    }

    [TestMethod]
    public async Task MissingProgramFailsToStart()
    {
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => SystemProcessRunner.Instance.RunAsync(
            new ProcessRequest(Path.Combine(TestEnvironment.DataRoot, "no-such-dfu-util"), ["--version"]), (_, _) => { }, CancellationToken.None));
    }
}
