using MissionPlanner.App.Core.Services;
using MissionPlanner.App.Core.ViewModels.ConfigScreens;

namespace MissionPlanner.App.Tests.ConfigScreens;

[TestClass]
public class ToolsFtpPathTests
{
    [TestMethod]
    [DataRow("/", "APM", "/APM")]
    [DataRow("/APM", "LOGS", "/APM/LOGS")]
    [DataRow("/APM/", "LOGS", "/APM/LOGS")]
    [DataRow("@SYS/", "uarts.txt", "@SYS/uarts.txt")]
    [DataRow("@ROMFS/", "scripts", "@ROMFS/scripts")]
    [DataRow("", "x", "x")]
    public void JoinAddsOneSeparator(string dir, string name, string expected) =>
        Assert.AreEqual(expected, FtpPath.Join(dir, name));

    [TestMethod]
    [DataRow("/", "/")]
    [DataRow("@SYS/", "@SYS")]
    [DataRow("@ROMFS/", "@ROMFS")]
    [DataRow("/APM/LOGS", "LOGS")]
    [DataRow("/APM/LOGS/", "LOGS")]
    public void DisplayNameIsTheLastSegment(string path, string expected) =>
        Assert.AreEqual(expected, FtpPath.DisplayName(path));

    [TestMethod]
    public void ValidateNewNameRejectsWhatTheVehicleCannotTake()
    {
        Assert.IsNull(FtpPath.ValidateNewName("/APM", "scripts"));
        Assert.IsNull(FtpPath.ValidateNewName("/APM", "my file.lua"));
        Assert.IsNotNull(FtpPath.ValidateNewName("/APM", ""));
        Assert.IsNotNull(FtpPath.ValidateNewName("/APM", "   "));
        Assert.IsNotNull(FtpPath.ValidateNewName("/APM", " lead"));
        Assert.IsNotNull(FtpPath.ValidateNewName("/APM", "."));
        Assert.IsNotNull(FtpPath.ValidateNewName("/APM", ".."));
        Assert.IsNotNull(FtpPath.ValidateNewName("/APM", "a/b"));
        Assert.IsNotNull(FtpPath.ValidateNewName("/APM", "a\\b"));
        Assert.IsNotNull(FtpPath.ValidateNewName("/APM", new string('x', 240)));
    }

    [TestMethod]
    public void SafeLocalNameStripsDirectories()
    {
        Assert.AreEqual("passwd", FtpPath.SafeLocalName("../../etc/passwd"));
        Assert.AreEqual("b.txt", FtpPath.SafeLocalName("a\\b.txt"));
        Assert.AreEqual("00000001.BIN", FtpPath.SafeLocalName("00000001.BIN"));
        Assert.IsNull(FtpPath.SafeLocalName(".."));
        Assert.IsNull(FtpPath.SafeLocalName("/"));
        Assert.IsNull(FtpPath.SafeLocalName(""));
    }

    [TestMethod]
    public void UniqueLocalPathNumbersBeforeTheExtension()
    {
        var taken = new HashSet<string> { Path.Combine("d", "log.bin"), Path.Combine("d", "log (1).bin") };
        Assert.AreEqual(Path.Combine("d", "new.bin"), FtpPath.UniqueLocalPath("d", "new.bin", taken.Contains));
        Assert.AreEqual(Path.Combine("d", "log (2).bin"), FtpPath.UniqueLocalPath("d", "log.bin", taken.Contains));
    }

    [TestMethod]
    [DataRow(0UL, "0 B")]
    [DataRow(1023UL, "1023 B")]
    [DataRow(1536UL, "1.5 KB")]
    [DataRow(20480UL, "20 KB")]
    [DataRow(5UL * 1024 * 1024, "5.0 MB")]
    [DataRow(3UL * 1024 * 1024 * 1024, "3.00 GB")]
    public void SizeIsHumanReadable(ulong bytes, string expected) => Assert.AreEqual(expected, FtpFormat.Size(bytes));

    [TestMethod]
    public void EntryFormatsItsColumns()
    {
        var file = new FtpEntry("/APM/LOGS", "00000012.BIN", false, 2048, new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Assert.AreEqual("/APM/LOGS/00000012.BIN", file.Path);
        Assert.AreEqual("2.0 KB", file.SizeText);
        Assert.AreEqual(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), file.ModifiedText);
        Assert.AreEqual("File", file.Kind);

        var dir = new FtpEntry("/", "APM", true);
        Assert.AreEqual("", dir.SizeText);
        Assert.AreEqual("", dir.ModifiedText);
        Assert.AreEqual("Folder", dir.Kind);
    }
}

[TestClass]
public class ToolsFtpListingTests
{
    private static FtpListing Sample()
    {
        var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var listing = new FtpListing();
        listing.Load("/APM", [
            new("/APM", ".", true), new("/APM", "..", true), new("/APM", "", true),
            new("/APM", "scripts", true), new("/APM", "LOGS", true),
            new("/APM", "b.param", false, 300, t0.AddDays(2)),
            new("/APM", "A.bin", false, 5000, t0.AddDays(1)),
            new("/APM", "c.lua", false, 10, null),
            new("/APM", "a.bin", false, 5000, t0.AddDays(3)),
            new("/APM", "c.lua", false, 10, null), // duplicate from a retried listing page
        ]);
        return listing;
    }

    private static string Names(IEnumerable<FtpEntry> entries) => string.Join(",", entries.Select(e => e.Name));

    [TestMethod]
    public void LoadDropsDotsBlanksAndDuplicates()
    {
        var listing = Sample();
        Assert.AreEqual(6, listing.All.Count);
        Assert.IsFalse(listing.All.Any(e => e.Name is "" or "." or ".."));
        Assert.AreEqual("LOGS,scripts", Names(listing.Folders));
    }

    [TestMethod]
    public void NameSortKeepsFoldersFirstAndIgnoresCase()
    {
        var listing = Sample();
        Assert.AreEqual("LOGS,scripts,A.bin,a.bin,b.param,c.lua", Names(listing.View()));

        listing.ToggleSort(FtpSortColumn.Name);
        Assert.IsTrue(listing.Descending);
        Assert.AreEqual("scripts,LOGS,c.lua,b.param,a.bin,A.bin", Names(listing.View()));
    }

    [TestMethod]
    public void SizeSortThenNameAndFoldersStayOnTop()
    {
        var listing = Sample();
        listing.ToggleSort(FtpSortColumn.Size);
        Assert.IsFalse(listing.Descending);
        Assert.AreEqual("LOGS,scripts,c.lua,b.param,A.bin,a.bin", Names(listing.View()));

        listing.ToggleSort(FtpSortColumn.Size);
        Assert.AreEqual("scripts,LOGS,a.bin,A.bin,b.param,c.lua", Names(listing.View()));
    }

    [TestMethod]
    public void ModifiedSortPutsUnknownTimesFirst()
    {
        var listing = Sample();
        listing.SetSort(FtpSortColumn.Modified, descending: false);
        Assert.AreEqual("LOGS,scripts,c.lua,A.bin,b.param,a.bin", Names(listing.View()));
    }

    [TestMethod]
    public void FilterMatchesSubstringsAndWildcards()
    {
        var listing = Sample();
        listing.Filter = "BIN";
        Assert.AreEqual("A.bin,a.bin", Names(listing.View()));

        listing.Filter = "*.lua";
        Assert.AreEqual("c.lua", Names(listing.View()));

        listing.Filter = "?.bin";
        Assert.AreEqual("A.bin,a.bin", Names(listing.View()));

        listing.Filter = "s*";
        Assert.AreEqual("scripts", Names(listing.View()));

        listing.Filter = "  ";
        Assert.AreEqual(6, listing.View().Count);
    }

    [TestMethod]
    public void LoadRebasesEntriesOnTheListedFolder()
    {
        var listing = new FtpListing();
        listing.Load("@SYS/", [new FtpEntry("@SYS", "uarts.txt", false, 12)]);
        Assert.AreEqual("@SYS/uarts.txt", listing.All[0].Path);
    }
}

[TestClass]
public class ToolsFtpFolderNodeTests
{
    [TestMethod]
    public void NewFolderShowsAPlaceholderAndListsOnFirstExpand()
    {
        var requests = new List<string>();
        var node = new FtpFolderNode("/", null, n => requests.Add(n.Path));
        Assert.AreEqual(1, node.Children.Count);
        Assert.IsTrue(node.Children[0].IsPlaceholder);

        node.IsExpanded = true;
        node.IsExpanded = false;
        Assert.AreEqual(1, requests.Count);

        node.SetChildren([new FtpEntry("/", "APM", true), new FtpEntry("/", "x.txt", false), new FtpEntry("/", "..", true)]);
        Assert.IsTrue(node.IsLoaded);
        Assert.AreEqual("APM", string.Join(",", node.Children.Select(c => c.Name)));
        Assert.AreEqual("/APM", node.Children[0].Path);

        node.IsExpanded = true;
        Assert.AreEqual(1, requests.Count, "a listed folder is not listed again on expand");
    }

    [TestMethod]
    public void SetChildrenKeepsSurvivingNodesAndTheirState()
    {
        var node = new FtpFolderNode("/APM", null, null);
        node.SetChildren([new FtpEntry("/APM", "LOGS", true), new FtpEntry("/APM", "scripts", true)]);
        var logs = node.FindChild("LOGS")!;
        logs.IsExpanded = true;

        node.SetChildren([new FtpEntry("/APM", "scripts", true), new FtpEntry("/APM", "LOGS", true), new FtpEntry("/APM", "fences", true)]);

        Assert.AreEqual("fences,LOGS,scripts", string.Join(",", node.Children.Select(c => c.Name)));
        Assert.AreSame(logs, node.FindChild("LOGS"));
        Assert.IsTrue(logs.IsExpanded);

        node.SetChildren([new FtpEntry("/APM", "fences", true)]);
        Assert.AreEqual("fences", string.Join(",", node.Children.Select(c => c.Name)));
    }
}

[TestClass]
public class ToolsMavFtpViewModelTests
{
    private ToolsUiThread _ui = null!;
    private ToolsDialogs _dialogs = null!;
    private ToolsFakeFtpClient _ftp = null!;
    private MavFtpViewModel _vm = null!;
    private string _temp = null!;

    [TestInitialize]
    public async Task Init()
    {
        _ui = new ToolsUiThread();
        _dialogs = new ToolsDialogs();
        _ftp = new ToolsFakeFtpClient();
        _ftp.Dirs.UnionWith(["/APM", "/APM/LOGS", "/APM/scripts", "@SYS", "@ROMFS"]);
        _ftp.Files["/APM/LOGS/00000001.BIN"] = [1, 2, 3];
        _ftp.Files["/APM/LOGS/00000002.BIN"] = [4, 5, 6, 7];
        _ftp.Files["/boot.txt"] = "hello"u8.ToArray();
        _temp = Path.Combine(TestEnvironment.DataRoot, "ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);

        _vm = await _ui.Run(() =>
        {
            var services = new AppServices(_ui, _dialogs, new ToolsWindows());
            var vm = new MavFtpViewModel(services, () => _ftp);
            vm.OnShown();
            return vm;
        });
        await Idle();
    }

    [TestCleanup]
    public void Cleanup() => _ui.Dispose();

    private Task Idle() => _ui.WaitFor(() => !_vm.IsBusy, "the browser to go idle");

    private Task<string> EntryNames() => _ui.Run(() => string.Join(",", _vm.Entries.Select(e => e.Name)));

    private async Task Open(string path)
    {
        // open the parents first so the tree has listed the folder
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = "";
        foreach (var part in parts)
        {
            prefix += "/" + part;
            var p = prefix;
            await _ui.Run(() =>
            {
                var node = FindNode(_vm.Roots, p) ?? throw new AssertFailedException("no tree node " + p);
                _vm.SelectedFolder = node;
                return true;
            });
            await Idle();
        }
        await Idle();
    }

    private static FtpFolderNode? FindNode(IEnumerable<FtpFolderNode> nodes, string path)
    {
        foreach (var n in nodes)
        {
            if (n.Path == path)
                return n;
            if (FindNode(n.Children, path) is { } found)
                return found;
        }

        return null;
    }

    private Task Select(params string[] names) => _ui.Run(() =>
    {
        _vm.SetSelection(_vm.Entries.Where(e => names.Contains(e.Name)));
        return true;
    });

    [TestMethod]
    public async Task ShowingThePageListsTheRootsAndTheRootFolder()
    {
        Assert.AreEqual("/,@ROMFS,@SYS", await _ui.Run(() => string.Join(",", _vm.Roots.Select(r => r.Name))));
        Assert.AreEqual("/", await _ui.Run(() => _vm.CurrentPath));
        Assert.AreEqual("APM,boot.txt", await EntryNames());
        Assert.AreEqual("APM", await _ui.Run(() => string.Join(",", _vm.Roots[0].Children.Select(c => c.Name))));
        Assert.IsTrue(await _ui.Run(() => _vm.Summary.StartsWith("1 folder, 1 file")));
    }

    [TestMethod]
    public async Task ExpandingAFolderListsItLazily()
    {
        await _ui.Run(() => _vm.Roots[1].IsExpanded = true);
        await Idle();
        Assert.IsTrue(_ftp.Calls.Contains("list @ROMFS/"));
        Assert.AreEqual(0, await _ui.Run(() => _vm.Roots[1].Children.Count), "an empty folder loses its placeholder");
        Assert.AreEqual("/", await _ui.Run(() => _vm.CurrentPath), "expanding does not open the folder");
    }

    [TestMethod]
    public async Task RootFoldersAlreadyListedInSlashAreNotRepeated()
    {
        // ArduPilot 4.6 lists @SYS inside "/"
        _ftp.Dirs.Add("/@SYS");
        await _ui.Run(() => _vm.RefreshCommand.ExecuteAsync(null));
        await Idle();
        Assert.AreEqual("/,@ROMFS", await _ui.Run(() => string.Join(",", _vm.Roots.Select(r => r.Name))));
    }

    [TestMethod]
    public async Task OpeningAFolderEntryNavigatesAndUpReturns()
    {
        var apm = await _ui.Run(() => _vm.Entries.First(e => e.Name == "APM"));
        await _ui.Run(() => _vm.OpenEntryAsync(apm));
        await Idle();
        Assert.AreEqual("/APM", await _ui.Run(() => _vm.CurrentPath));
        Assert.AreEqual("LOGS,scripts", await EntryNames());

        await _ui.Run(() => _vm.UpCommand.Execute(null));
        await Idle();
        Assert.AreEqual("/", await _ui.Run(() => _vm.CurrentPath));
    }

    [TestMethod]
    public async Task UploadVerifiesTheVehicleCrc()
    {
        await Open("/APM/scripts");
        var local = Path.Combine(_temp, "hello.lua");
        File.WriteAllText(local, "print('hi')");
        _dialogs.Files.Enqueue(local);

        await _ui.Run(() => _vm.UploadCommand.ExecuteAsync(null));
        await Idle();

        CollectionAssert.AreEqual(File.ReadAllBytes(local), _ftp.Files["/APM/scripts/hello.lua"]);
        Assert.IsTrue(_ftp.Calls.Contains("crc /APM/scripts/hello.lua"));
        Assert.IsNull(await _ui.Run(() => _vm.Error));
        Assert.Contains("CRC verified", await _ui.Run(() => _vm.ResultText));
        Assert.AreEqual("hello.lua", await EntryNames(), "the folder is listed again after the upload");
    }

    [TestMethod]
    public async Task UploadWithAWrongCrcIsReported()
    {
        await Open("/APM/scripts");
        var local = Path.Combine(_temp, "bad.lua");
        File.WriteAllText(local, "x = 1");
        _ftp.CorruptCrc = true;
        _dialogs.Files.Enqueue(local);

        await _ui.Run(() => _vm.UploadCommand.ExecuteAsync(null));
        await Idle();

        Assert.Contains("CRC mismatch", await _ui.Run(() => _vm.Error) ?? "");
    }

    [TestMethod]
    public async Task UploadOverAnExistingFileAsksFirst()
    {
        await Open("/APM/LOGS");
        var local = Path.Combine(_temp, "00000001.BIN");
        File.WriteAllBytes(local, [9, 9]);
        _dialogs.Files.Enqueue(local);
        _dialogs.Confirms.Enqueue(false);

        await _ui.Run(() => _vm.UploadCommand.ExecuteAsync(null));
        await Idle();

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, _ftp.Files["/APM/LOGS/00000001.BIN"]);
        Assert.IsTrue(_dialogs.Asked.Any(a => a.StartsWith("confirm: 00000001.BIN already exists")));
    }

    [TestMethod]
    public async Task DownloadOneFileToTheChosenName()
    {
        await Open("/APM/LOGS");
        await Select("00000002.BIN");
        var target = Path.Combine(_temp, "two.bin");
        _dialogs.Files.Enqueue(target);

        await _ui.Run(() => _vm.DownloadCommand.ExecuteAsync(null));
        await Idle();

        CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 7 }, File.ReadAllBytes(target));
        Assert.IsTrue(_dialogs.Asked.Contains("save: 00000002.BIN"));
    }

    [TestMethod]
    public async Task DownloadSeveralFilesToAFolderNeverOverwrites()
    {
        await Open("/APM/LOGS");
        await Select("00000001.BIN", "00000002.BIN");
        File.WriteAllBytes(Path.Combine(_temp, "00000001.BIN"), [0]);
        _dialogs.Files.Enqueue(_temp);

        await _ui.Run(() => _vm.DownloadCommand.ExecuteAsync(null));
        await Idle();

        CollectionAssert.AreEqual(new byte[] { 0 }, File.ReadAllBytes(Path.Combine(_temp, "00000001.BIN")));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_temp, "00000001 (1).BIN")));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 7 }, File.ReadAllBytes(Path.Combine(_temp, "00000002.BIN")));
    }

    [TestMethod]
    public async Task DeleteAFolderAsksThenRemovesItRecursively()
    {
        await Open("/APM");
        await Select("LOGS");

        _dialogs.Confirms.Enqueue(false);
        await _ui.Run(() => _vm.DeleteCommand.ExecuteAsync(null));
        await Idle();
        Assert.IsTrue(_ftp.Dirs.Contains("/APM/LOGS"), "declining keeps the folder");

        await Select("LOGS");
        _dialogs.Confirms.Enqueue(true);
        await _ui.Run(() => _vm.DeleteCommand.ExecuteAsync(null));
        await Idle();

        Assert.IsTrue(_ftp.Calls.Contains("rmdir -r /APM/LOGS"));
        Assert.IsFalse(_ftp.Files.Keys.Any(k => k.StartsWith("/APM/LOGS/")));
        Assert.IsTrue(_dialogs.Asked.Any(a => a.Contains("Delete the folder LOGS and everything in it?")));
        Assert.AreEqual("scripts", await EntryNames());
        Assert.AreEqual("scripts", await _ui.Run(() => string.Join(",", FindNode(_vm.Roots, "/APM")!.Children.Select(c => c.Name))));
    }

    [TestMethod]
    public async Task DeleteFilesUsesRemoveFile()
    {
        await Open("/APM/LOGS");
        await Select("00000001.BIN", "00000002.BIN");
        _dialogs.Confirms.Enqueue(true);

        await _ui.Run(() => _vm.DeleteCommand.ExecuteAsync(null));
        await Idle();

        Assert.IsTrue(_ftp.Calls.Contains("rm /APM/LOGS/00000001.BIN"));
        Assert.IsTrue(_ftp.Calls.Contains("rm /APM/LOGS/00000002.BIN"));
        Assert.AreEqual("", await EntryNames());
    }

    [TestMethod]
    public async Task NewFolderValidatesThenCreates()
    {
        await Open("/APM");
        _dialogs.Prompts.Enqueue("bad/name");
        await _ui.Run(() => _vm.NewFolderCommand.ExecuteAsync(null));
        Assert.IsNotNull(await _ui.Run(() => _vm.Error));
        Assert.IsFalse(_ftp.Calls.Any(c => c.StartsWith("mkdir")));

        _dialogs.Prompts.Enqueue("fences");
        await _ui.Run(() => _vm.NewFolderCommand.ExecuteAsync(null));
        await Idle();
        Assert.IsTrue(_ftp.Calls.Contains("mkdir /APM/fences"));
        Assert.AreEqual("fences,LOGS,scripts", await EntryNames());
        Assert.IsNull(await _ui.Run(() => _vm.Error));
    }

    [TestMethod]
    public async Task AFalseReturnIsShownInline()
    {
        await Open("/APM");
        _ftp.FailCreate = true;
        _dialogs.Prompts.Enqueue("terrain");
        await _ui.Run(() => _vm.NewFolderCommand.ExecuteAsync(null));
        await Idle();
        Assert.Contains("did not create", await _ui.Run(() => _vm.Error) ?? "");
    }

    [TestMethod]
    public async Task RenameUsesFullPaths()
    {
        await Open("/APM/LOGS");
        await Select("00000001.BIN");
        _dialogs.Prompts.Enqueue("first.BIN");

        await _ui.Run(() => _vm.RenameCommand.ExecuteAsync(null));
        await Idle();

        Assert.IsTrue(_ftp.Calls.Contains("mv /APM/LOGS/00000001.BIN /APM/LOGS/first.BIN"));
        Assert.AreEqual("00000002.BIN,first.BIN", await EntryNames());
    }

    [TestMethod]
    public async Task ListingErrorsAreShownInline()
    {
        _ftp.ThrowOnList = "/APM";
        await Open("/APM");
        Assert.Contains("kErrFailErrno", await _ui.Run(() => _vm.Error) ?? "");
        Assert.AreEqual("", await EntryNames());

        await _ui.Run(() => _vm.DismissErrorCommand.Execute(null));
        Assert.IsFalse(await _ui.Run(() => _vm.HasError));
    }

    [TestMethod]
    public async Task OperationsRunOneAtATimeOffTheUiThread()
    {
        await _ui.Run(() =>
        {
            // a burst of requests, as fast clicking would make
            foreach (var r in _vm.Roots)
                r.IsExpanded = true;
            _ = _vm.RefreshCommand.ExecuteAsync(null);
            return true;
        });
        await Idle();
        Assert.AreEqual(1, _ftp.MaxConcurrent);
    }

    [TestMethod]
    public async Task CancelStopsATransferAndResetsSessions()
    {
        await Open("/APM/LOGS");
        await Select("00000001.BIN");
        _ftp.BlockDownloads = true;
        _dialogs.Files.Enqueue(Path.Combine(_temp, "never.bin"));

        var download = await _ui.Run<Task>(() => _vm.DownloadCommand.ExecuteAsync(null));
        await _ui.WaitFor(() => _ftp.Calls.Contains("get /APM/LOGS/00000001.BIN"), "the download to start");
        Assert.IsTrue(await _ui.Run(() => _vm.CancelCommand.CanExecute(null)));
        await _ui.Run(() => _vm.CancelCommand.Execute(null));
        await Idle();
        await download;

        Assert.IsTrue(_ftp.ResetCount >= 1, "sessions reset on cancel");
        Assert.IsFalse(File.Exists(Path.Combine(_temp, "never.bin")));
        Assert.Contains("cancelled", await _ui.Run(() => _vm.ResultText));
    }

    [TestMethod]
    public async Task SortingAndFilteringReorderTheList()
    {
        await Open("/APM/LOGS");
        await _ui.Run(() => _vm.SortBy(FtpSortColumn.Size));
        Assert.AreEqual("00000001.BIN,00000002.BIN", await EntryNames());
        await _ui.Run(() => _vm.SortBy(FtpSortColumn.Size));
        Assert.AreEqual("00000002.BIN,00000001.BIN", await EntryNames());

        await _ui.Run(() => _vm.Filter = "*1.bin");
        Assert.AreEqual("00000001.BIN", await EntryNames());
        Assert.Contains("1 shown", await _ui.Run(() => _vm.Summary));
    }
}
