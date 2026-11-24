using MissionPlanner.App.Core.ViewModels.Tools;

namespace MissionPlanner.App.Tests.Tools;

[TestClass]
public class OpticalFlowTests
{
    /// <summary>A 64×64 gradient sent as the PX4Flow does: 253-byte pieces, the last one short.</summary>
    [TestMethod]
    public void PiecesMakeTheImage()
    {
        const int w = 64, h = 64, payload = 253;
        var image = new byte[w * h];
        for (var i = 0; i < image.Length; i++)
            image[i] = (byte)(i % w * 4 + i / w);
        var packets = (image.Length + payload - 1) / payload;

        var a = new OpticalFlowImageAssembler();
        Assert.IsNull(a.Data(new MAVLink.mavlink_encapsulated_data_t { seqnr = 0, data = new byte[payload] }), "no handshake yet");
        a.Handshake(new MAVLink.mavlink_data_transmission_handshake_t
        {
            size = (uint)image.Length, width = w, height = h, packets = (ushort)packets, payload = payload, jpg_quality = 0, type = 0,
        });

        GrayImage? done = null;
        // out of order is fine: each piece has its place
        foreach (var seq in Enumerable.Range(0, packets - 1).Reverse().Append(packets - 1))
        {
            var piece = new byte[payload];
            Array.Copy(image, seq * payload, piece, 0, Math.Min(payload, image.Length - seq * payload));
            var r = a.Data(new MAVLink.mavlink_encapsulated_data_t { seqnr = (ushort)seq, data = piece });
            if (seq != packets - 1)
                Assert.IsNull(r);
            else
                done = r;
        }

        Assert.IsNotNull(done);
        Assert.AreEqual(w, done.Width);
        Assert.AreEqual(h, done.Height);
        CollectionAssert.AreEqual(image, done.Pixels);

        var bgra = done.ToBgra();
        var p = 5 * w + 7;
        Assert.AreEqual(image[p], bgra[4 * p]);
        Assert.AreEqual(image[p], bgra[4 * p + 1]);
        Assert.AreEqual(image[p], bgra[4 * p + 2]);
        Assert.AreEqual(255, bgra[4 * p + 3]);
    }
}
