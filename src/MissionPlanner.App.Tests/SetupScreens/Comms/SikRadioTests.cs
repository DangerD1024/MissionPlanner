using MissionPlanner.App.Core.ViewModels.SetupScreens;
using MissionPlanner.App.Core.ViewModels.SetupScreens.Comms;

namespace MissionPlanner.App.Tests.SetupScreens.Comms;

[TestClass]
public sealed class SikParserTests
{
    [TestMethod]
    public void ParsesThePlainAti5Reply()
    {
        var p = SikParser.ParseParameters("ATI5\r\nS0:FORMAT=25\r\nS1:SERIAL_SPEED=57\r\nS2:AIR_SPEED=64\r\nS3:NETID=25\r\nS4:RESERVED=0\r\ngarbage\r\n");
        CollectionAssert.AreEqual(new[] { "FORMAT", "SERIAL_SPEED", "AIR_SPEED", "NETID" }, p.Select(x => x.Name).ToArray());
        Assert.AreEqual("S3", p[3].Designator);
        Assert.AreEqual(25, p[3].Value);
        Assert.IsNull(p[3].Min);
    }

    [TestMethod]
    public void ParsesTheRfdQueryFormWithRangeAndChoices()
    {
        var p = SikParser.ParseParameters("S2:AIR_SPEED(L)[4..1000]=125{4,64,125,250,500,1000,}\r\nR0:TARGET_RSSI[50..255]=255\r\nS16:GPI1_1R/CIN=0\r\n");
        Assert.HasCount(3, p);
        Assert.AreEqual(4, p[0].Min);
        Assert.AreEqual(1000, p[0].Max);
        CollectionAssert.AreEqual(new[] { 4, 64, 125, 250, 500, 1000 }, p[0].Choices!.ToArray());
        Assert.AreEqual("R0", p[1].Designator);
        Assert.AreEqual(255, p[1].Value);
        Assert.AreEqual("GPI1_1R/CIN", p[2].Name);
    }

    [TestMethod]
    public void MultipointPrefixesAreAccepted()
    {
        Assert.IsTrue(SikParser.HasMultipointPrefix("[1] RFD SiK 2.65 MP on RFD900X"));
        Assert.AreEqual("RFD SiK 2.65 MP on RFD900X", SikParser.StripMultipointPrefix("[1] RFD SiK 2.65 MP on RFD900X"));
        var p = SikParser.ParseParameters("[1] S3:NETID=7\r\n[1] S18:NODEID=1\r\n");
        Assert.AreEqual(7, p[0].Value);
        Assert.AreEqual("NODEID", p[1].Name);
    }

    [TestMethod]
    public void BoardAndBandNumbersInDecimalOrHex()
    {
        Assert.AreEqual(0x4e, SikParser.ParseNumber("78"));
        Assert.AreEqual(0x91, SikParser.ParseNumber("0x91"));
        Assert.IsNull(SikParser.ParseNumber("ERROR"));
        Assert.AreEqual("HM-TRP", SikParser.BoardName(78));
        Assert.AreEqual("RFD900X", SikParser.BoardName(0x83));
        Assert.AreEqual("915 MHz", SikParser.FrequencyName(145));
        Assert.AreEqual("433 MHz", SikParser.FrequencyName(0x43));
        Assert.IsTrue(SikParser.IsVersionBanner("SiK 2.0 on HM-TRP"));
        Assert.AreEqual("0123ABCD", SikParser.ParseAesKey("0123ABCD"));
        Assert.IsNull(SikParser.ParseAesKey("ERROR"));
    }

    [TestMethod]
    public void CatalogPicksEditorsAndKeepsTheCurrentValue()
    {
        var radio = new SikRadioInfo("SiK 2.0 on HM-TRP", 0x4e, 0x91, []);
        var air = SikCatalog.Describe(new SikParam("S2", "AIR_SPEED", 100), radio);
        Assert.AreEqual(SikEditorKind.Choice, air.Kind);
        Assert.IsTrue(air.Choices.Any(c => c.Value == 100), "an odd value the radio has stays selectable");
        Assert.IsTrue(air.Choices.Any(c => c.Value == 250));

        Assert.AreEqual(SikEditorKind.Flag, SikCatalog.Describe(new SikParam("S5", "ECC", 0), radio).Kind);
        Assert.AreEqual(SikEditorKind.ReadOnly, SikCatalog.Describe(new SikParam("S0", "FORMAT", 25), radio).Kind);

        var serial = SikCatalog.Describe(new SikParam("S1", "SERIAL_SPEED", 57), radio);
        Assert.AreEqual("57600", serial.Choices.Single(c => c.Value == 57).Label);

        var mav = SikCatalog.Describe(new SikParam("S6", "MAVLINK", 2), radio);
        Assert.AreEqual("Low latency", mav.Choices.Single(c => c.Value == 2).Label);

        var min = SikCatalog.Describe(new SikParam("S8", "MIN_FREQ", 915000), radio);
        Assert.AreEqual(SikEditorKind.Number, min.Kind);
        Assert.AreEqual(902000, min.Min);
        Assert.AreEqual(927000, min.Max);

        var tx = SikCatalog.Describe(new SikParam("S4", "TXPOWER", 20), radio with { BoardId = 0x83 });
        Assert.AreEqual(SikEditorKind.Number, tx.Kind, "RFD radios take 0..30 dBm");
        Assert.AreEqual(30, tx.Max);

        var reported = SikCatalog.Describe(new SikParam("S2", "AIR_SPEED", 125, 4, 1000, [4, 64, 125]), radio);
        CollectionAssert.AreEqual(new[] { 4, 64, 125 }, reported.Choices.Select(c => c.Value).ToArray(), "the radio's own list wins");
    }
}

[TestClass]
public sealed class SikAtClientTests
{
    internal static SikAtClient Fast(IByteLink link) => new(link)
    {
        GuardTimeMs = 10,
        EscapeTimeoutMs = 500,
        CommandTimeoutMs = 300,
        RemoteTimeoutMs = 300,
        QuietMs = 60,
    };

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void EntersCommandModeAndReadsBothRadios()
    {
        var radio = new FakeSikRadio();
        radio.Remote!.Set("NETID", 42);
        var log = new List<string>();
        var at = Fast(radio);
        at.Log = log.Add;

        Assert.IsTrue(at.EnterCommandMode());
        Assert.IsTrue(radio.InCommandMode);

        var local = at.ReadInfo(remote: false)!;
        Assert.AreEqual("SiK 2.0 on HM-TRP", local.Version);
        Assert.AreEqual("HM-TRP", local.BoardName);
        Assert.AreEqual("915 MHz", local.FrequencyName);
        Assert.HasCount(16, local.Parameters);
        Assert.AreEqual(25, local.Find("NETID")!.Value);
        StringAssert.Contains(local.Rssi, "L/R RSSI");
        Assert.IsNull(local.AesKey);

        var remote = at.ReadInfo(remote: true)!;
        Assert.AreEqual(42, remote.Find("NETID")!.Value);
        Assert.Contains("RTI5", radio.Executed.ToList());
        Assert.IsTrue(log.Any(l => l.StartsWith("> ATI5")) && log.Any(l => l.StartsWith("< S3:NETID=25")));

        at.ExitCommandMode();
        Assert.IsFalse(radio.InCommandMode);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void NoRemoteMeansNullAndARadioAlreadyInCommandModeStillWorks()
    {
        var radio = new FakeSikRadio(remotePresent: false);
        radio.StartInCommandMode();
        var at = Fast(radio);

        Assert.IsTrue(at.EnterCommandMode(), "'+++' typed at the prompt fails, a plain AT then answers OK");
        Assert.IsNotNull(at.ReadInfo(remote: false));
        Assert.IsNull(at.ReadInfo(remote: true));
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void SilentPortFailsToEnterCommandMode()
    {
        var at = Fast(new SilentLink());
        Assert.IsFalse(at.EnterCommandMode());
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void SetSaveAndReboot()
    {
        var radio = new FakeSikRadio();
        var at = Fast(radio);
        Assert.IsTrue(at.EnterCommandMode());

        Assert.IsTrue(at.SetParameter(remote: false, "S3", 77));
        Assert.IsFalse(at.SetParameter(remote: false, "S99", 1), "ERROR for an unknown register");
        Assert.IsTrue(at.SetParameter(remote: true, "S3", 77));
        Assert.AreEqual(77, radio.Local.Get("NETID"));
        Assert.AreEqual(25, radio.Local.StoredValue("NETID"), "not stored before AT&W");

        Assert.IsTrue(at.Save(remote: true));
        Assert.IsTrue(at.Save(remote: false));
        at.Reboot(remote: true);
        at.Reboot(remote: false);
        Assert.AreEqual(77, radio.Local.StoredValue("NETID"));
        Assert.AreEqual(77, radio.Remote!.StoredValue("NETID"));
        Assert.AreEqual(1, radio.Local.Reboots);
        Assert.IsFalse(radio.InCommandMode, "ATZ leaves command mode");
    }

    private sealed class SilentLink : IByteLink
    {
        public string Name => "silent";

        public bool IsOpen => true;

        public void Open()
        {
        }

        public void Close()
        {
        }

        public void Write(byte[] data, int offset, int count)
        {
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            Thread.Sleep(Math.Min(timeoutMs, 20));
            return 0;
        }

        public void DiscardInput()
        {
        }

        public void Dispose()
        {
        }
    }
}

[TestClass]
public sealed class SikRadioViewModelTests
{
    private static (SikRadioViewModel Vm, CommsUi Ui, FakeSikRadio Radio) Create(bool remote = true)
    {
        var ui = new CommsUi();
        var radio = new FakeSikRadio(remote);
        var vm = new SikRadioViewModel(ui.Services(), (_, _) => radio, SikAtClientTests.Fast);
        vm.SelectedPort ??= "/dev/ttyFAKE0";
        return (vm, ui, radio);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LoadShowsLocalAndRemoteSideBySide()
    {
        var (vm, _, radio) = Create();
        radio.Remote!.Set("TXPOWER", 11);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.HasSettings, vm.Status);
        Assert.IsTrue(vm.HasRemote);
        Assert.AreEqual("SiK 2.0 on HM-TRP", vm.LocalVersion);
        Assert.AreEqual("HM-TRP · 915 MHz", vm.LocalHardware);
        Assert.HasCount(16, vm.Rows);
        var tx = vm.Rows.Single(r => r.Name == "TXPOWER");
        Assert.AreEqual(20, tx.Local!.Value);
        Assert.AreEqual(11, tx.Remote!.Value);
        Assert.IsTrue(tx.Local.IsChoice);
        Assert.IsFalse(vm.Rows.Any(r => r.Mismatch));
        Assert.IsFalse(radio.InCommandMode, "Load leaves the radio passing data again (ATO)");
        Assert.IsTrue(radio.Disposed, "the port is closed after the operation");
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SaveWritesOnlyChangesThenStoresAndRestartsBoth()
    {
        var (vm, ui, radio) = Create();
        await vm.LoadCommand.ExecuteAsync(null);

        var netid = vm.Rows.Single(r => r.Name == "NETID");
        netid.Local!.Value = 99;
        Assert.IsTrue(netid.Mismatch, "net ID must match on both radios");
        vm.CopyLocalToRemoteCommand.Execute(null);
        Assert.AreEqual(99, netid.Remote!.Value);
        Assert.IsFalse(netid.Mismatch);
        vm.Rows.Single(r => r.Name == "ECC").Local!.Value = 1;
        vm.Rows.Single(r => r.Name == "ECC").Remote!.Value = 1;

        while (radio.Executed.TryDequeue(out _))
        {
        }

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.IsTrue(vm.Status.StartsWith("Saved."), vm.Status);
        var cmds = radio.Executed.ToList();
        CollectionAssert.IsSubsetOf(new[] { "RTS3=99", "RTS5=1", "ATS3=99", "ATS5=1", "RT&W", "RTZ", "AT&W", "ATZ" }, cmds);
        Assert.IsLessThan(cmds.IndexOf("ATS3=99"), cmds.IndexOf("RTS3=99"), "remote first, as Mission Planner");
        Assert.IsLessThan(cmds.IndexOf("ATZ"), cmds.IndexOf("RTZ"));
        Assert.IsFalse(cmds.Any(c => c.StartsWith("ATS1=") || c.StartsWith("ATS0=")), "unchanged settings are not written");
        Assert.AreEqual(99, radio.Local.StoredValue("NETID"));
        Assert.AreEqual(99, radio.Remote!.StoredValue("NETID"));
        Assert.AreEqual(1, radio.Remote.StoredValue("ECC"));
        Assert.IsFalse(netid.Local.IsChanged, "written values become the new originals");
        Assert.HasCount(1, ui.Confirms);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SaveRefusesAnInvalidFrequencyRange()
    {
        var (vm, ui, radio) = Create(remote: false);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.Rows.Single(r => r.Name == "MIN_FREQ").Local!.Value = 927000;
        vm.Rows.Single(r => r.Name == "MAX_FREQ").Local!.Value = 920000;
        var before = radio.Executed.Count;

        await vm.SaveCommand.ExecuteAsync(null);

        StringAssert.Contains(ui.Messages.Last(), "Min freq must be below Max freq");
        Assert.AreEqual(before, radio.Executed.Count, "nothing sent");
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResetToDefaultsResetsBothRadios()
    {
        var (vm, _, radio) = Create();
        radio.Local.Set("NETID", 5);
        radio.Local.Stored = radio.Local.Values.ToArray();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.ResetDefaultsCommand.ExecuteAsync(null);

        var cmds = radio.Executed.ToList();
        CollectionAssert.IsSubsetOf(new[] { "RT&F", "RT&W", "RTZ", "AT&F", "AT&W", "ATZ" }, cmds);
        Assert.AreEqual(25, radio.Local.StoredValue("NETID"));
        Assert.IsFalse(vm.HasSettings, "the old values are gone; Load shows the defaults");
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ANoAnswerRadioReportsTheProblem()
    {
        var ui = new CommsUi();
        var vm = new SikRadioViewModel(ui.Services(), (_, _) => new FakeSikRadioThatIgnoresEscape(), SikAtClientTests.Fast);
        vm.SelectedPort ??= "/dev/ttyFAKE0";

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.IsFalse(vm.HasSettings);
        StringAssert.Contains(vm.Status, "No answer to +++");
        Assert.HasCount(1, ui.Messages);
    }

    /// <summary>A port with something on it that is not a SiK radio.</summary>
    private sealed class FakeSikRadioThatIgnoresEscape : IByteLink
    {
        public string Name => "not a radio";

        public bool IsOpen => true;

        public void Open()
        {
        }

        public void Close()
        {
        }

        public void Write(byte[] data, int offset, int count)
        {
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs)
        {
            Thread.Sleep(Math.Min(timeoutMs, 20));
            return 0;
        }

        public void DiscardInput()
        {
        }

        public void Dispose()
        {
        }
    }
}
