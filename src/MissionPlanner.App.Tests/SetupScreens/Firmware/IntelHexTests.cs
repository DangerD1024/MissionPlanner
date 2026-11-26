using MissionPlanner.App.Core.ViewModels.SetupScreens.Firmware;
using static MissionPlanner.App.Tests.SetupScreens.Firmware.DfuTestSupport;

namespace MissionPlanner.App.Tests.SetupScreens.Firmware;

[TestClass]
public class IntelHexTests
{
    [TestMethod]
    public void GapsBetweenRecordsAreErasedFlash()
    {
        var hex = Linear(0x0800) +
                  Record(0, 0x0000, 1, 2, 3, 4) +
                  Record(0, 0x0010, 5, 6) +
                  End;

        var image = IntelHex.Parse(hex);

        Assert.AreEqual(0x08000000u, image.StartAddress);
        Assert.IsTrue(image.StartsAtFlashStart);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 5, 6 }, image.Data);
        Assert.AreEqual(12, image.GapBytes);
        Assert.AreEqual(0x08000012ul, image.EndAddress);
        Assert.AreEqual("0x08000000–0x08000011", image.RangeText);
    }

    [TestMethod]
    public void ExtendedLinearAddressMovesTheUpperHalf()
    {
        // records run across a 64 KB boundary: 0x0800FFF8.. then 0x08010000..
        var hex = Linear(0x0800) +
                  Record(0, 0xFFF8, 0, 1, 2, 3, 4, 5, 6, 7) +
                  Linear(0x0801) +
                  Record(0, 0x0000, 8, 9) +
                  End;

        var image = IntelHex.Parse(hex);

        Assert.AreEqual(0x0800FFF8u, image.StartAddress);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, image.Data);
        Assert.AreEqual(0, image.GapBytes);
    }

    [TestMethod]
    public void BootloaderAndFirmwareMakeOneImageFromTheFlashStart()
    {
        var (hex, expected) = WithBootloader();

        var image = IntelHex.Parse(hex);

        Assert.AreEqual(FlashImage.Stm32FlashStart, image.StartAddress);
        CollectionAssert.AreEqual(expected, image.Data);
        Assert.AreEqual(0x080202E1u, image.EntryPoint, "type 05 start address");
        Assert.AreEqual(0x20000 - 64, image.GapBytes);
    }

    [TestMethod]
    public void SegmentAddressesAndLowercaseAndWindowsLineEndings()
    {
        // 02: segment 0x1000 -> base 0x10000; 03: start segment address CS:IP
        var hex = (Record(0x02, 0, 0x10, 0x00) + Record(0, 0x0004, 0xAB, 0xCD) + Record(0x03, 0, 0x10, 0x00, 0x00, 0x04) + End)
            .ToLowerInvariant().Replace("\n", "  \r\n");

        var image = IntelHex.Parse(hex);

        Assert.AreEqual(0x10004u, image.StartAddress);
        CollectionAssert.AreEqual(new byte[] { 0xAB, 0xCD }, image.Data);
        Assert.AreEqual(0x10004u, image.EntryPoint);
    }

    [TestMethod]
    public void BadChecksumIsRejectedWithTheLine()
    {
        var good = Record(0, 0x0000, 1, 2, 3, 4);
        var bad = good[..^3] + (good[^3] == '0' ? "1" : "0") + good[^2..];
        var ex = Assert.ThrowsExactly<FormatException>(() => IntelHex.Parse(Linear(0x0800) + bad + End));
        StringAssert.Contains(ex.Message, "checksum");
        StringAssert.Contains(ex.Message, "line 2");
    }

    [TestMethod]
    public void BrokenFilesAreRejected()
    {
        StringAssert.Contains(Assert.ThrowsExactly<FormatException>(() => IntelHex.Parse(Linear(0x0800) + Record(0, 0, 1))).Message, "end record");
        StringAssert.Contains(Assert.ThrowsExactly<FormatException>(() => IntelHex.Parse("hello\n" + End)).Message, "Intel HEX");
        StringAssert.Contains(Assert.ThrowsExactly<FormatException>(() => IntelHex.Parse(End)).Message, "no data");
        // the byte count does not match the record length
        StringAssert.Contains(Assert.ThrowsExactly<FormatException>(() => IntelHex.Parse(":0400000001020304\n" + End)).Message, "data bytes");
        // two different values for one address
        StringAssert.Contains(Assert.ThrowsExactly<FormatException>(() =>
            IntelHex.Parse(Record(0, 0, 1, 2) + Record(0, 1, 9) + End)).Message, "two different values");
        // internal flash and external flash in one file
        StringAssert.Contains(Assert.ThrowsExactly<FormatException>(() =>
            IntelHex.Parse(Linear(0x0800) + Record(0, 0, 1) + Linear(0x9000) + Record(0, 0, 2) + End)).Message, "more than one memory");
    }

    [TestMethod]
    public void SameBytesTwiceAreAccepted()
    {
        var image = IntelHex.Parse(Record(0, 0, 1, 2) + Record(0, 1, 2, 3) + End);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, image.Data);
    }

    [TestMethod]
    public void BinaryFilesStartAtTheFlash()
    {
        var image = FlashImage.FromBinary([1, 2, 3]);
        Assert.AreEqual(0x08000000u, image.StartAddress);
        Assert.AreEqual(3, image.Data.Length);
        Assert.ThrowsExactly<FormatException>(() => FlashImage.FromBinary([]));
    }
}
