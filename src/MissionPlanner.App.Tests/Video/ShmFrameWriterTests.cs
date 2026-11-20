using System.IO.MemoryMappedFiles;
using MissionPlanner.App.Core.Video;

namespace MissionPlanner.App.Tests.Video;

[TestClass]
public class ShmFrameWriterTests
{
    [TestMethod]
    public void WritesSeqlockedFramesAReaderCanTake()
    {
        var path = Path.Combine(Path.GetTempPath(), "mp-shm-test-" + Guid.NewGuid().ToString("N"));
        const int w = 64, h = 48;
        var frameBytes = w * h * 4;
        var total = ShmFrameWriter.HeaderBytes + ShmFrameWriter.Slots * frameBytes;
        try
        {
            using var writer = new ShmFrameWriter(path);
            var f1 = new byte[frameBytes];
            for (var i = 0; i < f1.Length; i++) f1[i] = (byte)(i % 251);
            writer.Write(f1, w, h);

            using var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, total);
            using var a = file.CreateViewAccessor(0, total);
            Assert.AreEqual(ShmFrameWriter.Magic, a.ReadUInt32(0));
            Assert.AreEqual(1u, a.ReadUInt32(4));
            Assert.AreEqual((uint)w, a.ReadUInt32(8));
            Assert.AreEqual((uint)h, a.ReadUInt32(12));
            Assert.AreEqual((uint)(w * 4), a.ReadUInt32(16));
            Assert.AreEqual((uint)ShmFrameWriter.Slots, a.ReadUInt32(20));

            // seqlock read: seq must not change across the copy
            var seq1 = a.ReadUInt64(24);
            var slot = (int)a.ReadUInt32(40);
            var buf = new byte[frameBytes];
            a.ReadArray(ShmFrameWriter.HeaderBytes + slot * frameBytes, buf, 0, frameBytes);
            var seq2 = a.ReadUInt64(24);
            Assert.AreEqual(1ul, seq1);
            Assert.AreEqual(seq1, seq2);
            Assert.AreEqual(0, slot);
            CollectionAssert.AreEqual(f1, buf);

            // second frame lands in the next slot and bumps seq
            var f2 = new byte[frameBytes];
            for (var i = 0; i < f2.Length; i++) f2[i] = (byte)(255 - i % 251);
            writer.Write(f2, w, h);
            Assert.AreEqual(2ul, a.ReadUInt64(24));
            Assert.AreEqual(1u, a.ReadUInt32(40));
            a.ReadArray(ShmFrameWriter.HeaderBytes + 1 * frameBytes, buf, 0, frameBytes);
            CollectionAssert.AreEqual(f2, buf);
        }
        finally
        {
            File.Delete(path);
        }
    }
}