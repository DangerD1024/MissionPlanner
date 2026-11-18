using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using MavType = MissionPlanner.ArduPilot.APFirmware.MAV_TYPE;
using ReleaseType = MissionPlanner.ArduPilot.APFirmware.RELEASE_TYPES;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

[TestClass]
public class FirmwareCatalogTests
{
    [TestMethod]
    public void ParsesTheManifest()
    {
        var c = FirmwareFixtures.Catalog();
        Assert.AreEqual(15, c.Count);
        // only .apj entries with a board id can go through the bootloader
        Assert.AreEqual(9, c.Uploadable.Count);
        Assert.IsTrue(c.Uploadable.All(f => f.Format == "apj" && f.BoardId != 0));
    }

    [TestMethod]
    public void TilesOfferTheNewestReleaseOfEachVehicle()
    {
        var c = FirmwareFixtures.Catalog();
        var copter = c.Newest(MavType.Copter, ReleaseType.OFFICIAL)!;
        Assert.AreEqual(new Version(4, 7, 2), copter.MavFirmwareVersion);
        Assert.AreEqual("V4.7.2 OFFICIAL", FirmwareCatalog.VersionLabel(copter));

        Assert.AreEqual("V4.7.3-beta1 BETA", FirmwareCatalog.VersionLabel(c.Newest(MavType.Copter, ReleaseType.BETA)!));
        Assert.AreEqual("4.7.2 OFFICIAL", FirmwareCatalog.VersionLabel(c.Newest(MavType.HELICOPTER, ReleaseType.OFFICIAL)!));
        Assert.IsNull(c.Newest(MavType.SUBMARINE, ReleaseType.OFFICIAL));
        Assert.IsNull(c.Newest(MavType.Copter, ReleaseType.DEV));
    }

    [TestMethod]
    public void BoardIsFoundByItsUsbName()
    {
        var c = FirmwareFixtures.Catalog();
        // name first: fmuv3 shares the generic ArduPilot VID/PID but must not be offered
        CollectionAssert.AreEqual(new long[] { 1013 }, c.BoardIdsFor("MatekH743", @"USB\VID_1209&PID_5740").ToArray());
        CollectionAssert.AreEqual(new long[] { 1013 }, c.BoardIdsFor("matekh743", null).ToArray());
    }

    [TestMethod]
    public void BootloaderNameMatchesTheBootloaderString()
    {
        var c = FirmwareFixtures.Catalog();
        CollectionAssert.AreEqual(new long[] { 1013 }, c.BoardIdsFor("MatekH743-BL", "").ToArray());
        CollectionAssert.AreEqual(new long[] { 140 }, c.BoardIdsFor("CubeOrange-BL", null).ToArray());
    }

    [TestMethod]
    public void UnknownNameFallsBackToTheLinuxStyleVidPid()
    {
        var c = FirmwareFixtures.Catalog();
        // what ArduPilot's Linux.GetAllCOMPorts produces: no trailing '&', which APFirmware's regex needs
        CollectionAssert.AreEqual(new long[] { 140 }, c.BoardIdsFor("Some Product", @"USB\VID_2DAE&PID_1016").ToArray());
        // the Windows form with a revision suffix
        CollectionAssert.AreEqual(new long[] { 140 }, c.BoardIdsFor(null, @"USB\VID_2DAE&PID_1016&REV_0200").ToArray());
        // generic ArduPilot VID/PID: every board using it
        CollectionAssert.AreEquivalent(new long[] { 1013, 9 }, c.BoardIdsFor(null, "1209:5740").ToArray());
        Assert.AreEqual(0, c.BoardIdsFor("FTDI", @"USB\VID_0403&PID_6001").Count);
        Assert.AreEqual(0, c.BoardIdsFor(null, null).Count);
    }

    [TestMethod]
    public void CandidatesAreTheBoardsFilesForVehicleAndRelease()
    {
        var c = FirmwareFixtures.Catalog();
        var matek = c.Candidates([1013], MavType.Copter, ReleaseType.OFFICIAL);
        Assert.AreEqual(1, matek.Count);
        Assert.AreEqual("https://firmware.ardupilot.org/Copter/stable/MatekH743/arducopter.apj", matek[0].Url!.AbsoluteUri);

        Assert.AreEqual("https://firmware.ardupilot.org/Copter/beta/MatekH743/arducopter.apj",
            c.Candidates([1013], MavType.Copter, ReleaseType.BETA).Single().Url!.AbsoluteUri);
        Assert.AreEqual("https://firmware.ardupilot.org/Copter/stable/MatekH743-heli/arducopter-heli.apj",
            c.Candidates([1013], MavType.HELICOPTER, ReleaseType.OFFICIAL).Single().Url!.AbsoluteUri);
        Assert.AreEqual(0, c.Candidates([1013], MavType.SUBMARINE, ReleaseType.OFFICIAL).Count);

        // one board id, two builds: the user chooses
        var cube = c.Candidates([140], MavType.Copter, ReleaseType.OFFICIAL);
        CollectionAssert.AreEqual(new[] { "CubeOrange", "CubeOrange-bdshot" }, cube.Select(f => f.Platform).ToArray());
    }

    [TestMethod]
    public void BoardNamesForDisplay()
    {
        var c = FirmwareFixtures.Catalog();
        Assert.AreEqual("MatekH743", c.BoardName(1013));
        Assert.AreEqual("CubeOrange (or CubeOrange-bdshot)", c.BoardName(140));
        Assert.AreEqual("board id 77", c.BoardName(77));
    }

    [TestMethod]
    public void SitlBuildsArePickedByPlatform()
    {
        var c = FirmwareFixtures.Catalog();
        Assert.AreEqual("https://firmware.ardupilot.org/Copter/stable/SITL_x86_64_linux_gnu/arducopter",
            c.ForPlatform("SITL_x86_64_linux_gnu", MavType.Copter, ReleaseType.OFFICIAL)!.Url!.AbsoluteUri);
        Assert.AreEqual("https://firmware.ardupilot.org/Copter/stable/SITL_x86_64_linux_gnu-heli/arducopter-heli",
            c.ForPlatform("SITL_x86_64_linux_gnu", MavType.HELICOPTER, ReleaseType.OFFICIAL)!.Url!.AbsoluteUri);
        Assert.AreEqual("https://firmware.ardupilot.org/Copter/beta/SITL_x86_64_linux_gnu/arducopter",
            c.ForPlatform("SITL_x86_64_linux_gnu", MavType.Copter, ReleaseType.BETA)!.Url!.AbsoluteUri);
        Assert.AreEqual("https://firmware.ardupilot.org/Copter/stable/SITL_arm_linux_gnueabihf/arducopter",
            c.ForPlatform("SITL_arm_linux_gnueabihf", MavType.Copter, ReleaseType.OFFICIAL)!.Url!.AbsoluteUri);
        Assert.IsNull(c.ForPlatform("SITL_x86_64_linux_gnu", MavType.GROUND_ROVER, ReleaseType.OFFICIAL));
    }

    [TestMethod]
    public void UsbIdSpellings()
    {
        var id = new UsbId(0x1209, 0x5740);
        Assert.AreEqual(id, UsbId.Parse(@"USB\VID_1209&PID_5740"));
        Assert.AreEqual(id, UsbId.Parse(@"USB\VID_1209&PID_5740&"));
        Assert.AreEqual(id, UsbId.Parse(@"USB\VID_1209&PID_5740&MI_00\6&2B3C"));
        Assert.AreEqual(id, UsbId.Parse("0x1209/0x5740"));
        Assert.AreEqual(id, UsbId.Parse("1209:5740"));
        Assert.AreEqual(new UsbId(0x26AC, 0x11), UsbId.Parse("0x26AC/0x0011"));
        Assert.IsNull(UsbId.Parse("COM3"));
        Assert.IsNull(UsbId.Parse(""));
        Assert.AreEqual(@"USB\VID_1209&PID_5740", id.HardwareId);
    }
}

[TestClass]
public class FirmwareOptionsTests
{
    [TestMethod]
    public void FacetsNarrowEachOther()
    {
        var o = new FirmwareOptionsViewModel(FirmwareFixtures.Catalog().Uploadable);
        Assert.AreEqual(9, o.MatchCount);
        CollectionAssert.AreEqual(new[] { FirmwareOptionsViewModel.Any, "CubeOrange", "CubeOrange-bdshot", "fmuv3", "MatekH743" },
            o.PlatformOptions.ToArray());

        o.Platform = "MatekH743";
        Assert.AreEqual(6, o.MatchCount);
        CollectionAssert.AreEquivalent(new[] { FirmwareOptionsViewModel.Any, "Copter", "Heli", "Plane", "Antenna Tracker" },
            o.VehicleOptions.ToArray());

        o.Vehicle = "Copter";
        Assert.AreEqual(3, o.MatchCount);
        CollectionAssert.AreEquivalent(new[] { FirmwareOptionsViewModel.Any, "OFFICIAL", "BETA", "STABLE-4.6.3" }, o.ReleaseOptions.ToArray());

        o.Release = "OFFICIAL";
        Assert.AreEqual(1, o.MatchCount);
        Assert.IsNotNull(o.Selected, "a single match is selected");
        Assert.AreEqual("arducopter.apj", o.Selected!.File);

        o.Platform = FirmwareOptionsViewModel.Any;
        Assert.AreEqual(4, o.MatchCount); // copter official on every board
    }

    [TestMethod]
    public void SearchFiltersBoardsAndKeepsChoices()
    {
        var o = new FirmwareOptionsViewModel(FirmwareFixtures.Catalog().Uploadable);
        o.Vehicle = "Copter";
        o.Search = "cube";
        CollectionAssert.AreEqual(new[] { FirmwareOptionsViewModel.Any, "CubeOrange", "CubeOrange-bdshot" }, o.PlatformOptions.ToArray());
        Assert.AreEqual(2, o.MatchCount);
        Assert.AreEqual("Copter", o.Vehicle);

        o.ClearFilters();
        Assert.AreEqual(9, o.MatchCount);
        Assert.AreEqual(FirmwareOptionsViewModel.Any, o.Vehicle);
    }

    [TestMethod]
    public void StartsOnTheDetectedBoard()
    {
        var o = new FirmwareOptionsViewModel(FirmwareFixtures.Catalog().Uploadable, "CubeOrange");
        Assert.AreEqual("CubeOrange", o.Platform);
        Assert.AreEqual(1, o.MatchCount);
    }
}

[TestClass]
public class SerialDeviceScannerTests
{
    [TestMethod]
    public void ReadsUsbDetailsFromSysfs()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("symbolic links need privileges on Windows");

        var root = Path.Combine(TestEnvironment.DataRoot, "sysfs-" + Guid.NewGuid().ToString("N"));
        var usb = Path.Combine(root, "devices", "pci0000:00", "usb1", "1-2");
        Directory.CreateDirectory(Path.Combine(usb, "1-2:1.0", "tty", "ttyACM0"));
        Directory.CreateDirectory(Path.Combine(usb, "1-2:1.2", "tty", "ttyACM1"));
        File.WriteAllText(Path.Combine(usb, "idVendor"), "1209\n");
        File.WriteAllText(Path.Combine(usb, "idProduct"), "5740\n");
        File.WriteAllText(Path.Combine(usb, "product"), "MatekH743\n");
        File.WriteAllText(Path.Combine(usb, "manufacturer"), "ArduPilot\n");
        File.WriteAllText(Path.Combine(usb, "serial"), "3B004F000451303337333532\n");

        // an on-board UART without USB
        var platform = Path.Combine(root, "devices", "platform", "serial8250", "tty", "ttyS0");
        Directory.CreateDirectory(platform);

        var cls = Path.Combine(root, "class", "tty");
        foreach (var (tty, target) in new[]
                 {
                     ("ttyACM0", Path.Combine(usb, "1-2:1.0")), ("ttyACM1", Path.Combine(usb, "1-2:1.2")),
                     ("ttyS0", Path.Combine(root, "devices", "platform", "serial8250")),
                 })
        {
            Directory.CreateDirectory(Path.Combine(cls, tty));
            Directory.CreateSymbolicLink(Path.Combine(cls, tty, "device"), target);
        }

        var devices = SerialDeviceScanner.ScanSysfs(cls, "/dev");

        Assert.AreEqual(2, devices.Count);
        var d = devices[0];
        Assert.AreEqual("/dev/ttyACM0", d.PortName);
        Assert.AreEqual("MatekH743", d.Board);
        Assert.AreEqual("ArduPilot", d.Manufacturer);
        Assert.AreEqual(new UsbId(0x1209, 0x5740), d.Usb);
        Assert.AreEqual(@"USB\VID_1209&PID_5740", d.HardwareId);
        Assert.AreEqual("/dev/ttyACM1", devices[1].PortName);

        // and the board is recognised from it
        var row = PortRow.Create(d, FirmwareFixtures.Catalog());
        Assert.IsTrue(row.IsRecognised);
        Assert.AreEqual("MatekH743 · board id 1013", row.BoardText);

        var info = d.ToDeviceInfo();
        Assert.AreEqual("MatekH743", info.board);
        Assert.AreEqual("/dev/ttyACM0", info.name);
    }

    [TestMethod]
    public void ProbeListHasOneNamePerPort()
    {
        var links = new Dictionary<string, string>
        {
            ["/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if00"] = "/dev/ttyACM0",
            ["/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if02"] = "/dev/ttyACM1",
        };
        var names = new[]
        {
            "/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if00", "/dev/ttyACM0", "/dev/ttyACM1",
            "/dev/serial/by-id/usb-ArduPilot_MatekH743_3B00-if02", "/dev/rfcomm0", "/dev/usbmon1",
            "/dev/tty.usbmodem14101", "/dev/cu.usbmodem14101", "/dev/cu.Bluetooth-Incoming-Port", "COM3", "COM3",
        };

        var probe = SerialDeviceScanner.ProbeCandidates(names, n => links.GetValueOrDefault(n, n));

        CollectionAssert.AreEqual(new[] { "/dev/cu.usbmodem14101", "/dev/ttyACM0", "/dev/ttyACM1", "COM3" }, probe.ToArray());
    }

    [TestMethod]
    public void TerminalsAreNotSerialPorts()
    {
        Assert.IsFalse(SerialDeviceScanner.UartPresent("/dev/tty"));
        Assert.IsFalse(SerialDeviceScanner.UartPresent("/dev/tty0"));
        Assert.IsFalse(SerialDeviceScanner.UartPresent("/dev/tty63"));
        Assert.IsFalse(SerialDeviceScanner.UartPresent("/dev/console"));
        Assert.IsTrue(SerialDeviceScanner.UartPresent("/dev/ttyACM0"));
        Assert.IsTrue(SerialDeviceScanner.UartPresent("/dev/ttyUSB0"));
        Assert.IsTrue(SerialDeviceScanner.UartPresent("/dev/cu.usbmodem1"));
        Assert.IsTrue(SerialDeviceScanner.UartPresent("COM3"));
    }

    [TestMethod]
    public void BootloaderProductStringIsSpotted()
    {
        Assert.IsTrue(new SerialDevice("/dev/ttyACM0", "MatekH743-BL").LooksLikeBootloader);
        Assert.IsFalse(new SerialDevice("/dev/ttyACM0", "MatekH743").LooksLikeBootloader);
        var row = PortRow.Create(new SerialDevice("/dev/ttyACM0", "MatekH743-BL", Usb: new UsbId(0x1209, 0x5740)),
            FirmwareFixtures.Catalog());
        Assert.IsTrue(row.IsBootloader);
        StringAssert.EndsWith(row.BoardText, "in bootloader");
    }
}
