using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MissionPlanner.App.Core.Updates;
using MissionPlanner.App.Tests.FlightData;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Tests;

[TestClass]
public class UpdateTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v3.0.5b",
          "name": "Mission Planner 3.0.5b (beta)",
          "draft": false,
          "html_url": "https://github.com/DangerD1024/MissionPlanner/releases/tag/v3.0.5b",
          "body": "What's new",
          "assets": [
            { "name": "MissionPlanner-win-x64.msi", "size": 100, "browser_download_url": "https://example.invalid/latest.msi" },
            { "name": "MissionPlanner-3.0.5b-win-x64.msi", "size": 5, "digest": "sha256:DIGEST",
              "browser_download_url": "https://example.invalid/MissionPlanner-3.0.5b-win-x64.msi" },
            { "name": "MissionPlanner-3.0.5b-linux-x64.tar.gz", "size": 7,
              "browser_download_url": "https://example.invalid/MissionPlanner-3.0.5b-linux-x64.tar.gz" }
          ]
        }
        """;

    private string _folder = "";

    [TestInitialize]
    public void Init()
    {
        _folder = Path.Combine(TestEnvironment.DataRoot, "updates-" + Guid.NewGuid().ToString("N"));
        Settings.Instance.Remove(UpdateViewModel.SkipKey);
        Settings.Instance.Remove(UpdateViewModel.CheckKey);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Settings.Instance.Remove(UpdateViewModel.SkipKey);
        Settings.Instance.Remove(UpdateViewModel.CheckKey);
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    [TestMethod]
    public void VersionsCompareWithTheirBetaLetter()
    {
        Assert.IsTrue(UpdateCheck.IsNewer("3.0.5b", "3.0.4b"));
        Assert.IsTrue(UpdateCheck.IsNewer("v3.1", "3.0.9b"));
        Assert.IsTrue(UpdateCheck.IsNewer("3.0.4", "3.0.4b")); // the release after its beta
        Assert.IsTrue(UpdateCheck.IsNewer("3.0.4c", "3.0.4b"));
        Assert.IsTrue(UpdateCheck.IsNewer("3.0.10b", "3.0.9b"));
        Assert.IsFalse(UpdateCheck.IsNewer("3.0.4b", "3.0.4b"));
        Assert.IsFalse(UpdateCheck.IsNewer("3.0.4b", "3.0.4"));
        Assert.IsFalse(UpdateCheck.IsNewer("3.0.3b", "3.0.4b"));
        Assert.IsFalse(UpdateCheck.IsNewer("3.0", "3.0.0"));
        Assert.IsFalse(UpdateCheck.IsNewer("nonsense", "3.0.4b"));
    }

    [TestMethod]
    public void ReleaseJsonPicksTheVersionedInstallerForThisComputer()
    {
        Assert.AreEqual("win-x64.msi", UpdateCheck.AssetSuffix(OSPlatform.Windows, Architecture.X64));
        Assert.AreEqual("linux-arm64.tar.gz", UpdateCheck.AssetSuffix(OSPlatform.Linux, Architecture.Arm64));
        Assert.AreEqual("osx-arm64.dmg", UpdateCheck.AssetSuffix(OSPlatform.OSX, Architecture.Arm64));

        var win = UpdateCheck.Parse(ReleaseJson, "win-x64.msi")!;
        Assert.AreEqual("3.0.5b", win.Version);
        Assert.AreEqual("MissionPlanner-3.0.5b-win-x64.msi", win.AssetName);
        Assert.AreEqual(5, win.AssetSize);
        Assert.AreEqual("digest", win.Sha256);
        StringAssert.EndsWith(win.PageUrl, "/tag/v3.0.5b");

        var linux = UpdateCheck.Parse(ReleaseJson, "linux-x64.tar.gz")!;
        Assert.AreEqual("MissionPlanner-3.0.5b-linux-x64.tar.gz", linux.AssetName);
        Assert.IsNull(linux.Sha256);

        var mac = UpdateCheck.Parse(ReleaseJson, null)!;
        Assert.IsNull(mac.AssetUrl);
        Assert.IsNull(UpdateCheck.Parse("""{"tag_name":"v9.0","draft":true}""", null));
        Assert.IsNull(UpdateCheck.Parse("""{"message":"Not Found"}""", null));
    }

    private UpdateViewModel Create(out FakeUi ui, byte[] file, string json = ReleaseJson, List<string>? asked = null)
    {
        var services = FakeUi.Services(out ui);
        var sha = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        var answer = json.Replace("\"size\": 5, \"digest\": \"sha256:DIGEST\"", $"\"size\": {file.Length}, \"digest\": \"sha256:{sha}\"")
            .Replace("\"size\": 7,", $"\"size\": {file.Length},");
        return new UpdateViewModel(services,
            (url, _) =>
            {
                asked?.Add(url);
                return Task.FromResult(answer);
            },
            async (_, target, progress, _) =>
            {
                await target.WriteAsync(file);
                progress.Report(file.Length);
            })
        {
            CurrentVersion = "3.0.4b",
            DownloadFolder = _folder,
        };
    }

    [TestMethod]
    public async Task NewerReleaseShowsTheBannerAndDownloadsTheInstaller()
    {
        var asked = new List<string>();
        var bytes = "fake installer"u8.ToArray();
        var vm = Create(out _, bytes, asked: asked);
        await vm.CheckAtStartupAsync();

        Assert.AreEqual(UpdateCheck.LatestUrl, asked.Single());
        StringAssert.Contains(UpdateCheck.LatestUrl, UpdateCheck.Repository);
        Assert.IsTrue(vm.IsBannerVisible);
        Assert.AreEqual("Mission Planner 3.0.5b is available (you have 3.0.4b).", vm.BannerText);
        if (UpdateCheck.AssetSuffix() == null)
            return; // no installer for this platform
        Assert.IsTrue(vm.CanDownload);

        await vm.DownloadAsync();
        Assert.IsNotNull(vm.DownloadedFile, vm.StatusText);
        Assert.IsTrue(vm.IsDownloaded);
        Assert.IsFalse(vm.CanDownload);
        Assert.AreEqual(100, vm.DownloadPercent, 1e-9);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(vm.DownloadedFile!));
        StringAssert.Contains(Path.GetFileName(vm.DownloadedFile!), "3.0.5b");
        Assert.IsFalse(Directory.EnumerateFiles(_folder, "*.part").Any());
    }

    [TestMethod]
    public async Task DamagedDownloadIsThrownAway()
    {
        if (UpdateCheck.AssetSuffix() == null)
            Assert.Inconclusive("no installer for this platform");
        var services = FakeUi.Services(out _);
        // the release says 5 bytes; 4 arrive (a dropped connection)
        var vm = new UpdateViewModel(services, (_, _) => Task.FromResult(ReleaseJson.Replace("\"size\": 7,", "\"size\": 5,")),
            async (_, target, _, _) => await target.WriteAsync("1234"u8.ToArray()))
        {
            CurrentVersion = "3.0.4b",
            DownloadFolder = _folder,
        };
        await vm.CheckAsync(manual: true);
        await vm.DownloadAsync();
        Assert.IsNull(vm.DownloadedFile);
        StringAssert.StartsWith(vm.StatusText, "Download failed: ");
        Assert.AreEqual(0, Directory.EnumerateFiles(_folder).Count());
        Assert.IsTrue(vm.CanDownload); // can try again
    }

    [TestMethod]
    public async Task LatestVersionSkippedVersionAndSwitchedOff()
    {
        var vm = Create(out _, [1]);
        vm.CurrentVersion = "3.0.5b";
        await vm.CheckAsync(manual: true);
        Assert.IsFalse(vm.IsBannerVisible);
        Assert.IsNull(vm.Available);
        Assert.AreEqual("You have the latest version (3.0.5b).", vm.StatusText);

        // Skip: not offered at startup again, still shown by a manual check
        vm.CurrentVersion = "3.0.4b";
        await vm.CheckAsync(manual: false);
        Assert.IsTrue(vm.IsBannerVisible);
        vm.SkipCommand.Execute(null);
        Assert.IsFalse(vm.IsBannerVisible);
        Assert.AreEqual("3.0.5b", Settings.Instance[UpdateViewModel.SkipKey]);
        await vm.CheckAsync(manual: false);
        Assert.IsFalse(vm.IsBannerVisible);
        await vm.CheckAsync(manual: true);
        Assert.IsTrue(vm.IsBannerVisible);
        vm.LaterCommand.Execute(null);
        Assert.IsFalse(vm.IsBannerVisible);

        // switched off: the startup check asks nothing
        var asked = new List<string>();
        var off = Create(out _, [1], asked: asked);
        off.CheckAtStartup = false;
        Assert.AreEqual("False", Settings.Instance[UpdateViewModel.CheckKey]);
        await off.CheckAtStartupAsync();
        Assert.AreEqual(0, asked.Count);
        Assert.IsFalse(Create(out _, [1]).CheckAtStartup);
    }

    [TestMethod]
    public async Task ChecksumMismatchIsThrownAway()
    {
        var services = FakeUi.Services(out _);
        var vm = new UpdateViewModel(services, (_, _) => Task.FromResult(ReleaseJson), async (_, target, _, _) => await target.WriteAsync("12345"u8.ToArray()))
        {
            CurrentVersion = "3.0.4b",
            DownloadFolder = _folder,
        };
        await vm.CheckAsync(manual: true);
        if (vm.Available?.Sha256 == null)
            Assert.Inconclusive("this platform's test installer has no checksum");
        await vm.DownloadAsync();
        Assert.IsNull(vm.DownloadedFile);
        Assert.AreEqual("Download failed: the file is damaged (checksum mismatch)", vm.StatusText);
    }

    [TestMethod]
    public void HelpScreenIsBackWithUpdatesAndLinks()
    {
        var services = FakeUi.Services(out _);
        var shell = new MissionPlanner.App.Core.ViewModels.ShellViewModel(services);
        shell.NavigateCommand.Execute(MissionPlanner.App.Core.ViewModels.Screen.Help);
        Assert.IsTrue(shell.IsHelp);
        var help = (MissionPlanner.App.Core.ViewModels.HelpViewModel)shell.CurrentPage;
        Assert.AreSame(services.Updates, help.Updates);
        Assert.AreSame(services.Updates, shell.Updates);
        StringAssert.StartsWith(MissionPlanner.App.Core.ViewModels.HelpViewModel.DiscussUrl,
            "https://discuss.ardupilot.org/t/mission-planner-3-0-rewritten-cross-platform-ui");
        StringAssert.Contains(MissionPlanner.App.Core.ViewModels.HelpViewModel.IssuesUrl, UpdateCheck.Repository);

        // HELP is the last tab of the top bar, right after SETTINGS
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MissionPlanner.App", "Views", "MainWindow.axaml")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "MainWindow.axaml not found");
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "MissionPlanner.App", "Views", "MainWindow.axaml"));
        var tabs = System.Text.RegularExpressions.Regex.Matches(xaml, @"RadioButton Classes=""nav""[^>]*?Screen\.(\w+)\}",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();
        CollectionAssert.AreEqual(new[] { "Data", "Plan", "Setup", "Config", "Simulation", "Settings", "Help" }, tabs);
    }

    [TestMethod]
    public async Task OfflineCheckSaysSoQuietly()
    {
        var services = FakeUi.Services(out _);
        var vm = new UpdateViewModel(services, (_, _) => throw new HttpRequestException("No such host"), (_, _, _, _) => Task.CompletedTask)
        {
            CurrentVersion = "3.0.4b",
        };
        await vm.CheckAtStartupAsync();
        Assert.IsFalse(vm.IsBannerVisible);
        Assert.IsFalse(vm.IsBusy);
        Assert.AreEqual("Could not check for updates: No such host", vm.StatusText);
    }
}
