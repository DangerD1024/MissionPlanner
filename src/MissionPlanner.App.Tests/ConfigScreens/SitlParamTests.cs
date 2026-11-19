using System.Diagnostics;
using System.Globalization;
using MissionPlanner.App.Core.Links;
using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.Session;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Tests.ConfigScreens;

/// <summary>
/// The parameter pages against ArduPilot SITL (ArduCopter): an enumerated value, a bitmask and a
/// number are edited with the page's editors, written, read back from the vehicle, kept across a
/// reboot, then restored. Opt in with MP_TEST_SITL=1 (or the path of an ArduCopter SITL binary);
/// MP_TEST_SITL_INSTANCE picks the instance (TCP 5760 + 10 * N, default 7).
/// <code>MP_TEST_SITL=1 dotnet test src/MissionPlanner.App.Tests --filter TestCategory=Sitl</code>
/// </summary>
[TestClass]
public class SitlParamTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Drops posted work (the metadata download on connect, page refreshes): the test drives the page.</summary>
    private sealed class QuietDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
        }

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    private static string? FindBinary()
    {
        var opt = Environment.GetEnvironmentVariable("MP_TEST_SITL");
        if (string.IsNullOrWhiteSpace(opt))
            return null;
        if (File.Exists(opt))
            return opt;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mission Planner", "sitl");
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ArduCopter.exe" : "ArduCopter");
        return File.Exists(exe) ? exe : "";
    }

    private sealed class Sitl : IDisposable
    {
        private readonly Process _process;
        private readonly string _dir;

        public Sitl(string exe, int instance)
        {
            Port = 5760 + 10 * instance;
            _dir = Path.Combine(TestEnvironment.DataRoot, "sitl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var defaults = Path.Combine(Path.GetDirectoryName(exe)!, "copter.parm");
            var args = $"-M+ -O-35.363261,149.165230,584,353 -s1 --serial0 tcp:0 -I{instance.ToString(CultureInfo.InvariantCulture)}" +
                       (File.Exists(defaults) ? $" --defaults \"{defaults}\"" : "");
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = _dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // as the Simulation page does: SITL keeps its files relative to HOME
            psi.Environment["HOME"] = _dir;
            _process = Process.Start(psi)!;
            _process.OutputDataReceived += (_, e) => Keep(e.Data);
            _process.ErrorDataReceived += (_, e) => Keep(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public int Port { get; }

        /// <summary>The simulator's last output lines, for a failure message.</summary>
        public string Output
        {
            get
            {
                lock (_output)
                    return string.Join(Environment.NewLine, _output);
            }
        }

        private readonly Queue<string> _output = new();

        private void Keep(string? line)
        {
            if (line == null)
                return;
            lock (_output)
            {
                _output.Enqueue(line);
                if (_output.Count > 40)
                    _output.Dequeue();
            }
        }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Connects and downloads the parameters; SITL may still be starting or rebooting, so it tries for a while.</summary>
    private async Task ConnectAsync(AppServices services, Sitl sitl)
    {
        var sw = Stopwatch.StartNew();
        // connect only once SITL listens: a refused or failed attempt is harmless on Linux, but the
        // Windows (cygwin) build exits when its first client disconnects
        if (OperatingSystem.IsWindows())
        {
            while (!MissionPlanner.App.Tests.Sitl.SitlProcess.IsListening(sitl.Port) && !sitl.HasExited && sw.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(250);
        }
        for (var attempt = 1; ; attempt++)
        {
            if (sitl.HasExited)
                Assert.Fail($"SITL exited with code {sitl.ExitCode}:{Environment.NewLine}{sitl.Output}");
            try
            {
                await services.Session.ConnectAsync(LinkSpec.Tcp("127.0.0.1", sitl.Port), getParams: false);
                break;
            }
            catch (ConnectException ex) when (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                TestContext.WriteLine($"connect attempt {attempt}: {ex.Message}{Environment.NewLine}{sitl.Output}");
                await Task.Delay(1000);
            }
        }

        TestContext.WriteLine($"connected after {sw.Elapsed.TotalSeconds:F1} s");
        await services.Params.RefreshAsync();
        Assert.IsGreaterThan(300, services.Params.Count, "parameter download incomplete");
    }

    /// <summary>Asks the vehicle for one parameter (PARAM_REQUEST_READ).</summary>
    private static Task<double> ReadBackAsync(AppServices services, string name)
    {
        var v = services.Session.Vehicle;
        var mav = services.Session.Mav;
        return Task.Run(() => (double)mav.GetParam(v.sysid, v.compid, name));
    }

    [TestMethod]
    [TestCategory("Sitl")]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task EditorsWriteToSitlAndReadBack()
    {
        var exe = FindBinary();
        if (exe == null)
            Assert.Inconclusive("set MP_TEST_SITL=1 (or the path of an ArduCopter SITL binary) to run against SITL");
        if (exe.Length == 0)
            Assert.Inconclusive("ArduCopter SITL binary not found (install it from the app's SIMULATION screen)");

        var instance = int.TryParse(Environment.GetEnvironmentVariable("MP_TEST_SITL_INSTANCE"), out var n) ? n : 7;
        using var sitl = new Sitl(exe, instance);

        var dialogs = new FakeDialogs();
        var windows = new FakeWindows();
        var services = new AppServices(new QuietDispatcher(), dialogs, windows);
        try
        {
            await ConnectAsync(services, sitl);
            TestContext.WriteLine($"connected: {services.Session.Cs.firmware} {services.Session.Cs.version}, {services.Params.Count} params");
            await ParamPagesTestSupport.LoadOfflineDocsAsync(services);

            var page = new FullParameterListViewModel(services);
            await page.RebuildAsync();
            page.Tick();
            Assert.IsTrue(page.IsConnected);
            Assert.IsFalse(services.Session.Cs.armed);

            ParamRow Row(string name) => page.AllRows.Single(r => r.Name == name);
            var enumRow = Row("FS_GCS_ENABLE");
            var maskRow = Row("LOG_BITMASK");
            // names that exist in every Copter version (4.7 renamed many to metres)
            var numberName = services.Params.FirstExisting("DISARM_DELAY", "RC_SPEED", "BATT_CAPACITY");
            Assert.IsNotNull(numberName, "no plain number parameter to edit");
            var numberRow = Row(numberName);
            Assert.IsTrue(enumRow.IsOptions, "FS_GCS_ENABLE should offer its documented values");
            Assert.IsTrue(maskRow.IsBitmask, "LOG_BITMASK should offer the bitmask editor");
            Assert.AreEqual(ParamEditorKind.Number, numberRow.Kind);

            var original = new Dictionary<string, double>
            {
                [enumRow.Name] = enumRow.Value,
                [maskRow.Name] = maskRow.Value,
                [numberRow.Name] = numberRow.Value,
            };
            TestContext.WriteLine("original: " + string.Join(", ", original.Select(kv => $"{kv.Key}={kv.Value}")));

            // enum: another documented value from the list
            var choice = enumRow.Choices.First(c => !ParamValues.Same(c.Value, enumRow.Value) && c.Value is >= 1 and <= 5);
            Assert.IsTrue(await enumRow.ChooseAsync(choice));

            // bitmask: flip bit 0 and bit 2 in the Set Bitmask dialog
            windows.OnDialog = p =>
            {
                var editor = (BitmaskEditorViewModel)p;
                foreach (var b in editor.Documented.Where(b => b.Bit is 0 or 2))
                    b.IsChecked = !b.IsChecked;
                return true;
            };
            await maskRow.EditBitmaskCommand.ExecuteAsync(null);
            var expectedMask = (double)((long)maskRow.Value ^ 0b101);

            // number: typed
            var expectedNumber = numberRow.Meta?.Max is { } max && numberRow.Value + 1 > max ? numberRow.Value - 1 : numberRow.Value + 1;
            Assert.IsTrue(await page.CommitEditAsync(numberRow, ParamValues.Format(expectedNumber)));

            var expected = new Dictionary<string, double>
            {
                [enumRow.Name] = choice.Value,
                [maskRow.Name] = expectedMask,
                [numberRow.Name] = expectedNumber,
            };
            CollectionAssert.AreEquivalent(expected.Keys.ToArray(), page.Edits.Pending.Keys.ToArray());
            foreach (var (name, value) in expected)
                Assert.AreEqual(value, page.Edits.Pending[name], 1e-3, name);

            dialogs.ConfirmAnswer = true;
            var toasts = new List<Notification>();
            services.Notified += (_, n) => toasts.Add(n);
            await page.WriteCommand.ExecuteAsync(null);
            Assert.AreEqual(0, page.PendingCount, string.Join(" | ", dialogs.Messages.Select(m => m.Title + ": " + m.Message)));
            Assert.IsTrue(toasts.Any(t => t.Text.Contains("saved")), string.Join(" | ", toasts));

            // read back from the vehicle, one by one
            foreach (var (name, value) in expected)
                Assert.AreEqual(value, await ReadBackAsync(services, name), 1e-3, "read back " + name);

            // reboot from the page; the values are kept. Either the link drops while SITL restarts, or
            // (the Windows cygwin build, which also exits when its client disconnects, so the link must
            // not be closed here) it goes quiet and the new boot carries on over the same connection.
            await page.RebootCommand.ExecuteAsync(null);
            Assert.AreEqual("Reboot", dialogs.Confirms.Last().Title);
            StringAssert.StartsWith(page.StatusText, "Reboot command sent");
            var sw = Stopwatch.StartNew();
            var quiet = false;
            var resumed = false;
            while (services.Session.State == LinkState.Connected && sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (services.Session.SinceLastPacket > TimeSpan.FromSeconds(1))
                    quiet = true;
                else if (quiet)
                {
                    resumed = true;
                    break;
                }

                await Task.Delay(100);
            }

            TestContext.WriteLine($"after reboot: link {services.Session.State}, resumed {resumed}, after {sw.Elapsed.TotalSeconds:F1} s");
            if (resumed && services.Session.State == LinkState.Connected)
            {
                // same connection: let the new boot settle (sensors, EKF), then read the list again
                await Task.Delay(5000);
                await services.Params.RefreshAsync();
            }
            else
            {
                if (services.Session.State != LinkState.Disconnected)
                    await services.Session.DisconnectAsync();
                await ConnectAsync(services, sitl);
            }

            await page.RebuildAsync();
            foreach (var (name, value) in expected)
                Assert.AreEqual(value, Row(name).Value, 1e-3, "after reboot " + name);

            // restore through the page
            foreach (var (name, value) in original)
                Assert.IsTrue(await page.CommitEditAsync(Row(name), ParamValues.Format(value)), "restore " + name);
            await page.WriteCommand.ExecuteAsync(null);
            Assert.AreEqual(0, page.PendingCount);
            foreach (var (name, value) in original)
                Assert.AreEqual(value, await ReadBackAsync(services, name), 1e-3, "restored " + name);
        }
        finally
        {
            await services.Session.DisconnectAsync();
        }
    }
}
