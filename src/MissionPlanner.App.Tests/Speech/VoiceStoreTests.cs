using MissionPlanner.App.Core.Speech;

namespace MissionPlanner.App.Tests.Speech;

[TestClass]
public class VoiceCatalogTests
{
    [TestMethod]
    public void EveryVoiceHasAUniqueIdAReleaseUrlASizeAndALicence()
    {
        Assert.IsNotEmpty(VoiceCatalog.All);
        Assert.HasCount(VoiceCatalog.All.Count, VoiceCatalog.All.Select(v => v.Id).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.HasCount(VoiceCatalog.All.Count, VoiceCatalog.All.Select(v => v.Folder).Distinct());
        foreach (var v in VoiceCatalog.All)
        {
            StringAssert.StartsWith(v.Url.ToString(), "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/", v.Id);
            StringAssert.EndsWith(v.Archive, ".tar.bz2", v.Id);
            Assert.AreEqual(v.Archive[..^".tar.bz2".Length], v.Folder, v.Id);
            Assert.IsGreaterThan(1_000_000L, v.DownloadBytes, v.Id);
            Assert.IsGreaterThan(1_000_000L, v.InstalledBytes, v.Id);
            // only licences that allow redistribution and any use, commercial included
            StringAssert.Contains(v.License, "Apache-2.0", v.Id);
            StringAssert.StartsWith(v.LicenseUrl, "https://", v.Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(v.Description), v.Id);
            Assert.IsNotEmpty(v.Speakers, v.Id);
            Assert.HasCount(v.Speakers.Count, v.Speakers.Select(s => s.Id).Distinct(), v.Id + ": speaker ids are unique");
            Assert.IsTrue(v.Speakers.Any(s => s.Id == v.DefaultSpeaker), v.Id + ": the default speaker is listed");
            StringAssert.EndsWith(v.ModelFile, ".onnx", v.Id);
        }

        Assert.IsTrue(VoiceCatalog.SharedLexicon.Url.ToString().EndsWith("/lexicon-en-us.txt", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OneCleanFemaleEnglishVoiceIsRecommended()
    {
        var recommended = VoiceCatalog.All.Where(v => v.Recommended).ToList();
        Assert.HasCount(1, recommended);
        var voice = recommended[0];
        Assert.AreSame(voice, VoiceCatalog.Recommended);
        Assert.AreEqual(VoiceModelKind.Kokoro, voice.Kind);
        var speaker = voice.SpeakerOrDefault(-1);
        Assert.AreEqual("Heart", speaker.Name, "Kokoro's best-rated voice (grade A)");
        Assert.AreEqual("US", speaker.Accent);
        Assert.AreEqual("female", speaker.Gender);
    }

    [TestMethod]
    public void KokorosUkSpeakersAreReadWithTheBritishLexicon()
    {
        foreach (var s in VoiceCatalog.KokoroSpeakers)
            Assert.AreEqual(s.Accent == "UK", s.British, s.Name);
        Assert.IsTrue(VoiceCatalog.All.Where(v => v.Kind == VoiceModelKind.Kokoro).All(v => !v.SharedLexicon), "Kokoro brings its own lexicons");
        Assert.IsTrue(VoiceCatalog.All.Where(v => v.Kind == VoiceModelKind.Kitten).All(v => v.SharedLexicon));
    }
}

[TestClass]
public class VoiceStoreTests
{
    private static VoiceInfo Kokoro => VoiceCatalog.Find("kokoro-v1")!;

    private static VoiceInfo Kitten => VoiceCatalog.Find("kitten-nano-v0.8")!;

    [TestMethod]
    public async Task InstallsALocalArchiveAndUnpacksOnlyTheVoicesFiles()
    {
        var work = SpeechTestSupport.NewFolder("voices-src");
        var root = SpeechTestSupport.NewFolder("voices");
        var archive = SpeechTestSupport.BuildArchive(work, Kokoro);
        var voice = SpeechTestSupport.Sized(Kokoro, archive);
        var progress = new List<VoiceStatus>();
        var store = new VoiceStore(root, SpeechTestSupport.LocalDownload(archive));
        store.Changed += (_, id) =>
        {
            lock (progress)
                progress.Add(store.Status(voice));
        };

        Assert.AreEqual(VoiceInstallState.NotInstalled, store.Status(voice).State);
        Assert.IsTrue(await store.InstallAsync(voice));

        Assert.IsTrue(store.IsInstalled(voice));
        Assert.AreEqual(VoiceInstallState.Installed, store.Status(voice).State);
        var dir = store.VoiceDirectory(voice);
        Assert.AreEqual(Path.Combine(root, "kokoro-multi-lang-v1_0"), dir);
        foreach (var f in VoiceStore.RequiredFiles(voice))
            Assert.IsTrue(File.Exists(Path.Combine(dir, f)), f);
        Assert.IsTrue(File.Exists(Path.Combine(dir, "LICENSE")), "the licence comes along");
        Assert.IsTrue(File.Exists(Path.Combine(dir, VoiceStore.MarkerName)), "marker written last");
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "espeak-ng-data")), "eSpeak data is not used by sherpa-onnx 2.x");
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "dict")));
        Assert.IsFalse(File.Exists(Path.Combine(dir, "date-zh.fst")));
        Assert.IsFalse(File.Exists(Path.Combine(dir, "link")), "links are never unpacked");
        Assert.IsFalse(File.Exists(Path.Combine(root, "escaped.txt")), "no entry escapes the voice folder");
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "other-folder")));
        Assert.IsFalse(File.Exists(Path.Combine(root, voice.Archive)), "the download is removed after unpacking");
        Assert.IsEmpty(Directory.GetDirectories(root, ".*"), "no temporary folders left");
        lock (progress)
        {
            Assert.IsTrue(progress.Any(p => p.State == VoiceInstallState.Downloading), "download progress");
            Assert.IsTrue(progress.Any(p => p.State == VoiceInstallState.Installing), "unpacking progress");
        }

        Assert.IsGreaterThan(0L, store.InstalledSize(voice));
    }

    [TestMethod]
    public async Task ADownloadOfTheWrongSizeIsRejectedAndLeavesNothing()
    {
        var work = SpeechTestSupport.NewFolder("voices-src");
        var root = SpeechTestSupport.NewFolder("voices");
        var archive = SpeechTestSupport.BuildArchive(work, Kokoro);
        var voice = Kokoro with { DownloadBytes = new FileInfo(archive).Length + 1, InstalledBytes = 1000 };
        var store = new VoiceStore(root, SpeechTestSupport.LocalDownload(archive));

        Assert.IsFalse(await store.InstallAsync(voice));

        var status = store.Status(voice);
        Assert.AreEqual(VoiceInstallState.Failed, status.State);
        StringAssert.Contains(status.Error, "expected");
        Assert.IsFalse(store.IsInstalled(voice));
        Assert.IsEmpty(Directory.GetFileSystemEntries(root), "nothing left behind");
    }

    [TestMethod]
    public async Task AnArchiveWithoutTheModelIsRejected()
    {
        var work = SpeechTestSupport.NewFolder("voices-src");
        var root = SpeechTestSupport.NewFolder("voices");
        var archive = SpeechTestSupport.BuildArchive(work, Kokoro, leaveOut: [Kokoro.ModelFile]);
        var voice = SpeechTestSupport.Sized(Kokoro, archive);
        var store = new VoiceStore(root, SpeechTestSupport.LocalDownload(archive));

        Assert.IsFalse(await store.InstallAsync(voice));

        StringAssert.Contains(store.Status(voice).Error, Kokoro.ModelFile);
        Assert.IsFalse(store.IsInstalled(voice));
        Assert.IsFalse(Directory.Exists(store.VoiceDirectory(voice)));
        Assert.IsEmpty(Directory.GetFileSystemEntries(root));
    }

    [TestMethod]
    public async Task NotEnoughDiskSpaceStopsBeforeDownloading()
    {
        var root = SpeechTestSupport.NewFolder("voices");
        var fetched = new List<Uri>();
        var store = new VoiceStore(root, SpeechTestSupport.LocalDownload("unused", fetched), _ => 10_000_000);

        Assert.IsFalse(await store.InstallAsync(Kokoro));

        StringAssert.Contains(store.Status(Kokoro).Error, "disk space");
        Assert.IsEmpty(fetched);
    }

    [TestMethod]
    public async Task ACancelledDownloadLeavesNothing()
    {
        var root = SpeechTestSupport.NewFolder("voices");
        var started = new TaskCompletionSource();
        var store = new VoiceStore(root, async (_, path, _, cancel) =>
        {
            await File.WriteAllTextAsync(path + ".part", "partial", cancel);
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancel);
            }
            finally
            {
                File.Delete(path + ".part"); // as FileDownloader does
            }
        });

        var install = store.InstallAsync(Kokoro);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(store.Status(Kokoro).Busy);
        store.Cancel(Kokoro);

        Assert.IsFalse(await install.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(VoiceInstallState.NotInstalled, store.Status(Kokoro).State, "cancelled is not an error");
        Assert.IsEmpty(Directory.GetFileSystemEntries(root));
    }

    [TestMethod]
    public async Task DeleteRemovesTheVoiceAndTheSharedLexiconOnceUnused()
    {
        var work = SpeechTestSupport.NewFolder("voices-src");
        var root = SpeechTestSupport.NewFolder("voices");
        var archive = SpeechTestSupport.BuildArchive(work, Kitten, withHostileEntries: false);
        var voice = SpeechTestSupport.Sized(Kitten, archive);
        var fetched = new List<Uri>();
        var lexicon = new string('x', (int)VoiceCatalog.SharedLexicon.Bytes);
        var store = new VoiceStore(root, SpeechTestSupport.LocalDownload(archive, fetched, lexicon));

        Assert.IsTrue(await store.InstallAsync(voice));
        Assert.IsTrue(store.SharedLexiconInstalled, "Kitten needs sherpa-onnx's English lexicon");
        CollectionAssert.Contains(fetched, VoiceCatalog.SharedLexicon.Url);
        Assert.IsTrue(store.IsInstalled(voice));

        // reinstalling replaces the folder in one step
        Assert.IsTrue(await store.InstallAsync(voice));
        Assert.IsTrue(store.IsInstalled(voice));
        Assert.HasCount(1, fetched.Where(u => u == VoiceCatalog.SharedLexicon.Url), "the lexicon is fetched once");

        store.Delete(voice);
        Assert.IsFalse(store.IsInstalled(voice));
        Assert.IsFalse(Directory.Exists(store.VoiceDirectory(voice)));
        Assert.IsFalse(store.SharedLexiconInstalled, "no installed voice needs it any more");
        Assert.AreEqual(VoiceInstallState.NotInstalled, store.Status(voice).State);
    }

    [TestMethod]
    public void CleanUpRemovesWhatAnInterruptedRunLeft()
    {
        var root = SpeechTestSupport.NewFolder("voices");
        Directory.CreateDirectory(Path.Combine(root, ".tmp-kokoro-v1-1234"));
        Directory.CreateDirectory(Path.Combine(root, ".deleted-kokoro-multi-lang-v1_0-1234"));
        new VoiceStore(root).CleanUp();
        Assert.IsEmpty(Directory.GetFileSystemEntries(root));
    }
}
