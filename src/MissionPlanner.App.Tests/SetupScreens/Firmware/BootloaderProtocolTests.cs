using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

[TestClass]
public class BootloaderProtocolTests
{
    [TestMethod]
    public void ApjImageIsInflatedAndPaddedWithErasedBytes()
    {
        var image = FirmwareFixtures.Image(1001);
        var fw = ApjFirmware.Parse(FirmwareFixtures.Apj(1013, image));

        Assert.AreEqual(1013, fw.BoardId);
        Assert.AreEqual(1001, fw.ImageSize);
        Assert.AreEqual(1004, fw.Image.Length);
        CollectionAssert.AreEqual(image, fw.Image.Take(1001).ToArray());
        CollectionAssert.AreEqual(new byte[] { 0xff, 0xff, 0xff }, fw.Image.Skip(1001).ToArray());
        Assert.AreEqual("TESTBOARD", fw.Summary);
        Assert.AreEqual("abcdef12", fw.GitIdentity);
        Assert.AreEqual(0, fw.ExtfImageSize);
    }

    [TestMethod]
    public void ApjExternalFlashImageIsRead()
    {
        var extf = FirmwareFixtures.Image(70, seed: 3);
        var fw = ApjFirmware.Parse(FirmwareFixtures.Apj(1013, FirmwareFixtures.Image(64), extf));
        Assert.AreEqual(70, fw.ExtfImageSize);
        Assert.AreEqual(72, fw.ExtfImage.Length);
        CollectionAssert.AreEqual(extf, fw.ExtfImage.Take(70).ToArray());
    }

    [TestMethod]
    public void NotAFirmwareFileIsRejected()
    {
        Assert.ThrowsExactly<FormatException>(() => ApjFirmware.Parse("hello"));
        Assert.ThrowsExactly<FormatException>(() => ApjFirmware.Parse("""{"board_id": 9}"""));
        Assert.ThrowsExactly<FormatException>(() => ApjFirmware.Parse("""{"board_id": 9, "image": "AAAA", "image_size": 10}"""));
    }

    [TestMethod]
    public void CrcUsesTheBootloaderTable()
    {
        // px4uploader's table: entry 1 and 255
        Assert.AreEqual(0x77073096u, ApjFirmware.Crc32([0x01], 0));
        Assert.AreEqual(0x2D02EF8Du, ApjFirmware.Crc32([0xff], 0));

        // padding to the flash size is the CRC of the image followed by erased flash
        var fw = ApjFirmware.Parse(FirmwareFixtures.Apj(1, FirmwareFixtures.Image(100)));
        var flash = new byte[256];
        Array.Fill(flash, (byte)0xff);
        fw.Image.CopyTo(flash, 0);
        Assert.AreEqual(ApjFirmware.Crc32(flash, 0), fw.Crc(256));
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public void IdentifyReadsTheBoard()
    {
        using var bl = new Px4Bootloader(new FakeBootloaderPort("/dev/ttyACM0", 1013, flashSize: 128 * 1024));
        var info = bl.Identify();

        Assert.AreEqual(1013, info.BoardId);
        Assert.AreEqual(5, info.BootloaderRevision);
        Assert.AreEqual(128 * 1024, info.FlashSize);
        Assert.AreEqual("STM32H7[4-5]x,V", info.ChipDescription);
        Assert.AreEqual(0x450, info.Chip);
        Assert.AreEqual(24, info.SerialNumber!.Length);
        Assert.AreEqual("/dev/ttyACM0", info.PortName);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public void SilentPortFailsQuickly()
    {
        using var bl = new Px4Bootloader(new FakeBootloaderPort("/dev/ttyUSB0", 9) { Silent = true });
        var start = Environment.TickCount64;
        Assert.ThrowsExactly<TimeoutException>(() => bl.Identify());
        Assert.IsTrue(Environment.TickCount64 - start < 2000);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public void UnsupportedProtocolRevisionIsRefused()
    {
        using var bl = new Px4Bootloader(new FakeBootloaderPort("/dev/ttyACM0", 9, blRev: 1));
        Assert.ThrowsExactly<IOException>(() => bl.Identify());
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void UploadProgramsVerifiesAndReboots()
    {
        var image = FirmwareFixtures.Image(3000, seed: 5);
        var fw = ApjFirmware.Parse(FirmwareFixtures.Apj(1013, image));
        var port = new FakeBootloaderPort("/dev/ttyACM0", 1013);
        using var bl = new Px4Bootloader(port);
        bl.Identify();

        Assert.IsFalse(bl.HasSameFirmware(fw));

        var steps = new List<(FlashStep Step, double Pct)>();
        bl.Upload(fw, (s, p) => steps.Add((s, p)), _ => { }, CancellationToken.None);

        CollectionAssert.AreEqual(image, port.Flash.Take(image.Length).ToArray());
        Assert.IsTrue(port.Flash.Skip(fw.Image.Length).All(b => b == 0xff));
        Assert.IsTrue(port.Rebooted);
        Assert.AreEqual(100, steps.Last(s => s.Step == FlashStep.Program).Pct, 1e-9);
        Assert.IsTrue(steps.Any(s => s.Step == FlashStep.Verify));
        Assert.AreEqual(12, port.Commands.Count(c => c == 0x27), "3000 bytes in 252-byte chunks");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void SameFirmwareIsRecognisedByCrc()
    {
        var image = FirmwareFixtures.Image(2000, seed: 9);
        var fw = ApjFirmware.Parse(FirmwareFixtures.Apj(1013, image));
        var port = new FakeBootloaderPort("/dev/ttyACM0", 1013);
        port.Preload(fw.Image);
        using var bl = new Px4Bootloader(port);
        bl.Identify();
        Assert.IsTrue(bl.HasSameFirmware(fw));
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void RevisionTwoBootloaderVerifiesByReadingBack()
    {
        var image = FirmwareFixtures.Image(600, seed: 2);
        var fw = ApjFirmware.Parse(FirmwareFixtures.Apj(5, image));
        var port = new FakeBootloaderPort("/dev/ttyACM0", 5, blRev: 2);
        using var bl = new Px4Bootloader(port);
        bl.Identify();

        Assert.IsFalse(bl.HasSameFirmware(fw), "revision 2 cannot report a CRC");
        bl.Upload(fw, (_, _) => { }, _ => { }, CancellationToken.None);

        Assert.IsTrue(port.Commands.Contains(0x24), "CHIP_VERIFY");
        Assert.IsTrue(port.Commands.Contains(0x28), "READ_MULTI");
        Assert.IsFalse(port.Commands.Contains(0x29), "no GET_CRC");
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void FirmwareForAnotherBoardIsRefused()
    {
        var fw = ApjFirmware.Parse(FirmwareFixtures.Apj(140, FirmwareFixtures.Image(100)));
        var port = new FakeBootloaderPort("/dev/ttyACM0", 1013);
        using var bl = new Px4Bootloader(port);
        bl.Identify();
        Assert.ThrowsExactly<InvalidOperationException>(() => bl.Upload(fw, (_, _) => { }, _ => { }, CancellationToken.None));
        Assert.AreEqual(0, port.EraseCount);
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public void UploadStopsBetweenChunksWhenCancelled()
    {
        var fw = ApjFirmware.Parse(FirmwareFixtures.Apj(1013, FirmwareFixtures.Image(20000)));
        var port = new FakeBootloaderPort("/dev/ttyACM0", 1013);
        using var bl = new Px4Bootloader(port);
        bl.Identify();
        using var cts = new CancellationTokenSource();

        Assert.ThrowsExactly<OperationCanceledException>(() => bl.Upload(fw, (s, p) =>
        {
            if (s == FlashStep.Program && p > 20)
                cts.Cancel();
        }, _ => { }, cts.Token));
        Assert.IsFalse(port.Rebooted, "a cancelled flash leaves the board in its bootloader");
    }
}
